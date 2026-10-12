using System;
using System.Collections;
using System.Device.Wifi;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using nanoFramework.Azure.Devices.Client;
using nanoFramework.Azure.Devices.Shared;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.Runtime.Native;

namespace nfaz3166iothub
{
    /// <summary>
    /// AZ3166 telemetry to Azure IoT Hub (MQTT over TLS), with an OLED dashboard.
    ///
    /// Device to cloud: telemetry (all sensors, button states, Wi-Fi diagnostics) as JSON every
    ///                  TelemetryIntervalSeconds, device twin reported properties.
    /// Cloud to device: cloud-to-device messages (shown on the display), device twin desired property
    ///                  "telemetryInterval" (seconds), direct methods:
    ///                    ShowText      payload "text"   shows a message on the display
    ///                    SetInterval   payload 30       telemetry interval in seconds
    ///                    GetTelemetry  (no payload)     returns the latest readings
    ///                    Reboot        (no payload)     restarts the device
    /// </summary>
    public class Program
    {
        //////////////////////////////////////////////////////////////////////////////
        // configuration - UPDATE THESE

        private const string WifiSsid = "YourWiFiSSID";
        private const string WifiPassword = "YourWiFiPassword";

        // the IoT Hub host name, the device id registered in the hub and the device's primary (or secondary) key
        private const string IotHubHostName = "yourhub.azure-devices.net";
        private const string DeviceId = "az3166";
        private const string SasKey = "base64 primary key of the device";

        // false = don't read the device twin and don't report properties after connecting (telemetry, methods and
        // cloud-to-device messages still work). A switch for finding out why a hub drops the connection.
        private const bool UseDeviceTwin = true;

        // how often telemetry is sent (can be changed with the "telemetryInterval" desired property or the
        // SetInterval method). The free IoT Hub tier allows 8000 messages a day: at 15 s the device uses 5760.
        private static int _telemetryIntervalSeconds = 15;

        //////////////////////////////////////////////////////////////////////////////
        // board

        internal const int I2cBus = 1;      // I2C1 on PB8/PB9: sensors and OLED
        internal const int ButtonAPin = 4;  // PA4
        internal const int ButtonBPin = 10; // PA10

        private const string AppVersion = "1.0.2";

        private static Dashboard _dashboard;
        private static DeviceClient _hub;
        private static bool _hubDisconnected;
        private static bool _publishNow;
        private static bool _everConnected;
        private static bool _rebootRequested;
        private static DateTime _rebootAt;

        public static void Main()
        {
            Log("AZ3166 Azure IoT Hub");
            Log("App " + AppVersion);

            // a window to stop or redeploy from Visual Studio before any hardware is touched
            Log("Starting in 5 s...");
            Thread.Sleep(5000);

            _dashboard = new Dashboard();

            // open display, sensors and buttons on their own thread, so a hang there can't stop the rest
            new Thread(_dashboard.InitHardware).Start();

            if (_dashboard.HardwareReady.WaitOne(8000, false))
            {
                Log("Sensors " + (Telemetry.HasClimate ? "T" : "-") + (Telemetry.HasPressure ? "P" : "-") +
                    (Telemetry.HasMotion ? "M" : "-") + (Telemetry.HasMagnetometer ? "C" : "-") +
                    " OLED " + (_dashboard.DisplayPresent ? "ok" : "no"));
            }
            else
            {
                Log("HW init stuck: " + Dashboard.InitStep);
            }

            // the dashboard thread draws the boot log until start up is done
            _dashboard.Start();

            NetworkInterface network = NetworkInterface.GetAllNetworkInterfaces()[0];
            byte[] mac = network.PhysicalAddress;
            Telemetry.MacAddress = FormatMac(mac);
            Telemetry.Broker = IotHubHostName;

            Log("Device " + DeviceId);

            ConnectWifi();

            // the TLS certificate check needs the correct date
            WaitForTime();

            int secondsSincePublish = _telemetryIntervalSeconds;
            int secondsSinceScan = 0;
            int retryDelaySeconds = 0;

            while (true)
            {
                try
                {
                    UpdateNetworkStatus();

                    if (_rebootRequested && DateTime.UtcNow > _rebootAt)
                    {
                        Log("Rebooting");
                        Thread.Sleep(500);
                        Power.RebootDevice(RebootOption.NormalReboot);
                    }

                    if (Telemetry.WifiConnected && retryDelaySeconds > 0)
                    {
                        retryDelaySeconds--;
                    }
                    else if (Telemetry.WifiConnected && (_hub == null || _hubDisconnected))
                    {
                        if (ConnectHub())
                        {
                            secondsSincePublish = _telemetryIntervalSeconds;
                            retryDelaySeconds = 0;
                        }
                        else
                        {
                            retryDelaySeconds = 15;
                        }

                        // start up done, show the dashboard (the LOG page keeps the history)
                        Dashboard.Booting = false;
                    }

                    // a connection the hub dropped: wait before reconnecting, so a hub that keeps refusing isn't hammered
                    if (_hub != null && _hubDisconnected && retryDelaySeconds == 0)
                    {
                        Log("Hub: retry in 20 s");
                        retryDelaySeconds = 20;
                    }

                    Telemetry.MqttConnected = _hub != null && !_hubDisconnected && _hub.IsConnected;

                    if (Telemetry.MqttConnected && (secondsSincePublish >= _telemetryIntervalSeconds || _publishNow))
                    {
                        SendTelemetry();
                        secondsSincePublish = 0;
                        _publishNow = false;
                    }

                    // refresh the signal strength every 2 minutes
                    if (++secondsSinceScan >= 120)
                    {
                        secondsSinceScan = 0;
                        StartScan();
                    }
                }
                catch (Exception ex)
                {
                    Log("Error: " + ex.Message);
                    Telemetry.MqttConnected = false;
                    _hubDisconnected = true;
                    retryDelaySeconds = 10;
                }

                secondsSincePublish++;
                Thread.Sleep(1000);
            }
        }

