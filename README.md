# HackWizard

**HackWizard** is a free, open-source (MIT) Windows wizard that takes a beginner from a Windows PC to a bootable **OpenCore + macOS Recovery USB drive**, then gives firmware (BIOS/UEFI) instructions tailored to that PC.

It is **not tied to one machine**. It inspects any Windows PC, decides with an open, data-driven knowledge base what macOS can and cannot use on it, and either builds a matching EFI or explains clearly why that PC cannot run macOS usefully.

* Single-file, self-contained **x64 .NET 8 WPF** executable. Always elevated (`requireAdministrator`); UAC is never bypassed.
* Builds `config.plist` from **OpenCorePkg's own `Sample.plist`**, never from a third-party EFI. All components are downloaded at run time from their official projects; nothing proprietary is redistributed (see `THIRD-PARTY-NOTICES.md`).
* All hardware knowledge lives in `Rules/` (JSON + schema). The C# code contains no model-specific logic. Adding a new PC usually means adding rules, not code.

> ⚖️ Apple's macOS license permits macOS only on Apple-branded hardware. HackWizard makes you accept this before continuing.

## Which PCs work?

HackWizard runs on every Windows 10/11 PC. Whether it can *build a working macOS setup* depends on the hardware:

| Platform | Result |
|---|---|
| Intel 4th–10th gen laptops and desktops (Haswell → Comet Lake, Ice Lake) with their integrated graphics | ✅ Supported. macOS version chosen automatically (Haswell/Broadwell/Skylake iGPU → Monterey; Kaby Lake → Ventura; Coffee/Comet/Ice Lake → Sequoia) |
| Intel 11th–14th gen and Arrow Lake **desktops** with a supported AMD Radeon | ✅ Supported (CPUID spoof, MacPro7,1) |
| Intel 11th gen+ **laptops** (Tiger/Alder/Raptor/Meteor/Lunar Lake) | ⛔ Blocked: integrated Xe graphics has no macOS driver |
| AMD Ryzen **desktops** (Zen–Zen 4) with a supported AMD Radeon | ✅ Supported (AMD_Vanilla, MacPro7,1) |
| AMD Ryzen 2000–5000 **laptops / APUs** with Vega graphics | ⚠️ Supported via NootedRed (experimental) |
| AMD Ryzen 6000+ laptops (RDNA2/3 iGPU) | ⛔ Blocked: no macOS graphics driver |
| ARM PCs (Snapdragon), Legacy-BIOS-only PCs, Intel ≤ 3rd gen, Atom/Celeron N, AMD FX | ⛔ Blocked, with an explanation |
| NVIDIA GPUs | Disabled in macOS. Fine if another supported GPU drives the screen |

The full component matrix (Wi-Fi, Bluetooth, Ethernet, audio, storage…) is in [`docs/SUPPORT-MATRIX.md`](docs/SUPPORT-MATRIX.md).

## Architecture

| Project | Responsibility |
|---|---|
| **HackWizard.Core** | Preflight checks (incl. ARM64 detection); read-only hardware detection (WMI/CIM, PnP properties, CPUID, DSDT probes, LPC/EC/CPU/AWAC ACPI paths); knowledge-base loader and a **generic rules engine** (capabilities, groups, SMBIOS selection, macOS gating); plist reader/writer/diff; config builder; kext ordering; AMD_Vanilla patcher; AppleALC layout resolver; AML encoder and **SSDT generator** (EC-USBX, PLUG, AWAC, PMC, RHUB, PNLF, ALS0, XOSI); HTML/PDF reports and BIOS guide; Lenovo WMI. |
| **HackWizard.Downloads** | GitHub releases and source archives (rate-limit aware, digest-verified), resumable cached downloads, offline mode, **C# port of `macrecovery.py`** and chunklist verification. |
| **HackWizard.DiskWriter** | USB-only disk enumeration with layered safety checks, diskpart partitioning, **native FAT32 formatter**, GPT writer (simulation), copy and verify. |
| **HackWizard.App** | WPF MVVM shell (9 steps) and `EfiPipeline` orchestration. |
| **HackWizard.Tests** | Profile tests for 10 PCs (cross-checked against the Python simulator), golden `config.plist` snapshots, ocvalidate integration, low-level tests. |
| **tools/kb_tool.py** | Knowledge-base lint, **simulator** (`simulate hardware-report.json`), expected-result generator, manifest hashing. |

