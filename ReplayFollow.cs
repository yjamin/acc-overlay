namespace AccOverlay;

// Auto-follow for ACC replays (in-session and saved).
//
// The broadcast API gives no playhead, only ReplayRemainingTime, quantized to
// whole seconds. The replay always ENDS where driving stopped, so we anchor to
// the end: position = drive-time length - remaining. That needs no start pin
// and follows play, pause, scrub, rewind and speed changes by itself.
//
// Source of inputs when a replay starts:
//   - current ACC session has live data in memory -> use it (in-session replay)
//   - otherwise (saved replay opened from the menu) -> recordings on disk; the
//     session whose drive length best matches the replay length, else the newest.
public sealed class ReplayFollow
{
    private readonly Lock _lock = new();
    private readonly ReplayClock _clock;
    private readonly BroadcastClient _udp;
    private readonly Func<List<RecSample>> _liveSession;
    private readonly string _recDir;
    private readonly string? _forcedFile;
    private readonly Dictionary<string, (DateTime Stamp, List<DriveTimeline> Sessions)> _fileCache = new();

    private bool _inReplay;
    private int _generation;
    private DriveTimeline? _timeline;
    private string _sourceInfo = "";
    private double _nudgeMs;
    private double _lastPos;
    private double _lastRem;

    public ReplayFollow(BroadcastClient udp, Func<List<RecSample>> liveSession, string recDir, string? forcedFile)
    {
        _udp = udp;
        _liveSession = liveSession;
        _recDir = recDir;
        _forcedFile = forcedFile;
        _clock = new ReplayClock(udp.IntervalMs);
        udp.RealtimeReceived += OnRealtime;
    }

    public bool Active { get { lock (_lock) return _inReplay && _udp.IsFresh; } }

    // Manual fine-tune (+ = show later inputs).
    public void Nudge(double deltaMs) { lock (_lock) _nudgeMs += deltaMs; }
    public void ResetNudge() { lock (_lock) _nudgeMs = 0; }

    public string StatusLine
    {
        get
        {
            lock (_lock)
            {
                string len = _timeline is null ? "--" : $"{_timeline.LengthMs / 1000:0.0}s";
                return $"[REPLAY pos={_lastPos / 1000:0.0}s/{len} rem={_lastRem / 1000:0.0}s x{_clock.Rate:0.##} adj={_nudgeMs / 1000:+0.00;-0.00;+0.00}s src={_sourceInfo}]";
            }
        }
    }

    private void OnRealtime(bool isReplay, float rem, double t, double prevT)
    {
        lock (_lock)
        {
            if (!isReplay)
            {
                if (_inReplay) { _inReplay = false; _timeline = null; _generation++; }
                return;
            }
            if (!_inReplay)
            {
                _inReplay = true;
                _clock.Start(rem, t);
                SelectSource(rem);
            }
            else
            {
                _clock.OnPacket(rem, t, prevT);
            }
        }
    }

    // Caller holds _lock.
    private void SelectSource(double remAtStart)
    {
        int gen = ++_generation;
        var live = DriveTimeline.Split(_liveSession(), "current session");
        if (live.Count > 0 && live[^1].LengthMs > 1000)
        {
            _timeline = live[^1];
            _sourceInfo = "current session";
            return;
        }

        _timeline = null;
        _sourceInfo = "loading";
        Task.Run(() =>
        {
            var (tl, info) = PickFromFiles(remAtStart);
            lock (_lock)
            {
                if (gen != _generation) return;
                _timeline = tl;
                _sourceInfo = info;
            }
        });
    }

    private (DriveTimeline?, string) PickFromFiles(double replayLenMs)
    {
        var files = _forcedFile is not null ? [_forcedFile] : RecordingFile.Recent(_recDir, 15);
        var candidates = new List<DriveTimeline>(); // newest first
        foreach (var f in files)
        {
            var sessions = LoadSessions(f);
            for (int i = sessions.Count - 1; i >= 0; i--)
                if (sessions[i].LengthMs > 1000) candidates.Add(sessions[i]);
        }
        if (candidates.Count == 0) return (null, "no recording");

        // Saved replays open at their start, so the first remaining time is the replay length.
        double tol = Math.Max(3000, replayLenMs * 0.02);
        DriveTimeline? best = null;
        double bestErr = double.MaxValue;
        foreach (var c in candidates)
        {
            double err = Math.Abs(c.LengthMs - replayLenMs);
            if (err <= tol && err < bestErr) { best = c; bestErr = err; }
        }
        return best is not null
            ? (best, $"{Path.GetFileName(best.Source)} (length match)")
            : (candidates[0], $"{Path.GetFileName(candidates[0].Source)} (newest)");
    }