        /// <summary>Writes to the debug output and to the event log shown on the display.</summary>
        internal static void Log(string text)
        {
            Debug.WriteLine(text);
            Telemetry.AddLog(text);
        }

        //////////////////////////////////////////////////////////////////////////////
        // Wi-Fi

        private static WifiAdapter _wifi;
        private static readonly AutoResetEvent _scanDone = new AutoResetEvent(false);
        private static bool _wifiChanged;

        private static void ConnectWifi()
        {
            _wifi = WifiAdapter.FindAllAdapters()[0];
            _wifi.AvailableNetworksChanged += OnScanComplete;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            Telemetry.Ssid = WifiSsid;

            // the firmware reconnects to the network it last used on its own: if that's the configured one, give
            // it a moment, otherwise connect to the configured network
            string storedSsid = "";

            try
            {
                storedSsid = new Wireless80211Configuration(0).Ssid;
            }
            catch (Exception)
            {
                // no stored network
            }

            // (Ssid is null when the stored network can't be read)
            if (storedSsid == null)
            {
                storedSsid = "";
            }

            if (storedSsid == WifiSsid)
            {
                SetWifiState("Auto-join");
                Log("WiFi: stored " + Fit(storedSsid, 12));

                if (WaitForIp(10))
                {
                    Log("WiFi: auto-connected");
                    OnConnected();
                    Scan();
                    return;
                }
            }
            else if (storedSsid.Length > 0)
            {
                Log("Stored " + Fit(storedSsid, 13));
                Log("Switching to " + Fit(WifiSsid, 8));
            }

            while (true)
            {
                Scan();

                Telemetry.ConnectAttempts++;
                SetWifiState("Joining");
                Log("Join " + Fit(WifiSsid, 16));

                DateTime start = DateTime.UtcNow;
                WifiConnectionResult result = _wifi.Connect(WifiSsid, WifiReconnectionKind.Automatic, WifiPassword);
                Telemetry.JoinMs = ElapsedMs(start);
                Telemetry.LastConnectResult = StatusName(result.ConnectionStatus);
                Log("Join: " + Telemetry.LastConnectResult + " " + Seconds(Telemetry.JoinMs));

                if (result.ConnectionStatus == WifiConnectionStatus.Success)
                {
                    SetWifiState("DHCP");
                    start = DateTime.UtcNow;

                    if (WaitForIp(30))
                    {
                        Telemetry.DhcpMs = ElapsedMs(start);
                        OnConnected();
                        return;
                    }

                    Log("DHCP: no address");
                }

                SetWifiState("Retry");
                Log("Retry in 5s");
                Thread.Sleep(5000);
            }
        }

