param([switch]$SkipBuild, [string]$Message = "Update HackWizard")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot\..

# --- GitHub sign-in check ---
gh auth status *> $null
if ($LASTEXITCODE) { throw "Not signed in. Run: gh auth login --hostname github.com --git-protocol https --web" }
$user = (gh api user --jq .login).Trim()
$repo = "$user/HackWizard"
Write-Host "== GitHub account: $user" -ForegroundColor Cyan

# --- Use gh's login for git, and remove old saved GitHub logins ---
cmdkey /list | Select-String 'Target:\s*(\S*github\S*)' | ForEach-Object { cmdkey /delete:$($_.Matches[0].Groups[1].Value) | Out-Null }
gh auth setup-git

# --- Point the knowledge-base update URL at this repository ---
$mf = ".\Rules\manifest.json"
$url = "https://raw.githubusercontent.com/$repo/main/Rules"
(Get-Content $mf -Raw) -replace '"updateUrl":\s*"[^"]*"', "`"updateUrl`": `"$url`"" | Set-Content $mf -NoNewline -Encoding UTF8

# --- Bump KB version automatically (must always increase) ---
$v = [version](Get-Content $mf -Raw | ConvertFrom-Json).kbVersion
$new = "$($v.Major).$($v.Minor).$($v.Build + 1)"
python tools\kb_tool.py manifest --version $new | Out-Null
python tools\kb_tool.py lint
if ($LASTEXITCODE) { throw "Knowledge-base lint failed" }
Write-Host "== Knowledge base version $new, update URL $url" -ForegroundColor Cyan

# --- Build the EXE (it embeds the update URL) ---
if (-not $SkipBuild) { .\build.ps1 -SkipTests; if ($LASTEXITCODE) { throw "Build failed" } }

# --- Local git repository ---
if (-not (Test-Path .git)) { git init | Out-Null }
git branch -M main
if (-not (git config user.name))  { git config user.name  $user }
if (-not (git config user.email)) { git config user.email "$user@users.noreply.github.com" }
git add -A
git diff --cached --quiet
if ($LASTEXITCODE) { git commit -m "$Message (KB $new)" | Out-Null }

# --- Create the GitHub repository if missing, then push ---
gh repo view $repo *> $null
if ($LASTEXITCODE) {
    Write-Host "== Creating https://github.com/$repo" -ForegroundColor Cyan
    git remote remove origin 2>$null
    gh repo create $repo --public --source . --remote origin --push
} else {
    git remote remove origin 2>$null
    git remote add origin "https://github.com/$repo.git"
    git push -u origin main
}
if ($LASTEXITCODE) { throw "Push failed" }
Write-Host "== Done: https://github.com/$repo" -ForegroundColor Green
Write-Host "   EXE: $(Resolve-Path .\artifacts\publish\HackWizard.exe -ErrorAction SilentlyContinue)"
