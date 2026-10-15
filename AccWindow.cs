using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AccOverlay;

// Finds the ACC game window and returns its client area in screen coordinates.
public static class AccWindow
{
    private static readonly string[] ProcessCandidates =
    [
        "AC2-Win64-Shipping", // actual UE4 shipping binary
        "AC2",
        "ACC",
        "AssettoCorsaCompetizione",
    ];

    public static bool TryGetRect(out int x, out int y, out int w, out int h)
    {
        foreach (var name in ProcessCandidates)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var handle = p.MainWindowHandle;
                        if (handle == IntPtr.Zero || IsIconic(handle)) continue;
                        if (TryClientRect(handle, out x, out y, out w, out h) && w > 0 && h > 0)
                            return true;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
        }

        // Fallback: any top-level window with ACC in the title.
        var found = FindByTitle("Assetto Corsa Competizione");
        if (found != IntPtr.Zero && TryClientRect(found, out x, out y, out w, out h) && w > 0 && h > 0)
            return true;

        x = y = w = h = 0;
        return false;
    }

    private static bool TryClientRect(IntPtr hWnd, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        if (!GetClientRect(hWnd, out var r)) return false;
        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hWnd, ref origin)) return false;
        w = r.Right - r.Left;
        h = r.Bottom - r.Top;
        x = origin.X;
        y = origin.Y;
        return w > 0 && h > 0;
    }

    private static IntPtr FindByTitle(string part)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLength(hWnd);
            if (len is <= 0 or > 512) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (sb.ToString().Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                result = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
}