        private static void OnConnected()
        {
            UpdateNetworkStatus();
            SetWifiState("Connected");
            Telemetry.ConnectedSince = Telemetry.UptimeSeconds;

            Log("IP " + Telemetry.IpAddress + (Telemetry.DhcpMs > 0 ? " " + Seconds(Telemetry.DhcpMs) : ""));
            Log("GW " + Telemetry.Gateway);
            Log("DNS " + Telemetry.Dns);

            
        }

        private static void SetWifiState(string state)
        {
            Telemetry.WifiState = state;
            _wifiChanged = true;
        }

        private static bool WaitForIp(int timeoutSeconds)
        {
            for (int i = 0; i < timeoutSeconds; i++)
            {
                UpdateNetworkStatus();

                if (Telemetry.WifiConnected)
                {
                    return true;
                }

                Thread.Sleep(1000);
            }

            return false;
        }

        private static void UpdateNetworkStatus()
        {
            NetworkInterface ni = NetworkInterface.GetAllNetworkInterfaces()[0];
            string ip = ni.IPv4Address;
            bool connected = ip != null && ip.Length > 0 && ip != "0.0.0.0";

            string dns = "";
            string[] dnsServers = ni.IPv4DnsAddresses;

            if (dnsServers != null && dnsServers.Length > 0)
            {
                dns = dnsServers[0];
            }

            lock (Telemetry.Lock)
            {
                Telemetry.IpAddress = connected ? ip : "";
                Telemetry.SubnetMask = connected ? ni.IPv4SubnetMask : "";
                Telemetry.Gateway = connected ? ni.IPv4GatewayAddress : "";
                Telemetry.Dns = connected ? dns : "";
                Telemetry.WifiConnected = connected;
            }
        }

        // scan and wait for the result (logs what was found)
        private static void Scan()
        {
            SetWifiState("Scanning");

            _scanDone.Reset();

            if (!StartScan())
            {
                Log("Scan: busy");
                return;
            }

            if (!_scanDone.WaitOne(15000, false))
            {
                Log("Scan: timeout");
                return;
            }

            Log("Scan: " + Telemetry.NetworksFound + " networks");

            if (Telemetry.Rssi != 0)
            {
                Log(Fit(WifiSsid, 10) + " " + Telemetry.Rssi + "dBm " + Telemetry.SignalBars + "/4");
            }
            else
            {
                Log(Fit(WifiSsid, 12) + " not found");
            }
        }

        private static bool StartScan()
        {
            try
            {
                _wifi.ScanAsync();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void OnScanComplete(WifiAdapter sender, object e)
        {
            int best = 0;
            int count = 0;

            foreach (WifiAvailableNetwork network in sender.NetworkReport.AvailableNetworks)
            {
                count++;

                if (network.Ssid == Telemetry.Ssid && (best == 0 || network.NetworkRssiInDecibelMilliwatts > best))
                {
                    best = (int)network.NetworkRssiInDecibelMilliwatts;

                    lock (Telemetry.Lock)
                    {
                        Telemetry.Bssid = network.Bsid != null ? network.Bsid.ToString() : "";
                        Telemetry.SignalBars = (int)network.SignalBars;
                    }
                }
            }

            Telemetry.NetworksFound = count;
            Telemetry.Rssi = best;
            _wifiChanged = true;
            _scanDone.Set();
        }

        private static void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (e.IsAvailable)
            {
                Log("WiFi: link up");
            }
            else
            {
                Telemetry.LinkDrops++;
                SetWifiState("Link lost");
                Log("WiFi: link lost");
            }

            _wifiChanged = true;
        }

        private static void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            Telemetry.AddressChanges++;
            UpdateNetworkStatus();

            if (Telemetry.WifiConnected && Telemetry.WifiState != "Connected")
            {
                SetWifiState("Connected");
                Telemetry.ConnectedSince = Telemetry.UptimeSeconds;
                Log("IP " + Telemetry.IpAddress);
            }

            _wifiChanged = true;
        }

