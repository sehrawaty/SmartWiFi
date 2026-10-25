using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using SmartWiFi.Core;

namespace SmartWiFi.UI;

/// <summary>Wi-Fi glyphs drawn at runtime, one colour per state, sized for the current DPI.</summary>
internal static partial class TrayIcons
{
    public static readonly Color Online = Color.FromArgb(46, 170, 96);
    public static readonly Color Portal = Color.FromArgb(232, 155, 30);
    public static readonly Color Offline = Color.FromArgb(214, 72, 62);
    public static readonly Color Idle = Color.FromArgb(140, 150, 158);

    private static readonly Dictionary<(Connectivity, bool, bool), Icon> Cache = new();

    public static Icon For(Connectivity state, bool hotspot, bool paused)
    {
        var key = (state, hotspot, paused);
        if (Cache.TryGetValue(key, out var icon)) return icon;
        var color = state switch
        {
            Connectivity.Online => Online,
            Connectivity.CaptivePortal => Portal,
            Connectivity.NoInternet => Offline,
            _ => Idle,
        };
        icon = Draw(color, hotspot, paused);
        Cache[key] = icon;
        return icon;
    }

    private static Icon Draw(Color color, bool hotspot, bool paused)
    {
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size;
            var cx = s / 2f;
            var cy = s * 0.84f;
            var arcs = paused ? Idle : color;
            using var pen = new Pen(arcs, Math.Max(1.6f, s * 0.12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            foreach (var r in new[] { 0.66f, 0.40f })
            {
                var R = s * r;
                g.DrawArc(pen, cx - R, cy - R, 2 * R, 2 * R, 225, 90);
            }
            // The dot carries the state colour even while paused; a hollow dot marks the hotspot.
            var d = s * 0.24f;
            using var brush = new SolidBrush(color);
            if (hotspot)
            {
                using var ring = new Pen(color, Math.Max(1.4f, s * 0.09f));
                g.DrawEllipse(ring, cx - d / 2f, cy - d / 2f, d, d);
            }
            else
            {
                g.FillEllipse(brush, cx - d / 2f, cy - d / 2f, d, d);
            }
        }

        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    public static Icon AppIcon()
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("SmartWiFi.Assets.smartwifi.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
