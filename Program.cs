using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

// Usage: webcam-watcher <webhook-url> [machine-name] [--brightness 1-100]
// or set HA_WEBHOOK_URL / HA_MACHINE / HA_BRIGHTNESS. Machine name defaults to the
// computer name, brightness (key light %, used when this machine's camera comes on) to 70.
const string Usage = "Usage: webcam-watcher <webhook-url> [machine-name] [--brightness 1-100]  (or set HA_WEBHOOK_URL / HA_MACHINE / HA_BRIGHTNESS)";

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
    Console.Error.WriteLine(Usage);
    return 1;
}

const string WebcamKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
const int RegNotifyChangeName = 0x1, RegNotifyChangeLastSet = 0x4, RegNotifyThreadAgnostic = 0x10000000;
var heartbeat = TimeSpan.FromMinutes(1);
var retryDelay = TimeSpan.FromSeconds(10);
var fallbackPoll = TimeSpan.FromSeconds(5);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
using var changed = new AutoResetEvent(false);
RegistryKey? watchedKey = null;
var armed = false;

bool? lastSent = null;
var lastSentAt = DateTime.MinValue;

Console.WriteLine($"Reporting webcam use for '{machine}', key light {brightness}%");

while (true)
{
    // Ask Windows to signal when anything under the key changes. Armed before reading so a
    // change between the read and the wait isn't missed; one-shot, so re-armed after each signal.
    // Thread-agnostic because the awaits below can move this loop to another thread.
    if (!armed)
    {
        watchedKey ??= Registry.CurrentUser.OpenSubKey(WebcamKey);
        armed = watchedKey is not null && RegNotifyChangeKeyValue(watchedKey.Handle, true,
            RegNotifyChangeName | RegNotifyChangeLastSet | RegNotifyThreadAgnostic, changed.SafeWaitHandle, true) == 0;
    }

    var apps = ActiveApps();
    var active = apps.Count > 0;

    // Send on change, and resend every minute as a heartbeat - HA treats a machine
    // it hasn't heard from in a few minutes as inactive
    if (active != lastSent || DateTime.UtcNow - lastSentAt >= heartbeat)
    {
        try
        {
            var response = await http.PostAsJsonAsync(webhookUrl, new { machine, active, apps, brightness });
            response.EnsureSuccessStatusCode();
            lastSent = active;
            lastSentAt = DateTime.UtcNow;
            Console.WriteLine($"{DateTime.Now:T} webcam {(active ? "ON" : "off")} {string.Join(", ", apps)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.Now:T} send failed: {ex.Message}");
        }
    }

    // Wake on a camera change or when the next heartbeat is due. Retry sooner after a failed send,
    // and poll if the key can't be watched (it only exists once an app has used the camera)
    var wait = active != lastSent ? retryDelay
        : !armed ? fallbackPoll
        : lastSentAt + heartbeat - DateTime.UtcNow;
    if (changed.WaitOne(wait > TimeSpan.Zero ? wait : TimeSpan.Zero)) armed = false;
}

[DllImport("advapi32.dll")]
static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool bWatchSubtree, int dwNotifyFilter, SafeWaitHandle hEvent, bool fAsynchronous);

static List<string> ActiveApps()
{
    var active = new List<string>();
    using var root = Registry.CurrentUser.OpenSubKey(WebcamKey);
    if (root is null) return active;

    foreach (var name in root.GetSubKeyNames())
    {
        if (name != "NonPackaged")
        {
            if (IsInUse(root, name)) active.Add(name);
            continue;
        }

        using var nonPackaged = root.OpenSubKey(name);
        if (nonPackaged is null) continue;
        foreach (var exe in nonPackaged.GetSubKeyNames())
            if (IsInUse(nonPackaged, exe)) active.Add(exe.Replace('#', '\\'));
    }
    return active;
}

// Windows sets LastUsedTimeStop to 0 while an app has the camera open
static bool IsInUse(RegistryKey parent, string name)
{
    using var key = parent.OpenSubKey(name);
    return key?.GetValue("LastUsedTimeStart") is long start && start != 0
        && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;
}