        private static string StatusName(WifiConnectionStatus status)
        {
            if (status == WifiConnectionStatus.Success)
            {
                return "OK";
            }

            if (status == WifiConnectionStatus.InvalidCredential)
            {
                return "BadPassword";
            }

            if (status == WifiConnectionStatus.NetworkNotAvailable)
            {
                return "NotFound";
            }

            if (status == WifiConnectionStatus.Timeout)
            {
                return "Timeout";
            }

            if (status == WifiConnectionStatus.UnsupportedAuthenticationProtocol)
            {
                return "BadAuth";
            }

            if (status == WifiConnectionStatus.AccessRevoked)
            {
                return "Revoked";
            }

            return "Failed";
        }

        private static int ElapsedMs(DateTime start)
        {
            double ms = (DateTime.UtcNow - start).TotalMilliseconds;

            // the clock can jump when SNTP sets the time
            return ms < 0 || ms > 120000 ? 0 : (int)ms;
        }

        private static string Seconds(int milliseconds)
        {
            return milliseconds <= 0 ? "" : (milliseconds / 1000.0).ToString("F1") + "s";
        }

        private static string Fit(string text, int maxChars)
        {
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }

        //////////////////////////////////////////////////////////////////////////////
        // time

        // Azure IoT Hub's certificate can only be verified with the right date. The firmware sets the system
        // time over SNTP once the network is up; if that hasn't happened this asks an NTP server itself.
        private static void WaitForTime()
        {
            Log("Time: waiting...");

            for (int i = 0; i < 20; i++)
            {
                if (TimeIsValid())
                {
                    Log("Time " + TimeText());
                    return;
                }

                Thread.Sleep(1000);
            }

            Log("Time: asking NTP");

            for (int attempt = 0; attempt < 3 && !TimeIsValid(); attempt++)
            {
                if (!SetTimeFromNtp("pool.ntp.org"))
                {
                    SetTimeFromNtp("time.cloudflare.com");
                }
            }

            if (TimeIsValid())
            {
                Log("Time " + TimeText());
            }
            else
            {
                Log("No time! TLS will fail");
            }
        }

        private static bool TimeIsValid()
        {
            return DateTime.UtcNow.Year >= 2025;
        }

        private static string TimeText()
        {
            DateTime now = DateTime.UtcNow;
            return now.Year.ToString() + "-" + now.Month.ToString("D2") + "-" + now.Day.ToString("D2") + " " +
                now.Hour.ToString("D2") + ":" + now.Minute.ToString("D2");
        }

        // minimal SNTP client (RFC 4330)
        private static bool SetTimeFromNtp(string server)
        {
            Socket socket = null;

            try
            {
                IPAddress address = Dns.GetHostEntry(server).AddressList[0];
                EndPoint endpoint = new IPEndPoint(address, 123);

                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.ReceiveTimeout = 3000;

                byte[] request = new byte[48];
                request[0] = 0x1B; // version 3, client mode

                socket.SendTo(request, endpoint);

                byte[] response = new byte[48];
                socket.ReceiveFrom(response, ref endpoint);

                // transmit timestamp: seconds since 1900
                long seconds = ((long)response[40] << 24) | ((long)response[41] << 16) | ((long)response[42] << 8) | response[43];

                if (seconds == 0)
                {
                    return false;
                }

                // seconds from 1900 to 1970
                DateTime time = DateTime.UnixEpoch.AddSeconds(seconds - 2208988800L);
                Rtc.SetSystemTime(time);

                return TimeIsValid();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("NTP " + server + ": " + ex.Message);
                return false;
            }
            finally
            {
                if (socket != null)
                {
                    socket.Close();
                }
            }
        }

        //////////////////////////////////////////////////////////////////////////////
        // Azure IoT Hub

        private static bool ConnectHub()
        {
            try
            {
                CloseHub();

                Log("Hub " + Fit(IotHubHostName, 17));

                // the TLS connection (port 8883) is verified with the Azure root certificates
                var roots = new X509Certificate(Certificates.AzureRoots);

                _hub = new DeviceClient(IotHubHostName, DeviceId, SasKey, MqttQoSLevel.AtLeastOnce, azureCert: roots);
                _hub.StatusUpdated += OnStatusUpdated;
                _hub.CloudToDeviceMessage += OnCloudToDeviceMessage;
                _hub.TwinUpdated += OnTwinUpdated;
                _hub.AddMethodCallback(ShowText);
                _hub.AddMethodCallback(SetInterval);
                _hub.AddMethodCallback(GetTelemetry);
                _hub.AddMethodCallback(Reboot);

                _hubDisconnected = false;

                DateTime start = DateTime.UtcNow;

                if (!_hub.Open())
                {
                    Log("Hub: open failed");
                    _dashboard.ShowToast("Hub refused");
                    return false;
                }

                Log("Hub: connected " + Seconds(ElapsedMs(start)));
                _dashboard.ShowToast("IoT Hub connected");

                if (_everConnected)
                {
                    Telemetry.MqttReconnects++;
                }

                _everConnected = true;

                if (UseDeviceTwin)
                {
                    ReadTwin();
                    ReportProperties();
                }
                else
                {
                    Log("Twin: off");
                }

                return true;
            }
            catch (Exception ex)
            {
                Log("Hub: " + Fit(ex.Message.Length > 0 ? ex.Message : "connect failed", 16));
                Debug.WriteLine("IoT Hub connect failed: " + ex.ToString());
                _dashboard.ShowToast("Hub unreachable");
                CloseHub();
                return false;
            }
        }

