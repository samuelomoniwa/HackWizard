<#
.SYNOPSIS
  Builds, tests, publishes and (optionally) Authenticode-signs HackWizard.exe.
.EXAMPLE
  .\build.ps1                                   # lint KB, build, unit tests, publish single-file EXE
  .\build.ps1 -IntegrationTests                 # also downloads OpenCore + AMD_Vanilla and runs ocvalidate on every profile
  .\build.ps1 -ApproveSnapshots                 # (re)write golden config.plist files after reviewing the diff
  .\build.ps1 -Sign -CertThumbprint ABCD...     # sign with a certificate from the Windows store
  .\build.ps1 -Sign -PfxPath .\codesign.pfx     # sign with a PFX (prompts for the password)
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$IntegrationTests,
    [switch]$ApproveSnapshots,
    [switch]$Sign,
    [string]$CertThumbprint,
    [string]$PfxPath,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$Output = "artifacts"
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = $PSScriptRoot
Push-Location $root
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw ".NET 8 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0" }
    Write-Host "== .NET SDK $(dotnet --version)" -ForegroundColor Cyan

    # 1) Knowledge base: refresh manifest hashes, then lint (Python optional but recommended)
    $py = Get-Command python -ErrorAction SilentlyContinue
    if ($py) {
        & $py.Source "$root\tools\kb_tool.py" manifest | Out-Null
        & $py.Source "$root\tools\kb_tool.py" lint; if ($LASTEXITCODE) { throw "Knowledge-base lint failed" }
    } else {
        & "$root\tools\Update-RulesManifest.ps1" | Out-Null
        Write-Warning "Python not found: skipping tools/kb_tool.py lint (CI runs it)."
    }

    # 2) Build
    dotnet restore HackWizard.sln
    dotnet build HackWizard.sln -c $Configuration --no-restore -p:Platform=x64
    if ($LASTEXITCODE) { throw "Build failed" }

    # 3) Tests
    if (-not $SkipTests) {
        if ($ApproveSnapshots) { $env:HACKWIZARD_APPROVE = "1" }
        if ($IntegrationTests) {
            $it = Join-Path $root "$Output\it"; New-Item -ItemType Directory -Force $it | Out-Null
            $headers = @{ "User-Agent" = "HackWizard-build" }
            if ($env:GITHUB_TOKEN) { $headers["Authorization"] = "Bearer $env:GITHUB_TOKEN" }
            $rel = Invoke-RestMethod -Headers $headers "https://api.github.com/repos/acidanthera/OpenCorePkg/releases/latest"
            $asset = $rel.assets | Where-Object { $_.name -match '^OpenCore-[0-9.]+-RELEASE\.zip$' } | Select-Object -First 1
            $zip = Join-Path $it $asset.name
            if (-not (Test-Path $zip)) { Invoke-WebRequest -Headers $headers $asset.browser_download_url -OutFile $zip }
            $ocDir = Join-Path $it ($asset.name -replace '\.zip$','')
            if (-not (Test-Path $ocDir)) { Expand-Archive $zip $ocDir }
            $patches = Join-Path $it "patches.plist"
            Invoke-WebRequest "https://raw.githubusercontent.com/AMD-OSX/AMD_Vanilla/master/patches.plist" -OutFile $patches
            $env:HACKWIZARD_OPENCORE_DIR = $ocDir
            $env:HACKWIZARD_AMD_PATCHES = $patches
            Write-Host "== Integration tests against OpenCore $($rel.tag_name)" -ForegroundColor Cyan
        }
        dotnet test tests\HackWizard.Tests\HackWizard.Tests.csproj -c $Configuration --no-build --logger "trx;LogFileName=results.trx" --results-directory "$Output\test-results"
        $testExit = $LASTEXITCODE
        Remove-Item Env:HACKWIZARD_APPROVE -ErrorAction SilentlyContinue
        if ($testExit) { throw "Tests failed (see $Output\test-results)" }
    }

    # 4) Publish (exact command from the specification)
    $pub = Join-Path $root "$Output\publish"
    dotnet publish src\HackWizard.App\HackWizard.App.csproj -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $pub
    if ($LASTEXITCODE) { throw "Publish failed" }
    $exe = Join-Path $pub "HackWizard.exe"

    # 5) Authenticode signing (unsigned EXEs trigger SmartScreen)
    if ($Sign) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK (Signing Tools)." }
        $signArgs = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256", "/d", "HackWizard")
        if ($PfxPath) {
            $pw = Read-Host "PFX password" -AsSecureString
            $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw))
            $signArgs += @("/f", $PfxPath, "/p", $plain)
        } elseif ($CertThumbprint) { $signArgs += @("/sha1", $CertThumbprint) } else { $signArgs += "/a" }
        & $signtool.FullName @signArgs $exe
        if ($LASTEXITCODE) { throw "Signing failed" }
        & $signtool.FullName verify /pa /v $exe
        if ($LASTEXITCODE) { throw "Signature verification failed" }
    }
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
    "$hash  HackWizard.exe" | Set-Content (Join-Path $pub "HackWizard.exe.sha256") -Encoding ascii
    Write-Host "== Done: $exe" -ForegroundColor Green
    Write-Host "   SHA-256 $hash"
}
finally { Pop-Location }
