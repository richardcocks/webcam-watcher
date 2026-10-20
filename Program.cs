namespace WebcamWatcher;

static class Program
{
    // Usage: webcam-watcher <webhook-url> [machine-name] [--brightness 1-100]
    // or set HA_WEBHOOK_URL / HA_MACHINE / HA_BRIGHTNESS. Machine name defaults to the
    // computer name, brightness (key light %, used when this machine's camera comes on) to 70.
    const string Usage = "Usage: webcam-watcher <webhook-url> [machine-name] [--brightness 1-100]\n\nor set HA_WEBHOOK_URL / HA_MACHINE / HA_BRIGHTNESS.";

    [STAThread]
    static int Main(string[] args)
    {
        var positional = new List<string>();
        string? brightnessArg = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--brightness" or "-b") brightnessArg = i + 1 < args.Length ? args[++i] : "";
            else positional.Add(args[i]);
        }

        var webhookUrl = positional.ElementAtOrDefault(0) ?? Environment.GetEnvironmentVariable("HA_WEBHOOK_URL");
        var machine = positional.ElementAtOrDefault(1) ?? Environment.GetEnvironmentVariable("HA_MACHINE") ?? Environment.MachineName;
        brightnessArg ??= Environment.GetEnvironmentVariable("HA_BRIGHTNESS");
        var brightness = 70;

        if (webhookUrl is null || (brightnessArg is not null && (!int.TryParse(brightnessArg, out brightness) || brightness is < 1 or > 100)))
        {
            MessageBox.Show(Usage, "webcam-watcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        // One per login session, so starting it by hand as well as at login doesn't run two
        using var single = new Mutex(true, @"Local\webcam-watcher", out var first);
        if (!first)
        {
            MessageBox.Show("webcam-watcher is already running - look for its icon in the system tray.", "webcam-watcher",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayApp(new Watcher(webhookUrl, machine, brightness)));
        return 0;
    }
}
