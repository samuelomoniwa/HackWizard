#!/usr/bin/env python3
"""HackWizard knowledge-base tool (no .NET needed).

  python tools/kb_tool.py lint                       # schema + cross-reference checks (used by CI)
  python tools/kb_tool.py simulate report.json [...] # evaluate hardware-report.json files like the app does
  python tools/kb_tool.py manifest [--version X.Y.Z] # recompute Rules/manifest.json hashes
  python tools/kb_tool.py expect                     # regenerate tests/.../Profiles/*.expected.json after an intended rule change

The evaluator mirrors src/HackWizard.Core/Rules/RulesEngine.cs; tests/HackWizard.Tests checks the C# side against the
same fixtures, so contributors can iterate on rules quickly and the two implementations keep each other honest."""
import hashlib, json, pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
RULES = ROOT / "Rules"

def load_kb():
    kb = {"rules": []}
    for f in sorted((RULES / "kb").glob("rules-*.json")):
        kb["rules"] += json.loads(f.read_text(encoding="utf-8"))["rules"]
    for n in ("sources", "macos", "models", "cpu-codenames"):
        kb[n] = json.loads((RULES / "kb" / f"{n}.json").read_text(encoding="utf-8"))
    return kb

def g(d, k, default=None):
    """Case-insensitive get (reports may be PascalCase or camelCase)."""
    for kk, v in d.items():
        if kk.lower() == k.lower(): return v
    return default

def match_id(p, ven, dev):
    a, b = p.split(":")
    if a.upper() != (ven or "").upper(): return False
    if b == "*": return True
    if b.endswith("*"): return (dev or "").upper().startswith(b[:-1].upper())
    return b.upper() == (dev or "").upper()

def match_cpu(m, c):
    if "cpuVendor" in m and m["cpuVendor"].lower() != g(c, "vendor", "").lower(): return False
    if m.get("cpuFamilies") and g(c, "family") not in m["cpuFamilies"]: return False
    if m.get("cpuModels") and g(c, "model") not in m["cpuModels"]: return False
    if "cpuModelMin" in m and g(c, "model") < m["cpuModelMin"]: return False
    if "cpuModelMax" in m and g(c, "model") > m["cpuModelMax"]: return False
    if "cpuSteppingMin" in m and g(c, "stepping") < m["cpuSteppingMin"]: return False
    if "cpuSteppingMax" in m and g(c, "stepping") > m["cpuSteppingMax"]: return False
    if "cpuNameRegex" in m and not re.search(m["cpuNameRegex"], g(c, "name", ""), re.I): return False
    return True

CPU_KEYS = ("cpuVendor", "cpuFamilies", "cpuModels", "cpuModelMin", "cpuModelMax", "cpuSteppingMin", "cpuSteppingMax", "cpuNameRegex")

def match_system(m, hw, uefi, include_acpi):
    s = g(hw, "system")
    if "manufacturerRegex" in m and not re.search(m["manufacturerRegex"], g(s, "manufacturer", ""), re.I): return False
    if m.get("machineTypes") and g(s, "machineType", "").upper() not in [x.upper() for x in m["machineTypes"]]: return False
    if "modelRegex" in m and not re.search(m["modelRegex"], f"{g(s,'model','')} {g(s,'version','')} {g(s,'family','')}", re.I): return False
    if "isLaptop" in m and bool(g(s, "isLaptop")) != m["isLaptop"]: return False
    if "trackpadInterface" in m and m["trackpadInterface"].lower() != g(hw, "trackpadInterface", "Unknown").lower(): return False
    if "isUefi" in m and m["isUefi"] != uefi: return False
    if "architecture" in m and m["architecture"].lower() != g(hw, "hostArchitecture", "X64").lower(): return False
    if include_acpi:
        hids = [g(a, "hid", "").upper() for a in g(hw, "acpiDevices", [])]
        if "acpiHid" in m and (m["acpiHid"].upper() in hids) != m.get("hasAcpiHid", True): return False
        if m.get("acpiHidsAbsent") and any(h.upper() in hids for h in m["acpiHidsAbsent"]): return False
        if "dsdtHas" in m and m["dsdtHas"] not in g(hw, "dsdtSymbols", []): return False
    if any(k in m for k in CPU_KEYS) and not match_cpu(m, g(hw, "cpu")): return False
    return True

