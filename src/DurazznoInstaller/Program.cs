using System.Windows;

namespace Durazzno.Installer;

/// <summary>
/// Durazzno App Installer: downloads the signed Durazzno App from Webin Cloud Server and installs it for this user.
/// --dir &lt;folder&gt; installs somewhere else (testing).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var dir = Array.IndexOf(args, "--dir");
        if (dir >= 0 && dir + 1 < args.Length)
            Installer.InstallDir = System.IO.Path.GetFullPath(args[dir + 1]);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/DurazznoAppInstaller;component/Theme.xaml"),
        });
        return app.Run(new InstallerWindow());
    }
}
