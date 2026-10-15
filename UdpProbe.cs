using System.Net.Sockets;
using System.Text;

namespace AccOverlay;

// Minimal ACC broadcast API probe: registers as a display client and logs
// every datagram. Answers one question: does the UDP stream stay alive
// during a SAVED replay, and does ReplaySessionTime advance?
// Protocol: kind(1B) + payload. Strings = u16 LE length + UTF-8.
public sealed class UdpProbe : IDisposable
{
    private readonly UdpClient _udp;
    private readonly StreamWriter _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly object _lock = new();
    private string _lastSummary = "waiting...";
    private readonly Dictionary<int, long> _counts = new();

    public string LastSummary { get { lock (_lock) return _lastSummary; } }

    public UdpProbe(string accHost, int accPort, string connectionPassword, string file, int intervalMs = 500)
    {
        _udp = new UdpClient(0);
        _udp.Connect(accHost, accPort);
        _udp.Client.ReceiveTimeout = 1000;
        _log = new StreamWriter(file, append: false) { AutoFlush = true };
        _log.WriteLine("time,type,len,detail");
        SendRegister(connectionPassword, intervalMs);
        _thread = new Thread(Run) { IsBackground = true, Name = "UDP-Probe" };
    }

    public void Start() => _thread.Start();

    private void SendRegister(string connPass, int intervalMs)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        BroadcastProtocol.WriteRegister(w, "AccOverlayProbe", connPass, intervalMs);
        _udp.Send(ms.ToArray(), (int)ms.Length);
    }

    private void Run()
    {
        var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var data = _udp.Receive(ref remote);
                Handle(data);
            }
            catch (SocketException) { /* timeout: just re-loop */ }
            catch { Thread.Sleep(200); }
        }
    }

    private void Handle(byte[] data)
    {
        if (data.Length == 0) return;
        int type = data[0];
        string detail = "";
        try
        {
            detail = type switch
            {
                1 => BroadcastProtocol.TryParseRegResult(data, out int id, out bool ok, out bool ro, out string err)
                    ? $"reg ok={ok} readonly={ro} id={id} err={err}" : "reg parse-error",
                2 => BroadcastProtocol.TryParseRealtime(data, out float sess, out bool rp, out float rpt, out float rem)
                    ? $"sessT={sess:0} replay={rp}" + (rp ? $" replayT={rpt:0} rem={rem:0}" : "") : "realtime parse-error",
                3 => BroadcastProtocol.TryParseCar(data, out int car, out int kmh, out int gear, out int laps, out int? cur)
                    ? $"car={car} kmh={kmh} gear={gear} laps={laps} curLapMs={cur}" : "car parse-error",
                4 or 5 or 6 => "(entry/track, skipped)",
                _ => "(unknown)",
            };
        }
        catch (Exception ex) { detail = "parse-error " + ex.GetType().Name; }
        lock (_lock)
        {
            _counts[type] = _counts.TryGetValue(type, out var c) ? c + 1 : 1;
            if (type is 1 or 2 or 3)
                _lastSummary = $"t={DateTime.Now:HH:mm:ss} type={type} {detail} | counts: " +
                    string.Join(" ", _counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}x{kv.Value}"));
        }
        _log.WriteLine($"{DateTime.UtcNow:O},{type},{data.Length},{Escape(detail)}");
    }

    private static string Escape(string s) => s.Replace(',', ';');

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Close(); } catch { }
        try { _log.Dispose(); } catch { }
        _cts.Dispose();
    }
}
