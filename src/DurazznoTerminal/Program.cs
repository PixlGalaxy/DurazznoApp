// ##Durazzno Terminal
// ##Designed And Made For: Durazzno App & Durazzno Terminal
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
//
// Recovery console for Durazzno App. It is a separate native exe on purpose: when DurazznoAppDaemon.exe fails to
// start (a bad update, broken settings), this still runs and can repair, roll back or reset it.

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Durazzno.Network;
using Microsoft.Win32;

namespace Durazzno.Terminal;

internal static class Program
{
    private const string Version = "2.0.0";
    private const string AppExeName = "DurazznoAppDaemon.exe";
    private const string ProcessName = "DurazznoAppDaemon";

    // Same public key as the app (Updates/UpdateSignature.cs): manifests must carry a valid manifest.json.sig.
    private const string UpdatePublicKey =
        "<RSAKeyValue><Modulus>yX69DyGX/yX8Eu/gNgrVTOsQQYjn6ppNo2h4cOiRsBHcJ6jZ65p466r8Y8izMX3i4/7PtfNqjYq3TS+UlPAYkKvMNa8jODRfw55TP9GRyy2apMXJp1yjiB9wX9xfOUHrPhNAbpfOE5SAiW8lDP5aBujH3v18bkMwKUtDcP3htB/jlTLAx1pqIe1NRbpu/3TUFqytO/KuH1dRY2xvdG3QdNbjdEo3IlmgJqYqcJ1Yz0t82GAck9qwczUsxc1Mj1DDwdSgZ4IGbAitovzCsj9rpa9mh7tuIe6cNYtZ3clOTVj1LME2Rfa/rS86r8SLqgtnyjNTyAEa+pv9EqdLZTuOWZqUlsfdUVCpXhA9X7BXjeWLedT8Y67BXXknQV8HlX1no5ADXLs4LHklX1KlYK0QiXI8F5WjAOvkdsFBcrmkYtAOk1jC4RV1+lMXT37EqfxWlvMTqtlbP7iCwDzoxW7urgA1dMWqOP5lNKRr7hueEbICUQILhtD+5DBixajaks5R</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DurazznoApp Daemon";
    private const string ToolsVersionFile = "tools-version.txt";
    private static readonly string[] ToolsUserData = ["DiskInfo.ini", "DiskMark32.ini", "Smart"];

    private static readonly string AppDir = AppContext.BaseDirectory;
    private static readonly string AppExe = Path.Combine(AppDir, AppExeName);
    private static readonly string OldExe = AppExe + ".old";
    private static readonly string ToolsDir = Path.Combine(AppDir, "Tools");
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DurazznoApp");
    private static readonly string SettingsFile = Path.Combine(DataDir, "settings.json");
    private static readonly string LogFile = Path.Combine(DataDir, "logs", "durazzno.log");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private sealed record Command(string Name, string Usage, string Help, Func<string[], Task> Run);

