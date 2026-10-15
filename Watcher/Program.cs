using System.Diagnostics;

// Starts "AccOverlay.exe --hidden" while ACC is running, stops it when ACC exits.
// Launched at login via HKCU\...\Run (see AccOverlay --install-autostart).

string? overlayExe = null, workDir = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--overlay") overlayExe = args[++i];
    else if (args[i] == "--workdir") workDir = args[++i];
}
overlayExe ??= Path.Combine(AppContext.BaseDirectory, "AccOverlay.exe");
workDir ??= Path.GetDirectoryName(overlayExe) ?? AppContext.BaseDirectory;

using var single = new Mutex(true, @"Local\AccOverlayWatcher", out bool first);
if (!first) return;
using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AccOverlayWatcher.Stop");

Process? child = null;
DateTime retryAt = DateTime.MinValue;
bool gaveUp = false; // overlay refused to start (already running manually) for this ACC run

while (!stop.WaitOne(2000))
{
    bool acc = IsAccRunning();

    if (child is { HasExited: true })
    {
        gaveUp = child.ExitCode == 3;
        child.Dispose();
        child = null;
        retryAt = DateTime.UtcNow.AddSeconds(5);
    }

    if (acc && child is null && !gaveUp && DateTime.UtcNow >= retryAt)
    {
        try
        {
            child = Process.Start(new ProcessStartInfo(overlayExe)
            {
                Arguments = "--hidden",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log($"start failed: {ex.Message}");
            retryAt = DateTime.UtcNow.AddSeconds(30);
        }
    }
    else if (!acc)
    {
        gaveUp = false;
        if (child is not null) { Stop(child); child = null; }
    }
}

if (child is not null) Stop(child);

static bool IsAccRunning()
{
    var procs = Process.GetProcessesByName("AC2-Win64-Shipping");
    foreach (var p in procs) p.Dispose();
    return procs.Length > 0;
}

static void Stop(Process p)
{
    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    p.Dispose();
}

void Log(string msg)
{
    try { File.AppendAllText(Path.Combine(workDir, "watcher.log"), $"{DateTime.Now:O} {msg}{Environment.NewLine}"); }
    catch { }
}
