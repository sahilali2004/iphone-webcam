using PhoneWebcam.Core.Protocol;
using PhoneWebcam.Core.Timing;

namespace PhoneWebcam.Core.Session;

public sealed record StatsSnapshot(
    double ReceiveFps,
    double DecodeFps,
    double BitrateMbps,
    int Width,
    int Height,
    long DroppedOnPhone,
    long DroppedOnPc,
    double? LatencyMs,
    double? DecodeMs,
    double? RttMs);

/// <summary>Rolling counters for the dashboard. <see cref="Sample"/> returns rates since the previous call.</summary>
public sealed class StreamStats
{
    private readonly object _gate = new();
    private long _windowStartUs = MonoClock.NowUs;
    private int _frames;
    private int _decoded;
    private long _bytes;
    private long _latencySumUs;
    private int _latencyCount;
    private long _decodeSumUs;
    private uint? _lastFrameId;
    private long _droppedOnPhone;
    private int _width;
    private int _height;

    public long DroppedOnPc { get; set; }

    public double? RttMs { get; set; }

    public void OnFrameReceived(VideoFrameHeader header, int bytes, long? latencyUs)
    {
        lock (_gate)
        {
            _frames++;
            _bytes += bytes;
            _width = header.Width;
            _height = header.Height;
            if (latencyUs is { } latency)
            {
                _latencySumUs += latency;
                _latencyCount++;
            }

            var discontinuity = (header.Flags & VideoFrameFlags.Discontinuity) != 0;
            if (_lastFrameId is { } last && !discontinuity && header.FrameId > last + 1)
                _droppedOnPhone += header.FrameId - last - 1;
            _lastFrameId = header.FrameId;
        }
    }

    public void OnFrameDecoded(long decodeUs)
    {
        lock (_gate)
        {
            _decoded++;
            _decodeSumUs += decodeUs;
        }
    }

    public StatsSnapshot Sample()
    {
        lock (_gate)
        {
            var now = MonoClock.NowUs;
            var seconds = Math.Max(1e-3, (now - _windowStartUs) / 1e6);
            var snapshot = new StatsSnapshot(
                _frames / seconds,
                _decoded / seconds,
                _bytes * 8 / seconds / 1e6,
                _width,
                _height,
                _droppedOnPhone,
                DroppedOnPc,
                _latencyCount > 0 ? _latencySumUs / (double)_latencyCount / 1000 : null,
                _decoded > 0 ? _decodeSumUs / (double)_decoded / 1000 : null,
                RttMs);

            _windowStartUs = now;
            _frames = _decoded = _latencyCount = 0;
            _bytes = _latencySumUs = _decodeSumUs = 0;
            return snapshot;
        }
    }
}
