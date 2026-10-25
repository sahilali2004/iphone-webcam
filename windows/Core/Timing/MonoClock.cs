using System.Diagnostics;

namespace PhoneWebcam.Core.Timing;

/// <summary>Monotonic microsecond clock (QPC). Split division avoids overflow for long uptimes.</summary>
public static class MonoClock
{
    private static readonly long Frequency = Stopwatch.Frequency;

    public static long NowUs
    {
        get
        {
            var ticks = Stopwatch.GetTimestamp();
            return ticks / Frequency * 1_000_000 + ticks % Frequency * 1_000_000 / Frequency;
        }
    }
}
