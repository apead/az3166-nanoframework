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

namespace nfaz3166iotcentral
{
    /// <summary>
    /// AZ3166 on Azure IoT Central, with an OLED dashboard.
    ///
    /// IoT Central doesn't give a device a fixed IoT Hub. The device first asks the Device Provisioning Service (DPS)
    /// which hub it belongs to, then connects to that hub:
    ///   1. DPS over MQTT + TLS (global.azure-devices-provisioning.net:8883), registration with the ID scope and the device key
    ///   2. IoT Hub over MQTT + TLS (port 8883) with a SAS token made from the same key
    /// There is no Azure client library: the app speaks MQTT itself and makes its SAS tokens (see Sas.cs), so it stays small.
    ///
    /// What IoT Central gets:
    ///   telemetry          temperature, humidity, pressure, accel, gyro, magnetometer, heading, buttons, uptime, rssi, freeMemory
    ///   properties         firmware, ip, mac, ssid, app (read only); telemetryInterval (writable, acknowledged)
    ///   commands           ShowText (text), GetTelemetry, Reboot
    /// Import AZ3166-device-model.json as the device template (see README.md).
    /// </summary>
    public class Program
    {
        //////////////////////////////////////////////////////////////////////////////
        // configuration - UPDATE THESE

        private const string WifiSsid = "YourWiFiSSID";
        private const string WifiPassword = "YourWiFiPassword";

        // IoT Central: Devices -> your device -> Connect shows the ID scope, device ID and primary key.
        private const string IdScope = "0ne00000000";
        private const string DeviceId = "az3166";

        // use ONE of these two keys:
        //   DeviceKey: the device's own primary key (from the device's Connect dialog)
        //   GroupKey:  the primary key of the enrollment group (Permissions -> Device connection groups -> SAS-IoT-Devices);
        //              the app derives the device key from it, so every board can run the same code with another DeviceId
        private const string DeviceKey = "";
        private const string GroupKey = "";

        // the device template's id (the "@id" of AZ3166-device-model.json). It ties the device to its template.
        // Leave empty to connect without a template: the data then shows up as "unmodeled" in IoT Central.
        private const string ModelId = "dtmi:azdevices:AZ3166;1";

        // how often telemetry is sent, and the MQTT keep alive
        private static int _telemetryIntervalSeconds = 15;
        private const ushort KeepAliveSeconds = 120;

        private const string DpsHost = "global.azure-devices-provisioning.net";

        //////////////////////////////////////////////////////////////////////////////
        // board

        internal const int I2cBus = 1;      // I2C1 on PB8/PB9: sensors and OLED
        internal const int ButtonAPin = 4;  // PA4
        internal const int ButtonBPin = 10; // PA10

        private const string AppVersion = "1.0.2 (Central)";

        private static Dashboard _dashboard;
        private static MqttClient _mqtt;
        private static string _hubHost;        // assigned by DPS
        private static string _deviceKey;      // the key the SAS tokens are made from
        private static bool _publishNow;
        private static bool _everConnected;
        private static bool _rebootRequested;
        private static DateTime _rebootAt;
        private static int _hubFailures;

