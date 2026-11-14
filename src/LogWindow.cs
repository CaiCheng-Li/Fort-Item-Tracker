namespace FortItemTracker;

/// <summary>Shows what the tracker reports: sessions, finds, test results and problems.</summary>
sealed class LogWindow : Form
{
    readonly TextBox box;

    public LogWindow(Icon icon)
    {
        Text = "Fort Item Tracker - Activity";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(LogicalToDeviceUnits(860), LogicalToDeviceUnits(460));
        box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10f),
            BackColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
        };
        Controls.Add(box);
        box.Text = string.Join(Environment.NewLine, Activity.Snapshot()) + Environment.NewLine;
        Activity.Line += OnLine;
        Shown += (_, _) => ScrollToEnd();
    }

    void OnLine(string line)
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() =>
        {
            box.AppendText(line + Environment.NewLine);
        });
    }

    void ScrollToEnd()
    {
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Activity.Line -= OnLine;
        base.OnFormClosed(e);
    }
}
