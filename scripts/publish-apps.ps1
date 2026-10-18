<#
.SYNOPSIS
    Builds the signed apps catalog (apps.json + apps.json.sig) for the Apps folder on Webin Cloud Server (WebinCloud.AppsPath).

.DESCRIPTION
    Source folder (default: apps-catalog\ at the repository root, git-ignored) holds:
      apps.json        written by hand, see webincloud\README.md:
                         { "apps": [ { "id": "...", "name": "...", "description": "...", "version": "1.0",
                                       "url": "MyApp.exe", "icon": "MyApp.png", "message": "optional" } ] }
      the app files and icons that apps.json names (or use full links instead)

    For every "url" that is a file in the source folder, the script fills in "sha256" and "size", so the app checks the
    download. The result goes to dist\apps\: upload everything in it to that folder (same names, overwriting).
    apps.json is signed with crypto\update-signing-key.xml; Durazzno ignores an unsigned or altered catalog.

.EXAMPLE
    .\scripts\publish-apps.ps1
    .\scripts\publish-apps.ps1 -Source D:\my-apps
#>
param([string]$Source)

$ErrorActionPreference = 'Stop'
$root    = Split-Path $PSScriptRoot -Parent
if (-not $Source) { $Source = Join-Path $root 'apps-catalog' }
$out     = Join-Path $root 'dist\apps'
$keyPath = Join-Path $root 'crypto\update-signing-key.xml'
$utf8    = New-Object System.Text.UTF8Encoding $false

if (-not (Test-Path $keyPath)) { throw "Signing key not found at $keyPath. See crypto\README.md." }
$catalogPath = Join-Path $Source 'apps.json'
if (-not (Test-Path $catalogPath)) { throw "No apps.json in $Source." }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

# Local files are written to the catalog as full Webin Cloud links on the server set in WebinCloud.cs
# ({server}/backend/api/public/{WebinCloud.AppsPath}/{file}/download?inline=1&path=).
$webinCloud = Get-Content (Join-Path $root 'src\DurazznoApp\Network\WebinCloud.cs') -Raw
if ($webinCloud -notmatch 'const string Server = "([^"]+)"') { throw 'Could not read WebinCloud.Server from WebinCloud.cs.' }
$server = $Matches[1].TrimEnd('/')
if ($webinCloud -notmatch 'const string AppsPath = "([^"]+)"') { throw 'Could not read WebinCloud.AppsPath from WebinCloud.cs.' }
$appsPath = $Matches[1].Trim('/')
function FileLink($name) { "$server/backend/api/public/$appsPath/$([Uri]::EscapeDataString($name))/download?inline=1&path=" }

$catalog = Get-Content $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$apps = @()
foreach ($app in $catalog.apps) {
    if (-not $app.name -or -not $app.url) { throw "Every app needs at least ""name"" and ""url""." }
    $entry = [ordered]@{}
    foreach ($p in $app.PSObject.Properties) { $entry[$p.Name] = $p.Value }
    if (-not $entry.Contains('id') -or -not $entry.id) { $entry.id = ($app.name -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant() }

    # "sha256" decides how the download is checked:
    #   missing           -> computed here from the local file (when "url" is a local file)
    #   64 hex characters -> that hash
    #   a link / file name of a text file with the hash (e.g. AppHash.txt) -> the app reads it before downloading
    #   ""                -> no check (apps that change on their own, scripts...)
    $hashGiven = $entry.Contains('sha256')
    foreach ($field in 'url', 'icon', 'sha256') {
        $value = $entry[$field]
        if (-not $value -or $value -match '^[a-z]+://') { continue }
        if ($field -eq 'sha256' -and $value -match '^[0-9A-Fa-f]{64}$') { continue }
        $file = Join-Path $Source $value
        if (-not (Test-Path $file)) { throw "$($app.name): $field ""$value"" is not in $Source (use a full link for files hosted elsewhere)." }
        Copy-Item $file $out -Force
        if ($field -eq 'url') {
            if (-not $hashGiven) { $entry.sha256 = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() }
            $entry.size = (Get-Item $file).Length
        }
        $entry[$field] = FileLink (Split-Path $value -Leaf)
    }
    if ($entry.Contains('sha256') -and -not $entry.sha256) { $entry.Remove('sha256') } # "" = not checked
    $apps += [pscustomobject]$entry
}

$outCatalog = Join-Path $out 'apps.json'
# ConvertTo-Json escapes & < > ' as & and friends; plain characters are equally valid JSON and readable.
$json = ([ordered]@{ apps = $apps } | ConvertTo-Json -Depth 6) -replace '\\u0026', '&' -replace '\\u0027', "'" -replace '\\u003c', '<' -replace '\\u003e', '>'
[System.IO.File]::WriteAllText($outCatalog, $json, $utf8)

$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.FromXmlString([System.IO.File]::ReadAllText($keyPath))
$signature = $rsa.SignData([System.IO.File]::ReadAllBytes($outCatalog), 'SHA256')
[System.IO.File]::WriteAllText("$outCatalog.sig", [Convert]::ToBase64String($signature), $utf8)
$rsa.Dispose()

Write-Host ""
Write-Host "Done. $($apps.Count) app(s) in the catalog, signed."
Write-Host "  Upload: everything in $out  ->  $appsPath"
