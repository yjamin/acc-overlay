using System.Net.Sockets;
using System.Text;

namespace AccOverlay;

// Persistent broadcast client: registers, keeps state from REALTIME_UPDATE,
// re-registers when the server goes silent. RealtimeReceived fires on the
// receive thread for every realtime packet, with its arrival time and the
// arrival time of the previous one (replay clock steps happen in between).
public sealed class BroadcastClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly string _connPass;
    private readonly int _intervalMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly object _lock = new();

    private bool _hasTraffic;
    private bool _regOk;
    private bool _isReplay;
    private float _rem;
    private double _lastPacketMs = double.NegativeInfinity;
    private double _lastRealtimeMs = double.NaN;
    private DateTime _lastRegisterUtc = DateTime.MinValue;

    /// <summary>(isReplay, remainingMs, receivedMs, previousReceivedMs or NaN)</summary>
    public event Action<bool, float, double, double>? RealtimeReceived;

    public bool HasTraffic { get { lock (_lock) return _hasTraffic; } }
    public bool RegisteredOk { get { lock (_lock) return _regOk; } }
    public bool IsReplay { get { lock (_lock) return _isReplay; } }
    public float RemMs { get { lock (_lock) return _rem; } }
    public int IntervalMs => _intervalMs;
    // ACC stopped sending (closed, or no session loaded).
    public bool IsFresh { get { lock (_lock) return MonoClock.Ms - _lastPacketMs < 2500; } }

    public BroadcastClient(string host, int port, string connectionPassword, int intervalMs = 100)
    {
        _connPass = connectionPassword;
        _intervalMs = intervalMs;
        _udp = new UdpClient(0);
        _udp.Connect(host, port);
        _udp.Client.ReceiveTimeout = 1000;
        _thread = new Thread(Run) { IsBackground = true, Name = "Broadcast" };
    }

    public void Start() => _thread.Start();

    private void SendRegister()
    {
        try
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            BroadcastProtocol.WriteRegister(w, "AccOverlay", _connPass, _intervalMs);
            _udp.Send(ms.ToArray(), (int)ms.Length);
            _lastRegisterUtc = DateTime.UtcNow;
        }
        catch { }
    }

    private void Run()
    {
        SendRegister();
        var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var data = _udp.Receive(ref remote);
                if (data.Length > 0) Handle(data, MonoClock.Ms);
            }
            catch (SocketException) { /* timeout */ }
            catch { Thread.Sleep(200); }
            if (!IsFresh && (DateTime.UtcNow - _lastRegisterUtc).TotalSeconds > 2)
            {
                lock (_lock) _lastRealtimeMs = double.NaN;
                SendRegister();
            }
        }
    }

    private void Handle(byte[] data, double nowMs)
    {
        bool fire = false, rp = false;
        float rem = 0;
        double prev = double.NaN;
        lock (_lock)
        {
            _hasTraffic = true;
            _lastPacketMs = nowMs;
            switch (data[0])
            {
                case 1:
                    if (BroadcastProtocol.TryParseRegResult(data, out _, out bool ok, out _, out _))
                        _regOk = ok;
                    break;
                case 2:
                    if (BroadcastProtocol.TryParseRealtime(data, out _, out rp, out _, out rem))
                    {
                        _isReplay = rp; _rem = rem;
                        prev = _lastRealtimeMs;
                        _lastRealtimeMs = nowMs;
                        fire = true;
                    }
                    break;
            }
        }
        // Outside our lock: handlers take their own lock.
        if (fire) RealtimeReceived?.Invoke(rp, rem, nowMs, prev);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Close(); } catch { }
        _cts.Dispose();
    }
}