```
HackWizard/
├─ build.ps1  HackWizard.sln  LICENSE  README.md  CONTRIBUTING.md  DECISIONS.md  THIRD-PARTY-NOTICES.md
├─ .github/   workflows/ci.yml, issue templates (hardware report, bug), PR template
├─ Rules/     manifest.json, schema/rules.schema.json,
│             kb/ rules-00-platform, 10-cpu, 20-smbios, 30-gpu, 40-network, 50-storage, 60-audio, 70-misc, 90-models,
│                 sources.json, macos.json, models.json (BIOS guides), cpu-codenames.json
├─ src/       HackWizard.Core / Downloads / DiskWriter / App
├─ tests/     HackWizard.Tests (Fixtures/Profiles/*.hardware.json + *.expected.json, Golden/, AppleALC fixture)
├─ tools/     kb_tool.py, Update-RulesManifest.ps1, New-ManifestKey.ps1, Sign-Manifest.ps1
└─ docs/      SUPPORT-MATRIX.md, TEST-PLAN.md
```

### Wizard flow
1. **Preflight**: architecture (ARM64 is rejected), admin, internet, free space, UEFI, Secure Boot, BitLocker, TPM, power, legal checkbox.
2. **Hardware detection**: JSON and HTML report.
3. **Compatibility**: traffic-light table, Mac model (SMBIOS), recommended macOS (newest version that the SMBIOS and every component support), blocking reasons and warnings.
4. **ACPI**: dump, then SSDTs generated from this PC's real ACPI paths.
5. **Downloads**: OpenCore, the kexts this PC needs (version-matched to the chosen macOS), AppleALC codec database, AMD_Vanilla (AMD only), macOS Recovery.
6. **EFI build**: Sample.plist → rules → ocvalidate (debug and release configs) → diff.
7. **USB writer**: dry run, image simulation, or real write with all safety checks.
8. **BIOS guide**: model profile (e.g. ThinkPad T495, any ThinkPad) or vendor keys plus generic settings filtered for this CPU and form factor; optional Lenovo WMI apply.
9. **Final checklist** and **Restart into BIOS**.

## Building

Requirements: Windows 10/11 x64, **.NET 8 SDK**, Python 3 + `pip install jsonschema` (recommended), Windows SDK for signing.

```powershell
.\build.ps1 -ApproveSnapshots     # first build only: creates tests/.../Golden/*.config.plist — review and commit them
.\build.ps1                       # lint KB, build, unit tests, publish
.\build.ps1 -IntegrationTests     # + ocvalidate on every test profile with the latest OpenCore
.\build.ps1 -Sign -CertThumbprint <sha1>
```

Publish uses exactly `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`. Output goes to `artifacts\publish\HackWizard.exe` plus `.sha256`. CI (GitHub Actions) runs the same steps and uploads the EXE.

## Using HackWizard

1. **Back up everything.** Installing macOS erases the internal disk. Save your BitLocker recovery key if encryption is on.
2. Connect power and internet, and insert a USB stick of 8 GB or more.
3. Run `HackWizard.exe` and click **Yes** on the UAC prompt. Follow the steps.
4. If the Compatibility step shows a red box, read it: it explains exactly why (and, for desktops, what to add, e.g. a supported Radeon card).
5. At the end: connect wired internet (Ethernet, a supported USB-Ethernet adapter, or an iPhone's USB hotspot), click **Restart into BIOS**, apply the listed settings, boot the USB from the boot menu, erase the internal disk as APFS in Disk Utility, and install.
6. After installing, follow `Utilities/Post-Install-Guide.html` on the USB.

Logs are in `%ProgramData%\HackWizard\logs` (also copied to the USB). **Copy diagnostics** excludes serials and MACs.

## Helping more PCs work

Every PC that works (or fails) makes the knowledge base better. See **[CONTRIBUTING.md](CONTRIBUTING.md)**. In short: attach your `hardware-report.json` to a *Hardware report* issue, or add rules plus a test profile and run `python tools/kb_tool.py simulate your-report.json`.

## Known limitations

* **AMD laptop sleep** is often broken. Many 10th-gen+ Intel laptops only support Windows Modern Standby, so sleep may not work there either.
* **NootedRed is experimental** (AMD Vega iGPUs): occasional crashes and Chromium artefacts. Needs ≥ 512 MB UMA frame buffer.
* **No AirDrop/Handoff/Sidecar/Continuity with Intel Wi-Fi.** On Sequoia and later, Intel Wi-Fi uses itlwm + HeliPort. Broadcom native cards lose Wi-Fi from Sonoma onward without root patches (not handled).
* **Online-recovery install requires a wired connection**: Wi-Fi does not work in the installer.
* Audio: layout-ids come from AppleALC's database. The first one is tried, and others are listed for manual testing. Tahoe is expected to lack analog audio (AppleHDA reported removed).
* I2C trackpads may need GPIO pinning. B550/A520 boards may need SSDT-CPUR. Intel I225/I226/I211 Ethernet may need third-party drivers. These are flagged, not automated.
* Fingerprint readers, IR cameras, most SD readers, Thunderbolt hot-plug and NVIDIA GPUs do not work.
* Image simulation writes GPT/FAT32 structures but mirrors files beside the image.
