using System;
using System.Device.Gpio;
using System.Diagnostics;
using System.Threading;

namespace nfaz3166iothubbasic
{
    /// <summary>Latest readings and status, shared between the dashboard thread and the cloud connection thread.</summary>
    public static class Telemetry
    {
        public static readonly object Lock = new object();

        // sensors
        public static bool HasClimate, HasPressure, HasMotion, HasMagnetometer;
        public static float Temperature, Humidity, Pressure, PressureTemperature;
        public static float AccelX, AccelY, AccelZ, GyroX, GyroY, GyroZ;
        public static float MagX, MagY, MagZ, Heading;
        public static bool ButtonA, ButtonB;

        // device and network
        public static long UptimeSeconds;
        public static string Ssid = "";
        public static string IpAddress = "";
        public static string MacAddress = "";
        public static int Rssi;
        public static bool WifiConnected, MqttConnected;
        public static string Broker = "";
        public static int MessagesSent;
        public static string LastCommand = "";
        public static int MqttReconnects;

        // Wi-Fi diagnostics
        public static string WifiState = "Starting";
        public static int NetworksFound;
        public static string Bssid = "";
        public static int SignalBars;
        public static string SubnetMask = "";
        public static string Gateway = "";
        public static string Dns = "";
        public static int ConnectAttempts;
        public static string LastConnectResult = "";
        public static int JoinMs, DhcpMs;
        public static int LinkDrops, AddressChanges;
        public static long ConnectedSince;

        // event log (boot screen and LOG page)
        public const int LogSize = 6;
        private static readonly string[] _log = new string[LogSize];
        private static int _logCount;

        public static void AddLog(string line)
        {
            lock (Lock)
            {
                if (_logCount < LogSize)
                {
                    _log[_logCount++] = line;
                }
                else
                {
                    Array.Copy(_log, 1, _log, 0, LogSize - 1);
                    _log[LogSize - 1] = line;
                }
            }
        }

        public static string[] GetLog()
        {
            lock (Lock)
            {
                string[] copy = new string[_logCount];
                Array.Copy(_log, copy, _logCount);
                return copy;
            }
        }
    }

    /// <summary>
    /// Owns the I2C bus (sensors and display) and the buttons: reads the sensors once per second and draws the
    /// dashboard on the OLED. Button A shows the previous page, button B the next one; with no buttons pressed
    /// the pages rotate on their own.
    /// </summary>
    public class Dashboard
    {
        private const int TickMs = 50;
        private const int RenderEveryTicks = 5;     // 250 ms
        private const int SensorEveryTicks = 20;    // 1 s
        private const int AutoAdvanceSeconds = 8;
        private const int ManualHoldSeconds = 30;

        private static readonly string[] PageTitles = { "CLIMATE", "MOTION", "COMPASS", "WIFI", "NETWORK", "LOG", "SYSTEM" };

        private Ssd1306 _display;
        private Hts221 _climate;
        private Lps22hb _pressure;
        private Lsm6dsl _motion;
        private Lis2mdl _magnetometer;
        private GpioPin _buttonA;
        private GpioPin _buttonB;
        private int _spinner;

        /// <summary>True until the start up sequence is done: the display shows the boot log.</summary>
        public static bool Booting = true;

        /// <summary>The hardware initialization step in progress (to report a hang).</summary>
        public static string InitStep = "";

        /// <summary>Set when <see cref="InitHardware"/> completes.</summary>
        public readonly AutoResetEvent HardwareReady = new AutoResetEvent(false);

        private int _page;
        private int _secondsOnPage;
        private int _secondsSinceButton = ManualHoldSeconds;
        private bool _lastA, _lastB;
        private string _toast = "";
        private int _toastTicks;
        private long _uptimeMs;
        private DateTime _lastTick;

        public bool DisplayPresent => _display != null && _display.IsPresent;

