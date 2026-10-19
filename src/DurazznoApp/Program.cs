// ##Durazzno App
// ##Designed And Made For: Durazzno App
// ##2022 - 2026 Galaxy Apps, ItzGalaxy Web Services
// ##Rebuilt in C# · October 2026
//
//   ⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣀⡤⠴⠒⠒⠒⢒⡶⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣠⠞⠁⠀⠀⠀⠀⠀⡾⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⡴⠁⠀⠀⠀⠀⠀⠀⠀⡇⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⠞⠁⠀⢀⣀⣀⣀⡀⠀⠀⢧⠀⠀⠀⠀⠀⡴⣄⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⠀⠀⠀⠀⠀⢀⣰⠿⠒⠋⠉⠉⠀⠀⠀⠈⠉⠓⠾⢄⣀⠀⠀⢠⡇⠸⡄⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⠀⠀⠀⠀⠀⣩⠏⠉⠒⢒⣲⡤⠤⣀⣀⠀⠀⠀⠀⠀⠈⠑⢤⡘⡄⠀⢳⡀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣀⠀
//   ⠀⠀⠀⠀⠀⢰⠾⣣⠴⠚⠉⠉⠀⠀⠀⠀⠀⠉⠓⠀⠀⠀⠀⠀⠀⠙⣧⠀⠀⢣⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣀⡤⣔⡿⡍⡇
//   ⠀⠀⠀⠀⠀⢸⡼⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠘⠆⠀⠀⢳⡀⠀⠀⠀⠀⠀⣀⡤⠞⠋⣡⠞⠉⠀⣇⡇
//   ⠀⠀⠀⠀⣴⠋⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⡠⠴⠒⠛⠓⠚⠉⠉⠉⠁⠀⣴⡟⠁⠀⠀⠀⣿⠁
//   ⠀⠀⠀⣼⣁⣠⡤⢴⠖⠒⠒⠦⠤⢴⣶⣢⠤⠤⠔⠚⠃⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⣞⡞⠀⠀⠀⠀⢰⢿⠀
//   ⠀⠀⠀⠉⠉⢙⣦⠞⠀⡠⠞⠉⢉⡷⡄⠉⢙⡶⠃⠀⠀⠀⢀⣠⣦⣤⣄⡀⠀⠀⠀⠀⠀⠀⠀⠀⢀⡎⡼⠀⠀⠀⠀⠀⡜⡞⠀
//   ⠀⠀⠀⣤⡖⠛⠀⠀⡜⠁⠀⡴⢋⣀⣹⣠⠋⠀⠀⠀⠀⣰⣿⠿⠛⠛⠿⣿⣷⣄⡀⠀⠀⠀⠀⠀⡼⢰⠃⠀⠀⠀⠀⡼⢱⠃⠀
//    ⢀⣴⠛⠀⠀⠀⢼⠁⠀⣼⢁⣿⡬⣿⠃⠀⠀⠀⠀⠐⠋⠁⠀⠀⠀⠀⠀⠙⢿⣷⣄⠀⠀⠀⣸⢃⡏⠀⠀⠀⢀⡜⢡⠏⠀⠀
//   ⠀⠸⡿⠀⠀⠀⠀⢻⡀⠀⠀⠀⠉⢠⠇⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠙⣿⠁⣀⡜⠁⡼⠀⠀⠀⡠⠋⣰⠋⠀⠀⠀
//   ⠀⠀⣧⠀⠀⠀⠀⠈⢳⣀⣙⣦⡤⠋⠀⠀⠀⠀⠀⠀⠀⣰⠄⠀⠀⠀⠀⠀⠀⢤⠀⣹⠌⢩⠏⢠⣇⣠⠴⠊⢀⡼⠁⠀⠀⠀⠀
//   ⠀⠀⠘⡆⢀⡴⠖⠋⠉⠉⠀⠀⠀⠀⠀⠀⠀⠘⢧⣤⣾⣷⣿⣿⣿⣷⣦⡀⠀⢸⠀⠻⣾⠉⠀⠀⠀⠀⢀⣠⠋⠀⠀⠀⠀⠀⠀
//   ⠀⠀⢠⡟⣥⠤⠦⠤⠤⢤⣀⣀⠀⠀⠀⠀⠀⠀⠀⠉⠁⠀⠀⠉⠛⠿⣿⣿⣄⣸⠀⠈⠻⣇⠀⢀⣀⠤⠟⠓⠃⠀⠀⠀⠀⠀⠀
//   ⠀⠀⠸⡆⢧⡀⠀⠀⠀⠀⠀⠈⠙⠲⡄⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠉⠉⠁⠀⠀⠀⣸⡍⠉⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⠀⢳⡄⠙⢦⡀⠀⠀⠀⣀⡤⠞⠁⠀⠀⠀⠀⢀⣀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣠⡿⠃⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⠀⠀⠙⢦⣀⣉⣷⡚⠉⠀⠀⠀⠀⠀⠀⠀⢀⡾⠋⠀⠀⠀⠀⠀⠀⠀⠀⠀⣠⠼⣿⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⠀⣦⣴⠚⢁⡾⠋⠉⠳⢤⣀⢀⣀⣀⣠⠔⠋⠀⠀⠀⠀⠀⠀⣀⣠⠤⠖⠋⠁⢀⣺⣆⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⢰⣷⡇⠀⡞⠀⠀⠀⠀⠀⡼⠋⠉⠁⠀⠀⣀⣀⠤⠴⠒⠊⠉⠉⠀⠀⠀⠀⠀⣼⣿⠻⠃⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⠈⢯⡛⢦⡀⠀⠀⠀⢀⡾⠥⠤⠔⠒⠊⠉⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠰⡆⣸⣹⠛⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⠀⢠⡿⠀⢹⣶⣶⣖⡋⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠹⡟⠋⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⠀⢀⡿⠿⠉⠉⠋⠀⠀⠉⠙⠶⣄⡀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢠⡀⠹⢤⣀⣀⣀⣀⣀⡠⠀⠀⠀⠀⠀⠀⠀⠀⠀
//   ⠀⡰⠋⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠉⠢⣄⠀⠀⠀⡀⠀⠀⠀⠀⠀⠀⠀⣼⠇⠀⠀⠀⠀⠀⠙⠋⠩⠿⢿⣁⠀⠀⠀⠀⠀⠀
//   ⡼⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠳⡄⠀⠉⠑⠶⢤⣀⣀⣠⣴⡟⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠘⠿⣅⠀⠀⠀⠀⠀
//   ⡇⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠙⠂⠀⠀⠀⠀⠀⠉⠉⠉⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⢦⠀⠀⠀⠀
//   ⡅⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⡇⠀⠀⠀
//   ⡇⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣇⠀⠀⠀
//   ⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠉⠀⠀

