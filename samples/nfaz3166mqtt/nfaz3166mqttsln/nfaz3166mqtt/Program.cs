using System;
using System.Device.Wifi;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;

namespace nfaz3166mqtt
{
    /// <summary>
    /// AZ3166 telemetry to an on-premise MQTT broker, with an OLED dashboard.
    ///
    /// Topics (base = "az3166/&lt;device id&gt;"):
    ///   base/status     "online" / "offline" (retained, offline is the last will)
    ///   base/info       device information as JSON (retained, sent on connect)
    ///   base/telemetry  all readings as JSON, every TelemetryIntervalSeconds
    ///   base/cmd        commands to the device:
    ///                     interval=N   telemetry interval in seconds
    ///                     text=...     show a message on the display
    ///                     ping         publish telemetry now
    /// </summary>
    public class Program
    {
        //////////////////////////////////////////////////////////////////////////////
        // configuration - UPDATE THESE

        private const string WifiSsid = "YourWiFiSSID";
        private const string WifiPassword = "YourWiFiPassword";

        // on-premise broker: host name or IP address, plain MQTT (no TLS)
        private const string MqttBroker = "192.168.1.10";
        private const int MqttPort = 1883;

        // leave empty if the broker allows anonymous clients
        private const string MqttUser = "";
        private const string MqttPassword = "";

        private const string TopicPrefix = "az3166";
        private static int TelemetryIntervalSeconds = 10;

        //////////////////////////////////////////////////////////////////////////////
        // board

        internal const int I2cBus = 1;      // I2C1 on PB8/PB9: sensors and OLED
        internal const int ButtonAPin = 4;  // PA4
        internal const int ButtonBPin = 10; // PA10

        private static Dashboard _dashboard;
        private static MqttClient _mqtt;
        private static string _deviceId;
        private static string _topicBase;
        private static bool _publishNow;
        private static bool _mqttConnectedOnce;

