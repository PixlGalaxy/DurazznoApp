# Durazzno Publisher: builds, signs and uploads Durazzno App (Main) and the Apps catalog through the Webin Cloud Server API.
# Start it with Publisher.bat. Signing needs publisher\crypto\update-signing-key.xml (git-ignored, never commit).
# Uploading needs publisher\.env with the Webin Cloud account (git-ignored; see .env.example).
Add-Type -Name Native -Namespace Publisher -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
'@
# Hide the console. Done here, not with powershell -WindowStyle Hidden: that start-up flag would also hide the form.
[void][Publisher.Native]::ShowWindow([Publisher.Native]::GetConsoleWindow(), 0)
# Sharp text at any display scale (otherwise Windows stretches the window as a blurry bitmap).
[void][Publisher.Native]::SetProcessDPIAware()

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$ErrorActionPreference = 'Stop'

$root        = Split-Path $PSScriptRoot -Parent
$scripts     = Join-Path $root 'scripts'
$project     = Join-Path $root 'src\DurazznoApp\DurazznoApp.csproj'
$catalogDir  = Join-Path $PSScriptRoot 'apps-catalog'
$catalogPath = Join-Path $catalogDir 'apps.json'
$distServer  = Join-Path $root 'dist\server'
$distApps    = Join-Path $root 'dist\apps'
$envFile     = Join-Path $PSScriptRoot '.env'
$utf8        = New-Object System.Text.UTF8Encoding $false

