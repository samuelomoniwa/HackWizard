<# Signs Rules/manifest.json. Canonical bytes = "kbVersion\n" + ordinal-sorted "path:SHA256\n" (KnowledgeBaseLoader.CanonicalBytes). PowerShell 7+. #>
param([Parameter(Mandatory)][string]$PrivateKeyPath, [string]$Manifest = (Join-Path $PSScriptRoot "..\Rules\manifest.json"))
$ErrorActionPreference = "Stop"
$m = Get-Content $Manifest -Raw | ConvertFrom-Json -AsHashtable
$sb = [Text.StringBuilder]::new()
[void]$sb.Append($m.kbVersion).Append("`n")
$keys = [string[]]@($m.files.Keys); [Array]::Sort($keys, [StringComparer]::Ordinal)
foreach ($k in $keys) { [void]$sb.Append($k).Append(":").Append($m.files[$k].ToUpperInvariant()).Append("`n") }
$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
$ecdsa.ImportPkcs8PrivateKey([IO.File]::ReadAllBytes($PrivateKeyPath), [ref]0)
$m.signature = [Convert]::ToBase64String($ecdsa.SignData([Text.Encoding]::UTF8.GetBytes($sb.ToString()), [System.Security.Cryptography.HashAlgorithmName]::SHA256))
(($m | ConvertTo-Json -Depth 5) -replace "`r`n", "`n") | Set-Content $Manifest -NoNewline -Encoding utf8NoBOM
Write-Host "Manifest signed."