        private static void CloseHub()
        {
            if (_hub == null)
            {
                return;
            }

            try
            {
                _hub.StatusUpdated -= OnStatusUpdated;
                _hub.CloudToDeviceMessage -= OnCloudToDeviceMessage;
                _hub.TwinUpdated -= OnTwinUpdated;
                _hub.Close();
            }
            catch (Exception)
            {
                // already closed
            }

            _hub.Dispose();
            _hub = null;
        }

        // (enums print as numbers in nanoFramework, so the names are spelled out)
        private static string StatusName(Status status)
        {
            switch (status)
            {
                case Status.Connected: return "Connected";
                case Status.Disconnected: return "Disconnected";
                case Status.TwinUpdated: return "TwinUpdated";
                case Status.TwinUpdateError: return "TwinUpdateError";
                case Status.TwinReceived: return "TwinReceived";
                case Status.TwinUpdateReceived: return "TwinUpdateReceived";
                case Status.IoTHubError: return "IoTHubError";
                case Status.IoTHubWarning: return "IoTHubWarning";
                case Status.IoTHubInformation: return "IoTHubInformation";
                case Status.IoTHubHighlightInformation: return "IoTHubHighlight";
                case Status.InternalError: return "InternalError";
                case Status.MessageReceived: return "MessageReceived";
                case Status.DirectMethodCalled: return "DirectMethodCalled";
                default: return "status " + ((int)status).ToString();
            }
        }

        private static void OnStatusUpdated(object sender, StatusUpdatedEventArgs e)
        {
            string name = StatusName(e.IoTHubStatus.Status);
            string message = e.IoTHubStatus.Message != null ? e.IoTHubStatus.Message : "";

            // full details in the Output window, short form in the on-screen log
            Debug.WriteLine("Hub status " + name + ": " + message);

            if (e.IoTHubStatus.Status == Status.Disconnected)
            {
                Log("Hub: Disconnected" + (message.Length > 0 ? " " + Fit(message, 8) : ""));
                _hubDisconnected = true;
            }
            else if (e.IoTHubStatus.Status == Status.IoTHubError || e.IoTHubStatus.Status == Status.InternalError ||
                     e.IoTHubStatus.Status == Status.TwinUpdateError)
            {
                Log(Fit(name, 8) + " " + Fit(message, 12));
            }
        }

        private static void SendTelemetry()
        {
            string json = BuildTelemetryJson();

            // waits (up to 5 s) for the hub to acknowledge the message
            bool delivered = _hub.SendMessage(json, new CancellationTokenSource(5000).Token);

            if (delivered)
            {
                Telemetry.MessagesSent++;
                Debug.WriteLine("Sent: " + json);
            }
            else
            {
                Log("Send failed");
            }
        }