# --- Webin Cloud Server settings ----------------------------------------------------------------
# publisher\.env (git-ignored, copy .env.example):
#   WEBINCLOUD_USER, WEBINCLOUD_PASSWORD   an account with write access to the two folders below
#   WEBINCLOUD_URL                         optional; default: WebinCloud.Server in src\DurazznoApp\Network\WebinCloud.cs
#   WEBINCLOUD_MAIN_FOLDER                 optional; server folder for the app (default DurazznoApp/Main)
#   WEBINCLOUD_APPS_FOLDER                 optional; server folder for the catalog (default DurazznoApp/Apps)
$webinCloudCs = Get-Content (Join-Path $root 'src\DurazznoApp\Network\WebinCloud.cs') -Raw
function CsConstant($name) {
    if ($webinCloudCs -notmatch "const string $name = `"([^`"]+)`"") { throw "Could not read WebinCloud.$name from WebinCloud.cs." }
    $Matches[1]
}
function Read-EnvFile {
    $values = @{}
    if (Test-Path $envFile) {
        foreach ($line in Get-Content $envFile -Encoding UTF8) {
            $l = $line.Trim()
            if (-not $l -or $l.StartsWith('#') -or -not $l.Contains('=')) { continue }
            $key, $value = $l.Split('=', 2)
            $values[$key.Trim()] = $value.Trim().Trim('"').Trim("'")
        }
    }
    $values
}
function Load-Settings {
    $script:settings = Read-EnvFile
    $script:server = $(if ($settings.WEBINCLOUD_URL) { $settings.WEBINCLOUD_URL } else { CsConstant 'Server' }).TrimEnd('/')
    $script:mainFolder = $(if ($settings.WEBINCLOUD_MAIN_FOLDER) { $settings.WEBINCLOUD_MAIN_FOLDER } else { 'DurazznoApp/Main' }).Trim('/')
    $script:appsFolder = $(if ($settings.WEBINCLOUD_APPS_FOLDER) { $settings.WEBINCLOUD_APPS_FOLDER } else { 'DurazznoApp/Apps' }).Trim('/')
}
Load-Settings
# Public (no sign-in) links Durazzno App reads: {server}/backend/api/public/{path name}/{file}/download
$publicMain = CsConstant 'MainPath'
$publicApps = CsConstant 'AppsPath'
function Public-Link($pathName, $file) { "$server/backend/api/public/$pathName/$([Uri]::EscapeDataString($file))/download?inline=1&path=" }

Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromMinutes(10)
$http.DefaultRequestHeaders.UserAgent.ParseAdd('DurazznoPublisher/2.0')
$script:token = $null

# --- Size ---------------------------------------------------------------------------------------
# Layout numbers below are in "units": 1 unit = 1 px at 100 % display scale, times 1.2 so it reads comfortably,
# capped so the main window fits the screen.
$baseWidth = 860; $baseHeight = 690
$g = [Drawing.Graphics]::FromHwnd([IntPtr]::Zero); $dpiScale = $g.DpiX / 96; $g.Dispose()
$area = [Windows.Forms.Screen]::FromPoint([Windows.Forms.Cursor]::Position).WorkingArea # where the window opens
$script:scale = [Math]::Min($dpiScale * 1.2, [Math]::Min($area.Width * 0.95 / $baseWidth, $area.Height * 0.9 / ($baseHeight + 40)))
$fontRatio = $script:scale / $dpiScale # fonts are in points, which already follow the display scale
function S($v) { [int][Math]::Round($v * $script:scale) }
function Pt($x, $y) { New-Object Drawing.Point (S $x), (S $y) }
function Sz($w, $h) { New-Object Drawing.Size (S $w), (S $h) }

# --- Look ---------------------------------------------------------------------------------------
$cBack   = [Drawing.Color]::FromArgb(24, 24, 28)
$cCard   = [Drawing.Color]::FromArgb(36, 36, 42)
$cInput  = [Drawing.Color]::FromArgb(48, 48, 56)
$cText   = [Drawing.Color]::FromArgb(236, 236, 240)
$cDim    = [Drawing.Color]::FromArgb(150, 150, 160)
$cAccent = [Drawing.Color]::FromArgb(245, 196, 0)
$cGood   = [Drawing.Color]::FromArgb(90, 200, 120)
$cBad    = [Drawing.Color]::FromArgb(235, 90, 90)
$font    = New-Object Drawing.Font('Segoe UI', (9.5 * $fontRatio))
$fontS   = New-Object Drawing.Font('Segoe UI', (8.5 * $fontRatio))
$fontH   = New-Object Drawing.Font('Segoe UI Semibold', (12.5 * $fontRatio))

function New-Label($parent, $text, $x, $y, $w = 110, $h = 24, $color = $cDim) {
    $l = New-Object Windows.Forms.Label
    $l.Text = $text; $l.Location = Pt $x $y; $l.Size = Sz $w $h; $l.ForeColor = $color
    $parent.Controls.Add($l); $l
}
function New-TextBox($parent, $x, $y, $w, $h = 0, [switch]$Multiline) {
    $t = New-Object Windows.Forms.TextBox
    $t.Location = Pt $x $y; $t.BackColor = $cInput; $t.ForeColor = $cText; $t.BorderStyle = 'FixedSingle'
    if ($Multiline) { $t.Multiline = $true; $t.ScrollBars = 'Vertical'; $t.Size = Sz $w $h } else { $t.Width = S $w }
    $parent.Controls.Add($t); $t
}
function New-CheckBox($parent, $text, $x, $y, $w) {
    $c = New-Object Windows.Forms.CheckBox
    $c.Text = $text; $c.Location = Pt $x $y; $c.Size = Sz $w 26
    $parent.Controls.Add($c); $c
}
function New-Button($parent, $text, $x, $y, $w = 110, [switch]$Primary) {
    $b = New-Object Windows.Forms.Button
    $b.Text = $text; $b.Location = Pt $x $y; $b.Size = Sz $w 34; $b.FlatStyle = 'Flat'; $b.Cursor = 'Hand'
    if ($Primary) { $b.BackColor = $cAccent; $b.ForeColor = [Drawing.Color]::Black; $b.FlatAppearance.BorderColor = $cAccent }
    else { $b.BackColor = $cInput; $b.ForeColor = $cText; $b.FlatAppearance.BorderColor = $cInput }
    $parent.Controls.Add($b); $b
}
function New-Card($parent, $title, $x, $y, $w, $h) {
    $p = New-Object Windows.Forms.Panel
    $p.Location = Pt $x $y; $p.Size = Sz $w $h; $p.BackColor = $cCard
    $parent.Controls.Add($p)
    $t = New-Label $p $title 14 10 400 30 $cAccent
    $t.Font = $fontH
    $p
}
function Show-Error($message) {
    [void][Windows.Forms.MessageBox]::Show($message, 'Durazzno Publisher', 'OK', 'Error')
}
function Confirm($message) {
    [Windows.Forms.MessageBox]::Show($message, 'Durazzno Publisher', 'YesNo', 'Question') -eq 'Yes'
}
function Set-Status($text, $color = $cDim) {
    $statusLabel.Text = $text; $statusLabel.ForeColor = $color
    [Windows.Forms.Application]::DoEvents()
}
function Set-Progress($fraction) {
    if ($null -eq $fraction) { $progressBack.Visible = $false; return }
    $progressBack.Visible = $true
    $progressFill.Width = [int]($progressBack.Width * [Math]::Max(0, [Math]::Min(1, $fraction)))
}

# --- Catalog ------------------------------------------------------------------------------------
$script:apps = New-Object System.Collections.ArrayList

function Load-Catalog {
    $script:apps.Clear()
    if (-not (Test-Path $catalogPath)) { return }
    $json = Get-Content $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($a in @($json.apps) | Where-Object { $_ }) {
        $e = [ordered]@{}
        foreach ($p in $a.PSObject.Properties) { $e[$p.Name] = $p.Value }
        [void]$script:apps.Add($e)
    }
}
function Save-Catalog {
    New-Item -ItemType Directory -Force $catalogDir | Out-Null
    $json = ([ordered]@{ apps = [object[]]$script:apps.ToArray() } | ConvertTo-Json -Depth 6) `
        -replace '\\u0026', '&' -replace '\\u0027', "'" -replace '\\u003c', '<' -replace '\\u003e', '>'
    [IO.File]::WriteAllText($catalogPath, $json, $utf8)
}
# Where an app's file lives, for the list.
function Describe-Source($url) {
    if (-not $url) { return '' }
    if ($url -match '/public/[^/]+/[^/]+/Apps/(.+)/download') { return 'server: ' + [Uri]::UnescapeDataString($Matches[1]) }
    if ($url -match '^[a-z]+://') { return $url }
    "local: $url"
}
function Refresh-Apps {
    $appList.Items.Clear()
    foreach ($a in $script:apps) {
        $item = New-Object Windows.Forms.ListViewItem([string]$a.name)
        [void]$item.SubItems.Add([string]$a.id)
        [void]$item.SubItems.Add([string]$a.version)
        [void]$item.SubItems.Add((Describe-Source $a.url))
        $item.Tag = $a
        [void]$appList.Items.Add($item)
    }
}
function Get-Slug($name) { ($name -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant() }

# A file field from the dialog: a full link stays as is; a local file is copied to apps-catalog\Dist\<id>\ and
# becomes "Dist/<id>/<file>" (publish-apps.ps1 turns it into a server link with the same path).
function Resolve-Source($value, $id) {
    $value = $value.Trim().Trim('"')
    if (-not $value -or $value -match '^[a-z]+://') { return $value }
    if ([IO.Path]::IsPathRooted($value)) {
        if (-not (Test-Path -LiteralPath $value -PathType Leaf)) { throw "File not found: $value" }
        $folder = Join-Path $catalogDir "Dist\$id"
        New-Item -ItemType Directory -Force $folder | Out-Null
        $leaf = Split-Path $value -Leaf
        $target = Join-Path $folder $leaf
        if ($value -ne $target) { Copy-Item -LiteralPath $value -Destination $target -Force }
        return "Dist/$id/$leaf"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $catalogDir $value) -PathType Leaf)) { throw "File not found in apps-catalog: $value" }
    $value
}

function Show-AppDialog($entry) {
    $dlg = New-Object Windows.Forms.Form
    $dlg.Text = $(if ($entry) { "Edit $($entry.name)" } else { 'Add app' })
    $dlg.ClientSize = Sz 600 546; $dlg.StartPosition = 'CenterParent'; $dlg.FormBorderStyle = 'FixedDialog'
    $dlg.MaximizeBox = $false; $dlg.MinimizeBox = $false; $dlg.BackColor = $cBack; $dlg.ForeColor = $cText; $dlg.Font = $font

    $fx = 130; $fw = 450 # field column
    New-Label $dlg 'Name' 18 20 | Out-Null;        $tName = New-TextBox $dlg $fx 16 $fw
    New-Label $dlg 'Id' 18 56 | Out-Null;          $tId = New-TextBox $dlg $fx 52 $fw
    New-Label $dlg 'Version' 18 92 | Out-Null;     $tVersion = New-TextBox $dlg $fx 88 $fw
    New-Label $dlg 'Description' 18 128 | Out-Null; $tDesc = New-TextBox $dlg $fx 124 $fw
    New-Label $dlg 'Message' 18 164 | Out-Null;    $tMsg = New-TextBox $dlg $fx 160 $fw 66 -Multiline
    New-Label $dlg 'File' 18 242 | Out-Null;       $tFile = New-TextBox $dlg $fx 238 350
    $bFile = New-Button $dlg 'Browse' 490 236 90
    New-Label $dlg 'Icon' 18 280 | Out-Null;       $tIcon = New-TextBox $dlg $fx 276 350
    $bIcon = New-Button $dlg 'Browse' 490 274 90
    New-Label $dlg 'SHA-256' 18 318 | Out-Null;    $tHash = New-TextBox $dlg $fx 314 $fw
    $cNoHash = New-CheckBox $dlg 'No hash check (apps that update themselves)' $fx 348 $fw
    New-Label $dlg 'Shortcuts' 18 386 | Out-Null
    $cDesktop = New-CheckBox $dlg 'Desktop' $fx 382 120
    $cStart = New-CheckBox $dlg 'Start Menu' 260 382 160
    $hint = New-Label $dlg ("File and icon: browse a local file (it is copied to apps-catalog\Dist\<id>\ and uploaded with the catalog) " +
        "or paste a full link. Version: text (1.2) or a link to a text file with it. " +
        "SHA-256: leave it empty for local files, or give the hash or a link to a hash file. " +
        "Shortcuts: only for apps with a window.") 18 418 562 74
    $hint.Font = $fontS
    $ok = New-Button $dlg 'Save' 360 498 106 -Primary
    $cancel = New-Button $dlg 'Cancel' 474 498 106
    $dlg.AcceptButton = $ok; $dlg.CancelButton = $cancel
    $cancel.DialogResult = 'Cancel'

    if ($entry) {
        $tName.Text = [string]$entry.name; $tId.Text = [string]$entry.id; $tVersion.Text = [string]$entry.version
        $tDesc.Text = [string]$entry.description; $tMsg.Text = [string]$entry.message
        $tFile.Text = [string]$entry.url; $tIcon.Text = [string]$entry.icon
        if ($entry.Contains('sha256')) {
            if ($entry.sha256) { $tHash.Text = [string]$entry.sha256 } else { $cNoHash.Checked = $true }
        }
        $cDesktop.Checked = $entry.desktopShortcut -eq $true
        $cStart.Checked = $entry.startMenuShortcut -eq $true
    } else { $tVersion.Text = '1.0' }
    $tHash.Enabled = -not $cNoHash.Checked
    $cNoHash.Add_CheckedChanged({ $tHash.Enabled = -not $cNoHash.Checked })

    $browse = {
        param($box, $filter)
        $open = New-Object Windows.Forms.OpenFileDialog
        $open.Filter = $filter
        if ($open.ShowDialog($dlg) -eq 'OK') { $box.Text = $open.FileName }
    }
    $bFile.Add_Click({ & $browse $tFile 'All files|*.*' })
    $bIcon.Add_Click({ & $browse $tIcon 'Images|*.png;*.jpg;*.jpeg|All files|*.*' })

    $script:dialogResult = $null
    $ok.Add_Click({
        try {
            $name = $tName.Text.Trim()
            if (-not $name) { throw 'The app needs a name.' }
            if (-not $tFile.Text.Trim()) { throw 'The app needs a file or a link.' }
            $id = $tId.Text.Trim(); if (-not $id) { $id = Get-Slug $name }
            if ($id -notmatch '^[A-Za-z0-9._-]+$') { throw 'The id can only have letters, numbers, dots, dashes and underscores.' }
            if ($script:apps | Where-Object { $_ -ne $entry -and $_.id -eq $id }) { throw "Another app already uses the id ""$id""." }

            $e = [ordered]@{}
            if ($entry) { foreach ($k in $entry.Keys) { $e[$k] = $entry[$k] } }
            $e.id = $id; $e.name = $name
            foreach ($pair in @(@('description', $tDesc.Text), @('version', $tVersion.Text), @('message', $tMsg.Text))) {
                if ($pair[1].Trim()) { $e[$pair[0]] = $pair[1].Trim() } elseif ($e.Contains($pair[0])) { $e.Remove($pair[0]) }
            }
            $e.url = Resolve-Source $tFile.Text $id
            $icon = Resolve-Source $tIcon.Text $id
            if ($icon) { $e.icon = $icon } elseif ($e.Contains('icon')) { $e.Remove('icon') }
            # "sha256" missing = computed for local files, "" = no check.
            if ($cNoHash.Checked) { $e.sha256 = '' }
            elseif ($tHash.Text.Trim()) { $e.sha256 = $tHash.Text.Trim() }
            elseif ($e.Contains('sha256')) { $e.Remove('sha256') }
            # Shortcuts: written only when on (missing = false).
            foreach ($pair in @(@('desktopShortcut', $cDesktop.Checked), @('startMenuShortcut', $cStart.Checked))) {
                if ($pair[1]) { $e[$pair[0]] = $true } elseif ($e.Contains($pair[0])) { $e.Remove($pair[0]) }
            }
            # A new local file means a new hash and size: let publish-apps.ps1 compute them.
            if ($e.url -notmatch '^[a-z]+://' -and $e.Contains('size')) { $e.Remove('size') }

            $script:dialogResult = $e
            $dlg.DialogResult = 'OK'
        } catch { Show-Error $_.Exception.Message }
    })

    [void]$dlg.ShowDialog($form)
    $script:dialogResult
}

# --- Builds (run in their own console window; it stays open on failure) -------------------------
$script:job = $null
$timer = New-Object Windows.Forms.Timer
$timer.Interval = 400

function Start-Build($scriptFile, $arguments, $label, $onSuccess) {
    $command = "try { & '$scriptFile' $arguments; exit 0 } catch { Write-Host `$_ -ForegroundColor Red; Read-Host 'Failed. Press Enter to close'; exit 1 }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $process = Start-Process powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded -PassThru
    $null = $process.Handle # keeps ExitCode readable after exit
    $script:job = @{ Process = $process; Label = $label; OnSuccess = $onSuccess }
    Set-Busy $true
    Set-Status "$label..." $cAccent
    $timer.Start()
}
$timer.Add_Tick({
    if (-not $script:job -or -not $script:job.Process.HasExited) { return }
    $timer.Stop()
    $job = $script:job; $script:job = $null
    if ($job.Process.ExitCode -eq 0) {
        Set-Status "$($job.Label): done." $cGood
        try { & $job.OnSuccess } catch { Set-Status $_.Exception.Message $cBad; Show-Error $_.Exception.Message }
    } else { Set-Status "$($job.Label) failed. See the console window." $cBad }
    Set-Busy $false
})
function Set-Busy($busy) {
    foreach ($b in $buildMain, $uploadMain, $addApp, $editApp, $removeApp, $uploadApps) { $b.Enabled = -not $busy }
    $form.UseWaitCursor = $busy
}

# --- Upload (Webin Cloud Server API) ------------------------------------------------------------
# Signs in with the account in publisher\.env (a "device" in Webin Cloud, signed out again when the upload ends) and
# sends each file as an upload session: 1 MiB chunks over several WebSockets at once (HTTP PUT if sockets can't
# open). The server writes into a hidden .part file and renames it over the old file only when every chunk is in, so
# clients never see a half-written file. Order matters: binaries first, the manifest / catalog last.
function Format-MB($bytes) { '{0:N1} MB' -f ($bytes / 1MB) }

# Waits for a .NET task without freezing the window.
function Wait-Task($task) {
    while (-not $task.Wait(40)) { [Windows.Forms.Application]::DoEvents() }
    if ($task.GetType().IsGenericType) { $task.Result }
}

function Invoke-Api($method, $path, $body = $null) {
    $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::new($method)), "$server/backend/api/$path"
    if ($script:token) { $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue 'Bearer', $script:token }
    if ($null -ne $body) {
        $request.Content = New-Object System.Net.Http.StringContent ((ConvertTo-Json $body -Compress), [Text.Encoding]::UTF8, 'application/json')
    }
    try {
        $response = Wait-Task ($http.SendAsync($request))
    } catch {
        throw "Can't reach Webin Cloud Server at $server. $($_.Exception.InnerException.Message)"
    }
    $text = Wait-Task ($response.Content.ReadAsStringAsync())
    if (-not $response.IsSuccessStatusCode) {
        $message = try { ($text | ConvertFrom-Json).error } catch { $null }
        $script:lastStatus = [int]$response.StatusCode
        throw "Webin Cloud Server answered $([int]$response.StatusCode): $(if ($message) { $message } else { $response.ReasonPhrase })"
    }
    if ($text) { try { $text | ConvertFrom-Json } catch { $text } }
}

