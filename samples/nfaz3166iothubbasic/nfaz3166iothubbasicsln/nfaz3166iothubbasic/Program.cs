using System;
using System.Device.Wifi;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.Runtime.Native;

namespace nfaz3166iothubbasic
{
    /// <summary>
    /// AZ3166 telemetry to Azure IoT Hub over plain MQTT with TLS (port 8883), with an OLED dashboard.
    /// No Azure client library: the app talks MQTT to the hub itself and creates its SAS token from the device key.
    /// Telemetry and cloud-to-device messages work on every IoT Hub tier. Device twin and direct methods are
    /// optional (EnableTwin / EnableDirectMethods) because the Basic tier doesn't support them.
    ///
    /// Topics used (all fixed by IoT Hub):
    ///   devices/{id}/messages/events/                          telemetry (device to cloud)
    ///   devices/{id}/messages/devicebound/#                    cloud-to-device messages
    ///   $iothub/methods/POST/{method}/?$rid={n}                direct method calls (answered on $iothub/methods/res/...)
    ///   $iothub/twin/...                                       device twin
    /// Direct methods: ShowText, SetInterval, GetTelemetry, Reboot (same as the Azure client version).
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

        // optional: a ready-made SAS token ("SharedAccessSignature sr=...&sig=...&se=...") used instead of the key,
        // e.g. from: az iot hub generate-sas-token --hub-name <hub> --device-id az3166 --duration 86400 --query sas -o tsv
        private const string SasToken = "";

        // Basic tier hubs support neither of these: turning them on there makes the hub close the connection.
        // Turn them on one at a time to find out what the hub accepts.
        private const bool EnableDirectMethods = false;
        private const bool EnableTwin = false;

        // how often telemetry is sent (the free IoT Hub tier allows 8000 messages a day: at 15 s the device uses 5760)
        private static int _telemetryIntervalSeconds = 15;

        // MQTT keep alive (the hub allows up to 1160 s)
        private const ushort KeepAliveSeconds = 120;

        //////////////////////////////////////////////////////////////////////////////
        // board

        internal const int I2cBus = 1;      // I2C1 on PB8/PB9: sensors and OLED
        internal const int ButtonAPin = 4;  // PA4
        internal const int ButtonBPin = 10; // PA10

        private const string AppVersion = "1.0 (MQTT)";

        private static Dashboard _dashboard;
        private static MqttClient _mqtt;
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
            Telemetry.MacAddress = FormatMac(network.PhysicalAddress);
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

                    bool connected = _mqtt != null && _mqtt.IsConnected;

                    if (Telemetry.WifiConnected && !connected)
                    {
                        if (retryDelaySeconds > 0)
                        {
                            retryDelaySeconds--;
                        }
                        else
                        {
                            if (ConnectHub())
                            {
                                secondsSincePublish = _telemetryIntervalSeconds;
                            }
                            else
                            {
                                Log("Hub: retry in 20 s");
                                retryDelaySeconds = 20;
                            }

                            // start up done, show the dashboard (the LOG page keeps the history)
                            Dashboard.Booting = false;
                        }
                    }

                    connected = _mqtt != null && _mqtt.IsConnected;
                    Telemetry.MqttConnected = connected;

                    if (connected && (secondsSincePublish >= _telemetryIntervalSeconds || _publishNow))
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
                    Log("Error: " + Fit(ex.Message, 15));
                    Debug.WriteLine("Main loop: " + ex.ToString());
                    Telemetry.MqttConnected = false;
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
        // Azure IoT Hub (MQTT)

