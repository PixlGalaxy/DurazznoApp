<#
.SYNOPSIS
    Builds Durazzno App (DurazznoAppDaemon.exe) and prepares a signed release for Webin Cloud Server.

.DESCRIPTION
    Output:
      dist\DurazznoApp\   Ready-to-run folder: DurazznoAppDaemon.exe (self-contained, single file) + DurazznoTerminal.exe
      dist\server\        Upload these files to the "durazznoapp.main" path name on Webin Cloud Server
                          (overwriting the previous ones; links stay the same):
                            manifest.json, manifest.json.sig, DurazznoAppDaemon.exe

    manifest.json is signed with crypto\update-signing-key.xml (git-ignored, see crypto\README.md);
    the app rejects any manifest whose signature does not verify.

.EXAMPLE
    .\scripts\publish.ps1
    .\scripts\publish.ps1 -Version 3.0.1 -Notes "Fixes offline icon" -MinimumSupportedVersion 3.0.0
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
$keyPath   = Join-Path $root 'crypto\update-signing-key.xml'
$utf8      = New-Object System.Text.UTF8Encoding $false

if (-not (Test-Path $keyPath)) { throw "Signing key not found at $keyPath. See crypto\README.md." }
if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

foreach ($dir in $appOut, $serverOut) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item $dir -ItemType Directory | Out-Null
}

# --- App ----------------------------------------------------------------------------------------
Write-Host "Publishing Durazzno App $Version..."
dotnet publish $project -c Release -o $appOut "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# --- Durazzno Terminal (native AOT recovery console, shipped next to the app) -------------------
# The native linker finds MSVC through vswhere.exe, which is not on PATH by default.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }
$terminalProject = Join-Path $root 'src\DurazznoTerminal\DurazznoTerminal.csproj'
$terminalOut = Join-Path $root 'dist\terminal'
Write-Host "Publishing Durazzno Terminal..."
dotnet publish $terminalProject -c Release -r win-x64 -o $terminalOut
if ($LASTEXITCODE -ne 0) { throw "Durazzno Terminal publish failed (needs Visual Studio's C++ build tools for Native AOT)." }
Copy-Item (Join-Path $terminalOut 'DurazznoTerminal.exe') $appOut
Remove-Item $terminalOut -Recurse -Force

# --- Manifest -----------------------------------------------------------------------------------
Copy-Item (Join-Path $appOut 'DurazznoAppDaemon.exe') $serverOut
$manifest = [ordered]@{
    app = [ordered]@{
        version = $Version
        url     = 'DurazznoAppDaemon.exe'   # relative to manifest.json
        sha256  = (Get-FileHash (Join-Path $serverOut 'DurazznoAppDaemon.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        size    = (Get-Item (Join-Path $serverOut 'DurazznoAppDaemon.exe')).Length
    }
}
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
Write-Host "  Run locally : $appOut\DurazznoAppDaemon.exe  (recovery console: DurazznoTerminal.exe)"
Write-Host "  Upload      : $serverOut\{manifest.json, manifest.json.sig, DurazznoAppDaemon.exe}  ->  path name durazznoapp.main"