function Connect-WebinCloud {
    Load-Settings # picks up edits to .env without restarting the Publisher
    if (-not (Test-Path $envFile)) { throw "Missing publisher\.env. Copy publisher\.env.example to publisher\.env and fill in the Webin Cloud user and password." }
    if (-not $settings.WEBINCLOUD_USER -or -not $settings.WEBINCLOUD_PASSWORD) { throw 'publisher\.env needs WEBINCLOUD_USER and WEBINCLOUD_PASSWORD.' }
    Set-Status "Signing in to $server..." $cAccent
    $script:token = $null
    try {
        $login = Invoke-Api 'POST' 'devices/login' @{
            usernameOrEmail = $settings.WEBINCLOUD_USER
            password        = $settings.WEBINCLOUD_PASSWORD
            deviceName      = "Durazzno Publisher ($env:COMPUTERNAME)"
            platform        = 'windows'
        }
    } catch {
        if ($script:lastStatus -eq 401) { throw 'Wrong user or password in publisher\.env.' }
        if ($script:lastStatus -eq 403) { throw 'That account still has the default password. Change it in the Webin Cloud web interface first.' }
        throw
    }
    $script:token = $login.accessToken
}

# Signs the Publisher's device out again, so it does not pile up in the account's device list.
function Disconnect-WebinCloud {
    if (-not $script:token) { return }
    try { Invoke-Api 'DELETE' 'devices/current' | Out-Null } catch { }
    $script:token = $null
}

