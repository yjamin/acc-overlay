namespace AccOverlay;

// Shared monotonic millisecond clock (high resolution, process-wide).
public static class MonoClock
{
    public static double Ms => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}
