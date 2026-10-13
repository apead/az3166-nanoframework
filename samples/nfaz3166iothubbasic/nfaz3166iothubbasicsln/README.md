# AZ3166 Azure IoT Hub, plain MQTT version (works on every IoT Hub tier)

> **Updated for firmware 2.0.0.0** (nanoFramework rebuilt on the latest nf-interpreter `main`). The NuGet packages
> now match its native libraries (System.Net 100.2.0.14, System.Device.Wifi 100.0.6.6; the others are unchanged), so
> **this version doesn't deploy to the 1.x firmware**. For 1.x firmware use the samples of release `v1.2.0.0`.
> If Visual Studio rejects the packages, update the nanoFramework extension.

Same dashboard, sensors and telemetry as `nfaz3166iothub`, but **without the Azure client library**: the app talks MQTT over TLS to the
hub itself and builds its own SAS token from the device key. Because of that it can run on the **Basic** tier, which the Azure client
library can't (it always uses device twin and direct methods, which Basic doesn't have and which make the hub close the connection).

Needs the TLS firmware 2.0.0.0 (`firmware/2.0.0.0-TLS`).

## Configure (top of `Program.cs`)

| Setting | |
|---|---|
| `WifiSsid`, `WifiPassword` | your 2.4 GHz network |
| `IotHubHostName` | e.g. `myhub.azure-devices.net` |
| `DeviceId` | the device id registered in the hub |
| `SasKey` | the device's primary key (the app makes the SAS token from it) |
| `SasToken` | optional: a ready-made token, used instead of the key |
| `EnableDirectMethods`, `EnableTwin` | **leave `false` on the Basic tier** (see below) |

## Finding out what your hub accepts

1. First run with both switches `false`. You should see `Hub: connected`, `Sub devicebound`, and telemetry arriving
   (`az iot hub monitor-events --hub-name <hub> --device-id <id>`). This works on every tier.
2. Set `EnableDirectMethods = true`. The log shows `Sub methods` and then either stays connected or reports `Hub: connection closed`.
3. Set `EnableTwin = true` (reads the desired `telemetryInterval`, reports firmware/ip/sensors).

If the hub closes the connection right after one of those subscriptions, that feature isn't available on your tier.

## Topics and features

* Telemetry: `devices/{id}/messages/events/`, JSON with all sensors, buttons and Wi-Fi diagnostics every 15 s.
* Cloud-to-device messages (`devices/{id}/messages/devicebound/#`) are shown on the display.
* Direct methods (if enabled): `ShowText` (payload `"Hello"`), `SetInterval` (`30`), `GetTelemetry`, `Reboot`.
* Device twin (if enabled): desired `telemetryInterval`; reported firmware, app, mac, ip, ssid, sensors, telemetryInterval.

```
az iot hub monitor-events --hub-name <hub> --device-id <id>
az iot device c2d-message send --hub-name <hub> --device-id <id> --data "Hello"
az iot hub invoke-device-method --hub-name <hub> --device-id <id> --method-name ShowText --method-payload '"Hi"'
```

## Notes

* The SAS token is valid for 24 h; the app makes a new one at every (re)connect. If the key is wrong, the hub refuses the login
  (`Hub: refused (...)`).
* Root certificates for the TLS check are in `Certificates.cs` (DigiCert Global Root G2 and the Microsoft TLS roots).
* `Sas.cs` holds a small SHA-256/HMAC implementation; its algorithm was checked against Python's `hashlib`/`hmac`
  (it hasn't been compiled or run on the device).
