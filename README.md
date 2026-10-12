# .NET nanoFramework on the MXCHIP AZ3166 (EMW3166), with Wi-Fi

An **unofficial community build** of [.NET nanoFramework](https://nanoframework.net) for the MXCHIP AZ3166 IoT DevKit
(STM32F412RG + BCM43362 Wi-Fi over SDIO) **with working Wi-Fi**, plus sample apps that use it: Wi-Fi, MQTT, Azure IoT Hub and Azure IoT Central, all with an OLED dashboard.

> Not affiliated with or supported by the nanoFramework project, MXCHIP or Microsoft. Use at your own risk.
> Upstream nanoFramework currently supports this board without Wi-Fi. This repository carries the Wi-Fi work until (and unless) it is contributed upstream.

## What is here

| Folder | |
|---|---|
| [`firmware/1.2.0.0-TLS`](firmware/1.2.0.0-TLS) | Firmware **with TLS** (mbedTLS 3.6.0): HTTPS, MQTT over TLS, Azure IoT Hub / Central. Needs most of the flash (CLR 654 KB of 720 KB) |
| [`firmware/1.2.0.0-NoTLS`](firmware/1.2.0.0-NoTLS) | Firmware **without TLS**: Wi-Fi and plain TCP/UDP (e.g. MQTT on port 1883). Smaller (CLR 484 KB) |
| [`samples/`](samples) | Five Visual Studio solutions with their NuGet packages included (see below) |
| [`LICENSES/`](LICENSES) | Licences and notices of the components in the firmware |

Both firmwares: 1.2.0.0, CLR stream 1.14.0, based on `nanoframework/nf-interpreter` commit `5ee25586` (31 Oct 2025). Checksums are in each folder's `SHA256SUMS`.

## Quick start

1. **Pick a firmware.** Anything that talks to Azure or HTTPS needs the **TLS** one. If you only need Wi-Fi or plain MQTT take the **NoTLS** one.
2. **Flash it** (STM32CubeProgrammer, free from st.com). The on-board ST-LINK is used; no boot-mode buttons are needed.
   * Connect the board, choose **ST-LINK**, **Connect** (if that fails: *Mode = Under reset*).
   * Program the **nanoBooter** `.hex`, then the **nanoCLR** `.hex` (tick *Verify programming*), disconnect and press RESET.
   * Command line: `STM32_Programmer_CLI -c port=SWD -d nanoBooter-1.2.0.0-TLS.hex -v -d nanoCLR-1.2.0.0-TLS.hex -v -rst`
   * Do a **full chip erase** first if the board ran a different firmware (for example the original MXCHIP one).
   * Flash layout: nanoBooter 0x08000000 (32 KB), configuration 0x08008000 (16 KB), nanoCLR 0x0800C000 (720 KB), your app 0x080C0000 (256 KB).
   * Check: Visual Studio, Device Explorer, *Device Capabilities*: CLR version **1.2.0.0**.
   * If an app hangs the board at start: hold **button A** while pressing RESET, then *Erase App* in Device Explorer (or erase sectors 10-11).
3. **Run a sample.** Install the [nanoFramework Visual Studio extension](https://marketplace.visualstudio.com/items?itemName=nanoframework.nanoFramework-VS2022-Extension)
   (**v2022.14.1.5 was used for this build**; newer ones may reject these package versions), open a sample's `.sln`, edit the settings at the top of `Program.cs`, *Rebuild*, F5.
   **Do not update the NuGet packages**: they have to match the native libraries in the firmware (mscorlib 100.5.0.24, System.Net 100.2.0.11, System.Device.Wifi 100.0.6.4, System.Device.Gpio 100.1.0.6, System.Device.I2c 100.0.0.2).

## Samples

| Sample | What it does | Firmware | Status |
|---|---|---|---|
| [`nfaz3166wifi`](samples/nfaz3166wifi) | Scan, connect, DHCP, DNS: a Wi-Fi smoke test | either | works on hardware |
| [`nfaz3166mqtt`](samples/nfaz3166mqtt) | All sensor telemetry to an MQTT broker (port 1883) plus an OLED dashboard (7 pages), commands over MQTT | either | works on hardware (tested with Mosquitto / Node-RED) |
| [`nfaz3166iothub`](samples/nfaz3166iothub) | Azure IoT Hub with the Azure client library (twin, direct methods) | TLS | TLS handshake and login work. Needs a Free or Standard hub (Basic has no twin / direct methods, the hub closes the connection). End-to-end telemetry not yet confirmed |
| [`nfaz3166iothubbasic`](samples/nfaz3166iothubbasic) | Azure IoT Hub over plain MQTT/TLS with its own SAS token (works on the Basic tier) | TLS | written, **not yet run on hardware** |
| [`nfaz3166iotcentral`](samples/nfaz3166iotcentral) | Azure IoT Central: provisioning (DPS), telemetry, writable property, commands; includes the device template `AZ3166-device-model.json` | TLS | **in progress**: runs up to the TLS connection to the provisioning service; the latest version (1.0.2) has not been run yet, the rest is untested |

Every sample contains only placeholder credentials. Put your own Wi-Fi name/password, hub keys and so on into your local copy and **don't commit them**.

## Tested

On one AZ3166 board: Wi-Fi scan, WPA2 connect, DHCP, DNS, SNTP time, reconnect/auto-connect at boot, per-board MAC address, OLED, the four I2C sensors
(HTS221, LPS22HB, LSM6DSL, LIS2MDL), two buttons, plain MQTT, and a TLS 1.2 handshake plus MQTT login to an Azure IoT Hub.
The 1.2.0.0 images are built from the same code as the 1.0.0.9 (no TLS) and 1.1.2.0 (TLS) builds that were tested; the 1.2.0.0 images themselves have not been flashed by the author yet.
**Not tested:** long-term stability, power saving, WPA-TKIP/WEP, ADC/DAC/CAN/SD card (not in these builds).

## Known limits

* Wi-Fi: 2.4 GHz, open / WPA / WPA2 personal only. No WEP, WPA-Enterprise, WPA3 or soft AP.
* TLS 1.2 client only (no TLS 1.3), ECDHE with RSA/ECDSA certificates, AES-GCM or AES-CBC, P-256 and P-384. Trimmed to fit the flash; no server mode, no PSK.
* The Wi-Fi chip's "data ready" interrupt (PC0) never fires on this board, so the driver polls every 20 ms.
  **EXTI line 0 is therefore taken: GPIO interrupts on pin 0 of any port (PA0, PB0, ...) can't be used.**
* Temporary diagnostics are still in the firmware: the RGB LED blinks blue for each frame sent and green for each frame received (red = Wi-Fi chip failed to start),
  and `[WiFi]` / `[TLS]` lines are written to the debug output while connecting.
* The TLS firmware leaves little free native memory: a TLS handshake that trusts several root certificates at once can run out of memory (mbedTLS error -20864). Trust one root at a time, as the IoT Central sample does.
* A second `I2cDevice` on the same bus used to crash upstream's ChibiOS I2C code (null pointer, the whole CLR froze). This firmware contains the fix.

## Firmware source

The firmware source changes are **not** part of this repository. This repository only holds the built firmware and the sample apps.
The firmware is based on `nanoframework/nf-interpreter` commit `5ee25586` (31 Oct 2025) with Wi-Fi added for this board. Newer nf-interpreter versions have moved to Kconfig and a shared Wi-Fi framework, so a port of the changes to current upstream is planned.

## Licences

* **Firmware binaries** combine nanoFramework (MIT), **ChibiOS (GPL v3)**, lwIP (BSD), mbedTLS (Apache-2.0, TLS build only) and the **Broadcom BCM43362 driver library and firmware (proprietary)**
  that ships with the upstream MXCHIP_AZ3166 target. Because of ChibiOS the firmware as a whole falls under GPL v3 terms. Details and licence texts: [`LICENSES/NOTICE.md`](LICENSES/NOTICE.md).
  I have not obtained separate permission from Broadcom/Cypress for the proprietary parts; they are redistributed as they appear in nanoFramework's own repository.
* **Sample apps**: no licence has been chosen yet, so by default all rights are reserved. A licence file will be added here.
