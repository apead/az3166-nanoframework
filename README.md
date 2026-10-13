# .NET nanoFramework on the MXCHIP AZ3166 (EMW3166), with Wi-Fi

An **unofficial community build** of [.NET nanoFramework](https://nanoframework.net) for the MXCHIP AZ3166 IoT DevKit
(STM32F412RG + BCM43362 Wi-Fi over SDIO) **with Wi-Fi**, plus sample apps that use it: Wi-Fi, MQTT, Azure IoT Hub and Azure IoT Central, all with an OLED dashboard.

> Not affiliated with or supported by the nanoFramework project, MXCHIP or Microsoft. Use at your own risk.
> Upstream nanoFramework supports this board without Wi-Fi. This repository carries the Wi-Fi work until (and unless) it is contributed upstream.

## What is here

| Folder | |
|---|---|
| [`firmware/2.0.0.0-TLS`](firmware/2.0.0.0-TLS) | Firmware **with TLS**: HTTPS, MQTT over TLS, Azure IoT Hub / IoT Central |
| [`firmware/2.0.0.0-NoTLS`](firmware/2.0.0.0-NoTLS) | Firmware **without TLS**: Wi-Fi and plain TCP/UDP (e.g. MQTT on port 1883), smaller |
| [`samples/`](samples) | Five Visual Studio solutions with their NuGet packages included (see below) |
| [`LICENSES/`](LICENSES) | Licences and notices of the components in the firmware |

Checksums are in each firmware folder's `SHA256SUMS`.

## Firmware 2.0.0.0

Built from the latest `nanoframework/nf-interpreter` (`main`, October 2026) with Wi-Fi support for this board added.

| | 2.0.0.0 TLS | 2.0.0.0 NoTLS |
|---|---|---|
| Wi-Fi, DHCP, DNS, SNTP time, TCP/UDP sockets | yes | yes |
| Plain MQTT (port 1883), HTTP | yes | yes |
| TLS 1.2 client (HTTPS, MQTT over TLS port 8883, Azure IoT Hub / Central) | yes | no |
| nanoCLR size (of 720 KB) | 662 KB | 488 KB |
| Flash for your app | 256 KB | 256 KB |

**Inside:** ChibiOS 21.11.x, lwIP 2.1.3, mbedTLS 3.6.7 (TLS build), built with the Arm GNU Toolchain 15.2.rel1.

**Native libraries** (the NuGet packages your app uses must match them):

| Library | Native version | NuGet package |
|---|---|---|
| mscorlib | 100.5.0.24 | nanoFramework.CoreLibrary 1.17.11 |
| System.Net | 100.2.0.14 | nanoFramework.System.Net 1.11.66 |
| System.Device.Wifi | 100.0.6.6 | nanoFramework.System.Device.Wifi 1.5.152 |
| System.Device.Gpio | 100.1.0.6 | nanoFramework.System.Device.Gpio 1.1.64 |
| System.Device.I2c | 100.0.0.2 | nanoFramework.System.Device.I2c 1.1.29 |
| nanoFramework.Runtime.Events | 100.0.8.0 | nanoFramework.Runtime.Events 1.11.39 |
| nanoFramework.Runtime.Native | 100.0.10.0 | nanoFramework.Runtime.Native 1.7.11 |
| nanoFramework.System.Collections | 100.0.2.0 | nanoFramework.System.Collections 1.5.75 |

Also included: System.Math, System.Device.Pwm, System.Device.Spi, System.IO.Ports, nanoFramework.System.Text, nanoFramework.Hardware.Stm32,
nanoFramework.Networking.Sntp. Not included: ADC, DAC, CAN, SD card, file system.

**Changes from 1.2.0.0:**
* Based on the current nanoFramework (Kconfig build configuration), mbedTLS 3.6.0 → 3.6.7.
* Newer System.Net (100.2.0.14) and System.Device.Wifi (100.0.6.6) native libraries: apps need the matching NuGet packages (the samples here have them).
* The Wi-Fi driver follows upstream's structure for Wi-Fi chips (it sits next to the Pico W and ISM43362 drivers).
* No debug output from the Wi-Fi driver and no LED blinking (1.x blinked the RGB LED for each frame and wrote `[WiFi]` / `[TLS]` lines).
* Same flash layout: moving between 1.2.0.0 and 2.0.0.0 keeps the stored Wi-Fi network.

## Quick start

1. **Pick a firmware.** Anything that talks to Azure or HTTPS needs the **TLS** one. For Wi-Fi and plain MQTT the **NoTLS** one is enough.
2. **Flash it** with STM32CubeProgrammer (free from st.com). The on-board ST-LINK is used; no boot-mode buttons are needed.
   * Connect the board, choose **ST-LINK**, **Connect** (if that fails: *Mode = Under reset*).
   * *Erasing & Programming*: program the **nanoBooter** `.hex`, then the **nanoCLR** `.hex` (tick *Verify programming*), then press RESET.
   * Or from the command line, in the firmware folder:
     ```
     STM32_Programmer_CLI -c port=SWD -d nanoBooter-2.0.0.0-TLS.hex -v -d nanoCLR-2.0.0.0-TLS.hex -v -rst
     ```
     (NoTLS: `nanoBooter-2.0.0.0-NO-TLS.hex` and `nanoCLR-2.0.0.0-NO-TLS.hex`)
   * Do a **full chip erase** first (`STM32_Programmer_CLI -c port=SWD -e all`) if the board ran a different firmware, for example the original MXCHIP one.
   * Flash layout: nanoBooter 0x08000000 (32 KB), configuration 0x08008000 (16 KB), nanoCLR 0x0800C000 (720 KB), your app 0x080C0000 (256 KB).
   * Check: Visual Studio, Device Explorer, *Device Capabilities*: CLR version **2.0.0.0**.
   * If an app hangs the board at start: hold **button A** while pressing RESET, then *Erase App* in Device Explorer (or erase sectors 10-11).
3. **Run a sample.** Install the [nanoFramework Visual Studio extension](https://marketplace.visualstudio.com/items?itemName=nanoframework.nanoFramework-VS2022-Extension),
   open a sample's `.sln`, edit the settings at the top of `Program.cs`, *Rebuild*, F5.
   **Don't update the NuGet packages**: they match the native libraries above.

Don't use `nanoff --update` for this board: it installs the official firmware, which has no Wi-Fi.

## Samples

| Sample | What it does | Firmware |
|---|---|---|
| [`nfaz3166wifi`](samples/nfaz3166wifi) | Scan, connect, DHCP, DNS: a Wi-Fi smoke test | either |
| [`nfaz3166mqtt`](samples/nfaz3166mqtt) | All sensor telemetry to an MQTT broker (port 1883), an OLED dashboard (7 pages) and commands over MQTT | either |
| [`nfaz3166iothub`](samples/nfaz3166iothub) | Azure IoT Hub with the Azure client library (telemetry, device twin, direct methods). Needs a Free or Standard hub (Basic has no twin / direct methods) | TLS |
| [`nfaz3166iothubbasic`](samples/nfaz3166iothubbasic) | Azure IoT Hub over plain MQTT/TLS with its own SAS token; works on every hub tier, including Basic | TLS |
| [`nfaz3166iotcentral`](samples/nfaz3166iotcentral) | Azure IoT Central: provisioning (DPS), telemetry, writable property, commands; includes the device template `AZ3166-device-model.json` | TLS |

Every sample contains only placeholder credentials. Put your own Wi-Fi name/password, hub keys and so on into your local copy and **don't commit them**.
Each sample has its own README with the setup steps.

## Known limits

* Wi-Fi: 2.4 GHz, open / WPA / WPA2 personal only. No WEP, WPA-Enterprise, WPA3 or soft AP.
* TLS 1.2 client only (no TLS 1.3), ECDHE with RSA/ECDSA certificates, AES-GCM or AES-CBC, P-256 and P-384. Trimmed to fit the flash; no server mode, no PSK.
* The Wi-Fi chip's "data ready" interrupt (PC0) doesn't fire on this board, so the driver checks the chip every 20 ms.
  **EXTI line 0 is therefore taken: GPIO interrupts on pin 0 of any port (PA0, PB0, ...) can't be used.**
* Native memory is tight with TLS: a handshake that trusts several root certificates at once can run out of memory (mbedTLS error -20864).
  Trust one root at a time, as the IoT Central sample does.
* Includes a fix for upstream's ChibiOS I2C code: a second `I2cDevice` on the same bus no longer crashes the CLR (the OLED and the sensors share I2C1).

## Previous version

Firmware 1.2.0.0 (based on nf-interpreter `5ee25586`, October 2025) and the samples for it are in the release tag [`v1.2.0.0`](https://github.com/apead/az3166-nanoframework/tree/v1.2.0.0).
The 1.2.0.0 samples don't deploy to 2.0.0.0 and the 2.0.0.0 samples don't deploy to 1.2.0.0 (different System.Net / System.Device.Wifi native versions).

## Firmware source

The firmware source changes are **not** part of this repository. This repository holds the built firmware and the sample apps.

## Licences

* **Firmware binaries** combine nanoFramework (MIT), **ChibiOS (GPL v3)**, lwIP (BSD), mbedTLS (Apache-2.0, TLS build only) and the **Broadcom BCM43362 driver library and firmware (proprietary)**
  that ships with the upstream MXCHIP_AZ3166 target. Because of ChibiOS the firmware as a whole falls under GPL v3 terms. Details and licence texts: [`LICENSES/NOTICE.md`](LICENSES/NOTICE.md).
  I have not obtained separate permission from Broadcom/Cypress for the proprietary parts; they are redistributed as they appear in nanoFramework's own repository.
* **Sample apps**: no licence has been chosen yet, so by default all rights are reserved.