        /// <summary>
        /// Opens the display, sensors and buttons, reporting each step (run on its own thread so a hang can be
        /// detected).
        /// </summary>
        public void InitHardware()
        {
            Step("OLED");
            _display = new Ssd1306(Program.I2cBus);
            Debug.WriteLine("  OLED present: " + _display.IsPresent);

            Step("HTS221");
            _climate = new Hts221();
            Telemetry.HasClimate = _climate.IsPresent;

            Step("LPS22HB");
            _pressure = new Lps22hb();
            Telemetry.HasPressure = _pressure.IsPresent;

            Step("LSM6DSL");
            _motion = new Lsm6dsl();
            Telemetry.HasMotion = _motion.IsPresent;

            Step("LIS2MDL");
            _magnetometer = new Lis2mdl();
            Telemetry.HasMagnetometer = _magnetometer.IsPresent;

            Step("buttons");
            var gpio = new GpioController();
            _buttonA = gpio.OpenPin(Program.ButtonAPin, PinMode.InputPullUp);
            _buttonB = gpio.OpenPin(Program.ButtonBPin, PinMode.InputPullUp);

            Step("done");

            // start up tracing done, the sensors are read every second from now on
            Register.Trace = false;

            HardwareReady.Set();
        }

        private static void Step(string name)
        {
            InitStep = name;
            Debug.WriteLine("Init: " + name);

            // give the debugger output time to get out, in case the next step hangs the device
            Thread.Sleep(300);
        }

        public void Start()
        {
            _lastTick = DateTime.UtcNow;
            new Thread(Run).Start();
        }

        /// <summary>Shows a message over the current page for a few seconds.</summary>
        public void ShowToast(string text)
        {
            _toast = text;
            _toastTicks = 5000 / TickMs;
        }

