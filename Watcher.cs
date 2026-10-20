using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WebcamWatcher;

enum WatcherState { Off, On, Error }

// Watches Windows' per-app webcam usage records and reports to the Home Assistant webhook on every
// change, plus a heartbeat each minute - HA treats a machine it hasn't heard from in a few minutes as inactive
sealed class Watcher(string webhookUrl, string machine, int brightness)
{
    const string WebcamKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    const int RegNotifyChangeName = 0x1, RegNotifyChangeLastSet = 0x4, RegNotifyThreadAgnostic = 0x10000000;
    const int MaxLogLines = 1000;
    static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(1);
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    static readonly TimeSpan FallbackPoll = TimeSpan.FromSeconds(5);

    readonly Queue<string> log = new();

    // Both raised on the watcher's thread
    public event Action? StatusChanged;
    public event Action<string>? Logged;

    public WatcherState State { get; private set; }
    public IReadOnlyList<string> Apps { get; private set; } = [];
    public DateTime? LastReport { get; private set; }

    public string[] LogLines()
    {
        lock (log) return [.. log];
    }

    public async Task RunAsync(CancellationToken stop)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var changed = new AutoResetEvent(false);
        RegistryKey? watchedKey = null;
        var armed = false;
        bool? lastSent = null;
        var lastSentAt = DateTime.MinValue;
        string? lastError = null;

        Log($"Reporting webcam use for '{machine}', key light {brightness}%");
        try
        {
            while (!stop.IsCancellationRequested)
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

                if (active != lastSent || DateTime.UtcNow - lastSentAt >= Heartbeat)
                {
                    try
                    {
                        await Send(http, active, apps, stop);
                        // Heartbeats aren't logged, only changes and recovery from failures
                        if (lastError is not null) Log("Reaching Home Assistant again");
                        if (active != lastSent) Log(active ? $"Camera on: {string.Join(", ", apps)}" : "Camera off");
                        lastSent = active;
                        lastSentAt = DateTime.UtcNow;
                        lastError = null;
                        LastReport = DateTime.Now;
                        State = active ? WatcherState.On : WatcherState.Off;
                        Apps = apps;
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested)
                    {
                        // Logged once per distinct error rather than on every retry
                        if (ex.Message != lastError) Log($"Send failed, retrying every {RetryDelay.TotalSeconds:0}s: {ex.Message}");
                        lastError = ex.Message;
                        State = WatcherState.Error;
                    }
                    StatusChanged?.Invoke();
                }

                // Wake on a camera change or when the next heartbeat is due. Retry sooner after a failed send,
                // and poll if the key can't be watched (it only exists once an app has used the camera)
                var wait = active != lastSent ? RetryDelay
                    : !armed ? FallbackPoll
                    : lastSentAt + Heartbeat - DateTime.UtcNow;
                if (WaitHandle.WaitAny([changed, stop.WaitHandle], wait > TimeSpan.Zero ? wait : TimeSpan.Zero) == 0) armed = false;
            }
        }
        finally
        {
            watchedKey?.Dispose();

            // Exiting mid-call: report the camera off now rather than leaving the light on until HA expires this machine
            if (lastSent == true)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await Send(http, false, [], timeout.Token);
                    Log("Exiting: reported camera off");
                }
                catch (Exception ex)
                {
                    Log($"Exiting: couldn't report camera off: {ex.Message}");
                }
            }
        }
    }

    async Task Send(HttpClient http, bool active, List<string> apps, CancellationToken cancel)
    {
        var response = await http.PostAsJsonAsync(webhookUrl, new { machine, active, apps, brightness }, cancel);
        response.EnsureSuccessStatusCode();
    }

    void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        lock (log)
        {
            log.Enqueue(line);
            while (log.Count > MaxLogLines) log.Dequeue();
        }
        Logged?.Invoke(line);
    }

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

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool bWatchSubtree, int dwNotifyFilter, SafeWaitHandle hEvent, bool fAsynchronous);
}