    private static readonly List<Command> Commands = [];

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Durazzno Terminal";
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"DurazznoTerminal/{Version}");
        Register();

        // One-shot use: DurazznoTerminal.exe repair
        if (args.Length > 0)
            return await Execute(string.Join(' ', args)) ? 0 : 1;

        Banner();
        await CheckServerAsync();
        Line();
        Say("Type help for the list of commands.", ConsoleColor.Yellow);

        while (true)
        {
            Write("$DURAZZNO_TERMINAL$> ", ConsoleColor.Yellow);
            var input = Console.ReadLine();
            if (input is null)
                return 0;
            if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (input.Trim().Length > 0)
                await Execute(input);
        }
    }

    private static async Task<bool> Execute(string input)
    {
        var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = Commands.FirstOrDefault(c => c.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            Say($"Unknown command \"{parts[0]}\". Try help.", ConsoleColor.Red);
            return false;
        }
        try
        {
            await command.Run(parts[1..]);
            return true;
        }
        catch (Exception ex)
        {
            Say($"Error: {ex.Message}", ConsoleColor.Red);
            return false;
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────────────────

    private static void Register()
    {
        Add("help", "help", "Shows this list.", _ =>
        {
            Say("Durazzno Terminal commands:", ConsoleColor.Cyan);
            foreach (var c in Commands)
                Say($"  {c.Usage,-22} {c.Help}", ConsoleColor.Gray);
            Say($"  {"exit",-22} Closes the terminal.", ConsoleColor.Gray);
            return Task.CompletedTask;
        });
        Add("clear", "clear", "Clears the screen.", _ => { Console.Clear(); Banner(); return Task.CompletedTask; });
        Add("status", "status", "App version, whether it runs, server, backup and startup state.", _ => StatusAsync());
        Add("start", "start", "Starts Durazzno App.", _ => { StartApp(); return Task.CompletedTask; });
        Add("stop", "stop", "Closes Durazzno App (also a frozen one).", _ => { StopApp(); return Task.CompletedTask; });
        Add("restart", "restart", "Closes and starts Durazzno App again.", _ => { StopApp(); StartApp(); return Task.CompletedTask; });
        Add("repair", "repair", "Downloads and reinstalls the latest Durazzno App and its tools (checks SHA-256).", _ => RepairAsync());
        Add("rollback", "rollback", "Goes back to the version installed before the last update.", _ => { Rollback(); return Task.CompletedTask; });
        Add("reset", "reset", "Resets Durazzno App settings to their defaults (keeps a backup).", _ => { Reset(); return Task.CompletedTask; });
        Add("logs", "logs [lines]", "Shows the end of the Durazzno App log (default 30 lines).", a => { Logs(a); return Task.CompletedTask; });
        Add("open", "open [app|data|logs]", "Opens the install, data or logs folder.", a => { Open(a); return Task.CompletedTask; });
        Add("server", "server [url|default]", "Shows or changes the update server (manifest URL), e.g. a test server.", a => { Server(a); return Task.CompletedTask; });
        Add("startup", "startup [on|off]", "Starts Durazzno App with Windows, or not.", a => { Startup(a); return Task.CompletedTask; });
        Add("timer", "timer off", "Stops TimerResolution from being applied when the app starts.", a => { Timer(a); return Task.CompletedTask; });
        Add("powercuts", "powercuts cancel", "Cancels a scheduled PowerCuts shutdown or restart.", a => { PowerCuts(a); return Task.CompletedTask; });
        Add("uninstall", "uninstall", "Removes Durazzno App, its startup entry and its data.", _ => { Uninstall(); return Task.CompletedTask; });
    }

    private static void Add(string name, string usage, string help, Func<string[], Task> run) => Commands.Add(new Command(name, usage, help, run));

    private static async Task StatusAsync()
    {
        Line();
        Field("Terminal", $"Durazzno Terminal {Version}");
        Field("App", File.Exists(AppExe) ? $"Durazzno App {FileVersionInfo.GetVersionInfo(AppExe).ProductVersion?.Split('+')[0]}" : "not found next to the terminal");
        Field("Running", Process.GetProcessesByName(ProcessName).Length > 0 ? "yes" : "no");
        Field("Previous version", File.Exists(OldExe) ? $"{FileVersionInfo.GetVersionInfo(OldExe).ProductVersion?.Split('+')[0]} (rollback available)" : "none");
        Field("Update server", ManifestUri().ToString());
        Field("Start with Windows", StartupEnabled() ? "on" : "off");
        Field("Install folder", AppDir);
        Field("Data folder", DataDir);
        await CheckServerAsync();
        Line();
    }

    private static void StartApp()
    {
        if (!File.Exists(AppExe))
            throw new FileNotFoundException($"{AppExeName} is not in {AppDir}. Run repair.");
        Process.Start(new ProcessStartInfo(AppExe) { UseShellExecute = true, WorkingDirectory = AppDir })?.Dispose();
        Say("Durazzno App started.", ConsoleColor.Green);
    }

    private static void StopApp()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        if (processes.Length == 0)
        {
            Say("Durazzno App is not running.", ConsoleColor.Gray);
            return;
        }
        foreach (var p in processes)
        {
            using (p)
            {
                // Its tray icon would linger after a kill until the mouse passes over it; harmless.
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        Say($"Durazzno App closed ({processes.Length} process{(processes.Length > 1 ? "es" : "")}).", ConsoleColor.Green);
    }

    private static async Task RepairAsync()
    {
        Line();
        var manifest = await GetManifestAsync();
        var app = manifest["app"] ?? throw new InvalidDataException("The manifest has no \"app\" entry.");
        var version = (string?)app["version"] ?? "?";
        Say($"Latest Durazzno App: {version}", ConsoleColor.Cyan);

        var newExe = AppExe + ".new";
        await DownloadVerifiedAsync(Resolve(manifest, app), (string?)app["sha256"], newExe, $"Durazzno App {version}");

        string? zip = null;
        if (manifest["tools"] is { } tools)
        {
            zip = Path.Combine(Path.GetTempPath(), $"DurazznoTools-{Guid.NewGuid():N}.zip");
            await DownloadVerifiedAsync(Resolve(manifest, tools), (string?)tools["sha256"], zip, "Tools");
        }

        StopApp();
        if (File.Exists(AppExe))
            File.Move(AppExe, OldExe, overwrite: true);
        File.Move(newExe, AppExe);
        Say($"Installed Durazzno App {version}. The previous exe is kept for rollback.", ConsoleColor.Green);

        if (zip is not null)
        {
            try
            {
                ReplaceTools(zip, (string?)manifest["tools"]!["version"] ?? "");
                Say("Tools reinstalled.", ConsoleColor.Green);
            }
            finally
            {
                File.Delete(zip);
            }
        }

        StartApp();
        Line();
    }

    private static void ReplaceTools(string zip, string version)
    {
        var staging = ToolsDir + ".new";
        var old = ToolsDir + ".old";
        if (!Directory.Exists(ToolsDir) && Directory.Exists(old))
            Directory.Move(old, ToolsDir); // an earlier swap stopped halfway
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);
        File.WriteAllText(Path.Combine(staging, ToolsVersionFile), version);

        if (Directory.Exists(ToolsDir))
        {
            foreach (var relative in ToolsUserData)
            {
                var source = Path.Combine(ToolsDir, relative);
                var target = Path.Combine(staging, relative);
                if (File.Exists(source))
                    File.Copy(source, target, overwrite: true);
                else if (Directory.Exists(source))
                    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(target, Path.GetRelativePath(source, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(file, dest, overwrite: true);
                    }
            }
            if (Directory.Exists(old))
                Directory.Delete(old, true);
            MoveDirectory(ToolsDir, old);
        }
        try
        {
            MoveDirectory(staging, ToolsDir);
        }
        catch
        {
            if (Directory.Exists(old) && !Directory.Exists(ToolsDir))
                Directory.Move(old, ToolsDir); // never leave the app without its tools
            throw;
        }
        try { Directory.Delete(old, true); } catch { }
    }

    /// <summary>
    /// Freshly extracted files are often still open in the antivirus for a moment, which makes a folder move fail
    /// with "access denied": retry for a few seconds before giving up.
    /// </summary>
    private static void MoveDirectory(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void Rollback()
    {
        if (!File.Exists(OldExe))
            throw new InvalidOperationException("There is no previous version to go back to.");
        var previous = FileVersionInfo.GetVersionInfo(OldExe).ProductVersion?.Split('+')[0];
        if (!Confirm($"Go back to Durazzno App {previous}?"))
            return;
        StopApp();
        var swap = AppExe + ".swap";
        if (File.Exists(AppExe))
            File.Move(AppExe, swap, overwrite: true);
        File.Move(OldExe, AppExe);
        if (File.Exists(swap))
            File.Move(swap, OldExe);
        Say($"Rolled back to Durazzno App {previous}. Run rollback again to undo.", ConsoleColor.Green);
        StartApp();
    }

    private static void Reset()
    {
        if (!File.Exists(SettingsFile))
        {
            Say("Settings are already at their defaults.", ConsoleColor.Gray);
            return;
        }
        if (!Confirm("Reset all Durazzno App settings to their defaults?"))
            return;
        var running = Process.GetProcessesByName(ProcessName).Length > 0;
        if (running)
            StopApp();
        File.Move(SettingsFile, SettingsFile + ".bak", overwrite: true);
        Say($"Settings reset. The old ones are in {SettingsFile}.bak", ConsoleColor.Green);
        if (running)
            StartApp();
    }

    private static void Logs(string[] args)
    {
        var count = args.Length > 0 && int.TryParse(args[0], out var n) ? Math.Clamp(n, 1, 2000) : 30;
        if (!File.Exists(LogFile))
            throw new FileNotFoundException("There is no log yet.");
        using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new Queue<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Enqueue(line);
            if (lines.Count > count)
                lines.Dequeue();
        }
        foreach (var line in lines)
            Say(line, line.Contains(" ERROR ") ? ConsoleColor.Red : line.Contains(" WARN ") ? ConsoleColor.DarkYellow : ConsoleColor.Gray);
    }

    private static void Open(string[] args)
    {
        var target = (args.FirstOrDefault() ?? "data").ToLowerInvariant() switch
        {
            "app" => AppDir,
            "logs" => Path.GetDirectoryName(LogFile)!,
            _ => DataDir,
        };
        Directory.CreateDirectory(target);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true })?.Dispose();
    }

    private static void Startup(string[] args)
    {
        var state = args.FirstOrDefault()?.ToLowerInvariant();
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (state == "on")
        {
            key.SetValue(RunValue, $"\"{AppExe}\" --background");
            Say("Durazzno App will start with Windows.", ConsoleColor.Green);
        }
        else if (state == "off")
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
            Say("Durazzno App will not start with Windows.", ConsoleColor.Green);
        }
        else
        {
            Say($"Start with Windows is {(StartupEnabled() ? "on" : "off")}. Use startup on or startup off.", ConsoleColor.Gray);
        }
    }

    private static void Server(string[] args)
    {
        var value = args.FirstOrDefault();
        if (value is null)
        {
            Say($"Update manifest: {ManifestUri()}", ConsoleColor.Gray);
            return;
        }
        if (value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            EditSettings(s => s["manifestUrl"] = null);
            Say($"Back to the default server: {WebinCloud.DefaultManifestUri}", ConsoleColor.Green);
            return;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
            throw new ArgumentException("That is not a valid URL.");
        var url = WebinCloud.ResolveManifest(value).ToString();
        EditSettings(s => s["manifestUrl"] = url);
        Say($"Update manifest set to {url}. Restart Durazzno App to use it.", ConsoleColor.Green);
    }

    private static void Timer(string[] args)
    {
        if (!string.Equals(args.FirstOrDefault(), "off", StringComparison.OrdinalIgnoreCase))
        {
            Say("Usage: timer off", ConsoleColor.Gray);
            return;
        }
        EditSettings(s =>
        {
            s["timerResolutionOnStartup"] = false;
            s["timerResolutionMs"] = null;
        });
        Say("TimerResolution will no longer be applied when Durazzno App starts. Restart the app (or Windows) to release the current one.", ConsoleColor.Green);
    }

    private static void PowerCuts(string[] args)
    {
        if (!string.Equals(args.FirstOrDefault(), "cancel", StringComparison.OrdinalIgnoreCase))
        {
            Say("Usage: powercuts cancel", ConsoleColor.Gray);
            return;
        }
        using var p = Process.Start(new ProcessStartInfo("shutdown.exe", "/a") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        try { File.Delete(Path.Combine(DataDir, "powercuts.json")); } catch { }
        Say(p.ExitCode == 0 ? "Scheduled shutdown cancelled." : "There was no scheduled shutdown.", p.ExitCode == 0 ? ConsoleColor.Green : ConsoleColor.Gray);
    }

    private static void Uninstall()
    {
        if (!Confirm("Remove Durazzno App, its startup entry and all its settings and logs? Type yes to continue."))
            return;
        StopApp();
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        // Notification senders (PowerCuts, DiskInfo, Webin Cloud Server...).
        using (var senders = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId", writable: true))
            foreach (var name in senders?.GetSubKeyNames().Where(n => n.StartsWith("Durazzno.", StringComparison.Ordinal)) ?? [])
                senders!.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        if (Directory.Exists(DataDir))
            Directory.Delete(DataDir, true);
        foreach (var file in new[] { AppExe, OldExe, AppExe + ".new" })
            if (File.Exists(file))
                File.Delete(file);
        if (Directory.Exists(ToolsDir))
            Directory.Delete(ToolsDir, true);

        // The terminal cannot delete its own exe while running: Windows removes it a moment after it closes.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & del /f /q \"{Environment.ProcessPath}\"")
        { CreateNoWindow = true, UseShellExecute = false })?.Dispose();
        Say("Durazzno App was removed. Goodbye!", ConsoleColor.Green);
        Environment.Exit(0);
    }

    // ── Server / manifest ─────────────────────────────────────────────────────────────────

    private static Uri ManifestUri()
    {
        string? configured = null;
        try
        {
            if (File.Exists(SettingsFile))
                configured = (string?)JsonNode.Parse(File.ReadAllText(SettingsFile))?["manifestUrl"];
        }
        catch
        {
            // Broken settings are exactly why the terminal exists: fall back to the default.
        }
        return WebinCloud.ResolveManifest(configured);
    }

    private static async Task<JsonNode> GetManifestAsync()
    {
        var uri = ManifestUri();
        var bytes = uri.IsFile ? await File.ReadAllBytesAsync(uri.LocalPath) : await Http.GetByteArrayAsync(uri);
        var sigUri = WebinCloud.SignatureOf(uri);
        var sig = uri.IsFile ? await File.ReadAllTextAsync(sigUri.LocalPath) : await Http.GetStringAsync(sigUri);
        using (var rsa = System.Security.Cryptography.RSA.Create())
        {
            rsa.FromXmlString(UpdatePublicKey);
            if (!rsa.VerifyData(bytes, Convert.FromBase64String(sig.Trim()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InvalidDataException("the update manifest's signature is not valid; refusing it");
        }
        var json = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidDataException($"{uri} did not return a valid update manifest");
        }
        if (node is null)
            throw new InvalidDataException("The manifest is empty.");
        node["__source"] = uri.ToString();
        return node;
    }

    private static Uri Resolve(JsonNode manifest, JsonNode package) =>
        WebinCloud.Sibling(new Uri((string)manifest["__source"]!), (string?)package["url"] ?? throw new InvalidDataException("A package has no url."));

    private static async Task CheckServerAsync()
    {
        Write("Connecting to Webin Cloud Server… ", ConsoleColor.Cyan);
        try
        {
            var manifest = await GetManifestAsync();
            Say($"ONLINE  ·  latest Durazzno App {(string?)manifest["app"]?["version"]}", ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            Say($"OFFLINE  ({ex.Message})", ConsoleColor.Red);
            Say("Online commands (repair) need the server; everything else works offline.", ConsoleColor.DarkGray);
        }
    }

    private static async Task DownloadVerifiedAsync(Uri uri, string? sha256, string destination, string label)
    {
        if (string.IsNullOrWhiteSpace(sha256))
            throw new InvalidDataException($"{label} has no SHA-256 in the manifest.");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (uri.IsFile)
            {
                File.Copy(uri.LocalPath, destination, overwrite: true);
                hash.AppendData(await File.ReadAllBytesAsync(destination));
            }
            else
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var file = File.Create(destination);
                var buffer = new byte[1 << 16];
                long got = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    got += read;
                    Progress(label, got, total);
                }
                Console.WriteLine();
            }

            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{label} failed the integrity check (SHA-256 mismatch).");
            Say($"{label}: downloaded and verified.", ConsoleColor.Green);
        }
        catch
        {
            try { File.Delete(destination); } catch { }
            throw;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    private static void EditSettings(Action<JsonObject> edit)
    {
        Directory.CreateDirectory(DataDir);
        JsonObject settings;
        try
        {
            settings = File.Exists(SettingsFile) ? JsonNode.Parse(File.ReadAllText(SettingsFile)) as JsonObject ?? [] : [];
        }
        catch
        {
            settings = [];
        }
        edit(settings);
        File.WriteAllText(SettingsFile, settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static bool Confirm(string question)
    {
        Say(question, ConsoleColor.Yellow);
        Write("Type yes to continue: ", ConsoleColor.Yellow);
        // Piped answers can start with a byte-order mark.
        var yes = string.Equals(Console.ReadLine()?.Trim().TrimStart('﻿'), "yes", StringComparison.OrdinalIgnoreCase);
        if (!yes)
            Say("Cancelled.", ConsoleColor.Gray);
        return yes;
    }

    private static void Progress(string label, long got, long total)
    {
        var mb = got / 1048576.0;
        var text = total > 0
            ? $"\r  {label}  {mb:0.0} / {total / 1048576.0:0.0} MB  [{new string('█', (int)(got * 30 / total)),-30}]"
            : $"\r  {label}  {mb:0.0} MB";
        Write(text, ConsoleColor.DarkCyan);
    }

    private static void Banner()
    {
        Say("", ConsoleColor.Cyan);
        Say("█▀▄ █░█ █▀█ ▄▀█ ▀█ ▀█ █▄░█ █▀█   ▄▀█ █▀█ █▀█   ▀█▀ █▀▀ █▀█ █▀▄▀█ █ █▄░█ ▄▀█ █░░", ConsoleColor.Cyan);
        Say("█▄▀ █▄█ █▀▄ █▀█ █▄ █▄ █░▀█ █▄█   █▀█ █▀▀ █▀▀   ░█░ ██▄ █▀▄ █░▀░█ █ █░▀█ █▀█ █▄▄", ConsoleColor.Cyan);
        Say("▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄", ConsoleColor.Cyan);
        Say($"Durazzno Terminal {Version}  ·  2022 - 2026 Galaxy Apps", ConsoleColor.DarkCyan);
        Say("", ConsoleColor.Cyan);
    }

    private static void Line() => Say(new string('=', 68), ConsoleColor.DarkGray);

    private static void Field(string name, string value)
    {
        Write($"  {name,-20}", ConsoleColor.DarkGray);
        Say(value, ConsoleColor.White);
    }

    private static void Say(string text, ConsoleColor color) => Write(text + Environment.NewLine, color);

    private static void Write(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }
}
