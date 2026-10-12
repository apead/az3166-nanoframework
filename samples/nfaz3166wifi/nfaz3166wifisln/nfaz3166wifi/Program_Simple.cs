using System;
using System.Diagnostics;
using System.Threading;

namespace nfaz3166wifi
{
    public class Program
    {
        public static void Main()
        {
            Debug.WriteLine("========================================");
            Debug.WriteLine("AZ3166 Simple Boot Test");
            Debug.WriteLine("========================================");
            
            try
            {
                Debug.WriteLine("Step 1: Basic boot successful");
                Thread.Sleep(1000);
                
                Debug.WriteLine("Step 2: Testing System.Device.Wifi availability...");
                
                // Try to access WiFi API without actually using it
                var wifiType = typeof(System.Device.Wifi.WifiAdapter);
                Debug.WriteLine($"WiFi API Type found: {wifiType.Name}");
                
                Debug.WriteLine("Step 3: All checks passed!");
                Debug.WriteLine("Firmware is running correctly.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR: {ex.Message}");
                Debug.WriteLine($"Type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    Debug.WriteLine($"Inner: {ex.InnerException.Message}");
                }
            }

            Debug.WriteLine("");
            Debug.WriteLine("Test complete. Sleeping...");
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
