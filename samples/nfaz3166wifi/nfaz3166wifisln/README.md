# AZ3166 WiFi Test Sample for nanoFramework

This sample demonstrates WiFi connectivity on the MXCHIP AZ3166 IoT DevKit using nanoFramework.

## Features

- ✅ WiFi adapter detection
- ✅ Network scanning
- ✅ WiFi connection with WPA2/PSK
- ✅ Network information display (IP, MAC, Gateway, DNS)
- ✅ DNS resolution test
- ✅ Event-driven network availability notifications

## Prerequisites

1. **Hardware:**
   - MXCHIP AZ3166 IoT DevKit
   - USB cable for power and debugging

2. **Software:**
   - Visual Studio 2022 with nanoFramework extension
   - nanoFramework firmware flashed to AZ3166
   - .NET nanoFramework VS extension

3. **WiFi Network:**
   - 2.4GHz WiFi network (BCM43362 chip limitation)
   - WPA2-PSK security recommended
   - SSID and password

## Setup Instructions

### 1. Configure WiFi Credentials

Edit `Program.cs` and update these constants with your WiFi network details:

```csharp
private const string WIFI_SSID = "YourWiFiSSID";
private const string WIFI_PASSWORD = "YourWiFiPassword";
```

### 2. Flash nanoFramework Firmware

If you haven't already flashed the custom nanoFramework firmware:

```bash
# From nf-interpreter/build directory
nanoff --target MXCHIP_AZ3166 --serialport COM3 --update
# Or manually flash nanoCLR.bin using STM32 tools
```

### 3. Restore NuGet Packages

In Visual Studio:
- Right-click the solution → Restore NuGet Packages
- Or use Package Manager Console: `Update-Package -reinstall`

### 4. Build and Deploy

1. Open `nfaz3166wifisln.sln` in Visual Studio 2022
2. Set the project as startup project
3. Connect AZ3166 via USB
4. Press F5 to build and deploy
5. Open Device Explorer (View → Other Windows → Device Explorer)
6. Watch the debug output

## Expected Output

When the sample runs successfully, you should see:

```
========================================
AZ3166 WiFi Test Sample
nanoFramework on MXCHIP AZ3166
========================================

Step 1: Getting WiFi adapter...
WiFi adapter found: ...

Step 2: Scanning for networks...

Available Networks: 5
----------------------------------------
#1:
  SSID: MyWiFi
  Signal: -45 dBm
  Channel: 2437 MHz
  Security: Wpa2Psk
...
----------------------------------------

Step 3: Connecting to WiFi...
SSID: MyWiFi
Connection result: Success

✓ WiFi Connected Successfully!

Network Information:
----------------------------------------
MAC Address: XX:XX:XX:XX:XX:XX
IP Address: 192.168.1.100
Subnet Mask: 255.255.255.0
Gateway: 192.168.1.1
DNS Server: 192.168.1.1
DHCP Enabled: True
Link Speed: 54000000 bps
----------------------------------------

Step 4: Testing network connectivity...
Testing DNS resolution...
✓ DNS working - resolved to: 142.250.185.196

Network connectivity test complete!

Test complete. Sleeping...
```

## Troubleshooting

### WiFi Not Connecting

1. **Check SSID/Password:**
   - Ensure credentials are correct
   - SSID is case-sensitive
   - Password must match exactly

2. **Check Network Band:**
   - BCM43362 only supports 2.4GHz
   - 5GHz networks will not appear in scan

3. **Check Security:**
   - WPA2-PSK is recommended
   - WEP may not work reliably
   - Open networks should work

4. **Signal Strength:**
   - Keep device within range (> -70 dBm)
   - Check for interference

### No WiFi Adapter Found

1. Verify nanoFramework firmware is flashed correctly
2. Check that `System.Device.Wifi` API is enabled in build
3. Ensure BCM43362 WiFi chip is properly initialized

### Build Errors

1. Restore NuGet packages
2. Check that all references point to correct package versions
3. Clean and rebuild solution

### Device Not Detected

1. Install STM32 USB drivers
2. Check Device Manager for "STM32 Virtual COM Port"
3. Try different USB port or cable
4. Reset device with RESET button

## Architecture

```
C# Application (Program.cs)
       ↓
System.Device.Wifi API
       ↓
nanoFramework WiFi PAL (nf_wireless.cpp)
       ↓
WICED SDK (BCM43362 driver)
       ↓
MICO Platform Layer (mico_platform.c)
       ↓
ChibiOS RTOS + lwIP
       ↓
STM32F412 SDIO Hardware
       ↓
BCM43362 WiFi Chip
```

## Code Structure

- **Program.cs** - Main test application
  - `TestWiFi()` - Main WiFi test flow
  - `Adapter_AvailableNetworksChanged()` - Scan results handler
  - `DisplayNetworkInfo()` - Shows IP configuration
  - `TestNetworkConnectivity()` - Tests DNS and network

## Next Steps

Once WiFi is working, you can:

1. **HTTP Client:** Use `System.Net.Http.HttpClient` for REST APIs
2. **MQTT Client:** Connect to IoT Hub or MQTT broker
3. **Azure IoT:** Use Azure IoT SDK for nanoFramework
4. **Web Server:** Create simple web server for device control
5. **Sensors:** Combine with I2C/SPI sensors and send data to cloud

## API Reference

### WifiAdapter Methods

- `FindAllAdapters()` - Get available WiFi adapters
- `ScanAsync()` - Start network scan
- `Connect(ssid, reconnection, password)` - Connect to network
- `Disconnect()` - Disconnect from network

### WifiAdapter Properties

- `NetworkInterface` - Get network interface for IP config
- `NetworkReport` - Get scan results

### WifiConnectionResult

- `ConnectionStatus` - Success, NetworkNotAvailable, etc.

## Resources

- [nanoFramework Documentation](https://docs.nanoframework.net/)
- [System.Device.Wifi API](https://github.com/nanoframework/System.Device.Wifi)
- [AZ3166 nanoFramework Port](https://github.com/nanoframework/nf-interpreter)
- [WiFi Samples](https://github.com/nanoframework/Samples)

## License

This sample is provided as-is under MIT License.

## Contributing

Issues and pull requests welcome! Please test on hardware before submitting.