        public static void Main()
        {
            Log("AZ3166 MQTT telemetry");
            Log("App 1.1.1 (network switch)");

            // a window to stop or redeploy from Visual Studio before any hardware is touched
            // (if the app hangs the device at start up, this is what makes it recoverable without erasing it)
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

            // device id from the last 3 bytes of the MAC address, e.g. az3166-a4c0fe
            _deviceId = "az3166-" + (mac[3].ToString("X2") + mac[4].ToString("X2") + mac[5].ToString("X2")).ToLower();
            _topicBase = TopicPrefix + "/" + _deviceId;
            Telemetry.Broker = MqttBroker;

            Log("ID " + _deviceId);

            ConnectWifi();

            int secondsSincePublish = TelemetryIntervalSeconds;
            int secondsSinceScan = 0;
            bool firstMqttAttempt = true;

            while (true)
            {
                try
                {
                    UpdateNetworkStatus();

                    if (Telemetry.WifiConnected && (_mqtt == null || !_mqtt.IsConnected))
                    {
                        ConnectMqtt();
                        secondsSincePublish = TelemetryIntervalSeconds;

                        if (firstMqttAttempt)
                        {
                            // start up done, show the dashboard (the LOG page keeps the history)
                            firstMqttAttempt = false;
                            Dashboard.Booting = false;
                        }
                    }

                    Telemetry.MqttConnected = _mqtt != null && _mqtt.IsConnected;

                    if (Telemetry.MqttConnected && _wifiChanged)
                    {
                        _wifiChanged = false;
                        Publish("wifi", BuildWifiJson(), true);
                    }

                    if (Telemetry.MqttConnected && (secondsSincePublish >= TelemetryIntervalSeconds || _publishNow))
                    {
                        PublishTelemetry();
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

            CheckBrokerRoute();
        }

        // a broker given by IP address outside the local subnet is reached through the gateway, which
        // (e.g. on an LTE router) usually can't get to a private address on another network
        private static void CheckBrokerRoute()
        {
            uint broker = ParseIp(MqttBroker);
            uint ip = ParseIp(Telemetry.IpAddress);
            uint mask = ParseIp(Telemetry.SubnetMask);

            if (broker == 0 || ip == 0 || mask == 0)
            {
                // host name, or no address
                return;
            }

            if ((broker & mask) != (ip & mask))
            {
                Log("Broker not on subnet");
                Log("  route via " + Telemetry.Gateway);
                Debug.WriteLine("The broker " + MqttBroker + " is not on the local subnet (" + Telemetry.IpAddress + "/" +
                    Telemetry.SubnetMask + "), it has to be reachable through the gateway " + Telemetry.Gateway);
            }
        }

        private static uint ParseIp(string text)
        {
            if (text == null)
            {
                return 0;
            }

            uint value = 0;
            int part = 0;
            int parts = 0;

            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '.')
                {
                    if (part > 255)
                    {
                        return 0;
                    }

                    value = (value << 8) | (uint)part;
                    part = 0;
                    parts++;
                }
                else if (text[i] >= '0' && text[i] <= '9')
                {
                    part = part * 10 + (text[i] - '0');
                }
                else
                {
                    // not an IP address
                    return 0;
                }
            }

            return parts == 4 ? value : 0;
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
        // MQTT

        private static void ConnectMqtt()
        {
            try
            {
                Log("MQTT " + Fit(MqttBroker, 16));

                if (_mqtt == null)
                {
                    _mqtt = new MqttClient(MqttBroker, MqttPort, false, null, null, MqttSslProtocols.None);
                    _mqtt.ProtocolVersion = MqttProtocolVersion.Version_3_1_1;
                    _mqtt.MqttMsgPublishReceived += OnMqttMessage;
                }

                MqttReasonCode code = _mqtt.Connect(
                    _deviceId,
                    MqttUser.Length > 0 ? MqttUser : null,
                    MqttPassword.Length > 0 ? MqttPassword : null,
                    true,                              // will retain
                    MqttQoSLevel.AtLeastOnce,          // will QoS
                    true,                              // will flag
                    _topicBase + "/status",            // will topic
                    "offline",                         // will message
                    true,                              // clean session
                    60);                               // keep alive (s)

                if (code != MqttReasonCode.Success)
                {
                    Log("MQTT: refused (" + ((int)code).ToString() + ")");
                    _dashboard.ShowToast("MQTT refused");
                    Thread.Sleep(5000);
                    return;
                }

                _mqtt.Subscribe(new string[] { _topicBase + "/cmd" }, new MqttQoSLevel[] { MqttQoSLevel.AtLeastOnce });

                Publish("status", "online", true);
                Publish("info", BuildInfoJson(), true);

                Publish("wifi", BuildWifiJson(), true);
                _wifiChanged = false;

                if (_mqttConnectedOnce)
                {
                    Telemetry.MqttReconnects++;
                }

                _mqttConnectedOnce = true;

                Log("MQTT: connected");
                Debug.WriteLine("Publishing to " + _topicBase + "/telemetry");
                _dashboard.ShowToast("MQTT connected");
            }
            catch (Exception ex)
            {
                Log("MQTT: unreachable");
                Log(Fit(ex.Message.Length > 0 ? ex.Message : "connect failed", 21));
                Debug.WriteLine("MQTT connect failed: " + ex.Message);
                _dashboard.ShowToast("MQTT unreachable");
                Thread.Sleep(5000);
            }
        }

        private static void Publish(string subtopic, string payload, bool retain)
        {
            _mqtt.Publish(_topicBase + "/" + subtopic, Encoding.UTF8.GetBytes(payload), null, null, MqttQoSLevel.AtMostOnce, retain);
        }

        private static void PublishTelemetry()
        {
            string json = BuildTelemetryJson();
            Publish("telemetry", json, false);

            Telemetry.MessagesSent++;
            Debug.WriteLine("Published: " + json);
        }

        private static void OnMqttMessage(object sender, MqttMsgPublishEventArgs e)
        {
            // runs on the MQTT client thread, never let an exception escape
            try
            {
                string command = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length).Trim();
                Debug.WriteLine("Command: " + command);
                Telemetry.LastCommand = command;

                if (command.StartsWith("interval="))
                {
                    int seconds = int.Parse(command.Substring(9));

                    if (seconds >= 1 && seconds <= 3600)
                    {
                        TelemetryIntervalSeconds = seconds;
                        _dashboard.ShowToast("interval " + seconds + "s");
                    }
                }
                else if (command.StartsWith("text="))
                {
                    _dashboard.ShowToast(command.Substring(5));
                }
                else if (command == "ping")
                {
                    _publishNow = true;
                    _dashboard.ShowToast("ping");
                }
                else
                {
                    _dashboard.ShowToast("? " + command);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Command failed: " + ex.Message);
            }
        }

        //////////////////////////////////////////////////////////////////////////////
        // JSON (built by hand to keep the app small)

        private static string BuildTelemetryJson()
        {
            var json = new StringBuilder();

            lock (Telemetry.Lock)
            {
                json.Append("{\"device\":\"").Append(_deviceId).Append("\"");
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

        private static string BuildInfoJson()
        {
            var json = new StringBuilder();

            json.Append("{\"device\":\"").Append(_deviceId).Append("\"");
            json.Append(",\"mac\":\"").Append(Telemetry.MacAddress).Append("\"");
            json.Append(",\"ip\":\"").Append(Telemetry.IpAddress).Append("\"");
            json.Append(",\"target\":\"").Append(Escape(nanoFramework.Runtime.Native.SystemInfo.TargetName)).Append("\"");
            json.Append(",\"platform\":\"").Append(Escape(nanoFramework.Runtime.Native.SystemInfo.Platform)).Append("\"");
            json.Append(",\"firmware\":\"").Append(nanoFramework.Runtime.Native.SystemInfo.Version.ToString()).Append("\"");
            json.Append(",\"sensors\":{");
            json.Append("\"hts221\":").Append(Telemetry.HasClimate ? "true" : "false");
            json.Append(",\"lps22hb\":").Append(Telemetry.HasPressure ? "true" : "false");
            json.Append(",\"lsm6dsl\":").Append(Telemetry.HasMotion ? "true" : "false");
            json.Append(",\"lis2mdl\":").Append(Telemetry.HasMagnetometer ? "true" : "false");
            json.Append("}");
            json.Append(",\"telemetryInterval\":").Append(TelemetryIntervalSeconds.ToString());
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