function Read-Chunk($stream, $index, $chunkSize, $size) {
    $start = [long]$index * $chunkSize
    $length = [int][Math]::Min($chunkSize, $size - $start)
    $buffer = New-Object byte[] $length
    $stream.Position = $start
    $read = 0
    while ($read -lt $length) {
        $n = $stream.Read($buffer, $read, $length - $read)
        if ($n -le 0) { throw 'The file got shorter while uploading.' }
        $read += $n
    }
    , $buffer
}

function Report-Upload($name) {
    $fraction = $script:sent / [Math]::Max(1, $script:total)
    Set-Progress $fraction
    Set-Status ("Uploading {0}   {1:N0} %   ({2} of {3})" -f $name, ($fraction * 100), (Format-MB $script:sent), (Format-MB $script:total)) $cAccent
}

# Chunks over upload sockets: one chunk in flight per socket, a chunk the server could not take (409 / 429 / 5xx)
# goes again. Returns $false when no socket can be opened (the caller falls back to HTTP).
function Send-OverSockets($source, $id, $chunkSize, $count, $size, $name) {
    $uri = New-Object Uri (($server -replace '^http', 'ws') + '/backend/api/uploads/socket')
    $hex = $id -replace '-', ''
    $idBytes = [byte[]](0..15 | ForEach-Object { [Convert]::ToByte($hex.Substring($_ * 2, 2), 16) })
    $none = [Threading.CancellationToken]::None
    $sockets = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt [Math]::Min(16, $count); $i++) {
        $ws = New-Object System.Net.WebSockets.ClientWebSocket
        $ws.Options.SetRequestHeader('Authorization', "Bearer $script:token")
        try {
            Wait-Task ($ws.ConnectAsync($uri, $none))
            [void]$sockets.Add(@{ Ws = $ws; Index = -1; Length = 0; Send = $null; Receive = $null; Buffer = (New-Object byte[] 8192) })
        } catch {
            $ws.Dispose()
            if ($sockets.Count -eq 0) { return $false }
            break
        }
    }

    $queue = New-Object 'System.Collections.Generic.Queue[int]'
    for ($i = 0; $i -lt $count; $i++) { $queue.Enqueue($i) }
    $tries = @{}
    $done = 0
    $stream = [IO.File]::OpenRead($source)
    try {
        while ($done -lt $count) {
            foreach ($s in $sockets) {
                if ($s.Index -ge 0 -or $queue.Count -eq 0) { continue }
                $index = $queue.Dequeue()
                $data = Read-Chunk $stream $index $chunkSize $size
                # Message: session id (16 bytes), chunk index (4 bytes, big-endian), chunk bytes.
                $message = New-Object byte[] (20 + $data.Length)
                [Array]::Copy($idBytes, 0, $message, 0, 16)
                $message[16] = [byte](($index -shr 24) -band 0xFF); $message[17] = [byte](($index -shr 16) -band 0xFF)
                $message[18] = [byte](($index -shr 8) -band 0xFF); $message[19] = [byte]($index -band 0xFF)
                [Array]::Copy($data, 0, $message, 20, $data.Length)
                $s.Index = $index
                $s.Length = $data.Length
                $s.Send = $s.Ws.SendAsync([ArraySegment[byte]]::new($message), [System.Net.WebSockets.WebSocketMessageType]::Binary, $true, $none)
                $s.Receive = $s.Ws.ReceiveAsync([ArraySegment[byte]]::new($s.Buffer), $none)
            }

            $busy = @($sockets | Where-Object { $_.Index -ge 0 })
            if ($busy.Count -eq 0) { throw 'Every upload connection closed.' }
            [void][Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]@($busy | ForEach-Object { $_.Receive }), 40)
            [Windows.Forms.Application]::DoEvents()

            foreach ($s in $busy) {
                if (-not $s.Receive.IsCompleted) { continue }
                $index = $s.Index
                $s.Index = -1
                if ($s.Receive.IsFaulted -or $s.Receive.IsCanceled -or ($s.Send -and $s.Send.IsFaulted) -or $s.Receive.Result.MessageType -eq 'Close') {
                    # The connection dropped: that chunk goes again on another one.
                    $queue.Enqueue($index)
                    $sockets.Remove($s)
                    try { $s.Ws.Dispose() } catch { }
                    if ($sockets.Count -eq 0) { throw 'The upload connections to Webin Cloud Server dropped.' }
                    continue
                }
                $reply = [Text.Encoding]::UTF8.GetString($s.Buffer, 0, $s.Receive.Result.Count) | ConvertFrom-Json
                if ($reply.ok) {
                    $done++
                    $script:sent += $s.Length
                    Report-Upload $name
                    continue
                }
                $status = [int]$reply.status
                $tries[$index] = 1 + [int]$tries[$index]
                if (($status -in 409, 429 -or $status -ge 500) -and $tries[$index] -le 5) {
                    Start-Sleep -Milliseconds (200 * $tries[$index])
                    $queue.Enqueue($index)
                    continue
                }
                throw "Webin Cloud Server refused part $index of $($name): $($reply.error)"
            }
        }
    } finally {
        $stream.Dispose()
        foreach ($s in $sockets) {
            try { [void]$s.Ws.CloseOutputAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'done', $none).Wait(2000) } catch { }
            try { $s.Ws.Dispose() } catch { }
        }
    }
    $true
}

