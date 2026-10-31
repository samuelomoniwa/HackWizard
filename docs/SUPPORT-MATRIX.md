# Support matrix (knowledge base 2.0.0)

Generated from `Rules/kb`. Status: ✅ supported · ⚠️ partial · ⛔ unsupported (blocking if it leaves no usable GPU/disk).

## CPU / platform → Mac model (SMBIOS) → newest macOS

| CPU | Laptop SMBIOS | Desktop SMBIOS | iGPU (framebuffer) | Newest macOS |
|---|---|---|---|---|
| Intel Haswell (4th) | MacBookPro11,4 | iMac16,2 | ✅ 0x0A260005 / 0x0D220003 | Monterey |
| Intel Broadwell (5th) | MacBookPro12,1 | iMac16,2 | ✅ 0x16260006 / 0x16220007 | Monterey |
| Intel Skylake (6th) | MacBookPro13,1 / 13,3 | iMac17,1 | ✅ 0x19160000 / 0x19120000 | Monterey |
| Intel Kaby Lake (7th) | MacBookPro14,1 / 14,3 | iMac18,1 (iMac18,3 with dGPU) | ✅ 0x591B0000 / 0x59120000 | Ventura |
| Intel 8th–10th U (Kaby R/Whiskey/Comet U) | MacBookPro15,2 | — | ✅ 0x3EA50009 (+device-id 0x3E9B for UHD 620/Comet) | Sequoia |
| Intel Coffee Lake (8th/9th) | MacBookPro15,1 | iMac19,1 | ✅ 0x3EA50009 / 0x3E9B0007 | Sequoia |
| Intel Comet Lake (10th) | MacBookPro16,1 / 16,3 | iMac20,1 | ✅ 0x3EA50009 / 0x3E9B0007 (+spoof) | Tahoe (desktop/H) / Sequoia (U62) |
| Intel Ice Lake (10th) | MacBookAir9,1 | — | ✅ 0x8A520000 | Sequoia |
| Intel Rocket/Alder/Raptor/Arrow (11th–15th) | ⛔ no iGPU driver | MacPro7,1 (AMD GPU required) | ⛔ | Tahoe |
| AMD Ryzen Zen–Zen 4 | MacBookPro16,2 (Vega APU via NootedRed) | MacPro7,1 (AMD GPU) / iMac20,1 (Vega APU) | ⚠️ NootedRed / ⛔ RDNA2+ | Tahoe* |
| AMD Zen 5 | experimental | experimental | — | — |

\* Tahoe is not *recommended* where AppleALC analog audio is needed.

## Graphics (discrete)
✅ Polaris (RX 460–590), Vega 56/64, Radeon VII, Navi 10/14/21/23 (RX 5500–5700, RX 6600–6950) · ⛔ NVIDIA (disabled), Navi 22/24/3x, Intel Arc (disabled).

## Networking
✅ Intel I217–I219 (IntelMausi), Realtek RTL8111/8100/8125, Killer E2200–E2500, Aquantia AQC107/113 · ⚠️ Intel I210/I211/I225/I226 · ⚠️ Intel Wi-Fi 7260–AX211 (OpenIntelWireless) · ✅ Broadcom BCM94360/43602 (≤ Ventura) · ⚠️ BCM4350 (AirportBrcmFixup) · ⛔ Realtek/MediaTek/Qualcomm/Killer-Qualcomm Wi-Fi.

## Bluetooth
⚠️ Intel (IntelBluetoothFirmware) · ✅ Broadcom (BrcmPatchRAM) · ⛔ Realtek/MediaTek/Qualcomm.

## Audio
✅ Any HDA codec in AppleALC's database (Realtek, Conexant, IDT, Cirrus, VIA, Analog Devices); layouts read from AppleALC · ⛔ SOF/SST DSP-only audio.

## Storage
✅ SATA/AHCI, NVMe (+NVMeFix) · ⛔ Samsung PM981/PM991/PM9B1, Micron 2200S, SK hynix BC501/PC601/PC611, Intel Optane H10/H20 · ⛔ RST/RAID/VMD until switched to AHCI in BIOS.

## Input / other
✅ PS/2 keyboards and trackpads · ⚠️ I2C-HID trackpads · ✅ USB webcams · ⛔ fingerprint/IR cameras, PCIe card readers · ⚠️ Thunderbolt.
