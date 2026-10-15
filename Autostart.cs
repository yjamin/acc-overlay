using System.Diagnostics;
using Microsoft.Win32;

namespace AccOverlay;

// Registers AccOverlayWatcher.exe (windowless) in HKCU\...\Run. The watcher
// starts "AccOverlay.exe --hidden" whenever ACC runs and stops it when ACC exits.
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AccOverlayWatcher";
    private const string WatcherProcess = "AccOverlayWatcher";
    private const string StopEventName = @"Local\AccOverlayWatcher.Stop";

    public static void Install()
    {
        string overlayExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AccOverlay.exe");
        string watcherExe = Path.Combine(AppContext.BaseDirectory, "AccOverlayWatcher.exe");
        if (!File.Exists(watcherExe))
        {
            Console.WriteLine($"Watcher not found: {watcherExe}. Build the solution first (dotnet build).");
            return;
        }
        // Recordings are stored relative to this directory (keeps them where they are today).
        string workDir = Environment.CurrentDirectory;
        string cmd = $"\"{watcherExe}\" --overlay \"{overlayExe}\" --workdir \"{workDir.TrimEnd('\\')}\"";

        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.SetValue(ValueName, cmd);

        StopRunning();
        Process.Start(new ProcessStartInfo(watcherExe)
        {
            Arguments = $"--overlay \"{overlayExe}\" --workdir \"{workDir.TrimEnd('\\')}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        });

        Console.WriteLine("Autostart installed. The overlay now starts with ACC (and at every Windows login the watcher runs in the background).");
        Console.WriteLine($"  Watcher:    {watcherExe}");
        Console.WriteLine($"  Recordings: {Path.Combine(workDir, "recordings")}");
        Console.WriteLine("Remove with: AccOverlay.exe --uninstall-autostart");
    }

    public static void Uninstall()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        StopRunning();
        Console.WriteLine("Autostart removed and watcher stopped.");
    }

    private static void StopRunning()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(StopEventName, out var stop))
                using (stop) stop.Set();
        }
        catch { }
        foreach (var p in Process.GetProcessesByName(WatcherProcess))
        {
            try { if (!p.WaitForExit(3000)) p.Kill(entireProcessTree: true); }
            catch { }
            finally { p.Dispose(); }
        }
    }
}
