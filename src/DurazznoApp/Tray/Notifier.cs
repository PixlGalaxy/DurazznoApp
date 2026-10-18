using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using Durazzno.Core;
using Microsoft.Win32;

namespace Durazzno.Tray;

/// <summary>Who a notification comes from: Durazzno App itself, or the feature that raised it.</summary>
internal enum NotifyLogo { Durazzno, PowerCuts, DiskInfo, WebinCloud, DiskMark, DiskMap, StartupHub, TimerResolution, DriverHub }

/// <summary>What happened: drawn as a coloured badge on the notification's picture (none for Default).</summary>
internal enum NotifyTone { Default, Success, Warning, Error, Cancelled }

/// <summary>
/// Windows toast notifications branded per feature, as the original Python PowerCuts showed them: a PowerCuts warning
/// says "PowerCuts" with the PowerCuts logo, a connection problem comes from "Webin Cloud Server" with its cloud.
/// Each sender is an AppUserModelID registered under HKCU\Software\Classes\AppUserModelId (name + icon, no installer or
/// shortcut needed). The WinRT toast API is called through raw COM so the app does not carry the 25 MB Windows SDK
/// projection. If toasts are unavailable, the tray balloon is used instead.
/// </summary>
internal static class Notifier
{
    private sealed record Sender(string Aumid, string DisplayName, Func<Bitmap> Logo);

    private static readonly Dictionary<NotifyLogo, Sender> Senders = new()
    {
        [NotifyLogo.Durazzno] = new("Durazzno.App", "Durazzno App", () => new Bitmap(IconFactory.Logo(LogoKind.Durazzno), 256, 256)),
        [NotifyLogo.PowerCuts] = new("Durazzno.PowerCuts", "PowerCuts", () => new Bitmap(IconFactory.Embedded("Plugins.powercuts", 256))),
        [NotifyLogo.DiskInfo] = new("Durazzno.DiskInfo", "DiskInfo", () => new Bitmap(IconFactory.Lucide("hard-drive", ColorTranslator.FromHtml("#3B82F6"), 256))),
        [NotifyLogo.WebinCloud] = new("Durazzno.WebinCloud", "Webin Cloud Server", () => new Bitmap(IconFactory.Embedded("Plugins.webincloud", 256))),
        [NotifyLogo.DiskMark] = ToolSender("diskmark"),
        [NotifyLogo.DiskMap] = ToolSender("diskmap"),
        [NotifyLogo.StartupHub] = ToolSender("startuphub"),
        [NotifyLogo.TimerResolution] = ToolSender("timerresolution"),
        [NotifyLogo.DriverHub] = ToolSender("driverhub"),
    };

    /// <summary>A tool's sender: its name and its sidebar icon in its colour.</summary>
    private static Sender ToolSender(string id)
    {
        var tool = Tools.ToolCatalog.All.First(t => t.Id == id);
        return new($"Durazzno.{tool.Name}", tool.Name, () => new Bitmap(IconFactory.Lucide(tool.Icon, ColorTranslator.FromHtml(tool.Accent), 256)));
    }

    /// <summary>The sender for a tool page's notifications (Durazzno App for anything else).</summary>
    public static NotifyLogo ForTool(string toolId) => toolId switch
    {
        "diskinfo" => NotifyLogo.DiskInfo,
        "diskmark" => NotifyLogo.DiskMark,
        "diskmap" => NotifyLogo.DiskMap,
        "startuphub" => NotifyLogo.StartupHub,
        "timerresolution" => NotifyLogo.TimerResolution,
        "driverhub" => NotifyLogo.DriverHub,
        "powercuts" => NotifyLogo.PowerCuts,
        _ => NotifyLogo.Durazzno,
    };

    private static readonly Dictionary<NotifyLogo, string> Registered = new();

    public static void Show(NotifyIcon tray, string title, string text, ToolTipIcon fallback, NotifyLogo logo, NotifyTone tone = NotifyTone.Default)
    {
        try
        {
            ShowToast(logo, title, text, tone);
            Log.Info($"Notification ({Senders[logo].DisplayName}): {title}");
            return;
        }
        catch (Exception ex)
        {
            Log.Warn($"Toast notification failed, using the tray balloon: {ex.Message}");
        }
        // A balloon needs both lines: a one-line toast becomes "sender: line".
        tray.ShowBalloonTip(5000, text.Length == 0 ? Senders[logo].DisplayName : title, text.Length == 0 ? title : text, fallback);
    }

    /// <summary>Removes the registered senders (used when cleaning up).</summary>
    public static void Unregister()
    {
        foreach (var sender in Senders.Values)
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{sender.Aumid}", throwOnMissingSubKey: false);
    }

    /// <summary>Writes the sender's logo next to the settings and registers its name and icon. Once per run.</summary>
    private static string Register(NotifyLogo logo)
    {
        if (Registered.TryGetValue(logo, out var png))
            return png;

        var sender = Senders[logo];
        var folder = Directory.CreateDirectory(Path.Combine(AppInfo.DataDir, "notifications")).FullName;
        png = Path.Combine(folder, $"{sender.Aumid}.png");
        using (var bitmap = sender.Logo())
            bitmap.Save(png, ImageFormat.Png);

        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{sender.Aumid}");
        key.SetValue("DisplayName", sender.DisplayName);
        key.SetValue("IconUri", png);
        Registered[logo] = png;
        return png;
    }

    private static readonly Dictionary<(NotifyLogo, NotifyTone), string> Badged = new();

