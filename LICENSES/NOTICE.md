# Third-party components and licences

This firmware image contains the following. This is information, not legal advice: check the licences yourself before you redistribute.

| Component | Licence | Notes |
|---|---|---|
| nanoFramework nf-interpreter (CLR, PAL, HAL) | MIT (LICENSES/nanoFramework-LICENSE.md) | .NET Foundation and contributors |
| ChibiOS/RT and HAL 21.11.x | **GPL v3** (LICENSES/ChibiOS-GPL-3.0.txt) | Giovanni Di Sirio. **Copyleft: recipients of the binary may request the corresponding source** (the source changes are not in this repository: open an issue here to request them). |
| lwIP 2.1.3 | BSD-style (LICENSES/lwIP-COPYING.txt) | Swedish Institute of Computer Science |
| mbedTLS 3.6.7 | Apache-2.0 OR GPL-2.0-or-later (LICENSES/mbedTLS-LICENSE.txt) | Arm Limited, TLS build only |
| Wi-Fi driver library `libwiced_sdk_bin.a` (WICED Wi-Fi Host Driver) and the BCM43362 chip firmware/NVRAM inside it | **Proprietary, Broadcom/Cypress (now Infineon).** No licence text ships with it in the nf-interpreter repository. | Binary-only. It came with the nanoFramework MXCHIP_AZ3166 target. Its redistribution terms are unclear to the author of this package: check them (or ask nanoFramework/MXCHIP/Infineon) before distributing this firmware widely or commercially. |
| ST CMSIS/device headers, startup code | BSD-3-Clause / Apache-2.0 (see source headers) | STMicroelectronics, Arm |
| The Wi-Fi/MQTT/IoT Hub sample apps | They use nanoFramework NuGet packages (MIT) | Placeholders only, no credentials |

The changes made for this build are offered under the same licences as the files they modify.
