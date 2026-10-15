using System.IO.MemoryMappedFiles;

namespace AccOverlay;

// Snapshot polled from ACC shared memory (Local\acpmf_physics + Local\acpmf_graphics).
// Physics offsets match SPageFilePhysics header with Pack=4:
// packetId(0) gas(4) brake(8) fuel(12) gear(16) rpms(20) steerAngle(24) speedKmh(28)
// tc in action float(204), abs in action float(252). In ACC these floats are 1
// while the system cuts in; tcInAction/absInAction ints further back stay 0.
// Graphics offsets match SPageFileGraphic header with Pack=4:
// packetId(0) status(4): 0=OFF, 1=REPLAY, 2=LIVE, 3=PAUSE.
// NOTE: during REPLAY the physics page does NOT reliably carry the replayed
// inputs (gas/brake/steer often freeze or read 0) — shared memory is driven
// by the live simulation, not the replay player. See Program --dump to verify
// on your machine.
public enum AccStatus
{
    NoGame = -1,
    Off = 0,
    Replay = 1,
    Live = 2,
    Pause = 3,
}

public readonly record struct TelemetrySnapshot(
    bool Connected,
    AccStatus Status,
    int PacketId,
    float Throttle,   // 0..1
    float Brake,      // 0..1
    float SteerAngle, // raw steering angle
    float SpeedKmh,
    int Gear,         // 0=R,1=N,2=1st...
    int Rpm,
    bool TcActive,
    bool AbsActive,
    // Graphics page (sync signals for replay matching):
    int GfxPacketId,
    int LapTimeMs,      // iCurrentTime: current lap time in ms
    int CompletedLaps,
    float DistanceM);   // distanceTraveled in current stint

public sealed class AccReader : IDisposable
{
    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private MemoryMappedFile? _gfxFile;
    private MemoryMappedViewAccessor? _gfxView;
    private readonly Lock _lock = new();
    private TelemetrySnapshot _current = new(false, AccStatus.NoGame, 0, 0, 0, 0, 0, 1, 0, false, false, 0, 0, 0, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public AccReader()
    {
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "ACC-SHMEM" };
    }

    public TelemetrySnapshot Current
    {
        get { lock (_lock) return _current; }
    }

    public void Start() => _thread.Start();
    public void Stop() => _cts.Cancel();

    private void PollLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _view ??= TryOpen();
                _gfxView ??= TryOpenGraphics();
                if (_view is null)
                {
                    Set(new TelemetrySnapshot(false, AccStatus.NoGame, 0, 0, 0, 0, 0, 1, 0, false, false, 0, 0, 0, 0));
                }
                else
                {
                    // Read header fields directly; robust against struct-size changes later in the page.
                    int packetId = _view.ReadInt32(0);
                    float gas = _view.ReadSingle(4);
                    float brake = _view.ReadSingle(8);
                    int gear = _view.ReadInt32(16);
                    int rpm = _view.ReadInt32(20);
                    float steer = _view.ReadSingle(24);
                    float speed = _view.ReadSingle(28);
                    float tc = _view.ReadSingle(FlagsTcOffset);
                    float abs = _view.ReadSingle(FlagsAbsOffset);
                    AccStatus status = ReadStatus();
                    ReadSync(out int gfxPid, out int lapMs, out int laps, out float dist);

                    if (float.IsNaN(gas) || float.IsInfinity(gas)) gas = 0;
                    if (float.IsNaN(brake) || float.IsInfinity(brake)) brake = 0;
                    Set(new TelemetrySnapshot(true, status, packetId,
                        Math.Clamp(gas, 0, 1), Math.Clamp(brake, 0, 1),
                        steer, speed, gear, rpm,
                        IsActive(tc), IsActive(abs),
                        gfxPid, lapMs, laps, dist));
                }
            }
            catch (FileNotFoundException)
            {
                ResetView();
                Set(new TelemetrySnapshot(false, AccStatus.NoGame, 0, 0, 0, 0, 0, 1, 0, false, false, 0, 0, 0, 0));
            }
            catch
            {
                // ACC restarts / map recreated: drop handle so we re-open next tick.
                ResetView();
            }
            Thread.Sleep(16); // ~60 Hz
        }
    }

    private AccStatus ReadStatus()
    {
        try
        {
            if (_gfxView is null) return AccStatus.Live; // physics up but graphics not yet: assume live (old behavior)
            int raw = _gfxView.ReadInt32(GraphicsStatusOffset);
            return raw switch
            {
                0 => AccStatus.Off,
                1 => AccStatus.Replay,
                2 => AccStatus.Live,
                3 => AccStatus.Pause,
                _ => AccStatus.Off,
            };
        }
        catch
        {
            ResetGraphicsView();
            return AccStatus.Live;
        }
    }

    private MemoryMappedViewAccessor? TryOpen()
    {
        try
        {
            _file = MemoryMappedFile.OpenExisting(@"Local\acpmf_physics", MemoryMappedFileRights.Read);
            return _file.CreateViewAccessor(0, ViewSize, MemoryMappedFileAccess.Read);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private MemoryMappedViewAccessor? TryOpenGraphics()
    {
        try
        {
            _gfxFile = MemoryMappedFile.OpenExisting(@"Local\acpmf_graphics", MemoryMappedFileRights.Read);
            return _gfxFile.CreateViewAccessor(0, GraphicsViewSize, MemoryMappedFileAccess.Read);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private const int FlagsTcOffset = 204;
    private const int FlagsAbsOffset = 252;
    private const int GraphicsStatusOffset = 4;
    // SPageFileGraphic (Pack=4, UTF-16 strings): completedLaps @132,
    // iCurrentTime @140, distanceTraveled @156.
    private const int GraphicsLapsOffset = 132;
    private const int GraphicsLapTimeOffset = 140;
    private const int GraphicsDistanceOffset = 156;
    private const long ViewSize = 512;
    private const long GraphicsViewSize = 256;

    // Sync signals: do these follow the replay playhead? Captured for diagnosis.
    private void ReadSync(out int gfxPid, out int lapMs, out int laps, out float dist)
    {
        gfxPid = 0; lapMs = 0; laps = 0; dist = 0;
        try
        {
            if (_gfxView is null) return;
            gfxPid = _gfxView.ReadInt32(0);
            laps = _gfxView.ReadInt32(GraphicsLapsOffset);
            lapMs = _gfxView.ReadInt32(GraphicsLapTimeOffset);
            dist = _gfxView.ReadSingle(GraphicsDistanceOffset);
            if (float.IsNaN(dist) || float.IsInfinity(dist)) dist = 0;
        }
        catch
        {
            ResetGraphicsView();
        }
    }

    private static bool IsActive(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && v > 0.01f;

    private void Set(TelemetrySnapshot s) { lock (_lock) _current = s; }

    private void ResetView()
    {
        try { _view?.Dispose(); } catch { }
        try { _file?.Dispose(); } catch { }
        _view = null;
        _file = null;
        ResetGraphicsView();
    }

    private void ResetGraphicsView()
    {
        try { _gfxView?.Dispose(); } catch { }
        try { _gfxFile?.Dispose(); } catch { }
        _gfxView = null;
        _gfxFile = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        ResetView();
        _cts.Dispose();
    }

    public static string GearLabel(int gear) => gear switch
    {
        0 => "R",
        1 => "N",
        _ => (gear - 1).ToString(),
    };
}