        private void Run()
        {
            int tick = 0;

            while (true)
            {
                try
                {
                    UpdateUptime();

                    if (_buttonA != null)
                    {
                        PollButtons();
                    }

                    if (tick % SensorEveryTicks == 0)
                    {
                        ReadSensors();
                        EverySecond();
                    }

                    if (tick % RenderEveryTicks == 0)
                    {
                        Render();
                    }

                    if (_toastTicks > 0)
                    {
                        _toastTicks--;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Dashboard: " + ex.Message);
                }

                tick++;
                Thread.Sleep(TickMs);
            }
        }

        // there is no monotonic tick counter, so accumulate elapsed time and ignore jumps (e.g. SNTP time sync)
        private void UpdateUptime()
        {
            DateTime now = DateTime.UtcNow;
            long elapsed = (long)(now - _lastTick).TotalMilliseconds;
            _lastTick = now;

            if (elapsed < 0 || elapsed > 2000)
            {
                elapsed = TickMs;
            }

            _uptimeMs += elapsed;
            Telemetry.UptimeSeconds = _uptimeMs / 1000;
        }

        private void PollButtons()
        {
            // buttons are active low
            bool a = _buttonA.Read() == PinValue.Low;
            bool b = _buttonB.Read() == PinValue.Low;

            if (a && !_lastA)
            {
                ChangePage(-1);
            }

            if (b && !_lastB)
            {
                ChangePage(1);
            }

            _lastA = a;
            _lastB = b;
            Telemetry.ButtonA = a;
            Telemetry.ButtonB = b;
        }

        private void ChangePage(int delta)
        {
            _page = (_page + delta + PageTitles.Length) % PageTitles.Length;
            _secondsOnPage = 0;
            _secondsSinceButton = 0;
        }

        private void EverySecond()
        {
            if (Booting)
            {
                return;
            }

            _secondsOnPage++;
            _secondsSinceButton++;

            if (_secondsSinceButton >= ManualHoldSeconds && _secondsOnPage >= AutoAdvanceSeconds)
            {
                _page = (_page + 1) % PageTitles.Length;
                _secondsOnPage = 0;
            }
        }

        private void ReadSensors()
        {
            if (_magnetometer == null)
            {
                return;
            }

            bool climate = _climate.Read();
            bool pressure = _pressure.Read();
            bool motion = _motion.Read();
            bool magnetometer = _magnetometer.Read();

            lock (Telemetry.Lock)
            {
                if (climate)
                {
                    Telemetry.Temperature = _climate.Temperature;
                    Telemetry.Humidity = _climate.Humidity;
                }

                if (pressure)
                {
                    Telemetry.Pressure = _pressure.Pressure;
                    Telemetry.PressureTemperature = _pressure.Temperature;
                }

                if (motion)
                {
                    Telemetry.AccelX = _motion.AccelX;
                    Telemetry.AccelY = _motion.AccelY;
                    Telemetry.AccelZ = _motion.AccelZ;
                    Telemetry.GyroX = _motion.GyroX;
                    Telemetry.GyroY = _motion.GyroY;
                    Telemetry.GyroZ = _motion.GyroZ;
                }

                if (magnetometer)
                {
                    Telemetry.MagX = _magnetometer.X;
                    Telemetry.MagY = _magnetometer.Y;
                    Telemetry.MagZ = _magnetometer.Z;
                    Telemetry.Heading = _magnetometer.Heading;
                }
            }
        }

        ///////////////////////////////////////////////////////////////////////
        // drawing

        private void Render()
        {
            if (_display == null || !_display.IsPresent)
            {
                return;
            }

            _display.Clear();

            if (Booting)
            {
                DrawBootScreen();
                _display.Show();
                return;
            }

            DrawHeader(PageTitles[_page]);

            switch (_page)
            {
                case 0:
                    DrawClimate();
                    break;
                case 1:
                    DrawMotion();
                    break;
                case 2:
                    DrawCompass();
                    break;
                case 3:
                    DrawWifi();
                    break;
                case 4:
                    DrawNetwork();
                    break;
                case 5:
                    DrawLog();
                    break;
                default:
                    DrawSystem();
                    break;
            }

            DrawPageDots();

            if (_toastTicks > 0 && _toast.Length > 0)
            {
                DrawToast();
            }

            _display.Show();
        }

        private void DrawHeader(string title)
        {
            _display.FillRect(0, 0, Ssd1306.Width, 10);
            _display.DrawText(2, 1, title, 1, false);

            // Wi-Fi signal bars
            int bars = 0;

            if (Telemetry.WifiConnected)
            {
                bars = Telemetry.Rssi == 0 ? 4 : (Telemetry.Rssi > -55 ? 4 : Telemetry.Rssi > -65 ? 3 : Telemetry.Rssi > -75 ? 2 : 1);
            }

            for (int i = 0; i < 4; i++)
            {
                int h = 2 + i * 2;
                int x = 100 + i * 3;

                if (i < bars)
                {
                    _display.FillRect(x, 9 - h, 2, h, false);
                }
                else
                {
                    _display.SetPixel(x, 8, false);
                }
            }

            // cloud connection: filled box when connected, outline when not
            if (Telemetry.MqttConnected)
            {
                _display.FillRect(116, 1, 11, 8, false);
                _display.DrawText(119, 1, "A", 1, true);
            }
            else
            {
                _display.DrawRect(116, 1, 11, 8, false);
            }
        }

        private void DrawPageDots()
        {
            int count = PageTitles.Length;
            int x0 = 64 - (count - 1) * 4;

            for (int i = 0; i < count; i++)
            {
                if (i == _page)
                {
                    _display.FillRect(x0 + i * 8 - 1, 61, 3, 3);
                }
                else
                {
                    _display.SetPixel(x0 + i * 8, 62);
                }
            }
        }

        private void DrawToast()
        {
            string text = _toast.Length > 19 ? _toast.Substring(0, 19) : _toast;
            int w = Ssd1306.TextWidth(text) + 8;
            int x = (Ssd1306.Width - w) / 2;

            _display.FillRect(x, 24, w, 15, false);
            _display.DrawRect(x, 24, w, 15);
            _display.DrawText(x + 4, 28, text);
        }

        private void DrawClimate()
        {
            if (!Telemetry.HasClimate)
            {
                _display.DrawTextCentered(28, "no HTS221 sensor");
                return;
            }

            float t, h, p;

            lock (Telemetry.Lock)
            {
                t = Telemetry.Temperature;
                h = Telemetry.Humidity;
                p = Telemetry.Pressure;
            }

            // big temperature
            int x = _display.DrawText(2, 14, t.ToString("F1"), 3);
            _display.DrawText(x, 14, "°C", 1);

            // humidity gauge on the right
            _display.DrawRect(114, 13, 12, 44);
            int fill = (int)(h * 40 / 100);
            _display.FillRect(116, 55 - fill, 8, fill);
            _display.DrawText(96, 13, "RH", 1);

            _display.DrawLine(2, 40, 106, 40);
            _display.DrawText(2, 44, "Hum  " + h.ToString("F1") + "%");

            if (Telemetry.HasPressure)
            {
                _display.DrawText(2, 53, "Pres " + p.ToString("F1") + " hPa");
            }
        }

        private void DrawMotion()
        {
            if (!Telemetry.HasMotion)
            {
                _display.DrawTextCentered(28, "no LSM6DSL sensor");
                return;
            }

            float ax, ay, az, gx, gy, gz;

            lock (Telemetry.Lock)
            {
                ax = Telemetry.AccelX;
                ay = Telemetry.AccelY;
                az = Telemetry.AccelZ;
                gx = Telemetry.GyroX;
                gy = Telemetry.GyroY;
                gz = Telemetry.GyroZ;
            }

            // bubble level
            const int cx = 26, cy = 35, r = 22;
            _display.DrawCircle(cx, cy, r);
            _display.DrawCircle(cx, cy, 6);
            _display.DrawLine(cx - r, cy, cx - 8, cy);
            _display.DrawLine(cx + 8, cy, cx + r, cy);
            _display.DrawLine(cx, cy - r, cx, cy - 8);
            _display.DrawLine(cx, cy + 8, cx, cy + r);

            int bx = cx + Clamp((int)(-ax * r), -(r - 4), r - 4);
            int by = cy + Clamp((int)(ay * r), -(r - 4), r - 4);
            _display.FillCircle(bx, by, 3);

            // values
            _display.DrawText(54, 13, "X" + Signed(ax, "F2") + "g");
            _display.DrawText(54, 22, "Y" + Signed(ay, "F2") + "g");
            _display.DrawText(54, 31, "Z" + Signed(az, "F2") + "g");

            double rate = Math.Sqrt(gx * gx + gy * gy + gz * gz);
            _display.DrawLine(54, 41, 126, 41);
            _display.DrawText(54, 45, "Rot " + rate.ToString("F0") + "d/s");

            int bar = Clamp((int)(rate * 72 / 250), 0, 72);
            _display.FillRect(54, 54, bar, 3);
        }

        private void DrawCompass()
        {
            if (!Telemetry.HasMagnetometer)
            {
                _display.DrawTextCentered(28, "no LIS2MDL sensor");
                return;
            }

            float heading, mx, my, mz;

            lock (Telemetry.Lock)
            {
                heading = Telemetry.Heading;
                mx = Telemetry.MagX;
                my = Telemetry.MagY;
                mz = Telemetry.MagZ;
            }

            const int cx = 26, cy = 36, r = 22;
            _display.DrawCircle(cx, cy, r);

            // tick marks every 45 degrees
            for (int a = 0; a < 360; a += 45)
            {
                double rad = a * Math.PI / 180;
                int len = a % 90 == 0 ? 4 : 2;
                _display.DrawLine(
                    cx + (int)((r - len) * Math.Sin(rad)),
                    cy - (int)((r - len) * Math.Cos(rad)),
                    cx + (int)(r * Math.Sin(rad)),
                    cy - (int)(r * Math.Cos(rad)));
            }

            _display.DrawText(cx - 2, cy - r + 6, "N");

            // needle pointing to north, relative to the board
            double needle = -heading * Math.PI / 180;
            int nx = cx + (int)((r - 6) * Math.Sin(needle));
            int ny = cy - (int)((r - 6) * Math.Cos(needle));
            _display.DrawLine(cx, cy, nx, ny);
            _display.DrawLine(cx + 1, cy, nx, ny);
            _display.FillCircle(cx, cy, 2);

            int x = _display.DrawText(56, 14, ((int)heading).ToString(), 2);
            _display.DrawText(x, 14, "°");
            _display.DrawText(56, 32, Cardinal(heading), 2);

            double field = Math.Sqrt(mx * mx + my * my + mz * mz);
            _display.DrawText(56, 51, field.ToString("F0") + " mG");
        }

        private void DrawBootScreen()
        {
            string[] spin = { "|", "/", "-", "\\" };
            _spinner = (_spinner + 1) % spin.Length;

            _display.FillRect(0, 0, Ssd1306.Width, 10);
            _display.DrawText(2, 1, "AZ3166 STARTING", 1, false);
            _display.DrawText(120, 1, spin[_spinner], 1, false);

            string[] log = Telemetry.GetLog();

            for (int i = 0; i < log.Length; i++)
            {
                _display.DrawText(0, 12 + i * 9, Fit(log[i], 21));
            }
        }

        private void DrawWifi()
        {
            string state, bssid, result;
            int rssi, bars, attempts, joinMs, dhcpMs, drops;
            long since;

            lock (Telemetry.Lock)
            {
                state = Telemetry.WifiState;
                bssid = Telemetry.Bssid;
                result = Telemetry.LastConnectResult;
                rssi = Telemetry.Rssi;
                bars = Telemetry.SignalBars;
                attempts = Telemetry.ConnectAttempts;
                joinMs = Telemetry.JoinMs;
                dhcpMs = Telemetry.DhcpMs;
                drops = Telemetry.LinkDrops;
                since = Telemetry.ConnectedSince;
            }

            _display.DrawText(0, 13, Fit(state, 12));
            _display.DrawText(80, 13, "try " + attempts.ToString());

            int x = _display.DrawText(0, 22, rssi == 0 ? "RSSI -" : rssi.ToString() + "dBm");

            // signal bars graphic
            for (int i = 0; i < 4; i++)
            {
                int h = 2 + i * 2;
                int bx = x + 4 + i * 4;

                if (i < bars)
                {
                    _display.FillRect(bx, 29 - h, 3, h);
                }
                else
                {
                    _display.DrawRect(bx, 29 - h, 3, h);
                }
            }

            _display.DrawText(88, 22, Fit(result, 7));
            _display.DrawText(0, 31, "AP " + (bssid.Length > 0 ? bssid : "-"));
            _display.DrawText(0, 40, "Join " + Seconds(joinMs) + " DHCP " + Seconds(dhcpMs));

            string up = Telemetry.WifiConnected ? Duration(Telemetry.UptimeSeconds - since) : "-";
            _display.DrawText(0, 49, "Drops " + drops.ToString() + "  up " + up);
        }

        private void DrawNetwork()
        {
            string ip, mask, gateway, dns, broker;
            int sent, reconnects;
            bool mqtt;

            lock (Telemetry.Lock)
            {
                ip = Telemetry.IpAddress;
                mask = Telemetry.SubnetMask;
                gateway = Telemetry.Gateway;
                dns = Telemetry.Dns;
                broker = Telemetry.Broker;
                sent = Telemetry.MessagesSent;
                reconnects = Telemetry.MqttReconnects;
                mqtt = Telemetry.MqttConnected;
            }

            _display.DrawText(0, 13, "IP  " + (ip.Length > 0 ? ip : "-"));
            _display.DrawText(0, 22, "MSK " + mask);
            _display.DrawText(0, 31, "GW  " + gateway);
            _display.DrawText(0, 40, "DNS " + dns);
            _display.DrawText(0, 49, (mqtt ? "Hub ok" : "Hub --") + " tx" + sent.ToString() + " rc" + reconnects.ToString());
        }

        private void DrawLog()
        {
            string[] log = Telemetry.GetLog();

            for (int i = 0; i < log.Length; i++)
            {
                _display.DrawText(0, 12 + i * 8, Fit(log[i], 21));
            }
        }

        private void DrawSystem()
        {
            long up = Telemetry.UptimeSeconds;
            string uptime = (up / 3600).ToString() + "h" + ((up / 60) % 60).ToString("D2") + "m" + (up % 60).ToString("D2") + "s";

            _display.DrawText(0, 13, "Up   " + uptime);
            _display.DrawText(0, 22, "FW   " + nanoFramework.Runtime.Native.SystemInfo.Version.ToString());
            _display.DrawText(0, 31, "MAC  " + Telemetry.MacAddress);
            _display.DrawText(0, 40, "Btn  A:" + (Telemetry.ButtonA ? "on " : "off") + " B:" + (Telemetry.ButtonB ? "on" : "off"));
            _display.DrawText(0, 49, "Cmd  " + Fit(Telemetry.LastCommand, 16));
        }

        ///////////////////////////////////////////////////////////////////////
        // helpers

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        private static string Signed(float value, string format)
        {
            return (value >= 0 ? "+" : "") + value.ToString(format);
        }

        private static string Fit(string text, int maxChars)
        {
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }

        private static string Seconds(int milliseconds)
        {
            return milliseconds <= 0 ? "-" : (milliseconds / 1000.0).ToString("F1") + "s";
        }

        private static string Duration(long seconds)
        {
            if (seconds < 0)
            {
                seconds = 0;
            }

            if (seconds < 3600)
            {
                return (seconds / 60).ToString() + "m" + (seconds % 60).ToString("D2") + "s";
            }

            return (seconds / 3600).ToString() + "h" + ((seconds / 60) % 60).ToString("D2") + "m";
        }

        private static string Cardinal(float heading)
        {
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return names[(int)((heading + 22.5f) / 45f) % 8];
        }
    }
}
