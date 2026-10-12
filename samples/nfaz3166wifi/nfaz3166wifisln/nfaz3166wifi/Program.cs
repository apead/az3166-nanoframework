using System;
using System.Diagnostics;
using System.Threading;
using System.Device.Wifi;
using System.Net.NetworkInformation;

namespace nfaz3166wifi
{
    public class Program
    {
        // WiFi credentials - UPDATE THESE WITH YOUR NETWORK DETAILS
        private const string WIFI_SSID = "YourWiFiSSID";
        private const string WIFI_PASSWORD = "YourWiFiPassword";

        public static void Main()
        {
            Debug.WriteLine("========================================");
            Debug.WriteLine("AZ3166 WiFi Test Sample");
            Debug.WriteLine("nanoFramework on MXCHIP AZ3166");
            Debug.WriteLine("========================================");
            Debug.WriteLine("");

            try
            {
                // Test WiFi functionality
                TestWiFi();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR: {ex.Message}");
                Debug.WriteLine($"Stack: {ex.StackTrace}");
            }

            Debug.WriteLine("");
            Debug.WriteLine("Test complete. Sleeping...");
            Thread.Sleep(Timeout.Infinite);
        }

        private static void TestWiFi()
        {
            Debug.WriteLine("Step 1: Getting WiFi adapter...");
            
            // Get the WiFi adapter
            var adapters = WifiAdapter.FindAllAdapters();
            
            if (adapters == null || adapters.Length == 0)
            {
                Debug.WriteLine("ERROR: No WiFi adapter found!");
                return;
            }
            
            WifiAdapter adapter = adapters[0];
            
            Debug.WriteLine("WiFi adapter found");
            Debug.WriteLine("");

            // Subscribe to WiFi events
            adapter.AvailableNetworksChanged += Adapter_AvailableNetworksChanged;

            Debug.WriteLine("Step 2: Scanning for networks...");
            adapter.ScanAsync();
            Thread.Sleep(5000); // Wait for scan to complete

            Debug.WriteLine("");
            Debug.WriteLine("Step 3: Connecting to WiFi...");
            Debug.WriteLine($"SSID: {WIFI_SSID}");
            
            // Attempt to connect
            WifiConnectionResult result = adapter.Connect(
                WIFI_SSID,
                WifiReconnectionKind.Automatic,
                WIFI_PASSWORD);

            Debug.WriteLine($"Connection result: {result.ConnectionStatus}");

            if (result.ConnectionStatus == WifiConnectionStatus.Success)
            {
                Debug.WriteLine("");
                Debug.WriteLine("✓ WiFi Connected Successfully!");
                Debug.WriteLine("");

                // the IP address comes from DHCP a few seconds after the connection is made
                WaitForIpAddress(adapter, 30);

                // Display network information
                DisplayNetworkInfo(adapter);

                // Test network connectivity
                TestNetworkConnectivity();
            }
            else
            {
                Debug.WriteLine($"✗ WiFi Connection Failed: {result.ConnectionStatus}");
            }
        }

        private static void Adapter_AvailableNetworksChanged(WifiAdapter sender, object e)
        {
            Debug.WriteLine("");
            Debug.WriteLine($"Available Networks: {sender.NetworkReport.AvailableNetworks.Length}");
            Debug.WriteLine("----------------------------------------");
            
            int count = 0;
            foreach (WifiAvailableNetwork network in sender.NetworkReport.AvailableNetworks)
            {
                count++;
                Debug.WriteLine($"#{count}:");
                Debug.WriteLine($"  SSID: {network.Ssid}");
                Debug.WriteLine($"  Signal: {network.NetworkRssiInDecibelMilliwatts} dBm ({network.SignalBars} bars)");
                
                if (count >= 10) // Limit to first 10 networks
                {
                    Debug.WriteLine("  ... (more networks available)");
                    break;
                }
            }
            Debug.WriteLine("----------------------------------------");
        }

        private static void DisplayNetworkInfo(WifiAdapter adapter)
        {
            // Get network interface by index
            int ifIndex = adapter.NetworkInterface;
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            
            if (interfaces == null || ifIndex >= interfaces.Length)
            {
                Debug.WriteLine("ERROR: Could not get network interface");
                return;
            }
            
            NetworkInterface ni = interfaces[ifIndex];
            
            Debug.WriteLine("Network Information:");
            Debug.WriteLine("----------------------------------------");
            Debug.WriteLine($"MAC Address: {GetMacAddress(ni)}");
            Debug.WriteLine($"IP Address: {ni.IPv4Address}");
            Debug.WriteLine($"Subnet Mask: {ni.IPv4SubnetMask}");
            Debug.WriteLine($"Gateway: {ni.IPv4GatewayAddress}");
            Debug.WriteLine($"DHCP Enabled: {ni.IsDhcpEnabled}");
            Debug.WriteLine("----------------------------------------");
        }

        private static string GetMacAddress(NetworkInterface ni)
        {
            byte[] mac = ni.PhysicalAddress;
            if (mac == null || mac.Length == 0)
                return "N/A";

            return $"{mac[0]:X2}:{mac[1]:X2}:{mac[2]:X2}:{mac[3]:X2}:{mac[4]:X2}:{mac[5]:X2}";
        }

        private static void WaitForIpAddress(WifiAdapter adapter, int timeoutSeconds)
        {
            Debug.WriteLine("Waiting for an IP address from DHCP...");

            for (int i = 0; i < timeoutSeconds; i++)
            {
                NetworkInterface ni = NetworkInterface.GetAllNetworkInterfaces()[adapter.NetworkInterface];

                if (ni.IPv4Address != null && ni.IPv4Address.Length > 0 && ni.IPv4Address != "0.0.0.0")
                {
                    Debug.WriteLine($"Got IP address after {i} s");
                    return;
                }

                Thread.Sleep(1000);
            }

            Debug.WriteLine($"✗ No IP address after {timeoutSeconds} s");
        }

        private static void TestNetworkConnectivity()
        {
            Debug.WriteLine("");
            Debug.WriteLine("Step 4: Testing network connectivity...");
            
            try
            {
                // Test DNS resolution
                Debug.WriteLine("Testing DNS resolution...");
                var hostEntry = System.Net.Dns.GetHostEntry("www.google.com");
                Debug.WriteLine($"✓ DNS working - resolved to: {hostEntry.AddressList[0]}");

                // Test ping (if available)
                Debug.WriteLine("");
                Debug.WriteLine("Network connectivity test complete!");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"✗ Network test failed: {ex.Message}");
            }
        }
    }
}
