using System.Drawing;
using Durazzno.Core;
using Microsoft.Win32;

namespace Durazzno.Tray;

internal sealed record Palette(
    Color Background,
    Color Text,
    Color MutedText,
    Color Hover,
    Color Border,
    bool IsDark);

internal static class Theme
{
    public static readonly Palette Light = new(
        Background: Color.FromArgb(0xFB, 0xFB, 0xFB),
        Text: Color.FromArgb(0x1C, 0x1C, 0x1E),
        MutedText: Color.FromArgb(0x8A, 0x8A, 0x8E),
        Hover: Color.FromArgb(0xEA, 0xEA, 0xEC),
        Border: Color.FromArgb(0xDD, 0xDD, 0xE0),
        IsDark: false);

    public static readonly Palette Dark = new(
        Background: Color.FromArgb(0x24, 0x24, 0x27),
        Text: Color.FromArgb(0xF2, 0xF2, 0xF3),
        MutedText: Color.FromArgb(0x8E, 0x8E, 0x93),
        Hover: Color.FromArgb(0x38, 0x38, 0x3C),
        Border: Color.FromArgb(0x3F, 0x3F, 0x45),
        IsDark: true);

    // Status colours, readable on both palettes.
    public static readonly Color Success = Color.FromArgb(0x22, 0xC5, 0x5E);
    public static readonly Color Warning = Color.FromArgb(0xF5, 0x9E, 0x0B);
    public static readonly Color Danger = Color.FromArgb(0xEF, 0x44, 0x44);
    public static readonly Color UpdateAccent = Color.FromArgb(0xF9, 0x73, 0x16);
    public static readonly Color OfflineBadge = UpdateAccent;
    public static readonly Color Info = Color.FromArgb(0x0E, 0xA5, 0xE9);
    public static readonly Color Neutral = Color.FromArgb(0x64, 0x74, 0x8B);

    public static Palette For(MenuTheme theme) => theme switch
    {
        MenuTheme.Light => Light,
        MenuTheme.Dark => Dark,
        _ => SystemUsesDarkApps() ? Dark : Light,
    };

    public static bool SystemUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
