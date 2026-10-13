# AZ3166 on Azure IoT Central (nanoFramework) + OLED dashboard

> **Updated for firmware 2.0.0.0** (nanoFramework rebuilt on the latest nf-interpreter `main`). The NuGet packages
> now match its native libraries (System.Net 100.2.0.14, System.Device.Wifi 100.0.6.6; the others are unchanged), so
> **this version doesn't deploy to the 1.x firmware**. For 1.x firmware use the samples of release `v1.2.0.0`.
> If Visual Studio rejects the packages, update the nanoFramework extension.

Connects the MXCHIP AZ3166 to **Azure IoT Central**: telemetry from every sensor, read-only and writable properties,
and commands, with the same OLED dashboard as the other samples.

How it connects (IoT Central doesn't give a device a fixed hub):

1. **DPS** (`global.azure-devices-provisioning.net:8883`, MQTT + TLS): the device registers with the *ID scope*, *device ID* and
   a key, and DPS answers with the IoT Hub that IoT Central assigned.
2. **IoT Hub** (that hub, port 8883, MQTT + TLS): telemetry, twin (properties) and commands, using a SAS token made from the same key.

No Azure client library is used (the app speaks MQTT itself, see `Sas.cs`), so it fits the 256 KB app area.

## 1. Firmware: the TLS build

Flash the **TLS** firmware 2.0.0.0 (`firmware/2.0.0.0-TLS`). The no-TLS firmware can't connect.
*Device Capabilities* in Visual Studio's Device Explorer should show the version you flashed.

## 2. IoT Central (once)

1. Create an application at <https://apps.azureiotcentral.com> (a custom application; the free trial works).
2. **Device templates -> New -> IoT device -> Next -> Skip** (or *Import a model*): choose **Import a model** and select
   `AZ3166-device-model.json` from this folder. Click **Publish** on the template.
   The template's id is `dtmi:azdevices:AZ3166;1`, the same as `ModelId` in `Program.cs`.
3. **Devices -> New**: pick the AZ3166 template, device ID `az3166` (lower case), **Create**.
4. Open the device and click **Connect**. Note **ID scope**, **Device ID** and **Primary key**.

   Instead of a key per device you can use the application's enrollment group: *Permissions -> Device connection groups ->
   SAS-IoT-Devices*, copy its **primary key** and use it as `GroupKey`. The app then derives each device's key from its device ID
   (`base64(HMAC-SHA256(groupKey, deviceId))`), so all boards can run the same build with a different `DeviceId`.

## 3. Configure and run

At the top of `Program.cs`:

| Setting | |
|---|---|
| `WifiSsid`, `WifiPassword` | your 2.4 GHz network |
| `IdScope` | e.g. `0ne00ABCDEF` |
| `DeviceId` | `az3166` (as created) |
| `DeviceKey` **or** `GroupKey` | the device's primary key, or the enrollment group's primary key (leave the other empty) |
| `ModelId` | leave as is; empty = connect without a template (data shows as *unmodeled*) |

Open `nfaz3166iotcentralsln.sln`, *Rebuild*, F5. The NuGet packages are included and match the firmware, don't update them.
Start up on the OLED / Output window: hardware, Wi-Fi, **time** (needed for the certificate check), `DPS ...`, `Assigned ...`,
`Hub: connected`, `Sub commands/c2d/twin/desired`. In IoT Central the device status changes to *Provisioned* and data flows in.

## 4. What IoT Central shows

| | |
|---|---|
| Telemetry (every 15 s) | `temperature`, `humidity`, `pressure`, `accel`, `gyro`, `magnetometer` (x/y/z), `heading`, `buttonA`, `buttonB`, `uptime`, `rssi`, `freeMemory` |
| Properties (read only) | `firmware`, `app`, `mac`, `ip`, `ssid` |
| Property (writable) | `telemetryInterval` in seconds (1-3600); the device acknowledges it (`ac`/`av` as IoT Central requires) |
| Commands | `ShowText` (text for the display), `GetTelemetry` (send now and return), `Reboot` |

Build dashboards from the device template's *Views*, or use the device's *Raw data* tab to see exactly what arrives.
Cloud-to-device messages (device *Command* in the raw-message API / IoT Hub) pop up on the display too.

## 5. Display

Seven pages rotating every 8 s; **button A** previous, **button B** next (rotation pauses 30 s after a press): CLIMATE, MOTION,
COMPASS, WIFI, NETWORK, LOG, SYSTEM. The header box shows "A" while connected to the hub.

## 6. Troubleshooting (see the LOG page or the Output window)

| Message | Meaning |
|---|---|
| `No time! TLS will fail` | no NTP reply: UDP 123 blocked or no internet |
| `DPS: Socket exception` / `-20864` | the TLS handshake ran out of memory (-20864 = mbedTLS `MD_ALLOC_FAILED`). The app trusts one root certificate at a time to keep the handshake small and logs the free memory (`DPS free ...K`) before each connection |
| `-9984` (`X509_CERT_VERIFY_FAILED`) | the server's certificate chain doesn't end at the root being tried; the app then tries the next of the three roots (`Root n/3 no match`, then `Root n/3 ok`). If all three fail, check the date/time and `Certificates.cs` |
| `DPS refused (n)` / `DPS error 401` | wrong ID scope, device ID or key (the key must belong to that device, or be the *group* key when set as `GroupKey`) |
| `DPS error 404` | the device isn't registered and there's no enrollment group that allows it: create the device in IoT Central |
| `DPS: assigning` repeating | DPS is still busy; it gives up after about a minute and retries |
| `DPS: disabled` / `failed` | the device is blocked, or the enrollment was removed |
| `Hub: refused` | device blocked/deleted in IoT Central, wrong key, or the clock is wrong |
| `Hub: connection closed` right after connecting | open the output window for the reason; check the device template matches `ModelId` |
| data shown as *unmodeled* | the template isn't published or its id differs from `ModelId` |

The TLS root certificates (`Certificates.cs`) are the DigiCert Global Root G2 and Microsoft TLS roots; DPS and IoT Hub chain to
DigiCert Global Root G2 today (checked with openssl for DPS). If Azure rotates its roots, update that file.
After a reboot the app asks DPS again (that returns the same hub within a few seconds), so nothing has to be stored on the device.

Don't commit your key: keep `DeviceKey`/`GroupKey` out of anything you share.