def match_caps(m, caps):
    if m.get("hasCapability") and not all(c in caps for c in m["hasCapability"]): return False
    if m.get("lacksCapability") and any(c in caps for c in m["lacksCapability"]): return False
    return True

def match_device(m, d):
    if "category" in m and m["category"].lower() != g(d, "category", "").lower(): return False
    if m.get("pciIds") and not any(match_id(p, g(d, "vendorId"), g(d, "deviceId")) for p in m["pciIds"]): return False
    if m.get("nameRegex") and not any(re.search(r, g(d, "name", ""), re.I) for r in m["nameRegex"]): return False
    if "devicePathRegex" in m and not re.search(m["devicePathRegex"], g(d, "ocDevicePath", "") or ""): return False
    if "acpiHid" in m and m["acpiHid"].lower() not in g(d, "pnpId", "").lower() and not any(m["acpiHid"].lower() in c.lower() for c in g(d, "compatibleIds", [])): return False
    return any(k in m for k in ("category", "pciIds", "nameRegex", "acpiHid"))

def match_disk(m, d):
    if "diskBus" in m and m["diskBus"].lower() != g(d, "busType", "").lower(): return False
    if "diskModelRegex" in m and not re.search(m["diskModelRegex"], g(d, "model", ""), re.I): return False
    return True

