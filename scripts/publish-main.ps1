<#
.SYNOPSIS
    Builds Durazzno App (DurazznoApp.exe) and prepares a signed release for Webin Cloud Server.

.DESCRIPTION
    Output:
      dist\DurazznoApp\   Ready-to-run folder: DurazznoApp.exe (self-contained, single file)
      dist\server\        Upload these files to durazznoapp/DurazznoApp/Main on Webin Cloud Server
                          (overwriting the previous ones; links stay the same):
                            manifest.json, manifest.json.sig, DurazznoApp.exe

    manifest.json is signed with publisher\crypto\update-signing-key.xml (git-ignored, see publisher\crypto\README.md);
    the app rejects any manifest whose signature does not verify.

.EXAMPLE
    .\scripts\publish-main.ps1
    .\scripts\publish-main.ps1 -Version 3.0.1 -Notes "Fixes offline icon" -MinimumSupportedVersion 3.0.0
    (without -Notes, scripts\release-notes\<version>.txt is used when it exists)
#>
param(
    [string]$Version,
    [string]$MinimumSupportedVersion,
    [string]$Notes
)

$ErrorActionPreference = 'Stop'

$root      = Split-Path $PSScriptRoot -Parent
$project   = Join-Path $root 'src\DurazznoApp\DurazznoApp.csproj'
$appOut    = Join-Path $root 'dist\DurazznoApp'
$serverOut = Join-Path $root 'dist\server'
$keyPath   = Join-Path $root 'publisher\crypto\update-signing-key.xml'
$utf8      = New-Object System.Text.UTF8Encoding $false

if (-not (Test-Path $keyPath)) { throw "Signing key not found at $keyPath. See publisher\crypto\README.md." }
if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

if (Test-Path $serverOut) { Remove-Item $serverOut -Recurse -Force }
New-Item $serverOut -ItemType Directory | Out-Null
# The app folder is cleaned except apps\ (apps downloaded from the Apps page; they may be running).
New-Item $appOut -ItemType Directory -Force | Out-Null
Get-ChildItem $appOut -Force | Where-Object { $_.Name -ne 'apps' } | Remove-Item -Recurse -Force

# --- App ----------------------------------------------------------------------------------------
Write-Host "Publishing Durazzno App $Version..."
dotnet publish $project -c Release -o $appOut "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# --- Manifest -----------------------------------------------------------------------------------
Copy-Item (Join-Path $appOut 'DurazznoApp.exe') $serverOut
$manifest = [ordered]@{
    app = [ordered]@{
        version = $Version
        url     = 'DurazznoApp.exe'   # relative to manifest.json
        sha256  = (Get-FileHash (Join-Path $serverOut 'DurazznoApp.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        size    = (Get-Item (Join-Path $serverOut 'DurazznoApp.exe')).Length
    }
}
# Release notes: -Notes, or scripts\release-notes\<version>.txt (shown as "What's new" in the app).
$notesFile = Join-Path $PSScriptRoot "release-notes\$Version.txt"
if (-not $Notes -and (Test-Path $notesFile)) { $Notes = (Get-Content $notesFile -Raw -Encoding UTF8).Trim() }
if ($Notes) { $manifest.app.notes = $Notes }
if ($MinimumSupportedVersion) { $manifest.minimumSupportedVersion = $MinimumSupportedVersion }

$manifestPath = Join-Path $serverOut 'manifest.json'
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), $utf8)

# --- Signature ----------------------------------------------------------------------------------
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.FromXmlString([System.IO.File]::ReadAllText($keyPath))
$signature = $rsa.SignData([System.IO.File]::ReadAllBytes($manifestPath), 'SHA256')
[System.IO.File]::WriteAllText("$manifestPath.sig", [Convert]::ToBase64String($signature), $utf8)
$rsa.Dispose()

Write-Host ""
Write-Host "Done. Durazzno App $Version, manifest signed."
Write-Host "  Run locally : $appOut\DurazznoApp.exe"
Write-Host "  Upload      : $serverOut\{manifest.json, manifest.json.sig, DurazznoApp.exe}  ->  durazznoapp/DurazznoApp/Main"