# The same chunks with HTTP PUT, six at a time (for proxies without WebSocket support).
function Send-OverHttp($source, $id, $chunkSize, $count, $size, $name) {
    $queue = New-Object 'System.Collections.Generic.Queue[int]'
    for ($i = 0; $i -lt $count; $i++) { $queue.Enqueue($i) }
    $running = @{}
    $tries = @{}
    $done = 0
    $stream = [IO.File]::OpenRead($source)
    try {
        while ($done -lt $count) {
            while ($running.Count -lt 6 -and $queue.Count -gt 0) {
                $index = $queue.Dequeue()
                $data = Read-Chunk $stream $index $chunkSize $size
                $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::Put), "$server/backend/api/files/upload/sessions/$id/chunks/$index"
                $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue 'Bearer', $script:token
                $request.Content = New-Object System.Net.Http.ByteArrayContent (, $data)
                $request.Content.Headers.ContentType = 'application/octet-stream'
                $running[$index] = @{ Task = $http.SendAsync($request); Length = $data.Length }
            }
            [void][Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]@($running.Values | ForEach-Object { $_.Task }), 40)
            [Windows.Forms.Application]::DoEvents()
            foreach ($index in @($running.Keys)) {
                $job = $running[$index]
                if (-not $job.Task.IsCompleted) { continue }
                $running.Remove($index)
                $ok = -not $job.Task.IsFaulted -and $job.Task.Result.IsSuccessStatusCode
                if ($ok) { $done++; $script:sent += $job.Length; Report-Upload $name; continue }
                $tries[$index] = 1 + [int]$tries[$index]
                if ($tries[$index] -gt 5) { throw "Part $index of $name could not be uploaded." }
                Start-Sleep -Milliseconds (200 * $tries[$index])
                $queue.Enqueue($index)
            }
        }
    } finally { $stream.Dispose() }
}

