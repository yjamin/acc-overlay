namespace AccOverlay;

// Recording rows reduced to the time ACC actually puts into a replay: only
// LIVE samples count. Menus, pauses and in-session replay viewing freeze the
// session clock, so they add no drive time (and are not in the replay).
public sealed class DriveTimeline
{
    private readonly RecSample[] _rows;
    private readonly double[] _driveMs;
    private readonly bool[] _joined; // row i directly continues row i-1 (interpolate)

    // Recorder steps are ~33ms; anything much longer is a gap, not driving.
    private const long MaxStepMs = 250;

    public string Source { get; }
    public double LengthMs => _driveMs.Length == 0 ? 0 : _driveMs[^1];

    private DriveTimeline(List<RecSample> rows, List<double> drive, List<bool> joined, string source)
    {
        _rows = rows.ToArray();
        _driveMs = drive.ToArray();
        _joined = joined.ToArray();
        Source = source;
    }

    // One timeline per ACC session; status OFF (menus) separates sessions.
    public static List<DriveTimeline> Split(IReadOnlyList<RecSample> rows, string source)
    {
        var result = new List<DriveTimeline>();
        var cur = new List<RecSample>();
        var drive = new List<double>();
        var joined = new List<bool>();
        double acc = 0;
        RecSample? prev = null;

        void Flush()
        {
            if (cur.Count > 1) result.Add(new DriveTimeline(cur, drive, joined, source));
            cur = new(); drive = new(); joined = new();
            acc = 0;
        }

        foreach (var r in rows)
        {
            if (r.Status == AccStatus.Off)
            {
                Flush();
            }
            else if (r.Status == AccStatus.Live)
            {
                bool cont = cur.Count > 0 && prev is { Status: AccStatus.Live } p
                    && r.TMs - p.TMs is > 0 and <= MaxStepMs;
                if (cont) acc += r.TMs - prev!.Value.TMs;
                cur.Add(r);
                drive.Add(acc);
                joined.Add(cont);
            }
            prev = r;
        }
        Flush();
        return result;
    }

    public bool TrySample(double posMs, out TelemetrySnapshot snap)
    {
        snap = default;
        if (_rows.Length == 0 || posMs < 0 || posMs > LengthMs + 100) return false;

        // Last row with drive time <= pos.
        int lo = 0, hi = _rows.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_driveMs[mid] <= posMs) lo = mid;
            else hi = mid - 1;
        }

        var a = _rows[lo];
        float thr = a.Thr, brk = a.Brk, steer = a.Steer, speed = a.Speed;
        int rpm = a.Rpm;
        if (lo + 1 < _rows.Length && _joined[lo + 1])
        {
            var b = _rows[lo + 1];
            double span = _driveMs[lo + 1] - _driveMs[lo];
            float f = span > 0 ? (float)Math.Clamp((posMs - _driveMs[lo]) / span, 0, 1) : 0;
            thr += (b.Thr - a.Thr) * f;
            brk += (b.Brk - a.Brk) * f;
            steer += (b.Steer - a.Steer) * f;
            speed += (b.Speed - a.Speed) * f;
            rpm += (int)((b.Rpm - a.Rpm) * f);
        }

        // PacketId = playhead ms: changes every frame while playing, freezes on pause
        // (the overlay chart scrolls/freezes on packetId changes).
        snap = new TelemetrySnapshot(true, AccStatus.Replay, (int)posMs,
            thr, brk, steer, speed, a.Gear, rpm, a.Tc, a.Abs,
            a.GfxPid, a.LapMs, a.Laps, a.Dist);
        return true;
    }
}