        private static bool ConnectHub()
        {
            try
            {
                CloseHub();

                Log("Hub " + Fit(IotHubHostName, 17));

                string password = SasToken.Length > 0 ? SasToken : CreateSasToken();

                // the TLS connection (port 8883) is verified with the Azure root certificates
                var roots = new X509Certificate(Certificates.AzureRoots);

                _mqtt = new MqttClient(IotHubHostName, 8883, true, roots, null, MqttSslProtocols.TLSv1_2);
                _mqtt.ProtocolVersion = MqttProtocolVersion.Version_3_1_1;
                _mqtt.MqttMsgPublishReceived += OnMessageReceived;
                _mqtt.ConnectionClosed += OnConnectionClosed;

                DateTime start = DateTime.UtcNow;

                MqttReasonCode code = _mqtt.Connect(
                    DeviceId,
                    IotHubHostName + "/" + DeviceId + "/?api-version=2021-04-12",
                    password,
                    false,                       // will retain
                    MqttQoSLevel.AtMostOnce,     // will QoS
                    false,                       // will flag
                    "",
                    "",
                    true,                        // clean session
                    KeepAliveSeconds);

                if (code != MqttReasonCode.Success)
                {
                    // bad key/token, device disabled, or unknown device
                    Log("Hub: refused (" + ((int)code).ToString() + ")");
                    _dashboard.ShowToast("Hub refused");
                    return false;
                }

                Log("Hub: connected " + Seconds(ElapsedMs(start)));

                // one subscription at a time, logged, so that a hub that closes the connection on one of them shows which
                Subscribe("devicebound", "devices/" + DeviceId + "/messages/devicebound/#");

                if (EnableDirectMethods)
                {
                    Subscribe("methods", "$iothub/methods/POST/#");
                }

                if (EnableTwin)
                {
                    Subscribe("twin", "$iothub/twin/res/#");
                    Subscribe("twin desired", "$iothub/twin/PATCH/properties/desired/#");

                    // read the desired properties, report ours
                    _mqtt.Publish("$iothub/twin/GET/?$rid=2", new byte[0], null, null, MqttQoSLevel.AtLeastOnce, false);
                    ReportProperties();
                }

                _dashboard.ShowToast("IoT Hub connected");

                if (_everConnected)
                {
                    Telemetry.MqttReconnects++;
                }

                _everConnected = true;

                return _mqtt.IsConnected;
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

        private static void Subscribe(string name, string topic)
        {
            _mqtt.Subscribe(new string[] { topic }, new MqttQoSLevel[] { MqttQoSLevel.AtLeastOnce });
            Log("Sub " + name);
            Thread.Sleep(300);
        }

        private static void CloseHub()
        {
            if (_mqtt == null)
            {
                return;
            }

            try
            {
                _mqtt.MqttMsgPublishReceived -= OnMessageReceived;
                _mqtt.ConnectionClosed -= OnConnectionClosed;

                if (_mqtt.IsConnected)
                {
                    _mqtt.Disconnect();
                }
            }
            catch (Exception)
            {
                // already closed
            }

            _mqtt = null;
        }

        private static void OnConnectionClosed(object sender, EventArgs e)
        {
            Log("Hub: connection closed");
        }

        // SAS token valid for 24 hours, made from the device key
        private static string CreateSasToken()
        {
            long expiry = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds + 24 * 3600;

            return Sas.CreateToken(IotHubHostName, DeviceId, SasKey, expiry);
        }

        private static void SendTelemetry()
        {
            string json = BuildTelemetryJson();

            _mqtt.Publish(
                "devices/" + DeviceId + "/messages/events/",
                Encoding.UTF8.GetBytes(json),
                null,
                null,
                MqttQoSLevel.AtLeastOnce,
                false);

            Telemetry.MessagesSent++;
            Debug.WriteLine("Sent: " + json);
        }

        // device twin: reported properties
        private static void ReportProperties()
        {
            string json = "{\"firmware\":\"" + SystemInfo.Version.ToString() + "\",\"app\":\"" + AppVersion + "\"," +
                "\"mac\":\"" + Telemetry.MacAddress + "\",\"ip\":\"" + Telemetry.IpAddress + "\"," +
                "\"ssid\":\"" + Escape(Telemetry.Ssid) + "\",\"telemetryInterval\":" + _telemetryIntervalSeconds.ToString() + "," +
                "\"sensors\":\"" + (Telemetry.HasClimate ? "HTS221 " : "") + (Telemetry.HasPressure ? "LPS22HB " : "") +
                (Telemetry.HasMotion ? "LSM6DSL " : "") + (Telemetry.HasMagnetometer ? "LIS2MDL" : "") + "\"}";

            _mqtt.Publish("$iothub/twin/PATCH/properties/reported/?$rid=1", Encoding.UTF8.GetBytes(json), null, null, MqttQoSLevel.AtLeastOnce, false);
        }

        private static void OnMessageReceived(object sender, MqttMsgPublishEventArgs e)
        {
            // runs on the MQTT client thread, never let an exception escape
            try
            {
                string topic = e.Topic;
                string body = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length);

                if (topic.StartsWith("$iothub/methods/POST/"))
                {
                    HandleMethod(topic, body);
                }
                else if (topic.StartsWith("$iothub/twin/res/"))
                {
                    // answer to the twin request (the reply to $rid=2 holds the desired properties)
                    Debug.WriteLine("Twin response " + Fit(topic, 40));

                    if (topic.IndexOf("$rid=2") >= 0)
                    {
                        ApplyDesired(body);
                    }
                }
                else if (topic.StartsWith("$iothub/twin/PATCH/properties/desired/"))
                {
                    ApplyDesired(body);
                    ReportProperties();
                }
                else
                {
                    // cloud-to-device message
                    Debug.WriteLine("Cloud message: " + body);
                    Telemetry.LastCommand = body;
                    Log("C2D " + Fit(body, 16));
                    _dashboard.ShowToast(Fit(body, 19));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Message handling: " + ex.Message);
            }
        }

        // desired properties: {"telemetryInterval":30,"$version":4}
        private static void ApplyDesired(string json)
        {
            int seconds = JsonNumber(json, "telemetryInterval");

            if (seconds >= 1 && seconds <= 3600)
            {
                _telemetryIntervalSeconds = seconds;
                Log("Interval " + seconds + "s (twin)");
            }
        }

        // direct methods. The topic is $iothub/methods/POST/{method name}/?$rid={request id}; the answer has to be
        // published to $iothub/methods/res/{status}/?$rid={request id}
        private static void HandleMethod(string topic, string payload)
        {
            string rest = topic.Substring("$iothub/methods/POST/".Length);
            int slash = rest.IndexOf('/');
            string method = slash >= 0 ? rest.Substring(0, slash) : rest;

            int ridPosition = topic.IndexOf("$rid=");
            string rid = ridPosition >= 0 ? topic.Substring(ridPosition + 5) : "0";

            string response;
            int status = 200;

            if (method == "ShowText")
            {
                string text = StripQuotes(payload);
                Telemetry.LastCommand = "ShowText";
                _dashboard.ShowToast(text);
                Log("Show " + Fit(text, 16));
                response = "{\"shown\":true}";
            }
            else if (method == "SetInterval")
            {
                int seconds = int.Parse(StripQuotes(payload));

                if (seconds < 1 || seconds > 3600)
                {
                    status = 400;
                    response = "{\"error\":\"1 to 3600 seconds\"}";
                }
                else
                {
                    _telemetryIntervalSeconds = seconds;
                    Telemetry.LastCommand = "SetInterval";
                    Log("Interval " + seconds + "s (method)");
                    response = "{\"interval\":" + seconds.ToString() + "}";
                }
            }
            else if (method == "GetTelemetry")
            {
                Telemetry.LastCommand = "GetTelemetry";
                _publishNow = true;
                response = BuildTelemetryJson();
            }
            else if (method == "Reboot")
            {
                Telemetry.LastCommand = "Reboot";
                Log("Reboot requested");

                // restart after the response has been sent
                _rebootAt = DateTime.UtcNow.AddSeconds(3);
                _rebootRequested = true;
                response = "{\"rebooting\":true}";
            }
            else
            {
                status = 404;
                response = "{\"error\":\"unknown method\"}";
            }

            _mqtt.Publish(
                "$iothub/methods/res/" + status.ToString() + "/?$rid=" + rid,
                Encoding.UTF8.GetBytes(response),
                null,
                null,
                MqttQoSLevel.AtMostOnce,
                false);
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

        // value of a number property in a JSON text, -1 if there is none (enough for the flat twin properties used here)
        private static int JsonNumber(string json, string name)
        {
            int position = json.IndexOf("\"" + name + "\"");

            if (position < 0)
            {
                return -1;
            }

            position = json.IndexOf(':', position);

            if (position < 0)
            {
                return -1;
            }

            position++;

            while (position < json.Length && (json[position] == ' ' || json[position] == '"'))
            {
                position++;
            }

            int value = 0;
            int digits = 0;

            while (position < json.Length && json[position] >= '0' && json[position] <= '9')
            {
                value = value * 10 + (json[position] - '0');
                position++;
                digits++;
            }

            return digits > 0 ? value : -1;
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