# One file into a server folder, replacing what is there; then checks the size the server lists.
function Send-File($source, $folder) {
    $name = Split-Path $source -Leaf
    $size = (Get-Item -LiteralPath $source).Length
    Report-Upload $name
    $session = Invoke-Api 'POST' 'files/upload/sessions' @{ path = $folder; name = $name; size = $size; onConflict = 'overwrite'; transport = 'socket' }
    $chunkSize = [long]$session.chunkSize
    $count = [int][Math]::Ceiling($size / $chunkSize)
    try {
        if (-not (Send-OverSockets $source $session.id $chunkSize $count $size $name)) {
            Send-OverHttp $source $session.id $chunkSize $count $size $name
        }
        Set-Status "Finishing $name..." $cAccent
        Invoke-Api 'POST' "files/upload/sessions/$($session.id)/complete" | Out-Null
    } catch {
        try { Invoke-Api 'DELETE' "files/upload/sessions/$($session.id)" | Out-Null } catch { }
        throw
    }
    $listing = Invoke-Api 'GET' "files?path=$([Uri]::EscapeDataString($folder))"
    $entry = @($listing.entries) | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $entry) { throw "$name is not in $folder after the upload." }
    if ($null -ne $entry.size -and [long]$entry.size -ne $size) { throw "$name arrived with the wrong size ($($entry.size) of $size bytes)." }
}

# Signs in, uploads every { Source; Folder } in order, and signs out.
function Send-Files($items) {
    $script:total = ($items | ForEach-Object { (Get-Item -LiteralPath $_.Source).Length } | Measure-Object -Sum).Sum
    $script:sent = 0
    Set-Busy $true
    try {
        Connect-WebinCloud
        foreach ($item in $items) { Send-File $item.Source $item.Folder }
    } finally {
        Disconnect-WebinCloud
        Set-Progress $null
        Set-Busy $false
    }
}

