# Test plan

## Automated (CI on every PR)
| Job | What |
|---|---|
| knowledge-base (Linux) | `kb_tool.py lint` (JSON Schema, duplicate ids, every kext has a source, every SMBIOS exists in macos.json); `expect` and `manifest` must leave the tree unchanged. |
| build-test (Windows) | Build; xUnit; publish; ocvalidate integration against the latest OpenCore release; uploads `HackWizard.exe`. |

### xUnit suites
| Suite | Covers |
|---|---|
| `ProfileTests` | 10 PCs — ThinkPad T495 (Ryzen Picasso), Coffee Lake desktop, Comet Lake U laptop (I2C, Killer AX1650, BC501 + SATA), Alder Lake desktop + RX 6600 XT, Alder Lake laptop (blocked), Haswell laptop (Monterey only), Ryzen 5600X + RX 580, Ryzen 6850U laptop (blocked), Snapdragon (blocked), RAID-only (blocked). C# result must equal the Python simulator's `.expected.json`; determinism. |
| `RulesEngineTests` | Intel vs AMD quirks, CPUID spoof, MacPro7,1 with dGPU, headless iGPU, NVIDIA disabled not blocking, Haswell → Monterey, legacy BIOS, bad NVMe with alternative, wildcard PCI ids, codenames. |
| `ConfigBuilderTests` | Golden `config.plist` for 6 profiles; Dortania quirk assertions per platform; XOSI rename; Intel Wi-Fi variant per macOS; duplicate VoodooInput disabled; release toggle. |
| `OcValidateIntegrationTests` | Real Sample.plist + AMD_Vanilla → ocvalidate (debug + release) for each buildable profile. |
| `PlistTests` | Round-trip, ordering, escaping, schema-drift guard, every rule config path exists in Sample. |
| `AmlTests` | AML encodings, resource descriptors, valid SSDT tables for every profile, EC/PLUG/AWAC(both)/PMC/RHUB generation and skip-with-warning. |
| `KextAndPatchTests` | Kext ordering, cycles, Info.plist parsing, AMD core-count patching, AppleALC layout resolution. |
| `DiskTests`, `DownloadTests`, `MiscTests` | FAT32/GPT/CRC/diskpart, macrecovery/chunklist/GitHub parsing, device paths, CPUID decode (Intel+AMD), device categorisation, KB integrity, BIOS guide filtering (model, generic ThinkPad, AMD desktop, Intel laptop), PDF. |

## Manual matrix (before a release)
| # | Scenario | Expected |
|---|---|---|
| M1 | Non-admin launch / UAC declined | App does not start / Preflight ✖ |
| M2 | Windows on ARM | Preflight ✖ architecture, Compatibility blocked |
| M3 | Legacy/CSM Windows | UEFI ✖, Compatibility blocked |
| M4 | Detection on: Intel laptop, Intel desktop + AMD GPU, AMD laptop, AMD desktop, a Surface/Snapdragon | Report lists GPU PCI ids + paths, LPC/EC/CPU ACPI paths, codec + HDA path, disk bus types |
| M5 | Offline mode with empty cache / interrupted download | Clear error / resume |
| M6 | GitHub rate limit | Wait or token hint |
| M7 | USB list with internal NVMe, USB HDD (system), 4 GB stick, 32 GB stick | Only the 32 GB stick |
| M8 | Wrong confirm number / stick swapped | Refused / aborted before writing |
| M9 | Dry run, Simulate, Real write on 64 GB stick | As described; 16 GB FAT32; verified |
| M10 | Lenovo WMI opt-in (with/without supervisor password) | Before/after table; nothing until Apply + confirm |
| M11 | Restart into BIOS on Lenovo, HP, Dell, ASUS desktop | Firmware UI opens |
| M12 | Boot + install on ≥ 1 machine per platform family (Intel laptop, Intel desktop, AMD desktop, AMD Vega laptop) | Installer reaches Disk Utility; install completes; post-install script works |
| M13 | Keyboard-only + light/dark theme | All controls reachable; readable |