        public static void Main()
        {
            Log("AZ3166 IoT Central");
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
            Telemetry.Broker = "IoT Central";

            Log("Device " + DeviceId);

            if (DeviceKey.Length == 0 && GroupKey.Length == 0)
            {
                Log("No key set!");
            }

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
                            // ask DPS for the hub first (and again after repeated hub failures: the device may have been moved)
                            bool ready = _hubHost != null || Provision();

                            if (ready && ConnectHub())
                            {
                                _hubFailures = 0;
                                secondsSincePublish = _telemetryIntervalSeconds;
                            }
                            else
                            {
                                if (ready && ++_hubFailures >= 3)
                                {
                                    _hubHost = null;
                                    _hubFailures = 0;
                                }

                                Log("Retry in 20 s");
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
        // Device Provisioning Service: which IoT Hub is this device assigned to?

        private static readonly AutoResetEvent _dpsReply = new AutoResetEvent(false);
        private static string _dpsTopic = "";
        private static string _dpsBody = "";

        private static int _rootIndex;

        private static string RootPem(int index)
        {
            return index == 0 ? Certificates.DigiCertGlobalRootG2 :
                (index == 1 ? Certificates.MicrosoftRsaRootG2 : Certificates.MicrosoftEccRootG2);
        }

        // MQTT over TLS (port 8883) trusting ONE root certificate at a time. Starts with the root that worked last time and
        // moves on to the next when the server's certificate chain doesn't end at it (mbedTLS error -9984).
        private static MqttClient ConnectTls(string host, string userName, string password, out MqttReasonCode code)
        {
            for (int attempt = 0; ; attempt++)
            {
                int index = (_rootIndex + attempt) % 3;

                var client = new MqttClient(host, 8883, true, new X509Certificate(RootPem(index)), null, MqttSslProtocols.TLSv1_2);
                client.ProtocolVersion = MqttProtocolVersion.Version_3_1_1;

                try
                {
                    code = client.Connect(DeviceId, userName, password, false, MqttQoSLevel.AtMostOnce, false, "", "", true, KeepAliveSeconds);
                    _rootIndex = index;
                    Log("Root " + (index + 1).ToString() + "/3 ok");

                    return client;
                }
                catch (Exception ex)
                {
                    if (attempt >= 2 || ex.Message.IndexOf("-9984") < 0)
                    {
                        throw;
                    }

                    Log("Root " + (index + 1).ToString() + "/3 no match");
                }
            }
        }

        // frees what can be freed and logs the free managed memory (a TLS handshake needs memory too)
        private static void LogMemory(string where)
        {
            Log(where + " free " + (nanoFramework.Runtime.Native.GC.Run(true) / 1024).ToString() + "K");
        }

        private static long UnixNow()
        {
            return (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        }

        private static bool Provision()
        {
            MqttClient dps = null;

            try
            {
                _deviceKey = DeviceKey.Length > 0 ? DeviceKey : Sas.DeriveDeviceKey(GroupKey, DeviceId);

                Log("DPS " + Fit(IdScope, 13));

                string token = Sas.CreateDpsToken(IdScope, DeviceId, _deviceKey, UnixNow() + 3600);
                LogMemory("DPS");

                _dpsReply.Reset();

                DateTime start = DateTime.UtcNow;

                MqttReasonCode code;
                dps = ConnectTls(DpsHost, IdScope + "/registrations/" + DeviceId + "/api-version=2019-03-31", token, out code);
                dps.MqttMsgPublishReceived += OnDpsMessage;

                if (code != MqttReasonCode.Success)
                {
                    // wrong ID scope, device id or key
                    Log("DPS refused (" + ((int)code).ToString() + ")");
                    _dashboard.ShowToast("DPS refused");
                    return false;
                }

                Log("DPS: connected " + Seconds(ElapsedMs(start)));

                dps.Subscribe(new string[] { "$dps/registrations/res/#" }, new MqttQoSLevel[] { MqttQoSLevel.AtLeastOnce });

                // the iotcModelId ties the device to its IoT Central device template
                string request = "{\"registrationId\":\"" + DeviceId + "\"" +
                    (ModelId.Length > 0 ? ",\"payload\":{\"iotcModelId\":\"" + ModelId + "\"}" : "") + "}";

                dps.Publish("$dps/registrations/PUT/iotdps-register/?$rid=1", Encoding.UTF8.GetBytes(request), null, null, MqttQoSLevel.AtLeastOnce, false);

                for (int attempt = 0; attempt < 20; attempt++)
                {
                    if (!_dpsReply.WaitOne(20000, false))
                    {
                        Log("DPS: no answer");
                        return false;
                    }

                    int status = DpsStatus(_dpsTopic);
                    string body = _dpsBody;

                    if (status == 202)
                    {
                        // still being assigned: ask again after the time DPS asks for
                        Log("DPS: assigning");

                        string operationId = JsonString(body, "operationId");
                        Thread.Sleep(DpsRetryAfter(_dpsTopic) * 1000);

                        dps.Publish(
                            "$dps/registrations/GET/iotdps-get-operationstatus/?$rid=" + (attempt + 2).ToString() + "&operationId=" + operationId,
                            new byte[0],
                            null,
                            null,
                            MqttQoSLevel.AtLeastOnce,
                            false);
                    }
                    else if (status == 200)
                    {
                        string hub = JsonString(body, "assignedHub");

                        if (hub.Length == 0)
                        {
                            // "failed" or "disabled"
                            Log("DPS: " + Fit(JsonString(body, "status"), 16));
                            Debug.WriteLine("DPS: " + body);
                            return false;
                        }

                        _hubHost = hub;
                        Telemetry.Broker = hub;
                        Log("Assigned " + Fit(hub, 13));
                        Debug.WriteLine("DPS: assigned to " + hub);
                        return true;
                    }
                    else
                    {
                        // 401 = bad key / id scope, 404 = device not registered (and no enrollment group), 429 = throttled
                        Log("DPS error " + status.ToString());
                        Debug.WriteLine("DPS " + _dpsTopic + " " + body);
                        _dashboard.ShowToast("DPS error " + status.ToString());
                        return false;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Log("DPS: " + Fit(ex.Message.Length > 0 ? ex.Message : "failed", 16));
                Debug.WriteLine("DPS failed: " + ex.ToString());
                _dashboard.ShowToast("DPS unreachable");
                return false;
            }
            finally
            {
                if (dps != null)
                {
                    try
                    {
                        dps.MqttMsgPublishReceived -= OnDpsMessage;

                        if (dps.IsConnected)
                        {
                            dps.Disconnect();
                        }
                    }
                    catch (Exception)
                    {
                        // already closed
                    }
                }
            }
        }

        private static void OnDpsMessage(object sender, MqttMsgPublishEventArgs e)
        {
            _dpsTopic = e.Topic;
            _dpsBody = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length);
            _dpsReply.Set();
        }

        // topic: $dps/registrations/res/{status}/?$rid=1&retry-after=3
        private static int DpsStatus(string topic)
        {
            const string prefix = "$dps/registrations/res/";

            if (!topic.StartsWith(prefix))
            {
                return -1;
            }

            string rest = topic.Substring(prefix.Length);
            int slash = rest.IndexOf('/');

            return int.Parse(slash >= 0 ? rest.Substring(0, slash) : rest);
        }

        private static int DpsRetryAfter(string topic)
        {
            int position = topic.IndexOf("retry-after=");

            if (position < 0)
            {
                return 3;
            }

            int seconds = JsonNumber("\"x\":" + topic.Substring(position + 12), "x");

            return seconds >= 1 && seconds <= 30 ? seconds : 3;
        }

        //////////////////////////////////////////////////////////////////////////////
        // Azure IoT Hub (MQTT), the hub IoT Central / DPS assigned

        private static int _requestId = 10;

        private static bool ConnectHub()
        {
            try
            {
                CloseHub();

                Log("Hub " + Fit(_hubHost, 17));

                string token = Sas.CreateToken(_hubHost, DeviceId, _deviceKey, UnixNow() + 24 * 3600);
                LogMemory("Hub");

                DateTime start = DateTime.UtcNow;

                // the model-id makes the device a Plug and Play device that uses its IoT Central device template
                string userName = _hubHost + "/" + DeviceId + "/?api-version=2021-04-12" +
                    (ModelId.Length > 0 ? "&model-id=" + Sas.UrlEncode(ModelId) : "");

                MqttReasonCode code;
                _mqtt = ConnectTls(_hubHost, userName, token, out code);
                _mqtt.MqttMsgPublishReceived += OnMessageReceived;
                _mqtt.ConnectionClosed += OnConnectionClosed;

                if (code != MqttReasonCode.Success)
                {
                    // bad key, device blocked or deleted in IoT Central
                    Log("Hub: refused (" + ((int)code).ToString() + ")");
                    _dashboard.ShowToast("Hub refused");
                    return false;
                }

                Log("Hub: connected " + Seconds(ElapsedMs(start)));

                Subscribe("commands", "$iothub/methods/POST/#");
                Subscribe("c2d", "devices/" + DeviceId + "/messages/devicebound/#");
                Subscribe("twin", "$iothub/twin/res/#");
                Subscribe("desired", "$iothub/twin/PATCH/properties/desired/#");

                // read the writable properties IoT Central has set, report ours
                _mqtt.Publish("$iothub/twin/GET/?$rid=2", new byte[0], null, null, MqttQoSLevel.AtLeastOnce, false);
                ReportProperties();

                _dashboard.ShowToast("IoT Central ok");

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

        private static void SendTelemetry()
        {
            string json = BuildTelemetryJson();

            // the content type tells IoT Central the message body is JSON
            _mqtt.Publish(
                "devices/" + DeviceId + "/messages/events/$.ct=application%2Fjson&$.ce=utf-8",
                Encoding.UTF8.GetBytes(json),
                null,
                null,
                MqttQoSLevel.AtLeastOnce,
                false);

            Telemetry.MessagesSent++;
            Debug.WriteLine("Sent: " + json);
        }

        // reported properties (device twin): shown as read only properties in IoT Central
        private static void ReportProperties()
        {
            string json = "{\"firmware\":\"" + SystemInfo.Version.ToString() + "\",\"app\":\"" + AppVersion + "\"," +
                "\"mac\":\"" + Telemetry.MacAddress + "\",\"ip\":\"" + Telemetry.IpAddress + "\"," +
                "\"ssid\":\"" + Escape(Telemetry.Ssid) + "\",\"telemetryInterval\":" + _telemetryIntervalSeconds.ToString() + "}";

            PublishReported(json);
        }

        private static void PublishReported(string json)
        {
            _mqtt.Publish(
                "$iothub/twin/PATCH/properties/reported/?$rid=" + (_requestId++).ToString(),
                Encoding.UTF8.GetBytes(json),
                null,
                null,
                MqttQoSLevel.AtLeastOnce,
                false);
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
                    HandleCommand(topic, body);
                }
                else if (topic.StartsWith("$iothub/twin/res/"))
                {
                    // the answer to the twin request ($rid=2) holds the desired properties; other answers are acknowledgements
                    if (topic.IndexOf("$rid=2") >= 0)
                    {
                        ApplyDesired(body);
                    }
                }
                else if (topic.StartsWith("$iothub/twin/PATCH/properties/desired/"))
                {
                    ApplyDesired(body);
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

        // writable property telemetryInterval: {"telemetryInterval":30,"$version":4}
        // IoT Central expects an acknowledgement: value, ac (status code), ad (description) and av (the desired version)
        private static void ApplyDesired(string json)
        {
            int seconds = JsonNumber(json, "telemetryInterval");

            if (seconds < 1)
            {
                return;
            }

            int version = JsonNumber(json, "$version");

            if (seconds > 3600)
            {
                PublishReported("{\"telemetryInterval\":{\"value\":" + _telemetryIntervalSeconds.ToString() +
                    ",\"ac\":400,\"ad\":\"1 to 3600 seconds\",\"av\":" + (version < 0 ? 0 : version).ToString() + "}}");
                return;
            }

            _telemetryIntervalSeconds = seconds;
            Log("Interval " + seconds + "s");

            PublishReported("{\"telemetryInterval\":{\"value\":" + seconds.ToString() +
                ",\"ac\":200,\"ad\":\"completed\",\"av\":" + (version < 0 ? 0 : version).ToString() + "}}");
        }

        // IoT Central commands arrive as direct methods named like the command. The topic is
        // $iothub/methods/POST/{command}/?$rid={request id}; the answer is published to $iothub/methods/res/{status}/?$rid={request id}
        private static void HandleCommand(string topic, string payload)
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
                response = "{\"error\":\"unknown command\"}";
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

        // value of a string property in a JSON text, "" if there is none (enough for the flat DPS answers)
        private static string JsonString(string json, string name)
        {
            int position = json.IndexOf("\"" + name + "\"");

            if (position < 0)
            {
                return "";
            }

            position = json.IndexOf(':', position);

            if (position < 0)
            {
                return "";
            }

            position++;

            while (position < json.Length && json[position] == ' ')
            {
                position++;
            }

            if (position >= json.Length || json[position] != '"')
            {
                return "";
            }

            int end = json.IndexOf('"', position + 1);

            return end < 0 ? "" : json.Substring(position + 1, end - position - 1);
        }

        //////////////////////////////////////////////////////////////////////////////
        // JSON (built by hand to keep the app small). The names match AZ3166-device-model.json

        private static string BuildTelemetryJson()
        {
            var json = new StringBuilder();

            lock (Telemetry.Lock)
            {
                json.Append("{\"uptime\":").Append(Telemetry.UptimeSeconds.ToString());

                if (Telemetry.HasClimate)
                {
                    json.Append(",\"temperature\":").Append(Telemetry.Temperature.ToString("F2"));
                    json.Append(",\"humidity\":").Append(Telemetry.Humidity.ToString("F2"));
                }

                if (Telemetry.HasPressure)
                {
                    json.Append(",\"pressure\":").Append(Telemetry.Pressure.ToString("F2"));
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
                json.Append(",\"rssi\":").Append(Telemetry.Rssi.ToString());
            }

            // free managed memory (runs the garbage collector)
            json.Append(",\"freeMemory\":").Append(nanoFramework.Runtime.Native.GC.Run(false).ToString());
            json.Append("}");

            return json.ToString();
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