def evaluate(kb, hw, uefi=True):
    res = {"items": [], "kexts": [], "excluded": set(), "bootArgs": [], "ssdts": [], "patches": [], "devprops": {}, "smbios": "", "caps": set(),
           "blocks": [], "warnings": [], "macosWarn": {}, "macosBlock": {}, "recommended": "", "audioLayout": None, "audioCodec": "", "audioPath": "", "matched": []}
    order = sorted(enumerate(kb["rules"]), key=lambda t: (t[1].get("priority", 0), t[0]))
    order = [r for _, r in order]
    matched_dev, groups, cpu_matched, bad_disks = set(), set(), False, set()
    requires, recommends, maxd = {}, {}, []
    pri = {"smbios": -10**9, "macos": -10**9}
    def add_arg(a):
        k = a.split("=")[0]; res["bootArgs"][:] = [x for x in res["bootArgs"] if x.split("=")[0] != k]; res["bootArgs"].append(a)
    def apply(r, device, dev):
        res["matched"].append(r["id"])
        notes = " ".join(r.get("notes", []))
        res["items"].append((r["component"], device, r["status"], r["id"]))
        if r.get("blocking"): res["blocks"].append(f"{r['component']}: {device} — {notes}")
        for k in r.get("kexts", []):
            if k.lower() not in [x.lower() for x in res["kexts"]]: res["kexts"].append(k)
        res["excluded"].update(r.get("excludeKexts", []))
        for a in r.get("bootArgs", []): add_arg(a)
        for s in r.get("ssdts", []):
            if s not in res["ssdts"]: res["ssdts"].append(s)
        res["patches"] += list(r.get("configPatches", {}).items())
        for path, props in r.get("deviceProperties", {}).items():
            p = (g(dev, "ocDevicePath", "") if dev else "") if path == "{device}" else path
            if not p: res["warnings"].append(f"{r['id']}: device path unknown"); continue
            res["devprops"].setdefault(p, {}).update(props)
        if res["audioLayout"] is None and not res["audioCodec"] and (r.get("audioLayouts") or r.get("audioAutoLayout")):
            res["audioPath"] = g(dev, "ocDevicePath", "") if dev else ""
            if r.get("audioLayouts"): res["audioLayout"] = r["audioLayouts"][0]
            elif dev: res["audioCodec"] = f"{g(dev,'vendorId')}:{g(dev,'deviceId')}"
        if r.get("smbios") and r.get("priority", 0) >= pri["smbios"]: res["smbios"] = r["smbios"]; pri["smbios"] = r.get("priority", 0)
        res["caps"].update(r.get("provides", []))
        for c, msg in r.get("requires", {}).items(): requires.setdefault(c, msg)
        for c, msg in r.get("recommends", {}).items(): recommends.setdefault(c, msg)
        m = r.get("macos") or {}
        if m.get("recommended") and r.get("priority", 0) >= pri["macos"]: res["recommended"] = m["recommended"]; pri["macos"] = r.get("priority", 0)
        for v, why in m.get("warn", {}).items(): res["macosWarn"].setdefault(v, []).append(why)
        for v, why in m.get("block", {}).items(): res["macosBlock"].setdefault(v, []).append(why)
        if "maxDarwin" in m: maxd.append((m["maxDarwin"], m.get("maxReason", "")))
        for p in r.get("postInstall", []): pass
    devices = g(hw, "devices", [])
    internal = [d for d in g(hw, "disks", []) if not g(d, "isRemovable") and g(d, "busType") != "USB"]
    for fallback_pass in (False, True):
        for r in [x for x in order if bool(x.get("fallback")) == fallback_pass]:
            m = r.get("match", {}); scope = r["scope"]
            if scope == "device":
                for i, d in enumerate(devices):
                    if fallback_pass and i in matched_dev: continue
                    if r.get("group") and (i, r["group"]) in groups: continue
                    if not (match_device(m, d) and match_system(m, hw, uefi, False) and match_caps(m, res["caps"])): continue
                    matched_dev.add(i)
                    if r.get("group"): groups.add((i, r["group"]))
                    pid = f"{g(d,'vendorId','')}:{g(d,'deviceId','')}"
                    apply(r, f"{g(d,'name')} [{pid}]", d)
            elif scope == "disk":
                for d in internal:
                    if not match_disk(m, d): continue
                    if r["status"] == "Unsupported": bad_disks.add(g(d, "number"))
                    apply(r, g(d, "model"), None)
            elif scope == "cpu":
                if fallback_pass and cpu_matched: continue
                if match_cpu(m, g(hw, "cpu")) and match_system(m, hw, uefi, True) and match_caps(m, res["caps"]):
                    if not fallback_pass: cpu_matched = True
                    apply(r, g(g(hw, "cpu"), "name"), None)
            elif scope == "system":
                if match_system(m, hw, uefi, True) and match_caps(m, res["caps"]): apply(r, "system", None)
            elif scope == "always":
                apply(r, "-", None)
    for c, msg in requires.items():
        if c not in res["caps"]: res["blocks"].append(msg)
    for c, msg in recommends.items():
        if c not in res["caps"]: res["warnings"].append(msg)
    if not internal: res["blocks"].append("Storage: no internal disk detected to install macOS on.")
    elif all(g(d, "number") in bad_disks for d in internal) and any(r["scope"] == "disk" and r.get("blockingIfNoAlternative") and r["id"] in res["matched"] for r in kb["rules"]):
        res["blocks"].append("Storage: every internal disk is unsupported by macOS.")
    if not res["smbios"] and not res["blocks"]: res["blocks"].append("No Mac model (SMBIOS) profile matches this CPU / form factor.")
    versions = kb["macos"]["versions"]
    for v in versions:
        if res["smbios"] and v["supportedSmbios"] and res["smbios"] not in v["supportedSmbios"]:
            res["macosBlock"].setdefault(v["id"], []).append(f"Apple does not support {res['smbios']} on {v['name']}.")
        for mx, why in maxd:
            if v["darwinMajor"] > mx: res["macosBlock"].setdefault(v["id"], []).append(why)
    allowed = sorted([v for v in versions if v["id"] not in res["macosBlock"]], key=lambda v: -v["darwinMajor"])
    if not res["recommended"] or res["recommended"] in res["macosBlock"]:
        pick = next((v for v in allowed if v["id"] not in res["macosWarn"]), allowed[0] if allowed else None)
        res["recommended"] = pick["id"] if pick else ""
    if not res["recommended"] and res["smbios"]: res["blocks"].append("No macOS version in the knowledge base supports this hardware combination.")
    res["effectiveKexts"] = [k for k in res["kexts"] if k.split("|")[0] not in res["excluded"]]
    return res

