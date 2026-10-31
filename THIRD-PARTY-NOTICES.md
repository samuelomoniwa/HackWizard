# Third-party components

HackWizard's repository and executable contain **only HackWizard's own code (MIT)**. The components below are **downloaded at run time** on the user's computer, directly from their official sources (`Rules/kb/sources.json`), and are used under their own licenses. Check each project's license before redistributing a USB image or EFI built by HackWizard.

| Component | Source | License (see upstream) |
|---|---|---|
| OpenCorePkg (OpenCore, drivers, macserial, ocvalidate) | acidanthera/OpenCorePkg | BSD-3-Clause |
| Lilu, VirtualSMC, WhateverGreen, AppleALC, VoodooPS2, BrightnessKeys, RestrictEvents, NVMeFix, BrcmPatchRAM, AirportBrcmFixup | acidanthera | BSD-3-Clause |
| IntelMausi | acidanthera/IntelMausi | GPL-2.0 |
| HfsPlus.efi | acidanthera/OcBinaryData | Apple; redistributed by acidanthera |
| NootedRed | ChefKissInc/NootedRed | ChefKiss Open Knowledge License v1 (restrictive — read before redistributing) |
| itlwm / AirportItlwm, IntelBluetoothFirmware, HeliPort | OpenIntelWireless | GPL-2.0 / ISC / BSD-3-Clause (per project) |
| VoodooI2C | VoodooI2C/VoodooI2C | GPL-2.0 |
| ECEnabler | 1Revenge1/ECEnabler | see upstream |
| RealtekRTL8111, RealtekRTL8100, LucyRTL8125Ethernet, AtherosE2200Ethernet | Mieze | GPL-2.0 |
| AMD_Vanilla patches | AMD-OSX/AMD_Vanilla | see upstream |
| USBToolBox | USBToolBox/tool, USBToolBox/kext | see upstream |
| macOS Recovery | Apple (osrecovery.apple.com) | Apple macOS license |

HackWizard's `macrecovery` implementation is an independent C# port of the protocol used by OpenCorePkg's `macrecovery.py` (BSD-3-Clause).
