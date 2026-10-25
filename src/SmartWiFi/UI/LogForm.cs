using System.Diagnostics;
using SmartWiFi.Core.Logging;

namespace SmartWiFi.UI;

/// <summary>Live view of the activity log.</summary>
internal sealed class LogForm : Form
{
    private readonly Log _log;
    private readonly TextBox _text = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9.5f),
        BackColor = SystemColors.Window,
    };

    public LogForm(Log log)
    {
        _log = log;
        Text = "SmartWiFi activity";
        Icon = TrayIcons.AppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 460);

        _text.Text = string.Join(Environment.NewLine, log.Recent.Select(e => e.ToString()));

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6), FlowDirection = FlowDirection.RightToLeft };
        bar.Controls.Add(Ui.Button("Close", (_, _) => Close()));
        bar.Controls.Add(Ui.Button("Open log folder", (_, _) => OpenFolder()));
        bar.Controls.Add(Ui.Button("Copy", (_, _) => { if (_text.TextLength > 0) Clipboard.SetText(_text.Text); }));

        Controls.Add(_text);
        Controls.Add(bar);

        _log.Logged += OnLogged;
        FormClosed += (_, _) => _log.Logged -= OnLogged;
        Shown += (_, _) => ScrollToEnd();
    }

    private void OnLogged(LogEntry e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _text.AppendText((_text.TextLength > 0 ? Environment.NewLine : "") + e);
            ScrollToEnd();
        });
    }

    private void ScrollToEnd()
    {
        _text.SelectionStart = _text.TextLength;
        _text.ScrollToCaret();
    }

    private void OpenFolder()
    {
        if (_log.CurrentFile is { } f && Path.GetDirectoryName(f) is { } dir)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
