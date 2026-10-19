using Microsoft.Win32;

namespace Durazzno.Tweaks;

/// <summary>
/// One registry value a tweak sets. Default is Windows' own value, used to undo a tweak Durazzno did not apply (null:
/// the value does not exist). DeleteKeyOnUndo removes a key the tweak created (the classic context menu).
/// </summary>
internal sealed record RegValue(RegistryHive Hive, string Key, string Name, object Applied, RegistryValueKind Kind, object? Default,
    string? DeleteKeyOnUndo = null);

/// <summary>What it takes for a tweak to show: nothing, restarting File Explorer, or restarting Windows.</summary>
internal enum TweakEffect { Now, Explorer, Restart }

internal sealed record Tweak(string Id, string Category, string Name, string Description, RegValue[] Values,
    TweakEffect Effect = TweakEffect.Now, bool Windows11Only = false, Action? Refresh = null)
{
    /// <summary>Writes under HKEY_LOCAL_MACHINE: one UAC prompt.</summary>
    public bool NeedsAdmin => Values.Any(v => v.Hive == RegistryHive.LocalMachine);
}

/// <summary>The tweaks WinTweaks offers. Every one is a set of registry values and can be undone.</summary>
internal static class TweakCatalog
{
    public const string Privacy = "PRIVACY";
    public const string Performance = "PERFORMANCE";
    public const string Explorer = "EXPLORER & TASKBAR";

    private const RegistryHive User = RegistryHive.CurrentUser;
    private const RegistryHive Machine = RegistryHive.LocalMachine;
    private const RegistryValueKind Dword = RegistryValueKind.DWord;
    private const RegistryValueKind Text = RegistryValueKind.String;

    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ClassicMenu = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    public static readonly IReadOnlyList<Tweak> All =
    [
        // ── Privacy ─────────────────────────────────────────────────────────────────────
        new("ads-id", Privacy, "No advertising ID", "Apps can't use your advertising ID to show you personalized ads.",
            [new(User, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, Dword, 1)]),
        new("tailored", Privacy, "No tailored experiences", "Microsoft doesn't use your diagnostic data for tips and ads.",
            [new(User, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, Dword, 1)]),
        new("suggestions", Privacy, "No suggested apps or ads", "Stops app suggestions in Start and Settings, and apps that install themselves.",
        [
            new(User, ContentDelivery, "SubscribedContent-338388Enabled", 0, Dword, 1),
            new(User, ContentDelivery, "SubscribedContent-338389Enabled", 0, Dword, 1),
            new(User, ContentDelivery, "SubscribedContent-353694Enabled", 0, Dword, 1),
            new(User, ContentDelivery, "SubscribedContent-353696Enabled", 0, Dword, 1),
            new(User, ContentDelivery, "SystemPaneSuggestionsEnabled", 0, Dword, 1),
            new(User, ContentDelivery, "SilentInstalledAppsEnabled", 0, Dword, 1),
        ]),
        new("lockscreen-tips", Privacy, "No lock screen tips", "No fun facts, tips or ads on the lock screen.",
        [
            new(User, ContentDelivery, "RotatingLockScreenOverlayEnabled", 0, Dword, 1),
            new(User, ContentDelivery, "SubscribedContent-338387Enabled", 0, Dword, 1),
        ]),
        new("web-search", Privacy, "No web results in search", "Searching from Start only looks on this PC, not on Bing.",
        [
            new(User, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, Dword, null),
            new(User, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, Dword, null),
        ], TweakEffect.Explorer),
        new("telemetry", Privacy, "Required diagnostic data only", "Windows sends Microsoft only the diagnostic data it needs to work.",
            [new(Machine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1, Dword, null)]),
        new("activity", Privacy, "No activity history", "Windows stops keeping a history of the apps and files you use.",
        [
            new(Machine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, Dword, null),
            new(Machine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, Dword, null),
        ]),

        // ── Performance ─────────────────────────────────────────────────────────────────
        new("game-mode", Performance, "Game Mode", "Windows gives games priority and holds back updates while you play.",
            [new(User, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, Dword, null)]),
        new("game-dvr", Performance, "No background recording", "Turns off Game Bar's background capture, which can cost FPS.",
        [
            new(User, @"System\GameConfigStore", "GameDVR_Enabled", 0, Dword, 1),
            new(User, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, Dword, null),
        ]),
        new("mouse-accel", Performance, "No mouse acceleration", "Turns off Enhance pointer precision: the same hand movement always moves the pointer the same distance.",
        [
            new(User, @"Control Panel\Mouse", "MouseSpeed", "0", Text, "1"),
            new(User, @"Control Panel\Mouse", "MouseThreshold1", "0", Text, "6"),
            new(User, @"Control Panel\Mouse", "MouseThreshold2", "0", Text, "10"),
        ], Refresh: SystemSettings.ApplyMouse),
        new("menu-delay", Performance, "Faster menus", "Menus open after 100 ms instead of 400 ms.",
            [new(User, @"Control Panel\Desktop", "MenuShowDelay", "100", Text, "400")], Refresh: SystemSettings.ApplyMenuDelay),
        new("gpu-scheduling", Performance, "Hardware-accelerated GPU scheduling", "The graphics card manages its own memory, which can lower latency. Needs a recent GPU and driver.",
            [new(Machine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, Dword, null)], TweakEffect.Restart),

        // ── Explorer & taskbar ──────────────────────────────────────────────────────────
        new("file-extensions", Explorer, "Show file extensions", "See .exe, .pdf and the rest, so a file can't pretend to be something else.",
            [new(User, Advanced, "HideFileExt", 0, Dword, 1)], TweakEffect.Explorer),
        new("hidden-files", Explorer, "Show hidden files", "File Explorer shows hidden files and folders.",
            [new(User, Advanced, "Hidden", 1, Dword, 2)], TweakEffect.Explorer),
        new("this-pc", Explorer, "Open File Explorer to This PC", "File Explorer opens on your drives instead of Quick access.",
            [new(User, Advanced, "LaunchTo", 1, Dword, null)], TweakEffect.Explorer),
        new("classic-menu", Explorer, "Classic right-click menu", "Shows the full menu right away, without Show more options.",
            [new(User, ClassicMenu + @"\InprocServer32", "", "", Text, null, DeleteKeyOnUndo: ClassicMenu)], TweakEffect.Explorer, Windows11Only: true),
        new("taskbar-left", Explorer, "Taskbar on the left", "Start and the taskbar icons go back to the left, like Windows 10.",
            [new(User, Advanced, "TaskbarAl", 0, Dword, null)], Windows11Only: true),
        new("end-task", Explorer, "End task in the taskbar", "Right-click an app in the taskbar to close it, even when it hangs.",
            [new(User, Advanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, Dword, null)], TweakEffect.Explorer, Windows11Only: true),
        new("copilot", Explorer, "No Copilot", "Removes Copilot from the taskbar and turns it off.",
        [
            new(User, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, Dword, null),
            new(User, Advanced, "ShowCopilotButton", 0, Dword, null),
        ], TweakEffect.Explorer, Windows11Only: true),
    ];

    public static bool IsWindows11 { get; } = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>The tweaks that apply to this Windows.</summary>
    public static IEnumerable<Tweak> ForThisPc => All.Where(t => !t.Windows11Only || IsWindows11);
}
