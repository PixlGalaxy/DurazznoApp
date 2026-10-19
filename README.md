# Durazzno App

Tray app for Windows with the PC tools I actually use, all in one window: disk health, disk benchmark, disk usage,
startup items, timer resolution, drivers and scheduled shutdowns. It updates itself from my Webin Cloud Server.

It's a C# (.NET 9, WPF) rewrite of the original 2022 Python version. Everything ships as a single exe,
`DurazznoApp.exe`, with no .NET or Python install needed.

## What's inside

- **Dashboard**: CPU, memory, storage, uptime, hardware and drives at a glance.
- **DiskInfo**: health, temperature and S.M.A.R.T. for SATA, NVMe and USB drives, same approach as CrystalDiskInfo.
  It only polls while the page is open, plus one check at startup that warns if a disk is going bad.
- **DiskMark**: the CrystalDiskMark tests (SEQ1M, RND4K) running on DiskSpd.
- **DiskMap**: see what's eating space on each drive.
- **Startup**: everything that starts with Windows (Run keys, startup folders, tasks, services) with signature
  checks, and you can turn things on and off.
- **TimerResolution**: set and hold the Windows timer resolution. Only applied if you turn it on.
- **DriverHub**: my take on Driver Easy, without the paid database. Updates come from Windows Update, the Microsoft
  Update Catalog, Intel, NVIDIA, AMD and the Dell / HP / Lenovo catalogs. Drivers are checked before and after
  installing: if a device stops working after an update, it goes back to the old driver automatically. It can also
  roll back drivers, remove old devices, clean up the driver store and back up drivers.
- **PowerCuts**: schedule a shutdown or restart, with warnings 5 minutes and 1 minute before.

## Updates

The app checks Webin Cloud Server in the background. With auto-update on, new versions download quietly and install
the next time the PC starts. Otherwise you get a notification and an **APPLY UPDATE** button. Releases are signed,
so the app only installs builds I published. If a new version crashes right after installing, the app goes back to
the previous one on its own.

The server address lives in one place: `WebinCloud.Server` in `src/DurazznoApp/Network/WebinCloud.cs`.

## Building

```powershell
dotnet build DurazznoApp.sln
.\scripts\publish-main.ps1 -Version 3.0.1 -Notes "what changed"
```

You need the .NET 9 SDK. Signing a release needs the private key in `publisher/crypto/`, which is not in the repo.

`publish-main.ps1` leaves the app in `dist\DurazznoApp\` and the files to upload in `dist\server\`.

## Project layout

```
src/DurazznoApp/         the app
scripts/publish-main.ps1 build and sign a release
scripts/publish-apps.ps1 build and sign the Apps catalog
```

Settings and logs are stored in `%LOCALAPPDATA%\DurazznoApp\`.

## Credits

Icons by [Lucide](https://lucide.dev), Rajdhani font (OFL), benchmarks powered by
[DiskSpd](https://github.com/microsoft/diskspd).