    private List<DriveTimeline> LoadSessions(string path)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (_fileCache)
            {
                if (_fileCache.TryGetValue(path, out var c) && c.Stamp == stamp) return c.Sessions;
            }
            var sessions = DriveTimeline.Split(RecordingFile.ReadAll(path), path);
            lock (_fileCache) _fileCache[path] = (stamp, sessions);
            return sessions;
        }
        catch { return []; }
    }

    public TelemetrySnapshot Current
    {
        get
        {
            lock (_lock)
            {
                _lastRem = _clock.EstimateRemaining(MonoClock.Ms);
                if (_timeline is null)
                    return NoData(0);
                _lastPos = _timeline.LengthMs - _lastRem + _nudgeMs;
                return _timeline.TrySample(_lastPos, out var s) ? s : NoData(_lastPos);
            }
        }
    }

    // Replay part not covered by any recording: show neutral inputs, not wrong ones.
    private static TelemetrySnapshot NoData(double pos) =>
        new(true, AccStatus.Replay, (int)pos, 0, 0, 0, 0, 1, 0, false, false, 0, 0, 0, 0);
}

// Sub-second replay clock reconstructed from the 1s-quantized remaining time.
// Model (fits the UDP logs): rem = D - 1000*floor(played/1000), so a rem step
// marks the instant the playhead crossed a whole second. Between steps we
// extrapolate at the learned replay speed, clamped to the current 1s bucket.
internal sealed class ReplayClock
{
    private static readonly double[] Speeds = [0.1, 0.125, 0.25, 0.5, 1, 2, 4, 8, 16];

    private readonly double _halfInterval;
    private double _q;               // last reported remaining (quantized)
    private double _anchorR, _anchorT;
    private double _rate = 1;        // replay speed; + = forward, - = rewind
    private double _lastStepT = double.NaN;
    private double _lastGap = double.NaN;

    public double Rate => _rate;

    public ReplayClock(int udpIntervalMs) => _halfInterval = udpIntervalMs / 2.0;

    public void Start(double rem, double t)
    {
        // Replays open at their start (played = 0 -> remaining = rem exactly).
        _q = rem;
        _anchorR = rem;
        _anchorT = t;
        _rate = 1;
        _lastStepT = double.NaN;
        _lastGap = double.NaN;
    }

    public void OnPacket(double rem, double t, double prevT)
    {
        double delta = rem - _q;
        if (Math.Abs(delta) < 1) return;

        double estBefore = EstimateRemaining(t);
        double k = Math.Round(delta / 1000);
        bool step = k != 0 && Math.Abs(k) <= 4 && Math.Abs(delta - k * 1000) < 50;
        // The change happened somewhere between the previous packet and this one.
        double stepT = double.IsNaN(prevT) || t - prevT > 4 * _halfInterval ? t - _halfInterval : (prevT + t) / 2;

        if (step)
        {
            int dir = k < 0 ? 1 : -1;
            if (!double.IsNaN(_lastStepT))
            {
                double gap = (stepT - _lastStepT) / Math.Abs(k);
                double expected = 1000 / Math.Max(Math.Abs(_rate), 0.05);
                // Faster than expected can only mean higher speed: adopt at once.
                // Slower needs two agreeing gaps, so a pause (one long gap)
                // isn't mistaken for slow motion.
                if (gap > 0 && gap < 0.8 * expected)
                    _rate = dir * Snap(1000 / gap);
                else if (!double.IsNaN(_lastGap) && gap > 0 && Math.Abs(gap - _lastGap) < 0.2 * _lastGap)
                    _rate = dir * Snap(1000 / gap);
                _lastGap = gap;
            }
            if (Math.Sign(_rate) != dir) _rate = dir * Math.Abs(_rate);
            _lastStepT = stepT;
            // Forward: remaining just dropped to rem. Rewind: it just rose past rem - 1000.
            _anchorR = dir > 0 ? rem : rem - 999;
            _anchorT = stepT;
        }
        else
        {
            // Scrub/skip: keep the sub-second phase (skips are whole seconds), relearn timing.
            double frac = Math.Clamp(_q - estBefore, 0, 999);
            _anchorR = rem - frac;
            _anchorT = t;
            _lastStepT = double.NaN;
            _lastGap = double.NaN;
        }
        _q = rem;
    }

    public double EstimateRemaining(double t)
    {
        double r = _anchorR - _rate * (t - _anchorT);
        return Math.Clamp(r, Math.Max(0, _q - 999), _q);
    }

    private static double Snap(double rate)
    {
        foreach (var s in Speeds)
            if (Math.Abs(rate - s) <= s * 0.15) return s;
        return Math.Clamp(rate, 0.05, 32);
    }
}
