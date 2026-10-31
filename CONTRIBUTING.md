# Contributing to HackWizard

Thanks for helping more PCs work! Most contributions are **knowledge-base changes** (JSON) and need no C#.

## 1. Share a hardware report (easiest)
Run HackWizard to step 2, then open a **Hardware report** issue and attach `%ProgramData%\HackWizard\reports\hardware-report.json`. Say what worked if you installed macOS (graphics, audio layout, Wi-Fi, sleep, trackpad, USB).

## 2. Add or fix rules
1. Read `Rules/schema/rules.schema.json`. Rules are matched in `priority` order (lower first). Important concepts:
   * `scope`: `device` (PCI/USB/HDA devices), `disk`, `cpu`, `system`, `always`.
   * `group`: device rules in the same group are exclusive per device (first match wins). Put specific rules (e.g. a device-id spoof) at a lower priority than generic ones.
   * `provides` / `requires` / `recommends`: capabilities like `gpu`, `gpu-dgpu`, `net-install`. `hasCapability` / `lacksCapability` let later rules react (e.g. headless iGPU when an AMD card is present).
   * `smbios`: the highest-priority match wins; macOS versions are gated by `macos.json → supportedSmbios`.
   * `macos.maxDarwin`: last supported Darwin major (21 = Monterey).
   * `configPatches`: dotted paths that **must exist** in OpenCore's `Sample.plist`.
   * `kexts`: names provided by `sources.json`, or `@Variant` resolved per macOS version.
2. Cite your source (Dortania guide page, upstream README, or a hardware-report issue) in the PR.
3. Add a test profile: copy your `hardware-report.json` to `tests/HackWizard.Tests/Fixtures/Profiles/<name>.hardware.json` (remove serials/MACs).
4. Check the result without .NET:
   ```
   pip install jsonschema
   python tools/kb_tool.py lint
   python tools/kb_tool.py simulate tests/HackWizard.Tests/Fixtures/Profiles/<name>.hardware.json
   python tools/kb_tool.py expect      # regenerate expected results for all profiles — review the diff!
   python tools/kb_tool.py manifest
   ```
5. If you have Windows + .NET 8: `./build.ps1` (and `-IntegrationTests` to run ocvalidate).

## 3. Code changes
* Keep hardware knowledge out of C#. If you need a new kind of condition, add it to `KbMatch` **and** to `tools/kb_tool.py` **and** to the schema.
* If you change the rules engine, update `tools/kb_tool.py` to match; the profile tests enforce parity.
* Safety rules for the disk writer are non-negotiable: never list or write non-USB disks; re-validate the target before every destructive step.
* Never bypass UAC, Secure Boot or other security controls. BIOS changes only via documented vendor interfaces, opt-in and confirmed.

## Upstream-only policy
Kexts, drivers and tools must come from their official projects at run time. Do not commit binaries, prebuilt EFIs or macOS images.
