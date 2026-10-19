##                                                                         ##
##        Durazzno App 2.x -> 3.0 migration (old Python app side)          ##
##                                                                         ##
#
# Goes at the top of the old DurazznoApp.pyw (before the single-instance check) and runs on every start:
#
#   try:    download DurazznoAppInstaller.exe, run it with --headless; on exit code 0 open the new app, schedule the
#           clean-up of C:\Galaxy_Apps (and the old shortcuts) and quit.
#   except: anything fails (no internet, server down, installer error) -> the old app just opens as usual and the
#           migration is tried again the next time it starts.
#
# Installer exit codes: 0 installed, 1 unexpected error, 2 Webin Cloud Server unavailable,
# 3 release not trusted (signature / SHA-256), 4 could not install (permissions, files in use).

import base64
import ctypes
import os
import subprocess
import tempfile
import urllib.request

INSTALLER_URL = ("https://webincloud.itzgalaxy.com/backend/api/public/durazznoapp/DurazznoApp/"
                 "Installer/DurazznoAppInstaller.exe/download?inline=1&path=")
NEW_APP = os.path.join(os.environ["LOCALAPPDATA"], "Programs", "DurazznoApp", "DurazznoApp.exe")
OLD_DIR = r"C:\Galaxy_Apps"

# Runs after this process has exited (Windows won't delete a folder a running process works in). Elevated when the
# user allows it, because the old installer put its shortcuts in the all-users Start Menu, Startup and Public Desktop.
CLEANUP = r"""
$ErrorActionPreference = 'SilentlyContinue'
Wait-Process -Id {pid} -Timeout 60
Get-CimInstance Win32_Process |
    Where-Object {{ $_.ExecutablePath -like '{old}\*' -or ($_.Name -match '^pythonw?\.exe$|^pyw?\.exe$' -and $_.CommandLine -like '*Galaxy_Apps*') }} |
    ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force }}
Start-Sleep -Seconds 2
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in 'Startup', 'CommonStartup', 'DesktopDirectory', 'CommonDesktopDirectory', 'Programs', 'CommonPrograms') {{
    Get-ChildItem ([Environment]::GetFolderPath($folder)) -Filter *.lnk -Recurse |
        Where-Object {{ $shell.CreateShortcut($_.FullName).TargetPath -like '*Galaxy_Apps*' }} | Remove-Item -Force
}}
foreach ($menu in [Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('Programs')) {{
    foreach ($name in 'PowerCuts', 'ModPack Switcher') {{
        $folder = Join-Path $menu $name
        if ((Test-Path $folder) -and -not (Get-ChildItem $folder -Recurse -Force)) {{ Remove-Item $folder -Force }}
    }}
}}
Remove-Item '{old}' -Recurse -Force
"""


def _run_cleanup():
    script = CLEANUP.format(pid=os.getpid(), old=OLD_DIR)
    # Only the old app's own processes are stopped: anything running from C:\Galaxy_Apps, or Python running a script
    # from it. -EncodedCommand keeps the path out of this PowerShell's command line.
    encoded = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
    params = f"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}"
    shell = ctypes.windll.shell32
    if shell.ShellExecuteW(None, "runas", "powershell.exe", params, None, 0) <= 32:
        # Permission declined: remove what a normal user can (the folder and per-user shortcuts).
        shell.ShellExecuteW(None, "open", "powershell.exe", params, None, 0)


def migrate_to_durazzno_3():
    """Moves this PC to Durazzno App 3. Returns only if it could not (the old app then starts as usual)."""
    try:
        installer = os.path.join(tempfile.gettempdir(), "DurazznoAppInstaller.exe")
        urllib.request.urlretrieve(INSTALLER_URL, installer)
        code = subprocess.run([installer, "--headless"], timeout=900).returncode
        if code != 0:
            raise RuntimeError(f"Durazzno App Installer exit code {code}")

        subprocess.Popen([NEW_APP, "--open"])
        _run_cleanup()
        os._exit(0)
    except Exception:
        return


migrate_to_durazzno_3()
