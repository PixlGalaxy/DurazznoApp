using System.Text.Json;
using System.Text.Json.Serialization;

namespace Durazzno.Core;

internal enum IconStyle { Colorful, Monochrome }

internal enum MenuTheme { System, Light, Dark }

internal enum LogoKind { Durazzno, Galaxy }

/// <summary>Main window scale: Compact 80 %, Normal 90 %, Large 100 %.</summary>
internal enum UiSize { Compact, Normal, Large }

internal sealed class AppSettings
{
    public IconStyle IconStyle { get; set; } = IconStyle.Colorful;
    public MenuTheme Theme { get; set; } = MenuTheme.System;
    public LogoKind Logo { get; set; } = LogoKind.Durazzno;
    public bool AutoUpdate { get; set; } = true;
    public UiSize UiSize { get; set; } = UiSize.Compact;

    /// <summary>Timer resolution (ms) TimerResolution should hold; null = not requesting.</summary>
    public double? TimerResolutionMs { get; set; }
    public bool TimerResolutionOnStartup { get; set; }

    /// <summary>DriverHub checks for new drivers in the background and notifies (every DriverCheckHours).</summary>
    public bool DriverCheckNotify { get; set; }
    public int DriverCheckHours { get; set; } = 24;
    public DateTime? DriverCheckLast { get; set; }

    /// <summary>Updates already announced, so the background check only notifies about new ones.</summary>
    public List<string>? DriverNotifiedIds { get; set; }

    /// <summary>Driver updates the user hid (right-click → Hide this update) until a newer one appears.</summary>
    public List<string>? HiddenDriverUpdates { get; set; }

    /// <summary>Optional override of the update manifest location (URL or local path), for testing.</summary>
    public string? ManifestUrl { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. "Start with Windows" lives in the registry instead.</summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path = Path.Combine(AppInfo.DataDir, "settings.json");

    public SettingsStore()
    {
        IsFirstRun = !File.Exists(_path);
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public bool IsFirstRun { get; }

    public event EventHandler? Changed;

    public void Update(Action<AppSettings> change)
    {
        var next = Current.Clone();
        change(next);
        Current = next;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        // Keep the developer override; it is not a user preference.
        Current = new AppSettings { ManifestUrl = Current.ManifestUrl };
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path))
            return new AppSettings();

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("settings.json is unreadable, using defaults", ex);
            TryCopy(_path, _path + ".broken");
            return new AppSettings();
        }
    }

    private void Save()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    private static void TryCopy(string from, string to)
    {
        try { File.Copy(from, to, overwrite: true); } catch { }
    }
}