function Upload-Main {
    $files = @(
        (Join-Path $distServer 'DurazznoApp.exe'),
        (Join-Path $distServer 'manifest.json.sig'), (Join-Path $distServer 'manifest.json')) # manifest last
    foreach ($f in $files) { if (-not (Test-Path $f)) { throw "Missing $f. Build first." } }
    Send-Files @($files | ForEach-Object { @{ Source = $_; Folder = $mainFolder } })
}
function Upload-Apps {
    $catalog = Join-Path $distApps 'apps.json'
    if (-not (Test-Path $catalog)) { throw "Missing $catalog. Build first." }
    # App files first (same relative folders), then the signature and the catalog itself.
    $items = @(Get-ChildItem $distApps -Recurse -File | Where-Object { $_.Name -notin 'apps.json', 'apps.json.sig' } | ForEach-Object {
        $relative = Split-Path $_.FullName.Substring($distApps.Length + 1) -Parent
        @{ Source = $_.FullName; Folder = $(if ($relative) { "$appsFolder/$($relative.Replace('\', '/'))" } else { $appsFolder }) }
    })
    $items += @{ Source = "$catalog.sig"; Folder = $appsFolder }
    $items += @{ Source = $catalog; Folder = $appsFolder }
    Send-Files $items
}

# --- Status -------------------------------------------------------------------------------------
function Read-Version($manifestPath) {
    try { (Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json).app.version } catch { $null }
}
# What Durazzno App sees: the public manifest and catalog (no sign-in needed).
function Read-Public($pathName, $file) {
    try {
        $response = Wait-Task ($http.GetAsync((Public-Link $pathName $file)))
        if (-not $response.IsSuccessStatusCode) { return @{ Online = $true; Json = $null } }
        @{ Online = $true; Json = (Wait-Task ($response.Content.ReadAsStringAsync())) | ConvertFrom-Json }
    } catch {
        @{ Online = $false; Json = $null }
    }
}
function Refresh-Main {
    $built = Read-Version (Join-Path $distServer 'manifest.json')
    $builtLabel.Text = 'Built: ' + $(if ($built) { $built } else { 'nothing yet' })
    Set-Status "Reading $server..." $cAccent
    $main = Read-Public $publicMain 'manifest.json'
    $serverLabel.Text = 'Server: ' + $(if (-not $main.Online) { 'unreachable' } elseif ($main.Json) { $main.Json.app.version } else { 'nothing yet' })
    $serverLabel.ForeColor = $(if ($main.Online) { $cText } else { $cBad })
    $apps = if ($main.Online) { Read-Public $publicApps 'apps.json' }
    $appsServerLabel.Text = $(if ($apps.Json) { "$(@($apps.Json.apps).Count) app(s) on the server" } else { '' })
    $setup = if (-not (Test-Path $envFile)) { '  ·  publisher\.env missing (needed to upload)' } else { '' }
    Set-Status $(if ($main.Online) { "Ready.$setup" } else { "Cannot reach $server.$setup" }) $(if ($main.Online -and -not $setup) { $cDim } elseif ($main.Online) { $cAccent } else { $cBad })
}
function Notes-Path { Join-Path $scripts "release-notes\$($versionBox.Text.Trim()).txt" }
function Load-Notes {
    $path = Notes-Path
    $notesBox.Text = if (Test-Path $path) { (Get-Content $path -Raw -Encoding UTF8).Trim() } else { '' }
}

# --- Window -------------------------------------------------------------------------------------
$form = New-Object Windows.Forms.Form
$form.Text = 'Durazzno Publisher'
$form.ClientSize = Sz $baseWidth $baseHeight; $form.StartPosition = 'CenterScreen'; $form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox = $false; $form.BackColor = $cBack; $form.ForeColor = $cText; $form.Font = $font
$icon = Join-Path $root 'src\DurazznoApp\Assets\Logos\durazzno_icon.ico'
if (Test-Path $icon) { $form.Icon = New-Object Drawing.Icon $icon }

# Main
$main = New-Card $form 'DURAZZNO APP  (Main)' 16 16 828 244
New-Label $main 'Version' 14 54 64 | Out-Null
$versionBox = New-TextBox $main 80 50 100
$builtLabel = New-Label $main '' 200 54 220 24 $cText
$serverLabel = New-Label $main '' 430 54 384 24 $cText
New-Label $main "What's new (scripts\release-notes\<version>.txt)" 14 90 500 | Out-Null
$notesBox = New-TextBox $main 14 116 800 76 -Multiline
$buildMain = New-Button $main 'Build' 560 200 120
$uploadMain = New-Button $main 'Upload to server' 690 200 124 -Primary

