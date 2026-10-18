using System.Reflection;

namespace Durazzno.Core;

internal static class AppInfo
{
    public const string Name = "Durazzno App";
    public const string WebsiteUrl = "https://www.itzgalaxy.com";

    public static readonly Version Version = Normalize(Assembly.GetEntryAssembly()?.GetName().Version);

    public static string VersionText => Version.ToString(3);

    /// <summary>Folder that holds the exe (also correct for single-file publish).</summary>
    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>The app's file name. Versions before 3.0.1 were DurazznoAppDaemon.exe; updating renames it.</summary>
    public const string ExeFileName = "DurazznoApp.exe";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(BaseDir, ExeFileName);


    /// <summary>Per-user data: settings and logs. Kept out of the install folder so updates never touch it.</summary>
    public static string DataDir { get; } = Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DurazznoApp")).FullName;

    /// <summary>Compares versions on major.minor.patch only, so "3.1" and "3.1.0" are equal.</summary>
    public static Version Normalize(Version? v) =>
        v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
}
