namespace AccOverlay;

using System.Globalization;

public readonly record struct RecSample(
    long TMs, AccStatus Status, int PacketId,
    float Thr, float Brk, float Steer, float Speed,
    int Gear, int Rpm, bool Tc, bool Abs,
    int GfxPid, int LapMs, int Laps, float Dist);

public static class RecordingFile
{
    public const string Pattern = "session-*.csv";

    // Opened with ReadWrite sharing so the file currently being recorded can be read too.
    public static List<RecSample> ReadAll(string path)
    {
        var rows = new List<RecSample>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        sr.ReadLine(); // header
        string? line;
        while ((line = sr.ReadLine()) is not null)
            if (TryParse(line, out var r)) rows.Add(r);
        return rows;
    }

    private static bool TryParse(string line, out RecSample r)
    {
        r = default;
        var p = line.Split(',');
        if (p.Length < 11) return false;
        if (!long.TryParse(p[0], out var t)) return false;
        r = new RecSample(
            t,
            int.TryParse(p[1], out var st) ? (AccStatus)st : AccStatus.Live,
            int.TryParse(p[2], out var pid) ? pid : 0,
            F(p[3]), F(p[4]), F(p[5]), F(p[6]),
            int.TryParse(p[7], out var g) ? g : 1,
            int.TryParse(p[8], out var rpm) ? rpm : 0,
            p[9] == "1", p[10].Trim() == "1",
            p.Length > 11 && int.TryParse(p[11], out var gp) ? gp : 0,
            p.Length > 12 && int.TryParse(p[12], out var lm) ? lm : 0,
            p.Length > 13 && int.TryParse(p[13], out var lp) ? lp : 0,
            p.Length > 14 ? F(p[14]) : 0);
        return true;
    }

    // Newest session recordings first (follow logs and other csv files are ignored).
    public static string[] Recent(string dir, int max)
    {
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, Pattern)
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
            .Take(max)
            .ToArray();
    }

    public static string? Latest(string dir) => Recent(dir, 1).FirstOrDefault();

    private static float F(string s) => float.TryParse(s, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var v) ? v : 0;
}

// Live session recorder + replay-file player.
//
// Background: ACC shared memory does not carry pedal/steer inputs while a
// replay is playing (gas/brake read 0), so the overlay cannot show inputs for
// replays from shared memory alone. Workaround: record the live inputs while
// driving (file + in-memory copy of the current session) and map them onto
// the replay playhead (see ReplayFollow).
public sealed class TelemetryRecorder : IDisposable
{
    private readonly Func<TelemetrySnapshot> _getData;
    private readonly string? _path;
    private readonly int _intervalMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly Lock _sessionLock = new();
    private readonly List<RecSample> _session = new();
    private bool _started;

    public string? Path => _path;

    // path null = in-memory only (no file).
    public TelemetryRecorder(Func<TelemetrySnapshot> getData, string? path, int rateHz = 30)
    {
        _getData = getData;
        _path = path;
        _intervalMs = Math.Max(10, 1000 / Math.Clamp(rateHz, 1, 120));
        _thread = new Thread(Run) { IsBackground = true, Name = "Telemetry-Record" };
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _thread.Start();
    }

    // Copy of all samples since the current ACC session began (cleared on menus / ACC exit).
    public List<RecSample> CurrentSession()
    {
        lock (_sessionLock) return new List<RecSample>(_session);
    }

    private void Run()
    {
        StreamWriter? w = null;
        try
        {
            if (_path is not null)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path)) ?? ".");
                w = new StreamWriter(_path, append: false) { AutoFlush = true };
                w.WriteLine("tMs,status,packetId,thr,brk,steer,speedKmh,gear,rpm,tc,abs,gfxPid,lapMs,laps,dist");
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long next = 0;
            while (!_cts.IsCancellationRequested)
            {
                var s = _getData();
                if (s.Connected)
                {
                    var r = new RecSample(sw.ElapsedMilliseconds, s.Status, s.PacketId,
                        s.Throttle, s.Brake, s.SteerAngle, s.SpeedKmh,
                        s.Gear, s.Rpm, s.TcActive, s.AbsActive,
                        s.GfxPacketId, s.LapTimeMs, s.CompletedLaps, s.DistanceM);
                    lock (_sessionLock)
                    {
                        if (r.Status == AccStatus.Off) _session.Clear();
                        else _session.Add(r);
                    }
                    w?.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3:0.000},{4:0.000},{5:0.0},{6:0.0},{7},{8},{9},{10},{11},{12},{13},{14:0.0}",
                        r.TMs, (int)r.Status, r.PacketId,
                        r.Thr, r.Brk, r.Steer, r.Speed,
                        r.Gear, r.Rpm, r.Tc ? 1 : 0, r.Abs ? 1 : 0,
                        r.GfxPid, r.LapMs, r.Laps, r.Dist));
                }
                else
                {
                    lock (_sessionLock) _session.Clear();
                }

                // Schedule against the clock so Sleep granularity doesn't stretch the interval.
                next += _intervalMs;
                long wait = next - sw.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                else if (wait < -500) next = sw.ElapsedMilliseconds;
            }
        }
        catch { }
        finally { try { w?.Dispose(); } catch { } }
    }

    public void Dispose()
    {
        _cts.Cancel();
    }
}

public sealed class TelemetryPlayback
{
    private readonly List<RecSample> _rows;
    private double _posMs;
    private long _lastTick;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    public bool Playing { get; private set; } = true;
    public double Speed { get; private set; } = 1.0;
    public long DurationMs => _rows.Count == 0 ? 0 : _rows[^1].TMs;
    public long PositionMs => (long)Math.Clamp(_posMs, 0, DurationMs);
    public int Count => _rows.Count;

    public TelemetryPlayback(string path)
    {
        _rows = RecordingFile.ReadAll(path);
        _lastTick = _sw.ElapsedMilliseconds;
    }

    public void Toggle() => Playing = !Playing;
    public void Restart() => _posMs = 0;
    public void Seek(long deltaMs) => _posMs = Math.Clamp(_posMs + deltaMs, 0, DurationMs);
    public void CycleSpeed() => Speed = Speed >= 2.0 ? 0.25 : Speed * 2.0;

    public TelemetrySnapshot Current
    {
        get
        {
            var now = _sw.ElapsedMilliseconds;
            if (Playing && _rows.Count > 0)
            {
                _posMs += (now - _lastTick) * Speed;
                if (_posMs >= DurationMs) { _posMs = DurationMs; Playing = false; }
            }
            _lastTick = now;
            if (_rows.Count == 0)
                return new TelemetrySnapshot(false, AccStatus.NoGame, 0, 0, 0, 0, 0, 1, 0, false, false, 0, 0, 0, 0);
            var r = _rows[IndexAt((long)_posMs)];
            // Report as REPLAY so the overlay badge matches what you're watching.
            return new TelemetrySnapshot(true, AccStatus.Replay, r.PacketId,
                r.Thr, r.Brk, r.Steer, r.Speed, r.Gear, r.Rpm, r.Tc, r.Abs,
                r.GfxPid, r.LapMs, r.Laps, r.Dist);
        }
    }

    private int IndexAt(long tMs)
    {
        int lo = 0, hi = _rows.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_rows[mid].TMs <= tMs) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }
}