using System.Windows;
using System.Windows.Threading;
using Durazzno.Core;
using Durazzno.Hardware.Disks;
using Durazzno.Updates;

namespace Durazzno;

internal static class Program
{
    /// <summary>Passed by the Windows startup entry: start in the tray without opening the window.</summary>
    public const string BackgroundFlag = "--background";

    [STAThread]
    private static int Main(string[] args)
    {
        // Helper modes: no UI, no single-instance lock.
        if (args.Length >= 1 && args[0] == UpdateService.FinishFlag)
            return UpdateService.RunFinish(args);
        if (args.Length >= 3 && args[0] == DiskService.AgentFlag && int.TryParse(args[2], out var parentPid))
            return DiskService.RunAgent(args[1], parentPid);
        if (args.Length >= 2 && args[0] == DiskService.DumpFlag)
            return DiskService.RunDump(args[1]);
        if (args.Length >= 3 && args[0] == Hardware.Drivers.DriverUpdates.InstallFlag)
            return Hardware.Drivers.DriverUpdates.RunInstall(args[1], args[2]);
        // Diagnostics: --oem-test <out.txt> (with DURAZZNO_OEM=dell:SKU / hp:board / lenovo:type) lists the PC maker
        // catalog's offers for this PC's devices, without touching anything.
        if (args.Length >= 2 && args[0] == "--oem-test")
        {
            try
            {
                var devices = Hardware.Drivers.DeviceScanner.Scan();
                var offers = Hardware.Drivers.OemCatalogs.SearchAsync(devices, CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllLines(args[1], offers.Select(o => $"{o.Model} | {o.Title} | v{o.Version} | {o.DownloadUrl} | sha256={o.Sha256} md5={o.Md5} | {o.InstallArgs}{o.InstallCommand}"));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], ex.ToString());
                return 1;
            }
        }
        if (args.Length >= 5 && args[0] == Hardware.Drivers.DriverHistory.RollbackFlag)
            return Hardware.Drivers.DriverHistory.RunRollback(args[1], args[2], args[3], args[4]);
        if (args.Length >= 3 && args[0] == Hardware.Drivers.DriverStoreCleanup.DeleteFlag)
            return Hardware.Drivers.DriverStoreCleanup.RunDelete(args[1], args[2]);
        if (args.Length >= 3 && args[0] == Hardware.Drivers.DeviceCleanup.RemoveFlag)
            return Hardware.Drivers.DeviceCleanup.RunRemove(args[1], args[2]);
        if (args.Length >= 2 && args[0] == Startup.BootImpact.Flag)
            return Startup.BootImpact.RunElevated(args[1]);
        if (args.Length >= 3 && args[0] == Hardware.Storage.CleanupService.Flag)
            return Hardware.Storage.CleanupService.RunElevated(args[1], args[2]);
        if (args.Length >= 2 && args[0] == Hardware.Drivers.DriverUpdates.BackupFlag)
            return Hardware.Drivers.DriverUpdates.RunBackup(args[1]);

        var justUpdated = args.Contains(UpdateService.UpdatedFlag);
        // The first launch (Windows startup, the shortcut, an update) always starts in the tray. The window opens when
        // the app is launched again while it runs (the shortcut signals the running copy), from the tray, or after
        // APPLY UPDATE (--open, the window was open when the user clicked it).
        var openWindow = args.Contains(UpdateService.OpenFlag);

        // Right after a self-update the old process may still be shutting down, so wait for it.
        using var instance = SingleInstance.TryAcquire(justUpdated ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
        if (instance is null)
        {
            SingleInstance.SignalRunningInstance();
            return 0;
        }

        Log.Info($"{AppInfo.Name} {AppInfo.VersionText} starting from {AppInfo.BaseDir}");
        UpdateService.CleanupLeftovers();

        // Update before anything is shown: install a downloaded update, or fetch one quickly. Falls through on failure.
        var settings = new SettingsStore();
        if (!justUpdated && settings.Current.AutoUpdate && StartupUpdate.TryApply(settings, open: openWindow))
            return 0;

        // WinForms (tray icon) settings must be applied before any window exists.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/DurazznoApp;component/UI/Theme.xaml"),
        });
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

#if !DEBUG
        if (settings.IsFirstRun)
            StartupManager.SetEnabled(true);
#endif
        StartupManager.RepairPath();

        using var controller = new AppController(settings, justUpdated, args.Contains(UpdateService.FailedFlag));
        instance.ListenForActivation(() => controller.ShowMainWindow());
        if (openWindow)
            controller.ShowMainWindow();

        app.Run();

        Log.Info("Exiting");
        return 0;
    }
}
