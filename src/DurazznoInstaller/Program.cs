using System.Diagnostics;
using System.Windows;

namespace Durazzno.Installer;

/// <summary>
/// Durazzno App Installer: downloads the signed Durazzno App from Webin Cloud Server and installs it for this user.
/// A copy of it in the install folder is the uninstaller (Start Menu, Apps &amp; features): opened with Durazzno App
/// installed it offers Repair and Uninstall.
/// --dir &lt;folder&gt; installs somewhere else, with its own data folder (testing).
/// </summary>
internal static class Program
{
    private const string FromTempFlag = "--from-temp";

    [STAThread]
    private static int Main(string[] args)
    {
        var dir = Array.IndexOf(args, "--dir");
        if (dir >= 0 && dir + 1 < args.Length)
            Installer.UseTestFolder(args[dir + 1]);

        // The uninstaller lives in the folder it deletes: run a copy from %TEMP% instead, which removes itself at exit.
        var self = Process.GetCurrentProcess().MainModule!.FileName;
        var fromTemp = args.Contains(FromTempFlag);
        if (!fromTemp && string.Equals(self, Installer.InstallerCopyPath, StringComparison.OrdinalIgnoreCase))
        {
            var copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"DurazznoAppInstaller-{Guid.NewGuid():N}.exe");
            System.IO.File.Copy(self, copy);
            var forwarded = string.Join(" ", args.Select(a => $"\"{a}\"").Append(FromTempFlag));
            Process.Start(new ProcessStartInfo(copy, forwarded) { UseShellExecute = false })?.Dispose();
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/DurazznoAppInstaller;component/Theme.xaml"),
        });
        var code = app.Run(new InstallerWindow());

        if (fromTemp)
            DeleteSelfLater(self);
        return code;
    }

    /// <summary>Deletes the temporary copy a few seconds after this process has exited.</summary>
    private static void DeleteSelfLater(string path) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & del /f /q \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        })?.Dispose();
}
