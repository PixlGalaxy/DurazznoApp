# Durazzno App

Tray app (Windows) that bundles PC tools — CrystalDiskInfo, CrystalDiskMark, WinDirStat, Autoruns, Timer Resolution, Driver Easy, ItzGalaxy KMS — behind one menu. Rewritten in C# (.NET 9, WinForms) from the 2022 Python version kept in `Old/`.

The background process is **DurazznoApp Daemon** (`DurazznoAppDaemon.exe`): one self-contained file, no .NET or Python install needed.

- **Main window** (WPF, iCUE-style): double-click the tray icon, choose "Open Durazzno App", or launch the exe again. Sidebar: Dashboard (live CPU, memory, storage, uptime, hardware, drives), one page per tool, Settings. Closing the window sends it back to the tray; exit from the tray menu.
- **Tools** get their own names: DiskInfo (CrystalDiskInfo), DiskMark (CrystalDiskMark), DiskMap (WinDirStat), StartupHub (Autoruns), TimerResolution (Timer Resolution), DriverHub (Driver Easy). Until each native page is built, its page launches the bundled classic program.
- Started with Windows (`--background`) it stays in the tray; started by hand it opens the window.
- **DiskInfo** (native): health, temperature, endurance and the full S.M.A.R.T. table. Transports follow CrystalDiskInfo 9.9.3: NVMe storage query (works without admin), ATA SMART IOCTL / ATA pass-through, SAT for USB-SATA bridges, JMicron / ASMedia / Realtek USB-NVMe bridges, Intel RST NVMe. Everything except the in-box NVMe query needs admin; the app then starts an elevated helper (`--disk-agent`, one UAC prompt per session) and talks to it over a per-user named pipe. `DurazznoAppDaemon.exe --disk-dump out.json` writes a JSON report for diagnostics.
- **DiskMark** (native): the CrystalDiskMark 9 workloads (SEQ1M Q8T1 / Q1T1, RND4K Q32T1 / Q1T1, or the NVMe profile) run through the bundled DiskSpd with the same switches (`-S -W0 -L`, 5 s passes, best of N after a warm-up), on a random-filled test file that is deleted afterwards. Last result per drive is kept in `%LOCALAPPDATA%\DurazznoApp\diskmark.json`.
- **StartupHub** (native): Run/RunOnce keys, Startup folders, scheduled tasks, auto-start services and security-relevant locations (Winlogon, BootExecute, AppInit, IFEO), with Authenticode checks (embedded + catalog). Logon entries are toggled through the same StartupApproved values Task Manager uses; machine-wide changes ask for UAC per change.
- **TimerResolution** (native): NtQueryTimerResolution / NtSetTimerResolution, held by the tray app, optional "always active" on start, and a Sleep(1) latency test.
- **DriverHub** (native): devices via WMI (Win32_PnPEntity + Win32_PnPSignedDriver), problem codes, driver age, and driver updates from two sources: the Windows Update Agent API and the Microsoft Update Catalog, which lists every published version (including the newer ones Windows Update holds back). Catalog offers are only shown when they apply to this Windows build and architecture, list one of the device's IDs in "Supported Hardware IDs" no less specific than the ID the current driver matched (read from the driver's class key), match a vendor-specific ID (never ACPI\PNP… / class codes), come from the same provider as a vendor driver they replace (no Extension INFs), and are newer (higher version, or same version with a newer date). Software components (SWC\…: Intel DTT, THX, Thunderbolt HSA) are included. Offers the catalog lists only for a newer Windows are kept, but the INF's [Manufacturer] decorations must cover this build before installing. Catalog packages are downloaded from Microsoft's CDN, checked against the published SHA-1, unpacked with tar, the INF declaring the device's ID is located and its catalog must be Microsoft (WHQL) signed, then installed with UpdateDriverForPlugAndPlayDevices. Catalog answers are cached for 6 hours and searches run 4 at a time; if every search comes back empty the catalog is rate-limiting and the page says so. A third source is the chip makers themselves: Intel's public Driver & Support Assistant data package (`dsadata.intel.com/data/en`: hardware ID patterns, versions with Intel's comparison mask, supported builds, installer URL + SHA-1) and NVIDIA's GeForce Experience lookup by GPU device ID (newest WHQL Game Ready driver; 617.42 = 32.0.16.1742). AMD has no public API, so its official driver pages are read: the WHQL Recommended Adrenalin for RDNA Radeon GPUs (RX 5000+ and RDNA integrated graphics) and AMD Chipset Software (universal Ryzen package: chipset, CPU power management), compared with the installed "AMD Software" / "AMD Chipset Software" versions; downloads send amd.com as Referer like the site's own links. PC makers without an API (Razer, ASRock, ASUS, MSI, Gigabyte...) get a support-page button for BIOS, firmware and board-specific drivers. Their installers must carry an Intel / NVIDIA / AMD Authenticode signature; NVIDIA's runs silently (`-s -noreboot -noeula`), Intel's opens its own window when Intel disallows silent installs. Installs run elevated (`--install-drivers <job.json> <progress.jsonl>`, one UAC prompt per batch): a restore point is opened (SRSetRestorePoint), then each driver is downloaded, verified and installed one after another while JSON progress lines feed a bar on its row (green downloading %, blue installing, orange error); Cancel stops the current download and skips the rest, but a driver already installing always finishes. One DriverHub notification sums up the batch ("10/10 drivers installed successfully"). More views: "History" lists third-party drivers with older versions still in the driver store (SetupDiBuildDriverInfoList) and rolls a device back to one (DiInstallDevice, elevated `--rollback-driver`, restore point first); "Cleanup" lists driver packages no connected device uses, with their size, and deletes them (SetupUninstallOEMInf, elevated `--delete-driver-packages`); "Backups" makes and lists backups in Documents\Durazzno App\Driver Backups and restores them (pnputil /add-driver /install); right-click an update to install, hide it (listed under "Hidden") or skip it until the next check. PC maker catalogs are read on Dell (CatalogPC.cab by SKU, MD5, /s), HP (HPIA reference by board, SHA-256, /s) and Lenovo (per machine type, SHA-256, package command lines) machines; `DURAZZNO_OEM=dell:SKU` + `--oem-test <file>` checks a catalog from another PC. Notifications carry a coloured badge for the outcome (success, warning, error, cancelled). The "Unused" filter lists devices Windows remembers but that are not connected (as Uwe Sieber's DeviceCleanup does: SetupAPI including non-present devices, last connected from DEVPKEY_Device_LastRemovalDate / LastArrivalDate); remove one or all of them (CM_Uninstall_DevNode, elevated `--remove-devices`, one UAC prompt). "Notify about new drivers" runs the same search in the background every 6 h / day / week and announces only new updates; `--backup-drivers` exports with pnputil. Designed after reverse-engineering Driver Easy's architecture (WMI + SetupAPI scan, WUA search, restore point, UpdateDriverForPlugAndPlayDevices); its proprietary driver database is not used.
- **PowerCuts**: Durazzno counts down itself (also from the tray), warns 5 minutes and 1 minute before, and with 30 seconds left hands the shutdown / restart to Windows (`shutdown /t 30`), as the original 2022 plugin did. The deadline is saved in `%LOCALAPPDATA%\DurazznoApp\powercuts.json`, so it resumes after an app restart. Exiting from the tray mid-countdown asks first: keep running, exit and let Windows shut down on time (no warnings), or exit and cancel.
- **Notifications** are Windows toasts sent as separate branded senders (Durazzno App, PowerCuts, DiskInfo, Webin Cloud Server), registered under `HKCU\Software\Classes\AppUserModelId\Durazzno.*`; the tray balloon is the fallback. DiskInfo does not poll in the background: it refreshes only while its page is on screen, plus one S.M.A.R.T. check ~45 s after the app starts that notifies about disks in Caution / Bad health. Dialogs use Durazzno's own card-style dialog instead of the system message box.
- **Interface size** (Settings → Window): Compact 80 % (default), Normal 90 %, Large 100 %.
- **Durazzno Terminal** (`DurazznoTerminal.exe`, next to the app; Settings → Data → Open terminal): a separate Native AOT recovery console (~5 MB, no .NET needed) that still works when the app does not start. Commands: `status`, `start`, `stop`, `restart`, `repair` (re-download app + Tools from the manifest, SHA-256 checked), `rollback` (the app keeps the previous exe as `.old`), `reset` (settings, with a backup), `logs [n]`, `open [app|data|logs]`, `startup on|off`, `timer off`, `powercuts cancel`, `uninstall`. Also one-shot: `DurazznoTerminal.exe repair`. Publishing it needs Visual Studio's C++ build tools.

