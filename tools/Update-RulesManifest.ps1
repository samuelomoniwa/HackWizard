<# Recomputes Rules/manifest.json SHA-256 hashes for Rules/kb/*.json (same output as `python tools/kb_tool.py manifest`). #>
param([string]$KbVersion)
$ErrorActionPreference = "Stop"
$rules = Resolve-Path (Join-Path $PSScriptRoot "..\Rules")
$mf = Join-Path $rules "manifest.json"
$m = Get-Content $mf -Raw | ConvertFrom-Json
$files = [ordered]@{}
Get-ChildItem (Join-Path $rules "kb") -Filter *.json | Sort-Object Name | ForEach-Object { $files["kb/$($_.Name)"] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
$out = [ordered]@{ schemaVersion = $m.schemaVersion; kbVersion = $(if ($KbVersion) { $KbVersion } else { $m.kbVersion }); updateUrl = $m.updateUrl; files = $files; signature = "" }
[IO.File]::WriteAllText($mf, (($out | ConvertTo-Json -Depth 5) -replace "`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
$out