def lint():
    import jsonschema
    schema = json.loads((RULES / "schema" / "rules.schema.json").read_text(encoding="utf-8"))
    errors = []
    for f in sorted((RULES / "kb").glob("rules-*.json")):
        try: jsonschema.validate(json.loads(f.read_text(encoding="utf-8")), schema)
        except jsonschema.ValidationError as e: errors.append(f"{f.name}: {e.message} at {list(e.path)}")
    kb = load_kb()
    ids = [r["id"] for r in kb["rules"]]
    for i in {x for x in ids if ids.count(x) > 1}: errors.append(f"duplicate rule id {i}")
    provides = {p.lower() for s in kb["sources"]["sources"] for p in s["provides"]}
    variants = {k for v in kb["macos"]["versions"] for k in v["kextVariants"]}
    for r in kb["rules"]:
        for k in r.get("kexts", []):
            if k.startswith("@"):
                if k[1:] not in variants: errors.append(f"{r['id']}: unknown kext variant {k}")
            elif k.lower() not in provides: errors.append(f"{r['id']}: no source provides {k}")
    for v in kb["macos"]["versions"]:
        for name, ks in v["kextVariants"].items():
            for k in ks:
                if k.lower() not in provides: errors.append(f"macos {v['id']}: no source provides {k}")
    smbioses = {r["smbios"] for r in kb["rules"] if r.get("smbios")}
    known = {m for v in kb["macos"]["versions"] for m in v["supportedSmbios"]}
    for s in smbioses - known: errors.append(f"SMBIOS {s} is not supported by any macOS version in macos.json")
    for e in errors: print("ERROR:", e)
    print(f"{len(kb['rules'])} rules, {len(kb['sources']['sources'])} sources, {len(kb['macos']['versions'])} macOS versions: {'FAIL' if errors else 'OK'}")
    return 1 if errors else 0

def manifest(version=None):
    m = json.loads((RULES / "manifest.json").read_text(encoding="utf-8"))
    m["files"] = {f"kb/{p.name}": hashlib.sha256(p.read_bytes()).hexdigest().upper() for p in sorted((RULES / "kb").glob("*.json"))}
    if version: m["kbVersion"] = version
    m["signature"] = ""
    (RULES / "manifest.json").write_text(json.dumps(m, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps(m, indent=2))

def summary(name, r):
    print(f"== {name}")
    print(f"   blocked: {bool(r['blocks'])}  smbios: {r['smbios']}  recommended: {r['recommended']}  blocked versions: {sorted(r['macosBlock'])}")
    for b in r["blocks"]: print("   BLOCK:", b[:160])
    print("   kexts:", ", ".join(r["effectiveKexts"]))
    print("   ssdts:", ", ".join(r["ssdts"]), "| boot-args:", " ".join(r["bootArgs"]))
    print("   devprops:", json.dumps(r["devprops"]))
    print("   audio:", r["audioLayout"] or r["audioCodec"], r["audioPath"], "| caps:", sorted(r["caps"]))
    for w in r["warnings"]: print("   warn:", w[:140])

if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "lint"
    if cmd == "lint": sys.exit(lint())
    if cmd == "manifest": manifest(sys.argv[3] if len(sys.argv) > 3 and sys.argv[2] == "--version" else None)
    if cmd == "expect":
        # Writes <profile>.expected.json next to each fixture; the C# RulesEngine tests must produce the same result.
        kb = load_kb()
        for f in sorted((ROOT / "tests/HackWizard.Tests/Fixtures/Profiles").glob("*.hardware.json")):
            r = evaluate(kb, json.loads(f.read_text(encoding="utf-8")))
            exp = {"blocked": bool(r["blocks"]), "smbios": r["smbios"], "recommended": r["recommended"] if not r["blocks"] else "",
                   "blockedMacos": sorted(r["macosBlock"]), "kexts": sorted(r["effectiveKexts"]), "ssdts": sorted(r["ssdts"]),
                   "bootArgs": r["bootArgs"], "deviceProperties": {k: sorted(v) for k, v in sorted(r["devprops"].items())},
                   "audioLayout": r["audioLayout"], "audioCodec": r["audioCodec"], "capabilities": sorted(r["caps"]), "matchedRules": sorted(set(r["matched"]))}
            out = f.with_name(f.name.replace(".hardware.json", ".expected.json"))
            out.write_text(json.dumps(exp, indent=2) + "\n", encoding="utf-8", newline="\n")
            print("wrote", out.name)
    if cmd == "simulate":
        kb = load_kb()
        for p in sys.argv[2:]:
            summary(pathlib.Path(p).name, evaluate(kb, json.loads(pathlib.Path(p).read_text(encoding="utf-8"))))