## Layout

```
src/DurazznoApp/        the app
  Core/                 paths, settings, log, single instance, start with Windows
  Network/              Webin Cloud client + connectivity monitor
  Updates/              manifest model + self-updater
  Tools/                tool catalog and launcher
  Tray/                 tray icon, menu renderer, GDI icons
  Hardware/             CPU / memory / drive / hardware info
  UI/                   WPF main window, pages, theme (Rajdhani + RaceSpace fonts, Lucide icons)
tools/                  third-party binaries (NOT in git) -> shipped as Tools\ / Tools.zip
scripts/publish.ps1     builds a release
```

## Build

```powershell
dotnet build src/DurazznoApp/DurazznoApp.csproj          # dev build (bin/Debug)
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 [-Version 3.0.1] [-Notes "..."] [-MinimumSupportedVersion 3.0.0]
```

`publish.ps1` produces:

- `dist/DurazznoApp/`: `DurazznoAppDaemon.exe` + `Tools\`, ready to copy to any PC.
- `dist/server/`: `manifest.json`, `DurazznoAppDaemon.exe`, `Tools.zip`. Upload these to one folder on Webin Cloud Server.

The version comes from `<Version>` in the `.csproj` unless `-Version` is given.

## Online / offline

A connectivity monitor checks Webin Cloud Server at startup, whenever the network changes, every 10 min while online and every 30 s while offline. The app is never restarted to switch modes:

| State | Tray icon | Menu |
|---|---|---|
| Online | Logo (blue dot = update available, red dot = unsupported version) | Check for updates / Install update |
| Server unreachable / no internet | Logo with an orange dot | Retry connection; Driver Easy, KMS and the website are disabled if there is no internet |

## Update system

The app downloads `manifest.json` (default URL in `Network/ServerClient.cs`, `DefaultManifestUrl`). Relative URLs inside it are resolved against the manifest's own URL:

```json
{
  "app":   { "version": "3.0.1", "url": "DurazznoAppDaemon.exe", "sha256": "…", "notes": "optional" },
  "tools": { "version": "5a4de5c0df5e", "url": "Tools.zip", "sha256": "…" },
  "minimumSupportedVersion": "3.0.0"
}
```

- **app**: updated when `version` is higher than the running version.
- **tools**: updated when `version` differs from `Tools\tools-version.txt`. The version is a hash of the tools content, so Tools.zip is only downloaded again when a tool changed. Files the tools create (`DiskInfo.ini`, `DiskMark32.ini`, `Smart\`) are kept across updates.
- Every download is checked against its `sha256` before anything is replaced. HTTPS certificates are validated.
- The running exe is renamed to `.old`, the new one takes its place, the app restarts with `--updated`, and the next start deletes `.old`.
- With "Install updates automatically" on, updates install as soon as they are found. With it off, the user gets a notification and an "Install update" menu item.

To test against a local folder, add `"manifestUrl": "C:\\path\\to\\manifest.json"` to `%LOCALAPPDATA%\DurazznoApp\settings.json`.

## Data

`%LOCALAPPDATA%\DurazznoApp\`: `settings.json` and `logs\durazzno.log`. "Start with Windows" is the `DurazznoApp Daemon` value under `HKCU\…\CurrentVersion\Run`.

Icons: [Lucide](https://lucide.dev) (ISC, see `src/DurazznoApp/Assets/Lucide/LICENSE.txt`).
