# AZ3166 on Azure IoT Hub (nanoFramework) + OLED dashboard

> **Updated for firmware 2.0.0.0** (nanoFramework rebuilt on the latest nf-interpreter `main`). The NuGet packages
> now match its native libraries (System.Net 100.2.0.14, System.Device.Wifi 100.0.6.6; the others are unchanged), so
> **this version doesn't deploy to the 1.x firmware**. For 1.x firmware use the samples of release `v1.2.0.0`.
> If Visual Studio rejects the packages, update the nanoFramework extension.

Sends every reading the MXCHIP AZ3166 has to **Azure IoT Hub** over MQTT with TLS (port 8883), keeps a device twin, answers
direct methods and cloud-to-device messages, and shows a dashboard on the on-board OLED.

## 1. Firmware: you need the TLS build (2.0.0.0)

IoT Hub only accepts encrypted connections, so the firmware has to include TLS. The earlier 1.0.0.x firmware does **not**.
Flash `nanoBooter.hex` and `nanoCLR.hex` from `firmware/2.0.0.0-TLS`, no chip erase needed
(the stored Wi-Fi settings are kept). In Visual Studio's Device Explorer, *Device Capabilities* must show **2.0.0.0**.

Notes about this firmware build: TLS 1.2 only, ECDHE-RSA/ECDSA with AES-GCM or AES-CBC (P-256 and P-384), client side only
(trimmed to fit the flash, see `target_mbedtls_config.h`).

## 2. Azure side (once)

1. Create an **IoT Hub** (the free F1 tier is enough: 8000 messages a day).
2. *Devices* -> *Add device*: device id `az3166`, authentication *Symmetric key*.
3. Open the device and copy the **Primary key**.

or with the Azure CLI:

```
az iot hub device-identity create --hub-name <hub> --device-id az3166
az iot hub device-identity show   --hub-name <hub> --device-id az3166 --query authentication.symmetricKey.primaryKey -o tsv
```

## 3. Configure and run

At the top of `Program.cs`:

| Setting | |
|---|---|
| `WifiSsid`, `WifiPassword` | your 2.4 GHz network |
| `IotHubHostName` | e.g. `myhub.azure-devices.net` |
| `DeviceId` | `az3166` (as registered) |
| `SasKey` | the device's primary (or secondary) key |

Unzip to a fresh folder, open `nfaz3166iothubsln.sln`, *Rebuild*, F5. The NuGet packages are included and match the firmware's
native libraries, don't update them. The deployed app takes roughly 235-245 KB of the 256 KB app area; if the deployment
reports "not enough space", remove pages/features you don't need (e.g. the MOTION or COMPASS page in `Dashboard.cs`).

Start up sequence (on the OLED and in the Output window): hardware init, Wi-Fi (switching to `WifiSsid` if the board remembers
another network), **time**, then the hub:

```
Time: waiting...        the TLS certificate check needs the real date; the firmware sets it over SNTP,
Time 2026-10-11 09:41   the app asks pool.ntp.org itself if that hasn't happened after 20 s
Hub myhub.azure-device
Hub: connected 3.2s
```

## 4. What it sends and receives

**Telemetry** (device to cloud, JSON, every 15 s by default):

```json
{"device":"az3166","uptime":125,"temperature":24.31,"humidity":41.20,"pressure":1012.84,
 "pressureSensorTemperature":25.10,"accel":{"x":0.012,"y":-0.020,"z":1.003},"gyro":{"x":0.35,"y":-0.26,"z":0.09},
 "magnetometer":{"x":-120.0,"y":310.5,"z":-402.0},"heading":111.1,"buttonA":false,"buttonB":false,
 "wifi":{"state":"Connected","ssid":"MyWiFi","rssi":-47,"ip":"192.168.1.50","gateway":"192.168.1.1", ...},
 "messages":13,"interval":15,"freeMemory":120000}
```

**Device twin**: reported properties `firmware`, `platform`, `app`, `mac`, `ip`, `ssid`, `sensors`, `telemetryInterval`;
desired property `telemetryInterval` (seconds, 1-3600) changes how often telemetry is sent.

**Direct methods** (the method name has to match exactly, case sensitive):

| Method | Payload | Result |
|---|---|---|
| `ShowText` | `"Hello"` | shows the text on the display for 5 s |
| `SetInterval` | `30` | telemetry interval in seconds |
| `GetTelemetry` | none | returns the current readings and sends them now |
| `Reboot` | none | restarts the device after 3 s |

**Cloud-to-device messages** pop up on the display.

### Trying it (Azure CLI with the azure-iot extension)

```
az iot hub monitor-events   --hub-name <hub> --device-id az3166
az iot hub invoke-device-method --hub-name <hub> --device-id az3166 --method-name ShowText --method-payload '"Hello from Azure"'
az iot hub device-twin update   --hub-name <hub> --device-id az3166 --desired '{"telemetryInterval": 30}'
az iot device c2d-message send  --hub-name <hub> --device-id az3166 --data "Hello"
```

The same works in the portal (device *Direct method*, *Device twin*, *Message to device*) and in Azure IoT Explorer.

## 5. Display

Seven pages, rotating every 8 s; **button A** = previous, **button B** = next (rotation pauses for 30 s after a press):
CLIMATE, MOTION (bubble level), COMPASS, WIFI (diagnostics), NETWORK (IP, mask, gateway, DNS, hub state), LOG (events), SYSTEM.
The header has Wi-Fi signal bars and a box that is filled with an "A" while connected to IoT Hub.

## 6. Troubleshooting

| On the display / Output | Meaning |
|---|---|
| `No time! TLS will fail` | no NTP reply: UDP port 123 blocked on your network, or no internet |
| `Hub: open failed` / `Hub: ...` followed by a TLS error | wrong host name, no internet, or TLS problem (paste the Output window text) |
| `Hub: connected` then `Hub: Disconnected` | the hub dropped the connection. Check the hub's **pricing tier**: the Azure client uses device twin and direct methods, which the **Basic** tier doesn't support (use Free F1 or Standard). Also: device disabled, or the hub's daily message quota used up |
| `Hub unreachable` | DNS or the TLS handshake failed |

The root certificates the TLS connection is verified against are in `Certificates.cs` (DigiCert Global Root G2 plus Microsoft
TLS RSA/ECC Root G2, checked against the live Azure certificate chains). Azure rotates its certificates from time to time:
if connections that worked suddenly fail with a certificate error, check Microsoft's list of Azure TLS root certificates and
update that file.
