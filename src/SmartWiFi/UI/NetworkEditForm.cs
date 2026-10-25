using SmartWiFi.Core;
using SmartWiFi.Core.Models;
using SmartWiFi.Core.Portal;

namespace SmartWiFi.UI;

/// <summary>Add or edit one network: SSID, password, hotspot flag and captive-portal login.</summary>
internal sealed class NetworkEditForm : Form
{
    private readonly NetworkEntry _entry;
    private readonly ISecretStore _secrets;

    private readonly ComboBox _ssid = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _showPassword = new() { Text = "Show", AutoSize = true };
    private readonly CheckBox _clearPassword = new() { Text = "Forget the saved password", AutoSize = true };
    private readonly CheckBox _hotspot = new() { Text = "This is my phone hotspot (count its data, apply the daily cap)", AutoSize = true };

    private readonly CheckBox _portalEnabled = new() { Text = "Log in to a captive portal on this network", AutoSize = true };
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _url = new();
    private readonly ComboBox _method = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly TextBox _user = new();
    private readonly TextBox _portalPassword = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _ignoreCert = new() { Text = "Accept the portal's self-signed certificate", AutoSize = true };
    private readonly DataGridView _fields = new()
    {
        Height = 150,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly Control[] _portalControls;
    private bool _loadingPreset;
    private PortalPreset _currentPreset;

    public NetworkEditForm(NetworkEntry entry, IEnumerable<string> suggestions, ISecretStore secrets)
    {
        _entry = entry;
        _secrets = secrets;

        Text = string.IsNullOrEmpty(entry.Ssid) ? "Add network" : $"Edit {entry.Ssid}";
        Icon = TrayIcons.AppIcon();
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 680);

        _ssid.Items.AddRange(suggestions.Distinct().OrderBy(s => s).Cast<object>().ToArray());
        _ssid.Text = entry.Ssid;
        _password.PlaceholderText = entry.PasswordSecret is null
            ? "Leave empty to use the password Windows already has"
            : "Saved. Type a new one to change it";
        _clearPassword.Visible = entry.PasswordSecret is not null;
        _showPassword.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_showPassword.Checked;
        _hotspot.Checked = entry.IsHotspot;

        var p = entry.Portal;
        _portalEnabled.Checked = p.Enabled;
        _preset.Items.AddRange(new object[] { "Custom form", "FortiGate (fgtauth)", "Pronto Networks" });
        _preset.SelectedIndex = (int)p.Preset;
        _currentPreset = p.Preset;
        _url.Text = p.LoginUrl;
        _method.Items.AddRange(new object[] { "POST", "GET" });
        _method.SelectedItem = p.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "GET" : "POST";
        _user.Text = p.Username;
        _portalPassword.PlaceholderText = p.PasswordSecret is null ? "" : "Saved. Type a new one to change it";
        _ignoreCert.Checked = p.IgnoreCertificateErrors;
        _fields.Columns.Add("name", "Field");
        _fields.Columns.Add("value", "Value");
        foreach (var f in p.Fields.Count > 0 ? p.Fields : PortalLogin.Defaults(p.Preset).Fields)
            _fields.Rows.Add(f.Name, f.Value);
        if (string.IsNullOrEmpty(_url.Text)) _url.Text = PortalLogin.Defaults(p.Preset).LoginUrl;

        var grid = Ui.Grid();
        grid.Wide(Ui.Heading("Network"));
        grid.Row("Name (SSID)", _ssid);
        var pwRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
        pwRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pwRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _password.Dock = DockStyle.Fill;
        pwRow.Controls.Add(_password, 0, 0);
        pwRow.Controls.Add(_showPassword, 1, 0);
        grid.Row("Wi-Fi password", pwRow);
        grid.Row("", _clearPassword);
        grid.Wide(Ui.Note("The password is encrypted for your Windows account (DPAPI) and never written in clear."));
        grid.Wide(_hotspot);

        grid.Wide(Ui.Heading("Captive portal"));
        grid.Wide(_portalEnabled);
        grid.Row("Portal type", _preset);
        grid.Row("Login URL", _url);
        grid.Row("Method", _method);
        grid.Row("Username", _user);
        grid.Row("Portal password", _portalPassword);
        grid.Row("Form fields", _fields);
        grid.Wide(Ui.Note(
            "Values can use {username}, {password}, {magic} (FortiGate token), {redirect}, {portalUrl} and {portalBase}.\n" +
            "To copy a portal that isn't listed: open its login page, press F12 → Network, log in, and copy the URL and form fields of the request that was sent."));
        grid.Wide(_ignoreCert);

        _portalControls = new Control[] { _preset, _url, _method, _user, _portalPassword, _fields, _ignoreCert };
        _portalEnabled.CheckedChanged += (_, _) => UpdateEnabled();
        _preset.SelectedIndexChanged += (_, _) => LoadPreset();
        UpdateEnabled();

        Controls.Add(Ui.Scroll(grid));
        Controls.Add(Ui.DialogButtons(this, "Save", Save));
    }

