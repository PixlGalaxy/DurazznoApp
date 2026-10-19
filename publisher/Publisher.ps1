# Durazzno Publisher: builds, signs and uploads Durazzno App (Main) and the Apps catalog to Webin Cloud Server over SMB.
# Start it with Publisher.bat. Signing needs publisher\crypto\update-signing-key.xml (git-ignored, never commit).
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
$share       = '\\192.168.0.150\WebinCloud\Data\DurazznoApp'
$utf8        = New-Object System.Text.UTF8Encoding $false

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

# --- Upload (SMB) -------------------------------------------------------------------------------
function Assert-Share {
    Set-Status "Connecting to $share..." $cAccent
    if (-not (Test-Path -LiteralPath $share)) { throw "Cannot reach $share. Is the server on and the PC on the LAN?" }
}
function Format-MB($bytes) { '{0:N1} MB' -f ($bytes / 1MB) }

# name.ext -> name_new.ext, next to it.
function Get-StagedPath($target) {
    $leaf = Split-Path $target -Leaf
    Join-Path (Split-Path $target -Parent) ([IO.Path]::GetFileNameWithoutExtension($leaf) + '_new' + [IO.Path]::GetExtension($leaf))
}

function Copy-WithProgress($source, $target) {
    $name = Split-Path $source -Leaf
    $in = [IO.File]::OpenRead($source)
    $out = [IO.File]::Create($target)
    try {
        $buffer = New-Object byte[] (2MB)
        while (($read = $in.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $out.Write($buffer, 0, $read)
            $script:sent += $read
            $fraction = $script:sent / [Math]::Max(1, $script:total)
            Set-Progress $fraction
            Set-Status ("Uploading {0}   {1:N0} %   ({2} of {3})" -f $name, ($fraction * 100), (Format-MB $script:sent), (Format-MB $script:total)) $cAccent
        }
    } finally { $in.Dispose(); $out.Dispose() }
}

# Uploads files without ever leaving a half-written file under its real name: each one goes up as name_new.ext and
# is checked; only when all of them made it are the old files deleted and the new ones renamed, in the given order.
# On failure the _new files are removed and the server keeps the old ones.
function Send-Files($pairs) {
    $script:total = ($pairs | ForEach-Object { (Get-Item -LiteralPath $_.Source).Length } | Measure-Object -Sum).Sum
    $script:sent = 0
    $staged = New-Object System.Collections.ArrayList
    Set-Busy $true
    try {
        try {
            foreach ($p in $pairs) {
                $dir = Split-Path $p.Target -Parent
                if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
                $new = Get-StagedPath $p.Target
                [void]$staged.Add(@{ New = $new; Target = $p.Target })
                Copy-WithProgress $p.Source $new
                Set-Status "Checking $(Split-Path $p.Target -Leaf)..." $cAccent
                if ((Get-FileHash -LiteralPath $p.Source).Hash -ne (Get-FileHash -LiteralPath $new).Hash) {
                    throw "$(Split-Path $p.Target -Leaf) arrived damaged (hash mismatch). Nothing was replaced."
                }
            }
        } catch {
            foreach ($s in $staged) { Remove-Item -LiteralPath $s.New -Force -ErrorAction SilentlyContinue }
            throw
        }
        Set-Status 'Replacing the old files...' $cAccent
        foreach ($s in $staged) {
            if (Test-Path -LiteralPath $s.Target) { Remove-Item -LiteralPath $s.Target -Force }
            Rename-Item -LiteralPath $s.New -NewName (Split-Path $s.Target -Leaf)
        }
    } finally {
        Set-Progress $null
        Set-Busy $false
    }
}

function Upload-Main {
    $files = @(
        (Join-Path $distServer 'DurazznoApp.exe'),
        (Join-Path $distServer 'manifest.json.sig'), (Join-Path $distServer 'manifest.json')) # manifest last
    foreach ($f in $files) { if (-not (Test-Path $f)) { throw "Missing $f. Build first." } }
    Assert-Share
    Send-Files @($files | ForEach-Object { @{ Source = $_; Target = Join-Path "$share\Main" (Split-Path $_ -Leaf) } })
}
function Upload-Apps {
    $catalog = Join-Path $distApps 'apps.json'
    if (-not (Test-Path $catalog)) { throw "Missing $catalog. Build first." }
    Assert-Share
    # App files first (same relative paths), then the signature and the catalog itself.
    $pairs = @(Get-ChildItem $distApps -Recurse -File | Where-Object { $_.Name -notin 'apps.json', 'apps.json.sig' } | ForEach-Object {
        @{ Source = $_.FullName; Target = Join-Path "$share\Apps" $_.FullName.Substring($distApps.Length + 1) }
    })
    $pairs += @{ Source = "$catalog.sig"; Target = "$share\Apps\apps.json.sig" }
    $pairs += @{ Source = $catalog; Target = "$share\Apps\apps.json" }
    Send-Files $pairs
}

# --- Status -------------------------------------------------------------------------------------
function Read-Version($manifestPath) {
    try { (Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json).app.version } catch { $null }
}
function Refresh-Main {
    $built = Read-Version (Join-Path $distServer 'manifest.json')
    $builtLabel.Text = 'Built: ' + $(if ($built) { $built } else { 'nothing yet' })
    Set-Status "Reading the server..." $cAccent
    $online = Test-Path -LiteralPath $share
    $server = if ($online) { Read-Version "$share\Main\manifest.json" }
    $serverLabel.Text = 'Server: ' + $(if (-not $online) { 'unreachable' } elseif ($server) { $server } else { 'nothing yet' })
    $serverLabel.ForeColor = $(if ($online) { $cText } else { $cBad })
    $appsServer = if ($online) { try { @((Get-Content "$share\Apps\apps.json" -Raw -Encoding UTF8 | ConvertFrom-Json).apps).Count } catch { $null } }
    $appsServerLabel.Text = $(if ($null -ne $appsServer) { "$appsServer app(s) on the server" } else { '' })
    Set-Status $(if ($online) { 'Ready.' } else { "Cannot reach $share." }) $(if ($online) { $cDim } else { $cBad })
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