    /// <summary>
    /// The sender's logo with a status badge in the corner: green check (success), orange "!" (warning / partial),
    /// red cross (failed), grey dash (cancelled). The toast header keeps the plain logo.
    /// </summary>
    private static string Picture(NotifyLogo logo, NotifyTone tone)
    {
        var plain = Register(logo);
        if (tone == NotifyTone.Default)
            return plain;
        if (Badged.TryGetValue((logo, tone), out var cached))
            return cached;

        var path = Path.Combine(Path.GetDirectoryName(plain)!, $"{Senders[logo].Aumid}.{tone}.png");
        using (var source = new Bitmap(plain))
        using (var canvas = new Bitmap(256, 256))
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, 256, 256));

            var color = ColorTranslator.FromHtml(tone switch
            {
                NotifyTone.Success => "#22C55E",
                NotifyTone.Warning => "#F97316",
                NotifyTone.Error => "#EF4444",
                _ => "#94A3B8",
            });
            var badge = new Rectangle(150, 150, 100, 100);
            using (var ring = new SolidBrush(Color.FromArgb(0x15, 0x15, 0x18)))
                g.FillEllipse(ring, Rectangle.Inflate(badge, 8, 8));
            using (var fill = new SolidBrush(color))
                g.FillEllipse(fill, badge);
            using var pen = new Pen(Color.White, 13) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            switch (tone)
            {
                case NotifyTone.Success:
                    g.DrawLines(pen, [new Point(176, 202), new Point(194, 220), new Point(225, 184)]);
                    break;
                case NotifyTone.Warning:
                    g.DrawLine(pen, 200, 172, 200, 206);
                    using (var dot = new SolidBrush(Color.White))
                        g.FillEllipse(dot, 193, 218, 14, 14);
                    break;
                case NotifyTone.Error:
                    g.DrawLine(pen, 180, 180, 220, 220);
                    g.DrawLine(pen, 220, 180, 180, 220);
                    break;
                default:
                    g.DrawLine(pen, 178, 200, 222, 200);
                    break;
            }
            canvas.Save(path, ImageFormat.Png);
        }
        Badged[(logo, tone)] = path;
        return path;
    }

    private static void ShowToast(NotifyLogo logo, string title, string text, NotifyTone tone)
    {
        Register(logo);
        var png = Picture(logo, tone);
        var xml = "<toast><visual><binding template=\"ToastGeneric\">"
                  + $"<text>{SecurityElement.Escape(title)}</text>" + (text.Length > 0 ? $"<text>{SecurityElement.Escape(text)}</text>" : "")
                  + $"<image placement=\"appLogoOverride\" src=\"{SecurityElement.Escape(new Uri(png).AbsoluteUri)}\"/>"
                  + "</binding></visual></toast>";
        WinRtToast.Show(Senders[logo].Aumid, xml);
    }

    /// <summary>
    /// Minimal WinRT interop for ToastNotificationManager.CreateToastNotifier(id).Show(new ToastNotification(xml)):
    /// activation factories from combase, vtable calls on the interfaces (IUnknown + IInspectable occupy slots 0–5).
    /// </summary>
    private static unsafe class WinRtToast
    {
        private static readonly Guid IID_IToastNotificationManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
        private static readonly Guid IID_IToastNotificationFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
        private static readonly Guid IID_IXmlDocument = new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");
        private static readonly Guid IID_IXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");

        public static void Show(string aumid, string xml)
        {
            IntPtr manager = 0, factory = 0, inspectable = 0, xmlDoc = 0, xmlIO = 0, toast = 0, notifier = 0;
            try
            {
                manager = Factory("Windows.UI.Notifications.ToastNotificationManager", IID_IToastNotificationManagerStatics);
                factory = Factory("Windows.UI.Notifications.ToastNotification", IID_IToastNotificationFactory);

                using (var name = new HString("Windows.Data.Xml.Dom.XmlDocument"))
                    Check(RoActivateInstance(name.Handle, out inspectable));
                Check(Marshal.QueryInterface(inspectable, in IID_IXmlDocumentIO, out xmlIO));
                Check(Marshal.QueryInterface(inspectable, in IID_IXmlDocument, out xmlDoc));
                using (var content = new HString(xml))
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(xmlIO, 6))(xmlIO, content.Handle)); // LoadXml

                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(factory, 6))(factory, xmlDoc, &toast)); // CreateToastNotification
                using (var id = new HString(aumid))
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(manager, 7))(manager, id.Handle, &notifier)); // CreateToastNotifierWithId
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(notifier, 6))(notifier, toast)); // Show
            }
            finally
            {
                foreach (var p in new[] { notifier, toast, xmlIO, xmlDoc, inspectable, factory, manager })
                    if (p != 0)
                        Marshal.Release(p);
            }
        }

        private static IntPtr Factory(string className, Guid iid)
        {
            using var name = new HString(className);
            Check(RoGetActivationFactory(name.Handle, in iid, out var factory));
            return factory;
        }

        private static IntPtr Slot(IntPtr instance, int index) => (*(IntPtr**)instance)[index];

        private static void Check(int hr)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }

        private sealed class HString : IDisposable
        {
            public HString(string value) => Check(WindowsCreateString(value, (uint)value.Length, out Handle));
            public readonly IntPtr Handle;
            public void Dispose() => WindowsDeleteString(Handle);
        }

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string source, uint length, out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);

        [DllImport("combase.dll")]
        private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);
    }
}