# Apps
$appsCard = New-Card $form 'APPS CATALOG  (Apps)' 16 272 828 360
$appsServerLabel = New-Label $appsCard '' 420 14 394 24
$appsServerLabel.TextAlign = 'MiddleRight'
$appList = New-Object Windows.Forms.ListView
$appList.Location = Pt 14 50; $appList.Size = Sz 800 252; $appList.View = 'Details'; $appList.FullRowSelect = $true
$appList.MultiSelect = $false; $appList.HideSelection = $false; $appList.BorderStyle = 'None'
$appList.BackColor = $cInput; $appList.ForeColor = $cText; $appList.OwnerDraw = $true
foreach ($col in @(@('Name', 180), @('Id', 140), @('Version', 80), @('File', 396))) { [void]$appList.Columns.Add($col[0], (S $col[1])) }
$headerBrush = New-Object Drawing.SolidBrush $cCard
$appList.Add_DrawColumnHeader({
    param($s, $e)
    $e.Graphics.FillRectangle($headerBrush, $e.Bounds)
    $bounds = $e.Bounds; $bounds.X += 6
    [Windows.Forms.TextRenderer]::DrawText($e.Graphics, $e.Header.Text, $font, $bounds, $cDim, [Windows.Forms.TextFormatFlags]'VerticalCenter, Left')
})
$appList.Add_DrawItem({ param($s, $e) $e.DrawDefault = $true })
$appList.Add_DrawSubItem({ param($s, $e) $e.DrawDefault = $true })
$appsCard.Controls.Add($appList)
$addApp = New-Button $appsCard 'Add' 14 314 96
$editApp = New-Button $appsCard 'Edit' 118 314 96
$removeApp = New-Button $appsCard 'Remove' 222 314 96
$uploadApps = New-Button $appsCard 'Upload to server' 690 314 124 -Primary

# Progress bar (shown while uploading) and status line
$progressBack = New-Object Windows.Forms.Panel
$progressBack.Location = Pt 16 642; $progressBack.Size = Sz 828 8; $progressBack.BackColor = $cInput; $progressBack.Visible = $false
$progressFill = New-Object Windows.Forms.Panel
$progressFill.Location = New-Object Drawing.Point 0, 0; $progressFill.Size = New-Object Drawing.Size 0, $progressBack.Height
$progressFill.BackColor = $cAccent
$progressBack.Controls.Add($progressFill)
$form.Controls.Add($progressBack)
$statusLabel = New-Label $form '' 16 656 828 26

# --- Actions ------------------------------------------------------------------------------------
$versionBox.Add_Leave({ Load-Notes })

$buildMain.Add_Click({
    $version = $versionBox.Text.Trim()
    if ($version -notmatch '^\d+(\.\d+){1,3}$') { Show-Error 'The version must look like 3.0.1.'; return }
    if ($notesBox.Text.Trim()) {
        New-Item -ItemType Directory -Force (Split-Path (Notes-Path) -Parent) | Out-Null
        [IO.File]::WriteAllText((Notes-Path), $notesBox.Text.Trim() + "`r`n", $utf8)
    }
    Start-Build (Join-Path $scripts 'publish-main.ps1') "-Version '$version'" "Building Durazzno App $version" { Refresh-Main }
})
$uploadMain.Add_Click({
    try {
        $built = Read-Version (Join-Path $distServer 'manifest.json')
        if (-not $built) { throw 'Nothing built yet. Click Build first.' }
        if (-not (Confirm "Upload Durazzno App $built to the server?`n`nEvery installed copy will update to it.")) { return }
        Upload-Main
        Refresh-Main
        Set-Status "Durazzno App $built is on the server." $cGood
    } catch { Set-Status $_.Exception.Message $cBad; Show-Error $_.Exception.Message }
})

$addApp.Add_Click({
    $e = Show-AppDialog $null
    if ($e) { [void]$script:apps.Add($e); Save-Catalog; Refresh-Apps; Set-Status "$($e.name) added. Upload to publish it." $cGood }
})
$editAction = {
    if ($appList.SelectedItems.Count -eq 0) { return }
    $old = $appList.SelectedItems[0].Tag
    $e = Show-AppDialog $old
    if ($e) {
        $script:apps[$script:apps.IndexOf($old)] = $e
        Save-Catalog; Refresh-Apps; Set-Status "$($e.name) saved. Upload to publish the change." $cGood
    }
}
$editApp.Add_Click($editAction)
$appList.Add_DoubleClick($editAction)
$removeApp.Add_Click({
    if ($appList.SelectedItems.Count -eq 0) { return }
    $old = $appList.SelectedItems[0].Tag
    if (-not (Confirm "Remove $($old.name) from the catalog?`n`nIt disappears from the Online tab after the next upload.")) { return }
    $script:apps.Remove($old)
    $local = Join-Path $catalogDir "Dist\$($old.id)"
    if ($old.id -and (Test-Path $local)) { Remove-Item $local -Recurse -Force }
    Save-Catalog; Refresh-Apps; Set-Status "$($old.name) removed. Upload to publish the change." $cGood
})
$uploadApps.Add_Click({
    if (-not (Confirm "Sign the catalog ($($script:apps.Count) app(s)) and upload it to the server?")) { return }
    Start-Build (Join-Path $scripts 'publish-apps.ps1') '' 'Signing the apps catalog' {
        Upload-Apps
        Refresh-Main
        Set-Status "Apps catalog is on the server ($($script:apps.Count) app(s))." $cGood
    }
})

$form.Add_Shown({
    $versionBox.Text = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    Load-Notes
    try { Load-Catalog } catch { Show-Error "Could not read $catalogPath`n`n$($_.Exception.Message)" }
    Refresh-Apps
    Refresh-Main
})

try { [void]$form.ShowDialog() }
catch { Show-Error $_.Exception.Message }
