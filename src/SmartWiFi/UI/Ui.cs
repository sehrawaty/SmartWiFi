namespace SmartWiFi.UI;

/// <summary>Small helpers so the forms can be laid out in code (no designer files).</summary>
internal static class Ui
{
    public static readonly Font Hint = new(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Regular);

    /// <summary>A two-column "label | control" grid that grows downwards.</summary>
    public static TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(12, 10, 12, 6),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    public static T Row<T>(this TableLayoutPanel t, string label, T control) where T : Control
    {
        control.Dock = control is CheckBox or System.Windows.Forms.Button ? DockStyle.None : DockStyle.Fill;
        control.Margin = new Padding(4, 4, 4, 4);
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 4) };
        t.RowCount++;
        t.Controls.Add(l, 0, t.RowCount - 1);
        t.Controls.Add(control, 1, t.RowCount - 1);
        return control;
    }

    /// <summary>A control spanning both columns.</summary>
    public static T Wide<T>(this TableLayoutPanel t, T control) where T : Control
    {
        control.Margin = new Padding(0, 4, 4, 4);
        if (control is not Label and not CheckBox and not FlowLayoutPanel) control.Dock = DockStyle.Fill;
        t.RowCount++;
        t.Controls.Add(control, 0, t.RowCount - 1);
        t.SetColumnSpan(control, 2);
        return control;
    }

    public static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 2, 4, 8),
    };

    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 12, 4, 2),
    };

    public static NumericUpDown Number(decimal value, decimal min, decimal max, decimal step = 1) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = step,
        Value = Math.Clamp(value, min, max),
        Width = 90,
        Dock = DockStyle.None,
    };

    public static FlowLayoutPanel Buttons(params Control[] buttons)
    {
        var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        f.Controls.AddRange(buttons);
        return f;
    }

    public static Button Button(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        b.Click += onClick;
        return b;
    }

    /// <summary>The OK/Cancel strip along the bottom of a dialog.</summary>
    public static FlowLayoutPanel DialogButtons(Form form, string okText, Action onOk)
    {
        var ok = new Button { Text = okText, AutoSize = true, MinimumSize = new Size(90, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => onOk();
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
            Controls = { cancel, ok },
        };
    }

    public static Panel Scroll(Control content)
    {
        var p = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        p.Controls.Add(content);
        return p;
    }
}