        // device to cloud: properties reported in the device twin
        private static void ReportProperties()
        {
            try
            {
                var reported = new TwinCollection();

                reported.Add("firmware", SystemInfo.Version.ToString());
                reported.Add("platform", SystemInfo.TargetName);
                reported.Add("app", AppVersion);
                reported.Add("mac", Telemetry.MacAddress);
                reported.Add("ip", Telemetry.IpAddress);
                reported.Add("ssid", Telemetry.Ssid);
                reported.Add("telemetryInterval", _telemetryIntervalSeconds);
                reported.Add("sensors", (Telemetry.HasClimate ? "HTS221 " : "") + (Telemetry.HasPressure ? "LPS22HB " : "") +
                    (Telemetry.HasMotion ? "LSM6DSL " : "") + (Telemetry.HasMagnetometer ? "LIS2MDL" : ""));

                _hub.UpdateReportedProperties(reported, new CancellationTokenSource(5000).Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Reported properties: " + ex.Message);
            }
        }

        // reads the desired properties at start up
        private static void ReadTwin()
        {
            try
            {
                Twin twin = _hub.GetTwin(new CancellationTokenSource(5000).Token);

                if (twin != null)
                {
                    ApplyDesired(twin.Properties.Desired);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Twin: " + ex.Message);
            }
        }

        private static void OnTwinUpdated(object sender, TwinUpdateEventArgs e)
        {
            Debug.WriteLine("Twin updated");

            try
            {
                ApplyDesired(e.Twin);
                ReportProperties();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Twin update: " + ex.Message);
            }
        }

        // cloud to device: desired properties
        private static void ApplyDesired(TwinCollection desired)
        {
            if (desired != null && desired.Contains("telemetryInterval"))
            {
                int seconds = int.Parse(desired["telemetryInterval"].ToString());

                if (seconds >= 1 && seconds <= 3600)
                {
                    _telemetryIntervalSeconds = seconds;
                    Log("Interval " + seconds + "s (twin)");
                }
            }
        }

        // cloud to device: messages
        private static void OnCloudToDeviceMessage(object sender, CloudToDeviceMessageEventArgs e)
        {
            Debug.WriteLine("Cloud message: " + e.Message);
            Telemetry.LastCommand = e.Message;
            Log("C2D " + Fit(e.Message, 16));
            _dashboard.ShowToast(Fit(e.Message, 19));
        }

        // cloud to device: direct methods. The method name in the hub is the name of the method here,
        // the payload is JSON and the return value is the JSON response.

        private static string ShowText(int rid, string payload)
        {
            string text = StripQuotes(payload);
            Telemetry.LastCommand = "ShowText";
            _dashboard.ShowToast(text);
            Log("Show " + Fit(text, 16));

            return "{\"shown\":true}";
        }

        private static string SetInterval(int rid, string payload)
        {
            int seconds = int.Parse(StripQuotes(payload));

            if (seconds < 1 || seconds > 3600)
            {
                return "{\"error\":\"1 to 3600 seconds\"}";
            }

            _telemetryIntervalSeconds = seconds;
            Telemetry.LastCommand = "SetInterval";
            Log("Interval " + seconds + "s (method)");

            return "{\"interval\":" + seconds.ToString() + "}";
        }

        private static string GetTelemetry(int rid, string payload)
        {
            Telemetry.LastCommand = "GetTelemetry";
            _publishNow = true;

            return BuildTelemetryJson();
        }

        private static string Reboot(int rid, string payload)
        {
            Telemetry.LastCommand = "Reboot";
            Log("Reboot requested");

            // restart after the response has been sent
            _rebootAt = DateTime.UtcNow.AddSeconds(3);
            _rebootRequested = true;

            return "{\"rebooting\":true}";
        }

        private static string StripQuotes(string payload)
        {
            if (payload == null)
            {
                return "";
            }

            payload = payload.Trim();

            if (payload.Length >= 2 && payload[0] == '"' && payload[payload.Length - 1] == '"')
            {
                payload = payload.Substring(1, payload.Length - 2);
            }

            return payload;
        }

        //////////////////////////////////////////////////////////////////////////////
        // JSON (built by hand to keep the app small)

        private static string BuildTelemetryJson()
        {
            var json = new StringBuilder();

            lock (Telemetry.Lock)
            {
                json.Append("{\"device\":\"").Append(DeviceId).Append("\"");
                json.Append(",\"uptime\":").Append(Telemetry.UptimeSeconds.ToString());

                if (Telemetry.HasClimate)
                {
                    json.Append(",\"temperature\":").Append(Telemetry.Temperature.ToString("F2"));
                    json.Append(",\"humidity\":").Append(Telemetry.Humidity.ToString("F2"));
                }

                if (Telemetry.HasPressure)
                {
                    json.Append(",\"pressure\":").Append(Telemetry.Pressure.ToString("F2"));
                    json.Append(",\"pressureSensorTemperature\":").Append(Telemetry.PressureTemperature.ToString("F2"));
                }

                if (Telemetry.HasMotion)
                {
                    json.Append(",\"accel\":").Append(Vector(Telemetry.AccelX, Telemetry.AccelY, Telemetry.AccelZ, "F3"));
                    json.Append(",\"gyro\":").Append(Vector(Telemetry.GyroX, Telemetry.GyroY, Telemetry.GyroZ, "F2"));
                }

                if (Telemetry.HasMagnetometer)
                {
                    json.Append(",\"magnetometer\":").Append(Vector(Telemetry.MagX, Telemetry.MagY, Telemetry.MagZ, "F1"));
                    json.Append(",\"heading\":").Append(Telemetry.Heading.ToString("F1"));
                }

                json.Append(",\"buttonA\":").Append(Telemetry.ButtonA ? "true" : "false");
                json.Append(",\"buttonB\":").Append(Telemetry.ButtonB ? "true" : "false");
                json.Append(",\"wifi\":");
                AppendWifi(json);
                json.Append(",\"messages\":").Append((Telemetry.MessagesSent + 1).ToString());
                json.Append(",\"interval\":").Append(_telemetryIntervalSeconds.ToString());
            }

            // free managed memory (runs the garbage collector)
            json.Append(",\"freeMemory\":").Append(nanoFramework.Runtime.Native.GC.Run(false).ToString());
            json.Append("}");

            return json.ToString();
        }

        private static string BuildWifiJson()
        {
            var json = new StringBuilder();

            lock (Telemetry.Lock)
            {
                AppendWifi(json);
            }

            return json.ToString();
        }

        // Wi-Fi diagnostics object (call with Telemetry.Lock held)
        private static void AppendWifi(StringBuilder json)
        {
            json.Append("{\"state\":\"").Append(Telemetry.WifiState).Append("\"");
            json.Append(",\"ssid\":\"").Append(Escape(Telemetry.Ssid)).Append("\"");
            json.Append(",\"bssid\":\"").Append(Telemetry.Bssid).Append("\"");
            json.Append(",\"rssi\":").Append(Telemetry.Rssi.ToString());
            json.Append(",\"bars\":").Append(Telemetry.SignalBars.ToString());
            json.Append(",\"networksFound\":").Append(Telemetry.NetworksFound.ToString());
            json.Append(",\"ip\":\"").Append(Telemetry.IpAddress).Append("\"");
            json.Append(",\"mask\":\"").Append(Telemetry.SubnetMask).Append("\"");
            json.Append(",\"gateway\":\"").Append(Telemetry.Gateway).Append("\"");
            json.Append(",\"dns\":\"").Append(Telemetry.Dns).Append("\"");
            json.Append(",\"mac\":\"").Append(Telemetry.MacAddress).Append("\"");
            json.Append(",\"attempts\":").Append(Telemetry.ConnectAttempts.ToString());
            json.Append(",\"lastResult\":\"").Append(Telemetry.LastConnectResult).Append("\"");
            json.Append(",\"joinMs\":").Append(Telemetry.JoinMs.ToString());
            json.Append(",\"dhcpMs\":").Append(Telemetry.DhcpMs.ToString());
            json.Append(",\"linkDrops\":").Append(Telemetry.LinkDrops.ToString());
            json.Append(",\"addressChanges\":").Append(Telemetry.AddressChanges.ToString());
            json.Append(",\"connectedFor\":").Append(Telemetry.WifiConnected ? (Telemetry.UptimeSeconds - Telemetry.ConnectedSince).ToString() : "0");
            json.Append(",\"mqttReconnects\":").Append(Telemetry.MqttReconnects.ToString());
            json.Append("}");
        }

        private static string Vector(float x, float y, float z, string format)
        {
            return "{\"x\":" + x.ToString(format) + ",\"y\":" + y.ToString(format) + ",\"z\":" + z.ToString(format) + "}";
        }

        private static string Escape(string text)
        {
            var escaped = new StringBuilder();

            foreach (char c in text)
            {
                if (c == '"' || c == '\\')
                {
                    escaped.Append('\\');
                }

                escaped.Append(c);
            }

            return escaped.ToString();
        }

        private static string FormatMac(byte[] mac)
        {
            var text = new StringBuilder();

            for (int i = 0; i < mac.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(':');
                }

                text.Append(mac[i].ToString("X2"));
            }

            return text.ToString();
        }
    }
}
