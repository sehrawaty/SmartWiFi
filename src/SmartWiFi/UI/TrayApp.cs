using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using Microsoft.Win32;
using SmartWiFi.Core;
using SmartWiFi.Core.Data;
using SmartWiFi.Core.Engine;
using SmartWiFi.Core.Logging;
using SmartWiFi.Core.Models;
using SmartWiFi.Core.Net;
using SmartWiFi.Core.Notify;
using SmartWiFi.Core.Portal;
using SmartWiFi.Core.Storage;
using SmartWiFi.Platform;

namespace SmartWiFi.UI;

/// <summary>Owns the tray icon and menu, and wires the engine to the real Windows pieces.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private volatile AppSettings _settings;
    private readonly DpapiSecretStore _secrets = new();
    private readonly Log _log;
    private readonly WindowsWifiAdapter _wifi = new();
    private readonly HttpConnectivityProbe _probe;
    private readonly NtfyNotifier _ntfy;
    private readonly TailscaleCli _tailscale;
    private readonly DataMeter _meter;
    private readonly SwitchEngine _engine;
    private readonly CancellationTokenSource _cts = new();

    private readonly Control _ui = new();
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false, ShowCheckMargin = true };
    private readonly System.Windows.Forms.Timer _awakeTimer = new() { Interval = 30_000 };
    private bool _menuDirty = true;
    private Task? _loop;
    private SettingsForm? _settingsForm;
    private LogForm? _logForm;
    private bool _exiting;

    public TrayApp(bool background)
    {
        _ = _ui.Handle; // forces a window handle so background threads can BeginInvoke onto the UI thread

        Directory.CreateDirectory(AppPaths.Data);
        _log = new Log(AppPaths.Logs);
        _settings = JsonFile.Load<AppSettings>(AppPaths.Settings);

        _probe = new HttpConnectivityProbe(() => (_settings.General.ProbeUrl, _settings.General.ProbeExpected));
        _ntfy = new NtfyNotifier(() => _settings.Notifications, _secrets, new SystemClock(),
            JsonFile.Load<NotificationQueue>(AppPaths.Queue), q => TrySave(AppPaths.Queue, q));
        _tailscale = new TailscaleCli(() => _settings.Tailscale);
        _meter = new DataMeter(JsonFile.Load<DataUsageState>(AppPaths.Usage), u => TrySave(AppPaths.Usage, u));

        var desktop = new DesktopNotifier(() => _settings.Notifications.Desktop, ShowBalloon);
        _engine = new SwitchEngine(() => _settings, _wifi, _probe, new PortalLogin(), new CompositeNotifier(_ntfy, desktop),
            _tailscale, _meter, _secrets, _log, new SystemClock());

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.For(Connectivity.Disconnected, false, false),
            Text = "SmartWiFi: starting",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            // NotifyIcon only opens its menu on right-click; reuse the same menu for left-click.
            typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_icon, null);
        };
        _icon.BalloonTipClicked += (_, _) => ShowLog();
        _menu.Opening += (_, _) => { if (_menuDirty) BuildMenu(_engine.Status); };
        _menu.Closed += (_, _) => { if (_menuDirty) BuildMenu(_engine.Status); };
        BuildMenu(_engine.Status);

        _engine.StatusChanged += s => Post(() => OnStatus(s));
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _awakeTimer.Tick += (_, _) => KeepAwake.Apply(_settings.General.KeepAwakeOnAC);
        _awakeTimer.Start();
        KeepAwake.Apply(_settings.General.KeepAwakeOnAC);
        Autostart.Apply(_settings.General.StartWithWindows);

        if (!_wifi.IsAvailable)
            _log.Warn("no Wi-Fi adapter found; is Wi-Fi turned on?");

        _loop = Task.Run(() => _engine.RunAsync(_cts.Token));

        if (_settings.Networks.Count == 0)
        {
            ShowBalloon("Welcome to SmartWiFi", "Add your phone hotspot and hostel Wi-Fi to get started.");
            Post(ShowSettings);
        }
        else if (!background)
        {
            ShowBalloon("SmartWiFi is running", "It lives in the tray, next to the clock.");
        }
    }

    // ───────────── engine → UI ─────────────

    private void Post(Action a)
    {
        if (_ui.IsDisposed) return;
        try { _ui.BeginInvoke(a); }
        catch (InvalidOperationException) { }
    }

    private void OnStatus(EngineStatus s)
    {
        if (_exiting) return;
        var paused = s.PausedUntil is not null;
        _icon.Icon = TrayIcons.For(s.Checking ? Connectivity.CaptivePortal : s.State, s.IsHotspot, paused);
        _icon.Text = Truncate($"SmartWiFi: {s.Ssid ?? "not connected"} · {s.StateText}{(paused ? " · paused" : "")}", 127);

        if (_menu.Visible) _menuDirty = true;
        else BuildMenu(s);
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _engine.Nudge();

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        Post(() => KeepAwake.Apply(_settings.General.KeepAwakeOnAC));
        if (e.Mode == PowerModes.Resume)
        {
            _log.Info("woke from sleep");
            _engine.Nudge();
        }
    }

    // ───────────── menu ─────────────

    private void BuildMenu(EngineStatus s)
    {
        _menuDirty = false;
        _menu.SuspendLayout();
        foreach (ToolStripItem old in _menu.Items) old.Dispose();
        _menu.Items.Clear();

        var title = new ToolStripMenuItem(s.Ssid ?? (s.WifiAvailable ? "Not connected" : "No Wi-Fi adapter"))
        {
            Enabled = false,
            Font = new Font(_menu.Font, FontStyle.Bold),
        };
        _menu.Items.Add(title);
        _menu.Items.Add(Info(StatusLine(s)));

        if (s.IsHotspot || s.DataTodayBytes > 0)
        {
            var used = Mb(s.DataTodayBytes);
            _menu.Items.Add(Info(s.DataLimitBytes is { } lim ? $"Hotspot data today: {used} of {Mb(lim)}" : $"Hotspot data today: {used}"));
        }
        if (s.PausedUntil is { } p)
            _menu.Items.Add(Info(p > DateTime.Now.AddDays(1) ? "Auto-switching paused" : $"Auto-switching paused until {p:HH:mm}"));
        if (s.HoldSsid is { } hold && s.HoldUntil is { } until)
            _menu.Items.Add(Info($"Keeping {hold} until {until:HH:mm}"));

        _menu.Items.Add(new ToolStripSeparator());

        // Switch to ▸
        var switchTo = new ToolStripMenuItem("Switch to");
        if (_settings.Networks.Count == 0)
        {
            switchTo.DropDownItems.Add(new ToolStripMenuItem("No networks set up") { Enabled = false });
        }
        foreach (var n in _settings.Networks)
        {
            var seen = s.Visible.FirstOrDefault(v => v.Ssid == n.Ssid);
            var label = seen is null ? $"{n.Ssid}   (not in range)" : $"{n.Ssid}   {seen.SignalQuality}%";
            var ssid = n.Ssid;
            var item = new ToolStripMenuItem(label, null, async (_, _) => await Run(() => _engine.SwitchToAsync(ssid, _cts.Token)))
            {
                Checked = n.Ssid == s.Ssid,
                Enabled = seen is not null && n.Ssid != s.Ssid,
            };
            switchTo.DropDownItems.Add(item);
        }
        _menu.Items.Add(switchTo);

        var current = _settings.Find(s.Ssid);
        _menu.Items.Add(new ToolStripMenuItem("Log in to portal now", null, async (_, _) => await Run(() => _engine.LoginToPortalNowAsync(_cts.Token)))
        {
            Enabled = current?.Portal.Enabled == true,
        });

        if (s.TailscaleInstalled)
        {
            _menu.Items.Add(new ToolStripMenuItem("Tailscale", null, async (_, _) =>
                await Run(() => _engine.SetTailscaleAsync(s.TailscaleUp != true, _cts.Token)))
            {
                Checked = s.TailscaleUp == true,
                Enabled = s.TailscaleUp is not null,
            });
        }

        if (s.DataCapReached)
            _menu.Items.Add(new ToolStripMenuItem("Allow 500 MB more today", null, (_, _) => _engine.AllowMoreData(500)));

        if (s.PausedUntil is not null)
        {
            _menu.Items.Add(new ToolStripMenuItem("Resume auto-switching", null, (_, _) => _engine.Resume()));
        }
        else
        {
            var pause = new ToolStripMenuItem("Pause auto-switching");
            pause.DropDownItems.Add("For 15 minutes", null, (_, _) => _engine.Pause(TimeSpan.FromMinutes(15)));
            pause.DropDownItems.Add("For 1 hour", null, (_, _) => _engine.Pause(TimeSpan.FromHours(1)));
            pause.DropDownItems.Add("For 4 hours", null, (_, _) => _engine.Pause(TimeSpan.FromHours(4)));
            pause.DropDownItems.Add("Until I resume", null, (_, _) => _engine.Pause(TimeSpan.FromDays(3650)));
            _menu.Items.Add(pause);
        }

        _menu.Items.Add(new ToolStripMenuItem("Check now", null, (_, _) => _engine.Nudge()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        _menu.Items.Add(new ToolStripMenuItem("Activity log…", null, (_, _) => ShowLog()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Quit SmartWiFi", null, async (_, _) => await QuitAsync()));
        _menu.ResumeLayout();
    }

    private static ToolStripMenuItem Info(string text) => new(text) { Enabled = false };

    private static string StatusLine(EngineStatus s)
    {
        var parts = new List<string> { Capitalize(s.StateText) };
        if (s.SignalQuality is { } q) parts.Add($"{q}%");
        if (s.Rssi is { } r) parts.Add($"{r} dBm");
        if (s.ConnectedSince is { } since) parts.Add($"for {Duration(DateTime.Now - since)}");
        return string.Join(" · ", parts);
    }

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { _log.Error($"{e.GetType().Name}: {e.Message}"); }
    }

    // ───────────── windows ─────────────

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        IEnumerable<string> Suggestions() =>
            _engine.Status.Visible.Select(v => v.Ssid).Concat(_wifi.SavedProfileNames());

        _settingsForm = new SettingsForm(_settings, _secrets, Suggestions, _ntfy, _tailscale.ExePath);
        _settingsForm.FormClosed += (_, _) =>
        {
            if (_settingsForm?.DialogResult == DialogResult.OK)
            {
                _settings = _settingsForm.Result;
                try
                {
                    JsonFile.Save(AppPaths.Settings, _settings);
                    _log.Info("settings saved");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.Error($"couldn't save settings: {e.Message}");
                }
                Autostart.Apply(_settings.General.StartWithWindows);
                KeepAwake.Apply(_settings.General.KeepAwakeOnAC);
                _menuDirty = true;
                _engine.Nudge();
            }
            _settingsForm = null;
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void ShowLog()
    {
        if (_logForm is { IsDisposed: false })
        {
            _logForm.Activate();
            return;
        }
        _logForm = new LogForm(_log);
        _logForm.FormClosed += (_, _) => _logForm = null;
        _logForm.Show();
        _logForm.Activate();
    }

    private void ShowBalloon(string title, string text)
    {
        Post(() =>
        {
            if (_exiting) return;
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = Truncate(text, 250);
            _icon.ShowBalloonTip(5000);
        });
    }

    // ───────────── shutdown ─────────────

    private async Task QuitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _icon.Text = "SmartWiFi: quitting";

        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _awakeTimer.Stop();
        KeepAwake.Apply(false);

        _cts.Cancel();
        try
        {
            if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(3000));
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _engine.ShutdownAsync(shutdown.Token);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }

        _settingsForm?.Close();
        _logForm?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        _probe.Dispose();
        _ntfy.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _menu.Dispose();
            _awakeTimer.Dispose();
            _ui.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    // ───────────── helpers ─────────────

    private void TrySave<T>(string path, T value)
    {
        try { JsonFile.Save(path, value); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.Warn($"couldn't save {Path.GetFileName(path)}: {e.Message}"); }
    }

    private static string Mb(long bytes) =>
        bytes >= 1024L * DataMeter.MB ? $"{bytes / (1024.0 * DataMeter.MB):0.0} GB" : $"{bytes / (double)DataMeter.MB:0} MB";

    private static string Duration(TimeSpan t) =>
        t.TotalMinutes < 1 ? "under a minute"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : $"{(int)t.TotalDays} d {t.Hours} h";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Windows balloon/toast notifications, shown straight away.</summary>
internal sealed class DesktopNotifier : INotifier
{
    private readonly Func<bool> _enabled;
    private readonly Action<string, string> _show;

    public DesktopNotifier(Func<bool> enabled, Action<string, string> show)
    {
        _enabled = enabled;
        _show = show;
    }

    public void Enqueue(string title, string message, string? tags = null, int priority = 3)
    {
        if (_enabled() && priority >= 2) _show(title, message);
    }

    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
}
