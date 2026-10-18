using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Durazzno.Core;
using Microsoft.Win32;

namespace Durazzno.Startup;

internal enum StartupCategory { Logon, Tasks, Services, Advanced }

internal enum EntrySource { RunKey, RunOnceKey, PolicyRun, StartupFolder, Task, Service, Advanced }

/// <summary>One thing that runs automatically, plus everything needed to toggle or delete it.</summary>
internal sealed class StartupEntry
{
    public StartupCategory Category { get; init; }
    public EntrySource Source { get; init; }
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string? ImagePath { get; init; }
    public string Location { get; init; } = "";
    public string Description { get; init; } = "";
    public string Trigger { get; init; } = "";
    public bool Enabled { get; set; } = true;
    public bool CanToggle { get; init; }
    public bool CanDelete { get; init; }

    /// <summary>Changing it needs administrator rights (machine-wide entries).</summary>
    public bool Machine { get; init; }

    // Identity for actions.
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; } = RegistryView.Registry64;
    public string KeyPath { get; init; } = "";
    public string ValueName { get; init; } = "";
    public string? ApprovedKey { get; init; }   // StartupApproved\<Run|Run32|StartupFolder>
    public string? FilePath { get; init; }      // Startup folder item
    public string? TaskPath { get; init; }
    public string? ServiceName { get; init; }

    /// <summary>Advanced entries: true when the value is not the Windows default.</summary>
    public bool Unusual { get; init; }

    public bool MicrosoftTask { get; init; }

    public Signature Signature { get; set; } = new(SignatureState.Unknown, "");

    public bool IsMicrosoft => MicrosoftTask || Signature.IsMicrosoft;

    /// <summary>Durazzno App's own startup entry (shown as "This app" instead of unsigned).</summary>
    public bool IsThisApp => ImagePath is { } p
        && Path.GetFileName(p).Equals(Path.GetFileName(AppInfo.ExePath), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Collects autostart entries from the places that matter most (a focused subset of Autoruns).</summary>
internal static class StartupScanner
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string PolicyRunPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
    public const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private static readonly string StatePath = Path.Combine(AppInfo.DataDir, "startuphub.json");

    public static List<StartupEntry> Scan()
    {
        var list = new List<StartupEntry>();
        Safe(() => ScanRunKeys(list));
        Safe(() => ScanStartupFolders(list));
        Safe(() => ScanTasks(list));
        Safe(() => ScanServices(list));
        Safe(() => ScanAdvanced(list));

        // Signature checks are the slow part: run them in parallel.
        Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
            e => e.Signature = SignatureChecker.Check(e.ImagePath));
        return list;
    }

    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Warn($"StartupHub: {ex.Message}"); }
    }

    // ── Logon: Run keys ───────────────────────────────────────────────────────────────────

    private static void ScanRunKeys(List<StartupEntry> list)
    {
        void Read(RegistryHive hive, RegistryView view, string path, EntrySource source, string? approved)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path);
            if (key is null)
                return;

            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string command || string.IsNullOrWhiteSpace(command))
                    continue;

                var hiveName = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
                var wow = view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine ? @"\WOW6432Node" : "";
                list.Add(new StartupEntry
                {
                    Category = StartupCategory.Logon,
                    Source = source,
                    Name = name,
                    Command = command,
                    ImagePath = CommandLine.ResolveImage(command),
                    Location = $@"{hiveName}{wow}\{path}",
                    Trigger = source == EntrySource.RunOnceKey ? "Next logon only" : "At logon",
                    Enabled = approved is null || IsApproved(hive, approved, name),
                    CanToggle = approved is not null,
                    CanDelete = source != EntrySource.PolicyRun,
                    Machine = hive == RegistryHive.LocalMachine,
                    Hive = hive,
                    View = view,
                    KeyPath = path,
                    ValueName = name,
                    ApprovedKey = approved,
                });
            }
        }

        Read(RegistryHive.CurrentUser, RegistryView.Registry64, RunPath, EntrySource.RunKey, "Run");
        Read(RegistryHive.CurrentUser, RegistryView.Registry64, RunOncePath, EntrySource.RunOnceKey, null);
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, RunPath, EntrySource.RunKey, "Run");
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, RunPath, EntrySource.RunKey, "Run32");
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, RunOncePath, EntrySource.RunOnceKey, null);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, RunOncePath, EntrySource.RunOnceKey, null);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64, PolicyRunPath, EntrySource.PolicyRun, null);
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, PolicyRunPath, EntrySource.PolicyRun, null);
    }

    /// <summary>Task Manager's on/off state: first byte even = enabled, odd = disabled. Missing = enabled.</summary>
    public static bool IsApproved(RegistryHive hive, string approvedKey, string name)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"{ApprovedRoot}\{approvedKey}");
        return key?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    // ── Logon: Startup folders ────────────────────────────────────────────────────────────

    private static void ScanStartupFolders(List<StartupEntry> list)
    {
        void Read(Environment.SpecialFolder folder, RegistryHive hive)
        {
            var dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir))
                return;

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                var (target, args) = Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? Shortcut.Resolve(file) : (file, "");
                list.Add(new StartupEntry
                {
                    Category = StartupCategory.Logon,
                    Source = EntrySource.StartupFolder,
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = string.IsNullOrEmpty(args) ? target : $"\"{target}\" {args}",
                    ImagePath = target,
                    Location = dir,
                    Trigger = "At logon",
                    Enabled = IsApproved(hive, "StartupFolder", fileName),
                    CanToggle = true,
                    CanDelete = true,
                    Machine = hive == RegistryHive.LocalMachine,
                    Hive = hive,
                    ValueName = fileName,
                    ApprovedKey = "StartupFolder",
                    FilePath = file,
                });
            }
        }

        Read(Environment.SpecialFolder.Startup, RegistryHive.CurrentUser);
        Read(Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine);
    }

    // ── Scheduled tasks (Task Scheduler 2.0 COM) ──────────────────────────────────────────

    private static void ScanTasks(List<StartupEntry> list)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null)
            return;

        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();

        void Walk(dynamic folder)
        {
            foreach (dynamic task in folder.GetTasks(1 /* TASK_ENUM_HIDDEN */))
            {
                try
                {
                    AddTask(list, task);
                }
                catch (Exception ex)
                {
                    Log.Warn($"StartupHub task: {ex.Message}");
                }
            }

            foreach (dynamic sub in folder.GetFolders(0))
                Walk(sub);
        }

        Walk(service.GetFolder("\\"));
    }

    private static void AddTask(List<StartupEntry> list, dynamic task)
    {
        string path = task.Path;
        dynamic definition = task.Definition;

        string? exec = null, args = null;
        foreach (dynamic action in definition.Actions)
        {
            if ((int)action.Type == 0) // TASK_ACTION_EXEC
            {
                exec = action.Path;
                args = action.Arguments;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(exec))
            return;

        var triggers = new List<string>();
        foreach (dynamic trigger in definition.Triggers)
        {
            triggers.Add((int)trigger.Type switch
            {
                8 => "At startup",
                9 => "At logon",
                6 => "When idle",
                11 => "On unlock / connect",
                0 => "On event",
                1 => "Once",
                2 => "Daily",
                3 => "Weekly",
                4 => "Monthly",
                _ => "Scheduled",
            });
        }

        var command = string.IsNullOrWhiteSpace(args) ? exec! : $"{exec} {args}";
        string description = "";
        try { description = definition.RegistrationInfo.Description ?? ""; } catch { }

        list.Add(new StartupEntry
        {
            Category = StartupCategory.Tasks,
            Source = EntrySource.Task,
            Name = (string)task.Name,
            Command = command,
            ImagePath = CommandLine.ResolveImage($"\"{Environment.ExpandEnvironmentVariables(exec!.Trim('"'))}\""),
            Location = $"Task Scheduler{path[..Math.Max(1, path.LastIndexOf('\\'))]}",
            Description = description.StartsWith('$') ? "" : description,
            Trigger = triggers.Count == 0 ? "On demand" : string.Join(", ", triggers.Distinct()),
            Enabled = (bool)task.Enabled,
            CanToggle = true,
            Machine = true,
            TaskPath = path,
            MicrosoftTask = path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase),
        });
    }

    // ── Services ──────────────────────────────────────────────────────────────────────────

    private static void ScanServices(List<StartupEntry> list)
    {
        var disabledByUs = LoadDisabledServices();
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null)
            return;

        foreach (var name in services.GetSubKeyNames())
        {
            try
            {
                AddService(list, services, name, disabledByUs);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A few protected services cannot be read without admin rights; skip just those.
            }
        }
    }

    private static void AddService(List<StartupEntry> list, RegistryKey services, string name, HashSet<string> disabledByUs)
    {
        {
            using var key = services.OpenSubKey(name);
            if (key is null)
                return;

            var type = key.GetValue("Type") is int t ? t : 0;
            var start = key.GetValue("Start") is int s ? s : -1;
            if ((type & 0x30) == 0) // Win32 own/shared process services only (no drivers)
                return;

            var auto = start == 2;
            if (!auto && !disabledByUs.Contains(name))
                return;

            var imagePath = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            string? dll = null;
            using (var parameters = key.OpenSubKey("Parameters"))
                dll = parameters?.GetValue("ServiceDll") as string;

            var image = dll is not null ? Environment.ExpandEnvironmentVariables(dll) : CommandLine.ResolveImage(imagePath);
            var display = Indirect(key.GetValue("DisplayName") as string) ?? name;
            var delayed = key.GetValue("DelayedAutostart") is int d && d == 1;

            list.Add(new StartupEntry
            {
                Category = StartupCategory.Services,
                Source = EntrySource.Service,
                Name = display,
                Command = Environment.ExpandEnvironmentVariables(imagePath),
                ImagePath = image,
                Location = $@"HKLM\SYSTEM\CurrentControlSet\Services\{name}",
                Description = Indirect(key.GetValue("Description") as string) ?? "",
                Trigger = delayed ? "At startup (delayed)" : "At startup",
                Enabled = auto,
                CanToggle = true,
                Machine = true,
                ServiceName = name,
            });
        }
    }

    public static HashSet<string> LoadDisabledServices()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StatePath)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    public static void SaveDisabledServices(HashSet<string> names)
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(names)); }
        catch (Exception ex) { Log.Warn($"StartupHub state not saved: {ex.Message}"); }
    }

    // ── Advanced (security-relevant, read only) ───────────────────────────────────────────

    private static void ScanAdvanced(List<StartupEntry> list)
    {
        void Add(string name, string value, string location, bool unusual, string description, string? image = null) => list.Add(new StartupEntry
        {
            Category = StartupCategory.Advanced,
            Source = EntrySource.Advanced,
            Name = name,
            Command = value,
            ImagePath = image ?? CommandLine.ResolveImage(value),
            Location = location,
            Description = description,
            Trigger = unusual ? "Not the Windows default" : "Windows default",
            Unusual = unusual,
        });

        const string winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        using (var key = Registry.LocalMachine.OpenSubKey(winlogon))
        {
            var shell = key?.GetValue("Shell") as string ?? "";
            Add("Winlogon Shell", shell, $@"HKLM\{winlogon}", !shell.Trim().Equals("explorer.exe", StringComparison.OrdinalIgnoreCase),
                "The program Windows starts as the desktop shell. Normally explorer.exe.");
            var userinit = key?.GetValue("Userinit") as string ?? "";
            var expected = Path.Combine(Environment.SystemDirectory, "userinit.exe") + ",";
            Add("Winlogon Userinit", userinit, $@"HKLM\{winlogon}", !userinit.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase),
                "Runs at every logon. Normally only userinit.exe; anything appended here starts with Windows.");
        }

        const string session = @"SYSTEM\CurrentControlSet\Control\Session Manager";
        using (var key = Registry.LocalMachine.OpenSubKey(session))
        {
            var boot = key?.GetValue("BootExecute") as string[] ?? [];
            var text = string.Join(" | ", boot);
            Add("BootExecute", text, $@"HKLM\{session}", boot.Any(b => !b.Trim().Equals("autocheck autochk *", StringComparison.OrdinalIgnoreCase)),
                "Native programs run before Windows starts. Normally only the disk check (autocheck autochk *).", image: Path.Combine(Environment.SystemDirectory, "autochk.exe"));
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows");
            var dlls = key?.GetValue("AppInit_DLLs") as string ?? "";
            var load = key?.GetValue("LoadAppInit_DLLs") is int l && l == 1;
            if (!string.IsNullOrWhiteSpace(dlls))
                Add($"AppInit_DLLs ({(view == RegistryView.Registry32 ? "32-bit" : "64-bit")})", dlls,
                    @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", load,
                    load ? "These DLLs load into every program that uses user32.dll." : "Listed but not loaded (LoadAppInit_DLLs is off).");
        }

        const string ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        using (var key = Registry.LocalMachine.OpenSubKey(ifeo))
        {
            foreach (var exe in key?.GetSubKeyNames() ?? [])
            {
                try
                {
                    using var sub = key!.OpenSubKey(exe);
                    if (sub?.GetValue("Debugger") is string debugger && !string.IsNullOrWhiteSpace(debugger))
                        Add($"{exe} → debugger", debugger, $@"HKLM\{ifeo}\{exe}", true,
                            $"Whenever {exe} starts, Windows runs this program instead.");
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    // Protected key: skip it.
                }
            }
        }
    }

    private static string? Indirect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith('@'))
            return value;

        var sb = new StringBuilder(1024);
        return SHLoadIndirectString(value, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : value;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);
}

