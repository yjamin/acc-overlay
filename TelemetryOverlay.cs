using GameOverlay.Drawing;
using GameOverlay.Windows;

namespace AccOverlay;

// Click-through Direct2D overlay. Works when ACC runs in borderless windowed.
// True exclusive fullscreen bypasses DWM and cannot be covered by an external window.
public sealed class TelemetryOverlay : IDisposable
{
    private readonly GraphicsWindow _window;
    private readonly Func<TelemetrySnapshot> _getData;
    private readonly Dictionary<string, SolidBrush> _brushes = new();
    private readonly Dictionary<string, Font> _fonts = new();
    private bool _disposed;

    // Scrolling history for the chart (render thread only). ~4s at 60fps.
    private readonly Queue<float> _thrHist = new();
    private readonly Queue<float> _brkHist = new();
    private readonly Queue<float> _strHist = new();
    private readonly Queue<bool> _tcHist = new();
    private readonly Queue<bool> _absHist = new();
    private const int HistoryCapacity = 240;
    private int _lastPacketId = -1;

    // Layout: history chart on the left + 3 vertical bars on the right.
    public TelemetryOverlay(Func<TelemetrySnapshot> getData, int x = 860, int y = 680, int width = 600, int height = 250)
    {
        _getData = getData;
        var gfx = new Graphics
        {
            MeasureFPS = true,
            PerPrimitiveAntiAliasing = true,
            TextAntiAliasing = true,
            VSync = true,
        };
        _window = new GraphicsWindow(gfx)
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            FPS = 60,
            IsTopmost = true,
            IsVisible = true,
        };
        _window.SetupGraphics += OnSetup;
        _window.DestroyGraphics += OnDestroy;
        _window.DrawGraphics += OnDraw;
    }

    public void Run()
    {
        _window.Create();
        _window.Join(); // blocks; call from main thread or background
    }

    public int OverlayWidth => _window.Width;
    public int OverlayHeight => _window.Height;

    // Called from tracker loop; GraphicsWindow applies move/resize on its render thread.
    public void MoveResize(int x, int y, int w, int h)
    {
        try
        {
            _window.X = x;
            _window.Y = y;
            if (_window.Width != w || _window.Height != h)
            {
                _window.Width = w;
                _window.Height = h;
            }
        }
        catch { }
    }

    private void OnSetup(object? sender, SetupGraphicsEventArgs e)
    {
        var gfx = e.Graphics;
        if (e.RecreateResources)
        {
            foreach (var b in _brushes.Values) b.Dispose();
            _brushes.Clear();
        }
        _brushes["bg"] = gfx.CreateSolidBrush(10, 12, 16, 0.78f);
        _brushes["transparent"] = gfx.CreateSolidBrush(0, 0, 0, 0f);
        _brushes["track"] = gfx.CreateSolidBrush(45, 55, 68, 0.95f);
        _brushes["throttle"] = gfx.CreateSolidBrush(34, 197, 94);   // green
        _brushes["brake"] = gfx.CreateSolidBrush(239, 68, 68);       // red
        _brushes["tc"] = gfx.CreateSolidBrush(59, 130, 246);         // blue: TC cutting
        _brushes["tcDim"] = gfx.CreateSolidBrush(59, 130, 246, 0.28f); // TC lamp off
        _brushes["absActive"] = gfx.CreateSolidBrush(250, 204, 21);  // yellow: ABS working
        _brushes["absDim"] = gfx.CreateSolidBrush(250, 204, 21, 0.28f); // ABS lamp off
        _brushes["steer"] = gfx.CreateSolidBrush(156, 163, 175);     // grey
        _brushes["white"] = gfx.CreateSolidBrush(255, 255, 255);
        _brushes["gray"] = gfx.CreateSolidBrush(148, 163, 184);
        _brushes["red"] = gfx.CreateSolidBrush(248, 113, 113);
        _brushes["outline"] = gfx.CreateSolidBrush(71, 85, 105);

        if (!e.RecreateResources)
        {
            _fonts["small"] = gfx.CreateFont("Consolas", 13);
            _fonts["big"] = gfx.CreateFont("Consolas", 26, true);
        }
    }

    private void OnDestroy(object? sender, DestroyGraphicsEventArgs e)
    {
        foreach (var b in _brushes.Values) b.Dispose();
        _brushes.Clear();
        foreach (var f in _fonts.Values) f.Dispose();
        _fonts.Clear();
    }

    private void OnDraw(object? sender, DrawGraphicsEventArgs e)
    {
        var gfx = e.Graphics;
        var snap = _getData();
        gfx.ClearScene(_brushes["transparent"]);

        const float pad = 12f;
        float w = gfx.Width, h = gfx.Height;

        gfx.FillRoundedRectangle(_brushes["bg"], pad / 2, pad / 2, w - pad / 2, h - pad / 2, 10);
        gfx.DrawRoundedRectangle(_brushes["outline"], pad / 2, pad / 2, w - pad / 2, h - pad / 2, 10, 1.5f);

        float cx = pad + 8, cy = pad + 8;
        float contentW = w - 2 * (pad + 8);

        // Header
        string status = snap.Connected ? snap.Status switch
        {
            AccStatus.Replay => "REPLAY",
            AccStatus.Pause => "PAUSED",
            AccStatus.Off => "MENUS",
            AccStatus.Live => "LIVE",
            _ => "LIVE",
        } : "NO ACC";
        var statusBrush = !snap.Connected ? _brushes["red"] : snap.Status switch
        {
            AccStatus.Replay => _brushes["tc"],       // blue: replayed inputs
            AccStatus.Pause => _brushes["absActive"], // yellow: frozen frame
            AccStatus.Live => _brushes["throttle"],
            _ => _brushes["gray"],
        };
        gfx.DrawText(_fonts["small"], statusBrush, cx, cy, $"[{status}] {gfx.FPS}fps");
        cy += 20;

        // Gear / speed line + TC/ABS lamps (lamps stay dim when inactive).
        string gear = AccReader.GearLabel(snap.Gear);
        gfx.DrawText(_fonts["small"], _brushes["white"], cx, cy, $"G{gear} {snap.SpeedKmh,3:0}km/h");
        gfx.DrawText(_fonts["small"], snap.TcActive ? _brushes["tc"] : _brushes["tcDim"], cx + 130, cy, "TC");
        gfx.DrawText(_fonts["small"], snap.AbsActive ? _brushes["absActive"] : _brushes["absDim"], cx + 155, cy, "ABS");
        cy += 20;

        // Bar + chart area: history chart on the left, 3 vertical bars on the right.
        float gap = 12f;
        float labelH = 20f;
        float areaTop = cy;
        float areaH = h - (cy - (pad + 8)) - labelH - pad - 8;
        if (areaH < 40) areaH = 40;

        PushHistoryFiltered(snap);

        float chartW = (contentW - gap) * 0.80f;
        if (chartW < 80) chartW = 80;
        float barsX = cx + chartW + gap;
        float barsW = contentW - chartW - gap;
        float barGap = 6f;
        float barW = (barsW - 2 * barGap) / 3f;
        if (barW < 16) // overlay forced very narrow: shrink chart to keep bars usable
        {
            barW = 16;
            barsW = 3 * barW + 2 * barGap;
            chartW = contentW - barsW - gap;
            barsX = cx + chartW + gap;
        }

        DrawHistoryChart(gfx, cx, areaTop, chartW, areaH);

        // Pedals keep green/red fill; TC/ABS recolors the fill itself (see DrawPedalBar).
        DrawPedalBar(gfx, barsX, areaTop, barW, areaH, Math.Clamp(snap.Throttle, 0, 1), _brushes["throttle"], snap.TcActive, _brushes["tc"], "THR", "TC");
        DrawPedalBar(gfx, barsX + barW + barGap, areaTop, barW, areaH, Math.Clamp(snap.Brake, 0, 1), _brushes["brake"], snap.AbsActive, _brushes["absActive"], "BRK", "ABS");
        DrawSteerBar(gfx, barsX + 2 * (barW + barGap), areaTop, barW, areaH, SteerNorm(snap.SteerAngle), _brushes["steer"], "STR");
    }

    // Steering normalized to -1 (full left) .. +1 (full right). 0 = centered.
    // Handles both degrees (|angle| > 1.5) and normalized -1..1 input.
    private static float SteerNorm(float steerAngle)
    {
        const float MaxSteerDeg = 450f; // tune to car if full lock never reaches 0/100%
        return Math.Abs(steerAngle) <= 1.5f
            ? Math.Clamp(steerAngle, -1f, 1f)
            : Math.Clamp(steerAngle / MaxSteerDeg, -1f, 1f);
    }

    private void PushHistoryFiltered(TelemetrySnapshot snap)
    {
        // Physics packetId freezes when paused (live or replay paused) and in
        // menus. Skipping duplicates freezes the chart instead of scrolling a
        // flat line; replays still scroll normally because packetId advances.
        if (snap.Connected)
        {
            if (snap.PacketId == _lastPacketId) return;
            _lastPacketId = snap.PacketId;
        }
        else
        {
            _lastPacketId = -1;
        }
        PushHistory(snap);
    }

    private void PushHistory(TelemetrySnapshot snap)
    {
        _thrHist.Enqueue(Math.Clamp(snap.Throttle, 0, 1));
        _brkHist.Enqueue(Math.Clamp(snap.Brake, 0, 1));
        _strHist.Enqueue(SteerNorm(snap.SteerAngle));
        _tcHist.Enqueue(snap.TcActive);
        _absHist.Enqueue(snap.AbsActive);
        while (_thrHist.Count > HistoryCapacity) _thrHist.Dequeue();
        while (_brkHist.Count > HistoryCapacity) _brkHist.Dequeue();
        while (_strHist.Count > HistoryCapacity) _strHist.Dequeue();
        while (_tcHist.Count > HistoryCapacity) _tcHist.Dequeue();
        while (_absHist.Count > HistoryCapacity) _absHist.Dequeue();
    }

    // Scrolling time chart: gas/brake rise from the bottom (0..1),
    // steering oscillates around the center (-1..+1). Newest sample on the right.
    // NOTE: GameOverlay.Net takes (left, top, right, bottom), not (x, y, w, h).
    private void DrawHistoryChart(Graphics gfx, float x, float yTop, float chartW, float chartH)
    {
        if (chartW < 10) return;
        float left = x, top = yTop, right = x + chartW, bottom = yTop + chartH;
        float midY = top + chartH / 2f;
        gfx.FillRectangle(_brushes["track"], left, top, right, bottom);

        // Grid: 25%/75% faint, center = steering 0.
        gfx.DrawLine(_brushes["outline"], left, top + chartH * 0.25f, right, top + chartH * 0.25f, 1f);
        gfx.DrawLine(_brushes["outline"], left, top + chartH * 0.75f, right, top + chartH * 0.75f, 1f);
        gfx.DrawLine(_brushes["white"], left, midY, right, midY, 1f);

        int n = Math.Min(_thrHist.Count, Math.Min(_brkHist.Count, Math.Min(_strHist.Count, Math.Min(_tcHist.Count, _absHist.Count))));
        if (n >= 2)
        {
            float dx = chartW / (HistoryCapacity - 1);
            float startX = right - (n - 1) * dx;
            var thr = _thrHist.ToArray();
            var brk = _brkHist.ToArray();
            var str = _strHist.ToArray();
            var tc = _tcHist.ToArray();
            var abs = _absHist.ToArray();

            for (int i = 1; i < n; i++)
            {
                float x0 = startX + (i - 1) * dx;
                float x1 = startX + i * dx;
                // Pedals: 0 = bottom, 1 = top.
                float ty0 = bottom - thr[i - 1] * chartH;
                float ty1 = bottom - thr[i] * chartH;
                float by0 = bottom - brk[i - 1] * chartH;
                float by1 = bottom - brk[i] * chartH;
                // Steering: -1 = bottom, 0 = middle, +1 = top.
                float sy0 = midY - str[i - 1] * (chartH / 2f);
                float sy1 = midY - str[i] * (chartH / 2f);

                gfx.DrawLine(_brushes["steer"], x0, sy0, x1, sy1, 1.5f);
                gfx.DrawLine(_brushes["throttle"], x0, ty0, x1, ty1, 2f);
                gfx.DrawLine(_brushes["brake"], x0, by0, x1, by1, 2f);
            }

            // TC/ABS markers glued to their own curve: blue dots on the throttle
            // line, yellow dots on the brake line. Dots (not recolored segments) so
            // the color can never float off the curve on steep edges.
            // Skipped near 0 pedal so cuts don't paint on the baseline.
            const float dot = 2f;
            for (int i = 0; i < n; i++)
            {
                float xs = startX + i * dx;
                if (tc[i] && thr[i] > 0.05f)
                {
                    float y = bottom - thr[i] * chartH;
                    gfx.FillRectangle(_brushes["tc"], xs - dot, y - dot, xs + dot, y + dot);
                }
                if (abs[i] && brk[i] > 0.05f)
                {
                    float y = bottom - brk[i] * chartH;
                    gfx.FillRectangle(_brushes["absActive"], xs - dot, y - dot, xs + dot, y + dot);
                }
            }
        }
        gfx.DrawRectangle(_brushes["outline"], left, top, right, bottom, 1f);

        // Color key below the chart (matches the bar colors).
        gfx.DrawText(_fonts["small"], _brushes["throttle"], left, bottom + 2, "THR");
        gfx.DrawText(_fonts["small"], _brushes["brake"], left + 36, bottom + 2, "BRK");
        gfx.DrawText(_fonts["small"], _brushes["steer"], left + 72, bottom + 2, "STR");
    }

    // Pedals fill from the bottom up: 0 = empty, 1 = full.
    // TC/ABS recolors the fill itself so all paint stays inside the bar bounds.
    // (A thicker colored outline straddles the rect edge and bleeds outside.)
    // NOTE: GameOverlay.Net takes (left, top, right, bottom), not (x, y, w, h).
    private void DrawPedalBar(Graphics gfx, float x, float yTop, float barW, float barH, float fill01, IBrush fill, bool active, IBrush activeBrush, string label, string activeLabel)
    {
        fill01 = Math.Clamp(fill01, 0, 1);
        float left = x, top = yTop, right = x + barW, bottom = yTop + barH;
        gfx.FillRectangle(_brushes["track"], left, top, right, bottom);
        float filledH = barH * fill01;
        if (filledH > 0.5f)
            gfx.FillRectangle(active ? activeBrush : fill, left, bottom - filledH, right, bottom);
        gfx.DrawRectangle(_brushes["outline"], left, top, right, bottom, 1f);

        if (active)
            gfx.DrawText(_fonts["small"], activeBrush, left, bottom + 2, activeLabel);
        else
            gfx.DrawText(_fonts["small"], _brushes["gray"], left, bottom + 2, label);
    }

    // Steering fills from the center out: right (+) grows up, left (-) grows down.
    // 0 = empty (just the center mark), +/-1 = half-bar filled above/below center.
    private void DrawSteerBar(Graphics gfx, float x, float yTop, float barW, float barH, float steerNorm, IBrush fill, string label)
    {
        steerNorm = Math.Clamp(steerNorm, -1f, 1f);
        float left = x, top = yTop, right = x + barW, bottom = yTop + barH;
        float midY = top + barH / 2f;
        gfx.FillRectangle(_brushes["track"], left, top, right, bottom);

        float extent = barH / 2f * Math.Abs(steerNorm);
        if (extent > 0.5f)
        {
            if (steerNorm > 0)
                gfx.FillRectangle(fill, left, midY - extent, right, midY);
            else
                gfx.FillRectangle(fill, left, midY, right, midY + extent);
        }
        gfx.DrawRectangle(_brushes["outline"], left, top, right, bottom, 1f);

        // center mark = 0 deg
        gfx.DrawLine(_brushes["white"], left, midY, right, midY, 1.5f);

        gfx.DrawText(_fonts["small"], _brushes["gray"], left, bottom + 2, label);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Dispose();
    }
}
