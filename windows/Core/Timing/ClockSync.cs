namespace PhoneWebcam.Core.Timing;

/// <summary>
/// NTP-style estimate of (remoteClock − localClock) from PING/PONG samples.
/// Uses the lowest-RTT sample in a sliding window, because that one has the least queuing error.
/// See docs/protocol.md §6.
/// </summary>
public sealed class ClockSync(long windowUs = 10_000_000)
{
    private readonly record struct Sample(long RttUs, long OffsetUs, long AtUs);

    private readonly Queue<Sample> _samples = new();
    private readonly object _gate = new();

    public long? OffsetUs { get; private set; }

    public long? RttUs { get; private set; }

    /// <param name="t0">Local time the PING was sent.</param>
    /// <param name="t1">Remote time the PONG was produced.</param>
    /// <param name="t2">Local time the PONG was received.</param>
    public void AddSample(long t0, long t1, long t2)
    {
        var rtt = t2 - t0;
        if (rtt < 0)
            return;
        var offset = t1 - (t0 + rtt / 2);

        lock (_gate)
        {
            _samples.Enqueue(new Sample(rtt, offset, t2));
            while (_samples.Count > 1 && t2 - _samples.Peek().AtUs > windowUs)
                _samples.Dequeue();

            var best = _samples.MinBy(s => s.RttUs);
            OffsetUs = best.OffsetUs;
            RttUs = rtt;
        }
    }

    /// <summary>Converts a remote timestamp to local time, or null before the first sample.</summary>
    public long? ToLocal(long remoteUs) => OffsetUs is { } offset ? remoteUs - offset : null;
}