/// <summary>Turns a command line into the file that actually runs.</summary>
internal static class CommandLine
{
    private static readonly string[] Hosts = ["rundll32.exe", "rundll32"];

    public static string? ResolveImage(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        string exe, rest;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            exe = end > 0 ? text[1..end] : text.Trim('"');
            rest = end > 0 ? text[(end + 1)..].Trim() : "";
        }
        else
        {
            // Unquoted paths may contain spaces: take the shortest prefix that ends in an executable extension.
            var lower = text.ToLowerInvariant();
            var cut = new[] { ".exe", ".com", ".bat", ".cmd", ".dll", ".scr" }
                .Select(ext => lower.IndexOf(ext, StringComparison.Ordinal))
                .Where(i => i > 0)
                .DefaultIfEmpty(-1)
                .Min();
            if (cut > 0)
            {
                exe = text[..(cut + 4)];
                rest = text[(cut + 4)..].Trim();
            }
            else
            {
                var space = text.IndexOf(' ');
                exe = space > 0 ? text[..space] : text;
                rest = space > 0 ? text[(space + 1)..] : "";
            }
        }

        // rundll32 "x.dll",Entry → the DLL is what matters.
        if (Hosts.Any(h => Path.GetFileName(exe).Equals(h, StringComparison.OrdinalIgnoreCase)) && rest.Length > 0)
        {
            var dll = rest.Split(',')[0].Trim().Trim('"');
            if (dll.Length > 0)
                exe = dll;
        }

        exe = exe.Replace(@"\??\", "").Trim();
        if (exe.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe[12..]);
        if (exe.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
            exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);

        if (Path.IsPathRooted(exe))
            return exe;

        // Bare names: look where Windows would.
        foreach (var dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) }
                     .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)))
        {
            foreach (var candidate in new[] { exe, exe + ".exe" })
            {
                try
                {
                    var full = Path.Combine(dir, candidate);
                    if (File.Exists(full))
                        return full;
                }
                catch
                {
                    // Bad PATH entry.
                }
            }
        }

        return exe;
    }
}

/// <summary>Reads .lnk targets through the Windows Script Host COM object.</summary>
internal static class Shortcut
{
    public static (string Target, string Arguments) Resolve(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
                return (lnk, "");
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(lnk);
            string target = link.TargetPath;
            string args = link.Arguments;
            return (string.IsNullOrEmpty(target) ? lnk : target, args ?? "");
        }
        catch
        {
            return (lnk, "");
        }
    }
}