    private void UpdateEnabled()
    {
        foreach (var c in _portalControls) c.Enabled = _portalEnabled.Checked;
    }

    private void LoadPreset()
    {
        if (_loadingPreset || !Visible) return;
        var preset = (PortalPreset)_preset.SelectedIndex;
        var d = PortalLogin.Defaults(preset);
        var prev = PortalLogin.Defaults(_currentPreset);
        var rows = _fields.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
            .Select(r => (Convert.ToString(r.Cells[0].Value) ?? "", Convert.ToString(r.Cells[1].Value) ?? "")).ToList();
        var untouched = _url.Text.Trim() == prev.LoginUrl &&
                        rows.SequenceEqual(prev.Fields.Select(f => (f.Name, f.Value)));
        _currentPreset = preset;
        if (!untouched && rows.Count > 0 && MessageBox.Show(this, "Replace the URL and form fields with this portal type's defaults?", "SmartWiFi",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _loadingPreset = true;
        _url.Text = d.LoginUrl;
        _method.SelectedItem = d.Method;
        _fields.Rows.Clear();
        foreach (var f in d.Fields) _fields.Rows.Add(f.Name, f.Value);
        _loadingPreset = false;
    }

    private void Save()
    {
        var ssid = _ssid.Text.Trim();
        if (ssid.Length == 0)
        {
            MessageBox.Show(this, "Enter the network name.", "SmartWiFi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_portalEnabled.Checked && string.IsNullOrWhiteSpace(_url.Text))
        {
            MessageBox.Show(this, "Enter the portal's login URL, or pick a portal type.", "SmartWiFi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _entry.Ssid = ssid;
        if (_clearPassword.Checked) _entry.PasswordSecret = null;
        if (_password.Text.Length > 0) _entry.PasswordSecret = _secrets.Protect(_password.Text);
        _entry.IsHotspot = _hotspot.Checked;

        var p = _entry.Portal;
        p.Enabled = _portalEnabled.Checked;
        p.Preset = (PortalPreset)Math.Max(0, _preset.SelectedIndex);
        p.LoginUrl = _url.Text.Trim();
        p.Method = (string?)_method.SelectedItem ?? "POST";
        p.Username = _user.Text.Trim();
        if (_portalPassword.Text.Length > 0) p.PasswordSecret = _secrets.Protect(_portalPassword.Text);
        p.IgnoreCertificateErrors = _ignoreCert.Checked;
        p.Fields = _fields.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow)
            .Select(r => new FormField(Convert.ToString(r.Cells[0].Value)?.Trim() ?? "", Convert.ToString(r.Cells[1].Value) ?? ""))
            .Where(f => f.Name.Length > 0)
            .ToList();

        DialogResult = DialogResult.OK;
        Close();
    }
}
