using AccOverlay;

if (args.Contains("--install-autostart")) { Autostart.Install(); return 0; }
if (args.Contains("--uninstall-autostart")) { Autostart.Uninstall(); return 0; }

bool hidden = args.Contains("--hidden");
void Log(string s) { if (!hidden) Console.WriteLine(s); }

Log("ACC input telemetry overlay (GameOverlay.Net Direct2D)");
Log("Run ACC in BORDERLESS windowed. True exclusive fullscreen cannot be covered.");
Log("Live inputs are shown while driving and recorded to recordings/session-<time>.csv;");
Log("during an ACC replay the recorded inputs follow the replay automatically.");
Log("Usage: AccOverlay.exe [--fixed x y] [--w width] [--h height]");
Log("         [--dump file.csv] [--no-record] [--play file.csv] [--diag diag.csv]");
Log("         [--follow-replay file.csv] [--follow-log file.csv] [--udp-port N] [--udp-pass pw]");
Log("         [--hidden] [--install-autostart] [--uninstall-autostart]");
Log("Replay keys: LEFT/RIGHT nudge sync 0.1s (Shift: 1s), R reset nudge.");
Log("Playback keys (--play): SPACE play/pause, LEFT/RIGHT seek 5s, R restart, +/- speed.\n");

// Fixed position disables window-follow. Otherwise we anchor to the ACC client area.
bool follow = true;
int fixedX = 860, fixedY = 680;
int overlayW = 600, overlayH = 250;
string? dumpPath = null;
bool noRecord = false;
string? playPath = null;
string? diagPath = null;
string? udpProbePath = null;
int udpPort = 9000;
string udpPass = "asd";
string? followFile = null;
string? followLogPath = null;
const string RecDir = "recordings";
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--fixed" && i + 2 < args.Length
        && int.TryParse(args[i + 1], out var fx) && int.TryParse(args[i + 2], out var fy))
    { follow = false; fixedX = fx; fixedY = fy; i += 2; }
    else if (args[i] == "--w" && i + 1 < args.Length && int.TryParse(args[i + 1], out var ww))
    { overlayW = Math.Clamp(ww, 120, 800); i++; }
    else if (args[i] == "--h" && i + 1 < args.Length && int.TryParse(args[i + 1], out var hh))
    { overlayH = Math.Clamp(hh, 160, 900); i++; }
    // legacy: two bare numbers = fixed position
    else if (args.Length == 2 && i == 0
        && int.TryParse(args[0], out var px) && int.TryParse(args[1], out var py))
    { follow = false; fixedX = px; fixedY = py; break; }
    else if (args[i] == "--dump" && i + 1 < args.Length)
    { dumpPath = args[i + 1]; i++; }
    else if (args[i] == "--no-record")
    { noRecord = true; }
    else if (args[i] == "--play" && i + 1 < args.Length)
    { playPath = args[i + 1]; i++; }
    else if (args[i] == "--diag" && i + 1 < args.Length)
    { diagPath = args[i + 1]; i++; }
    else if (args[i] == "--udp-probe" && i + 1 < args.Length)
    { udpProbePath = args[i + 1]; i++; }
    else if (args[i] == "--udp-port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var up))
    { udpPort = up; i++; }
    else if (args[i] == "--udp-pass" && i + 1 < args.Length)
    { udpPass = args[i + 1]; i++; }
    else if (args[i] == "--follow-log" && i + 1 < args.Length)
    { followLogPath = args[i + 1]; i++; }
    else if (args[i] == "--follow-replay")
    {
        // Replay-follow is the default now; an optional file forces the source for saved replays.
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
        { followFile = args[i + 1]; i++; }
    }
}

// UDP probe mode: standalone, no overlay. Registers as a broadcast client and
// logs every datagram (drive ~10s, open the saved replay, quit, send the log).
if (udpProbePath is not null)
{
    using var probe = new UdpProbe("127.0.0.1", udpPort, udpPass, udpProbePath);
    probe.Start();
    Console.WriteLine($"UDP probe logging to {udpProbePath} (port {udpPort}). Drive, then open the replay.");
    var pcts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; pcts.Cancel(); };
    try
    {
        while (!pcts.IsCancellationRequested)
        {
            Console.Write($"\r{probe.LastSummary}   ");
            await Task.Delay(500, pcts.Token);
        }
    }
    catch (TaskCanceledException) { }
    Console.WriteLine("\nProbe stopped.");
    return 0;
}

