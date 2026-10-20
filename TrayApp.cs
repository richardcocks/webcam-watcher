using System.Drawing.Drawing2D;

namespace WebcamWatcher;

// No main window: a tray icon showing the camera state, with a menu for the log and exiting
sealed class TrayApp : ApplicationContext
{
    readonly Watcher watcher;
    readonly CancellationTokenSource stop = new();
    readonly Task run;
    readonly SynchronizationContext ui;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem status = new("Starting...") { Enabled = false };
    readonly Dictionary<WatcherState, Icon> icons = new()
    {
        [WatcherState.Off] = DrawIcon(Color.FromArgb(150, 150, 150)),
        [WatcherState.On] = DrawIcon(Color.FromArgb(220, 40, 40)),
        [WatcherState.Error] = DrawIcon(Color.FromArgb(240, 160, 0)),
    };
    LogForm? logForm;

    public TrayApp(Watcher watcher)
    {
        this.watcher = watcher;

        // Creating the first control installs the WinForms SynchronizationContext captured below
        var menu = new ContextMenuStrip();
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show log", null, (_, _) => ShowLog());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        ui = SynchronizationContext.Current ?? throw new InvalidOperationException("No UI synchronization context");

        tray = new NotifyIcon { Icon = icons[WatcherState.Off], Text = "webcam-watcher", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowLog();

        // The watcher raises these on its own thread
        watcher.StatusChanged += () => ui.Post(_ => UpdateStatus(), null);
        watcher.Logged += line => ui.Post(_ => logForm?.Append(line), null);
        run = Task.Run(() => watcher.RunAsync(stop.Token));
    }

    void UpdateStatus()
    {
        var text = watcher.State switch
        {
            WatcherState.On => "Camera on: " + string.Join(", ", watcher.Apps.Select(ShortName)),
            WatcherState.Error => "Can't reach Home Assistant",
            _ => "Camera off",
        };
        status.Text = text;
        tray.Icon = icons[watcher.State];

        // NotifyIcon.Text is limited to 127 characters
        var tip = $"webcam-watcher\n{text}" + (watcher.LastReport is { } at ? $"\nLast report {at:T}" : "");
        tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    void ShowLog()
    {
        if (logForm is null || logForm.IsDisposed) logForm = new LogForm(watcher.LogLines(), icons[WatcherState.Off]);
        logForm.Show();
        if (logForm.WindowState == FormWindowState.Minimized) logForm.WindowState = FormWindowState.Normal;
        logForm.Activate();
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        stop.Cancel();
        // Gives the watcher time to report the camera off if it was on
        try { run.Wait(TimeSpan.FromSeconds(4)); }
        catch (AggregateException) { }

        tray.Dispose();
        logForm?.Dispose();
        base.ExitThreadCore();
    }

    // "C:\...\ms-teams.exe" -> "ms-teams", "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "Microsoft.WindowsCamera"
    static string ShortName(string app) =>
        app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(app) : app.Split('_')[0];

    // A small camera whose lens colour shows the state. Drawn rather than shipped as .ico files;
    // the three icons live for the whole process, so their handles aren't freed.
    static Icon DrawIcon(Color lens)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var body = new SolidBrush(Color.FromArgb(240, 240, 240));
            using var outline = new Pen(Color.FromArgb(50, 50, 50), 2.5f);
            using var lensBrush = new SolidBrush(lens);

            g.FillRoundedRectangle(body, new Rectangle(2, 8, 21, 17), new Size(7, 7));
            g.DrawRoundedRectangle(outline, new Rectangle(2, 8, 21, 17), new Size(7, 7));
            Point[] viewfinder = [new(23, 14), new(30, 9), new(30, 24), new(23, 19)];
            g.FillPolygon(body, viewfinder);
            g.DrawPolygon(outline, viewfinder);
            g.FillEllipse(lensBrush, 7, 11, 11, 11);
            g.DrawEllipse(outline, 7, 11, 11, 11);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

sealed class LogForm : Form
{
    readonly TextBox text = new()
    {
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Dock = DockStyle.Fill,
        BackColor = SystemColors.Window,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    public LogForm(string[] lines, Icon icon)
    {
        Text = "webcam-watcher log";
        Icon = icon;
        ClientSize = new Size(820, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(text);
        text.Lines = lines;
    }

    public void Append(string line)
    {
        if (IsDisposed) return;
        text.AppendText((text.TextLength > 0 ? Environment.NewLine : "") + line);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        text.SelectionStart = text.TextLength;
        text.ScrollToCaret();
    }
}
