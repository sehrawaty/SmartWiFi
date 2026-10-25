using System.Security.Cryptography;
using System.Text.Json;
using SmartWiFi.Core;
using SmartWiFi.Core.Models;
using SmartWiFi.Core.Notify;
using SmartWiFi.Core.Storage;

namespace SmartWiFi.UI;

/// <summary>One window, four tabs: Networks, Notifications, Tailscale, General.</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _s;
    private readonly ISecretStore _secrets;
    private readonly Func<IEnumerable<string>> _suggestions;
    private readonly NtfyNotifier _ntfy;

    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        Dock = DockStyle.Fill,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };

    // Notifications
    private readonly CheckBox _nEnabled = new() { Text = "Send notifications to my phone with ntfy", AutoSize = true };
    private readonly TextBox _nServer = new();
    private readonly TextBox _nTopic = new();
    private readonly TextBox _nToken = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _nSwitch = new() { Text = "Network switches", AutoSize = true };
    private readonly CheckBox _nPortal = new() { Text = "Portal login keeps failing", AutoSize = true };
    private readonly CheckBox _nOnline = new() { Text = "Back online after an outage", AutoSize = true };
    private readonly CheckBox _nCap = new() { Text = "Hotspot data cap reached", AutoSize = true };
    private readonly CheckBox _nDesktop = new() { Text = "Also show Windows notifications on this laptop", AutoSize = true };

    // Tailscale
    private readonly CheckBox _tDown = new() { Text = "Turn Tailscale off while on the phone hotspot, back on afterwards", AutoSize = true };
    private readonly TextBox _tPath = new();
    private readonly TextBox _tUpArgs = new();

    // General
    private readonly CheckBox _gStart = new() { Text = "Start SmartWiFi when I sign in to Windows", AutoSize = true };
    private readonly CheckBox _gAwake = new() { Text = "Keep the laptop from sleeping while plugged in", AutoSize = true };
    private readonly CheckBox _gWatchdog = new() { Text = "Reconnect Wi-Fi when nothing has had internet for", AutoSize = true };
    private readonly NumericUpDown _gWatchdogMin;
    private readonly NumericUpDown _gInterval;
    private readonly NumericUpDown _gSwitchUp;
    private readonly NumericUpDown _gOffline;
    private readonly NumericUpDown _gSkip;
    private readonly NumericUpDown _gHold;
    private readonly NumericUpDown _gCap;
    private readonly TextBox _gProbeUrl = new();
    private readonly TextBox _gProbeExpected = new();

    public AppSettings Result => _s;

    public SettingsForm(AppSettings current, ISecretStore secrets, Func<IEnumerable<string>> suggestions, NtfyNotifier ntfy, string? tailscalePath)
    {
        _s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current, JsonFile.Options), JsonFile.Options)!;
        _secrets = secrets;
        _suggestions = suggestions;
        _ntfy = ntfy;

        Text = "SmartWiFi settings";
        Icon = TrayIcons.AppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 600);
        MinimumSize = new Size(560, 480);

        var g = _s.General;
        _gWatchdogMin = Ui.Number(g.WatchdogMinutes, 1, 60);
        _gInterval = Ui.Number(g.CheckIntervalSeconds, 5, 300);
        _gSwitchUp = Ui.Number(g.SwitchUpAfterScans, 1, 10);
        _gOffline = Ui.Number(g.OfflineSkipSeconds, 10, 600, 5);
        _gSkip = Ui.Number(g.SkipMinutes, 1, 120);
        _gHold = Ui.Number(g.ManualHoldMinutes, 1, 240);
        _gCap = Ui.Number(g.HotspotDailyCapMB, 0, 1_000_000, 100);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        tabs.TabPages.Add(NetworksTab());
        tabs.TabPages.Add(NotificationsTab());
        tabs.TabPages.Add(TailscaleTab(tailscalePath));
        tabs.TabPages.Add(GeneralTab());

        Controls.Add(tabs);
        Controls.Add(Ui.DialogButtons(this, "Save", Save));
    }

    // ───────────── Networks ─────────────

    private TabPage NetworksTab()
    {
        _list.Columns.Add("#", 36);
        _list.Columns.Add("Network", 220);
        _list.Columns.Add("Type", 110);
        _list.Columns.Add("Password", 130);
        _list.Columns.Add("Portal login", 140);
        _list.DoubleClick += (_, _) => Edit();
        FillList();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Padding = new Padding(8, 0, 0, 0),
        };
        foreach (var b in new[]
                 {
                     Wide(Ui.Button("Add…", (_, _) => Add())),
                     Wide(Ui.Button("Edit…", (_, _) => Edit())),
                     Wide(Ui.Button("Remove", (_, _) => Remove())),
                     Wide(Ui.Button("Move up", (_, _) => MoveSelected(-1))),
                     Wide(Ui.Button("Move down", (_, _) => MoveSelected(+1))),
                 })
            buttons.Controls.Add(b);

        var note = Ui.Note(
            "Top of the list = most preferred. SmartWiFi joins the first network that is in range and working, " +
            "switches up when a higher one is seen in two scans in a row, and falls back down the list when the current one loses internet.");
        note.Dock = DockStyle.Top;
        note.Padding = new Padding(0, 0, 0, 8);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        body.Controls.Add(_list);
        body.Controls.Add(buttons);
        body.Controls.Add(note);
        return new TabPage("Networks") { Controls = { body } };

        static Button Wide(Button b) { b.MinimumSize = new Size(110, 0); return b; }
    }

    private void FillList()
    {
        var selected = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        _list.BeginUpdate();
        _list.Items.Clear();
        for (var i = 0; i < _s.Networks.Count; i++)
        {
            var n = _s.Networks[i];
            _list.Items.Add(new ListViewItem(new[]
            {
                (i + 1).ToString(),
                n.Ssid,
                n.IsHotspot ? "Phone hotspot" : "Wi-Fi",
                n.PasswordSecret is null ? "Windows profile" : "Saved",
                n.Portal.Enabled ? n.Portal.Preset switch
                {
                    PortalPreset.FortiGate => "FortiGate",
                    PortalPreset.Pronto => "Pronto",
                    _ => "Custom form",
                } : "—",
            }));
        }
        _list.EndUpdate();
        if (selected >= 0 && selected < _list.Items.Count) _list.Items[selected].Selected = true;
    }

    private int Selected => _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;

    private void Add()
    {
        var entry = new NetworkEntry { IsHotspot = _s.Networks.Count == 0 };
        using var f = new NetworkEditForm(entry, _suggestions(), _secrets);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        if (_s.Networks.Any(n => n.Ssid == entry.Ssid))
        {
            MessageBox.Show(this, $"{entry.Ssid} is already in the list.", "SmartWiFi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _s.Networks.Add(entry);
        FillList();
        _list.Items[^1].Selected = true;
    }

    private void Edit()
    {
        if (Selected < 0) return;
        var copy = JsonSerializer.Deserialize<NetworkEntry>(JsonSerializer.Serialize(_s.Networks[Selected], JsonFile.Options), JsonFile.Options)!;
        using var f = new NetworkEditForm(copy, _suggestions(), _secrets);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        _s.Networks[Selected] = copy;
        FillList();
    }

    private void Remove()
    {
        if (Selected < 0) return;
        var n = _s.Networks[Selected];
        if (MessageBox.Show(this, $"Remove {n.Ssid} from SmartWiFi? Windows keeps its own saved profile.", "SmartWiFi",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _s.Networks.RemoveAt(Selected);
        FillList();
    }

    private void MoveSelected(int by)
    {
        var i = Selected;
        var j = i + by;
        if (i < 0 || j < 0 || j >= _s.Networks.Count) return;
        (_s.Networks[i], _s.Networks[j]) = (_s.Networks[j], _s.Networks[i]);
        FillList();
        _list.SelectedIndices.Clear();
        _list.Items[j].Selected = true;
        _list.Focus();
    }

    // ───────────── Notifications ─────────────

    private TabPage NotificationsTab()
    {
        var n = _s.Notifications;
        _nEnabled.Checked = n.Enabled;
        _nServer.Text = n.Server;
        _nTopic.Text = n.Topic;
        _nToken.PlaceholderText = n.TokenSecret is null ? "Only for protected topics" : "Saved. Type a new one to change it";
        _nSwitch.Checked = n.OnSwitch;
        _nPortal.Checked = n.OnPortalFailure;
        _nOnline.Checked = n.OnBackOnline;
        _nCap.Checked = n.OnDataCap;
        _nDesktop.Checked = n.Desktop;

        var grid = Ui.Grid();
        grid.Wide(Ui.Note(
            "Install the free ntfy app (Android / iOS), tap +, and subscribe to the same topic as below. " +
            "Anyone who knows a topic on ntfy.sh can read it, so use a long random name."));
        grid.Wide(_nEnabled);
        grid.Row("Server", _nServer);
        var topicRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
        topicRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topicRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _nTopic.Dock = DockStyle.Fill;
        topicRow.Controls.Add(_nTopic, 0, 0);
        topicRow.Controls.Add(Ui.Button("Random", (_, _) => _nTopic.Text = "smartwifi-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()), 1, 0);
        grid.Row("Topic", topicRow);
        grid.Row("Access token", _nToken);
        grid.Wide(Ui.Heading("Tell me when"));
        grid.Wide(_nSwitch);
        grid.Wide(_nPortal);
        grid.Wide(_nOnline);
        grid.Wide(_nCap);
        grid.Wide(Ui.Note("Messages raised while the laptop is offline are kept and delivered once it's back, with the time they happened."));
        grid.Wide(Ui.Buttons(Ui.Button("Send a test notification", async (b, _) => await SendTest((Button)b!))));
        grid.Wide(Ui.Heading("On this laptop"));
        grid.Wide(_nDesktop);
        return new TabPage("Notifications") { Controls = { Ui.Scroll(grid) } };
    }

    private async Task SendTest(Button button)
    {
        var settings = ReadNotifications();
        if (string.IsNullOrWhiteSpace(settings.Topic))
        {
            MessageBox.Show(this, "Enter a topic first.", "SmartWiFi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        button.Enabled = false;
        var ok = await _ntfy.SendTestAsync(settings, CancellationToken.None);
        button.Enabled = true;
        MessageBox.Show(this, ok ? "Sent. Check your phone." : "Couldn't reach the ntfy server. Check the server address and your internet.",
            "SmartWiFi", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private NotificationSettings ReadNotifications()
    {
        var n = _s.Notifications;
        var token = _nToken.Text.Length > 0 ? _secrets.Protect(_nToken.Text) : n.TokenSecret;
        return new NotificationSettings
        {
            Enabled = _nEnabled.Checked,
            Server = string.IsNullOrWhiteSpace(_nServer.Text) ? "https://ntfy.sh" : _nServer.Text.Trim(),
            Topic = _nTopic.Text.Trim(),
            TokenSecret = token,
            OnSwitch = _nSwitch.Checked,
            OnPortalFailure = _nPortal.Checked,
            OnBackOnline = _nOnline.Checked,
            OnDataCap = _nCap.Checked,
            Desktop = _nDesktop.Checked,
        };
    }

    // ───────────── Tailscale ─────────────

    private TabPage TailscaleTab(string? detectedPath)
    {
        var t = _s.Tailscale;
        _tDown.Checked = t.DownOnHotspot;
        _tPath.Text = t.CliPath;
        _tPath.PlaceholderText = detectedPath ?? "Not found. Install Tailscale or enter the path to tailscale.exe";
        _tUpArgs.Text = t.UpArguments;

        var grid = Ui.Grid();
        grid.Wide(Ui.Note(detectedPath is null
            ? "Tailscale wasn't found on this laptop. These settings do nothing until it's installed."
            : $"Found Tailscale at {detectedPath}."));
        grid.Wide(_tDown);
        grid.Wide(Ui.Note(
            "Leave this off if you reach the laptop over Tailscale while it's on the hotspot. " +
            "SmartWiFi only turns Tailscale back on if it was the one that turned it off, and it won't fight a change you make from the tray menu."));
        grid.Row("tailscale.exe", _tPath);
        grid.Row("Command to reconnect", _tUpArgs);
        grid.Wide(Ui.Note("If you normally run \"tailscale up\" with flags (for example --accept-routes), put the same flags here."));
        return new TabPage("Tailscale") { Controls = { Ui.Scroll(grid) } };
    }

    // ───────────── General ─────────────

    private TabPage GeneralTab()
    {
        var g = _s.General;
        _gStart.Checked = g.StartWithWindows;
        _gAwake.Checked = g.KeepAwakeOnAC;
        _gWatchdog.Checked = g.Watchdog;
        _gProbeUrl.Text = g.ProbeUrl;
        _gProbeExpected.Text = g.ProbeExpected;

        var grid = Ui.Grid();
        grid.Wide(_gStart);
        grid.Wide(_gAwake);
        grid.Wide(Ui.Note("Closing the lid can still sleep the laptop: set \"When I close the lid\" to \"Do nothing\" for \"Plugged in\" in Control Panel → Power Options."));
        var wd = Ui.Buttons(_gWatchdog, _gWatchdogMin, new Label { Text = "minutes", AutoSize = true, Margin = new Padding(4, 7, 0, 0) });
        grid.Wide(wd);

        grid.Wide(Ui.Heading("Hotspot data"));
        grid.Row("Daily cap (MB, 0 = none)", _gCap);
        grid.Wide(Ui.Note("When the cap is reached SmartWiFi moves to the next network until midnight. \"Allow more\" in the tray menu raises it for the day."));

        grid.Wide(Ui.Heading("Switching"));
        grid.Row("Check every (seconds)", _gInterval);
        grid.Row("Switch up after N scans", _gSwitchUp);
        grid.Row("Skip a network after (seconds offline)", _gOffline);
        grid.Row("…for (minutes)", _gSkip);
        grid.Row("Keep a menu pick for (minutes)", _gHold);

        grid.Wide(Ui.Heading("Internet check"));
        grid.Row("Probe URL", _gProbeUrl);
        grid.Row("Expected reply", _gProbeExpected);
        grid.Wide(Ui.Note("The default is the page Windows itself uses. It must be plain http:// so portals can intercept it."));
        return new TabPage("General") { Controls = { Ui.Scroll(grid) } };
    }

    // ───────────── Save ─────────────

    private void Save()
    {
        if (_s.Networks.Count == 0 &&
            MessageBox.Show(this, "No networks are set up, so SmartWiFi won't switch anything. Save anyway?", "SmartWiFi",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var notifications = ReadNotifications();
        if (notifications.Enabled && string.IsNullOrWhiteSpace(notifications.Topic))
        {
            MessageBox.Show(this, "Enter an ntfy topic, or turn phone notifications off.", "SmartWiFi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _s.Notifications = notifications;

        _s.Tailscale.DownOnHotspot = _tDown.Checked;
        _s.Tailscale.CliPath = _tPath.Text.Trim();
        _s.Tailscale.UpArguments = string.IsNullOrWhiteSpace(_tUpArgs.Text) ? "up" : _tUpArgs.Text.Trim();

        var g = _s.General;
        g.StartWithWindows = _gStart.Checked;
        g.KeepAwakeOnAC = _gAwake.Checked;
        g.Watchdog = _gWatchdog.Checked;
        g.WatchdogMinutes = (int)_gWatchdogMin.Value;
        g.CheckIntervalSeconds = (int)_gInterval.Value;
        g.SwitchUpAfterScans = (int)_gSwitchUp.Value;
        g.OfflineSkipSeconds = (int)_gOffline.Value;
        g.SkipMinutes = (int)_gSkip.Value;
        g.ManualHoldMinutes = (int)_gHold.Value;
        g.HotspotDailyCapMB = (long)_gCap.Value;
        g.ProbeUrl = string.IsNullOrWhiteSpace(_gProbeUrl.Text) ? new GeneralSettings().ProbeUrl : _gProbeUrl.Text.Trim();
        g.ProbeExpected = string.IsNullOrWhiteSpace(_gProbeExpected.Text) ? new GeneralSettings().ProbeExpected : _gProbeExpected.Text.Trim();

        DialogResult = DialogResult.OK;
        Close();
    }
}