// One live overlay at a time (the autostart watcher and a manual start would
// otherwise draw twice and record two files).
using var instance = new Mutex(true, @"Local\AccOverlay.Instance", out bool firstInstance);
if (playPath is null && !firstInstance)
{
    Log("AccOverlay is already running (autostart?). Exiting.");
    return 3;
}

using var reader = new AccReader();
reader.Start();
Func<TelemetrySnapshot> getData = () => reader.Current;
TelemetryPlayback? playback = null;
BroadcastClient? udp = null;
ReplayFollow? replayFollow = null;
TelemetryRecorder? recorder = null;

if (playPath is not null)
{
    try
    {
        playback = new TelemetryPlayback(playPath);
        Log($"Playback: {playPath} ({playback.Count} samples, {playback.DurationMs / 1000}s).");
        Log("Start the ACC replay, then press R to restart this file in sync.");
    }
    catch (Exception ex)
    {
        Log($"Cannot load playback file: {ex.Message}");
        return 1;
    }
    getData = () => playback!.Current;
}
else
{
    // Always record the live reader (in memory for in-session replays, plus file).
    string? recordPath = noRecord ? null
        : dumpPath ?? Path.Combine(RecDir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    recorder = new TelemetryRecorder(() => reader.Current, recordPath);
    recorder.Start();
    if (recordPath is not null) Log($"Recording to {recordPath}");

    udp = new BroadcastClient("127.0.0.1", udpPort, udpPass);
    udp.Start();
    replayFollow = new ReplayFollow(udp, recorder.CurrentSession, RecDir, followFile);
    getData = () => replayFollow!.Active ? replayFollow.Current : reader.Current;
    if (followFile is not null) Log($"Saved replays follow: {followFile}");
}

using var overlay = new TelemetryOverlay(getData, follow ? 860 : fixedX, follow ? 680 : fixedY, overlayW, overlayH);

var overlayThread = new Thread(overlay.Run) { IsBackground = true, Name = "Overlay" };
overlayThread.Start();

// --diag: 10 Hz raw dump of BOTH shared-memory pages to find a signal that
// follows the replay playhead (drive ~10s, open the replay, quit, send file).
StreamWriter? diag = null;
if (diagPath is not null)
{
    diag = new StreamWriter(diagPath, append: false) { AutoFlush = true };
    diag.WriteLine("time,physPid,thr,brk,steer,speedKmh,gear,rpm,gfxPid,status,lapMs,laps,dist");
}

// --follow-log: replay clock vs. shown inputs, for sync diagnosis.
StreamWriter? followLog = null;
if (followLogPath is not null && replayFollow is not null)
{
    followLog = new StreamWriter(followLogPath, append: false) { AutoFlush = true };
    followLog.WriteLine("utc,remRaw,status,thr,brk,steer,speedKmh,gear");
    Log($"Follow log: {followLogPath}");
}

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
bool keysOk = !hidden;
try
{
    int lastX = int.MinValue, lastY = int.MinValue;
    while (!cts.IsCancellationRequested)
    {
        if (follow && AccWindow.TryGetRect(out int ax, out int ay, out int aw, out int ah))
        {
            // Anchor: bottom-center inside the game client area, 60px above the bottom edge.
            int w = overlay.OverlayWidth, h = overlay.OverlayHeight;
            int nx = ax + (aw - w) / 2;
            int ny = ay + ah - h - 60;
            if (nx != lastX || ny != lastY)
            {
                overlay.MoveResize(nx, ny, w, h);
                lastX = nx; lastY = ny;
            }
        }
        else if (!follow)
        {
            overlay.MoveResize(fixedX, fixedY, overlayW, overlayH);
        }

        var keys = new List<ConsoleKeyInfo>();
        if (keysOk)
        {
            try { while (Console.KeyAvailable) keys.Add(Console.ReadKey(intercept: true)); }
            catch (InvalidOperationException) { keysOk = false; } // no interactive console
        }

        if (playback is not null)
        {
            foreach (var k in keys)
            {
                switch (k.Key)
                {
                    case ConsoleKey.Spacebar: playback.Toggle(); break;
                    case ConsoleKey.LeftArrow: playback.Seek(-5000); break;
                    case ConsoleKey.RightArrow: playback.Seek(5000); break;
                    case ConsoleKey.R: playback.Restart(); break;
                    case ConsoleKey.OemPlus:
                    case ConsoleKey.Add: playback.CycleSpeed(); break;
                }
            }
            var s = playback.Current;
            if (!hidden)
                Console.Write($"\r[PLAYBACK {(playback.Playing ? ">" : "II")} {playback.PositionMs / 1000}s/{playback.DurationMs / 1000}s {playback.Speed:0.##}x] thr={s.Throttle:0.00} brk={s.Brake:0.00} steer={s.SteerAngle:0.0} gear={AccReader.GearLabel(s.Gear)} {s.SpeedKmh:0}km/h   ");
        }
        else if (replayFollow is not null && udp is not null)
        {
            foreach (var k in keys)
            {
                double step = k.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 1000 : 100;
                switch (k.Key)
                {
                    case ConsoleKey.LeftArrow: replayFollow.Nudge(-step); break;
                    case ConsoleKey.RightArrow: replayFollow.Nudge(step); break;
                    case ConsoleKey.R: replayFollow.ResetNudge(); break;
                }
            }
            var s = getData();
            if (replayFollow.Active)
            {
                if (!hidden)
                    Console.Write($"\r{replayFollow.StatusLine} thr={s.Throttle:0.00} brk={s.Brake:0.00} steer={s.SteerAngle:0.0} gear={AccReader.GearLabel(s.Gear)} {s.SpeedKmh:0}km/h   ");
                followLog?.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:O},{1:0},\"{2}\",{3:0.000},{4:0.000},{5:0.0},{6:0.0},{7}",
                    DateTime.UtcNow, udp.RemMs, replayFollow.StatusLine,
                    s.Throttle, s.Brake, s.SteerAngle, s.SpeedKmh, s.Gear));
            }
            else if (!hidden)
            {
                string udpState = !udp.HasTraffic || !udp.IsFresh ? "UDP:no-signal" : !udp.RegisteredOk ? "UDP:rejected" : "UDP:ok";
                string mode = !s.Connected ? "NO ACC" : s.Status.ToString().ToUpperInvariant();
                bool accVisible = AccWindow.TryGetRect(out _, out _, out int gw, out int gh);
                Console.Write($"\r[{mode} #{s.PacketId}] thr={s.Throttle:0.00}{(s.TcActive ? "[TC]" : "")} brk={s.Brake:0.00}{(s.AbsActive ? "[ABS]" : "")} steer={s.SteerAngle:0.0} gear={AccReader.GearLabel(s.Gear)} {s.SpeedKmh:0}km/h {s.Rpm}rpm {udpState} game={(accVisible ? $"{gw}x{gh}" : "n/a")}   ");
            }
            if (diag is not null)
            {
                var r = reader.Current;
                string mode = !r.Connected ? "NO ACC" : r.Status.ToString().ToUpperInvariant();
                diag.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:O},{1},{2:0.000},{3:0.000},{4:0.0},{5:0.0},{6},{7},{8},{9},{10},{11},{12:0.0}",
                    DateTime.UtcNow, r.PacketId, r.Throttle, r.Brake, r.SteerAngle, r.SpeedKmh,
                    r.Gear, r.Rpm, r.GfxPacketId, mode, r.LapTimeMs, r.CompletedLaps, r.DistanceM));
            }
        }
        await Task.Delay(diag is null && followLog is null ? 500 : 100, cts.Token);
    }
}
catch (TaskCanceledException) { }

recorder?.Dispose();
udp?.Dispose();
followLog?.Dispose();
diag?.Dispose();
Log("\nQuitting (overlay window closes with process exit)...");
return 0;
