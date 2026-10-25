using IPhoneWebcam.Core.Diagnostics;
using IPhoneWebcam.Core.Protocol;

namespace IPhoneWebcam.Core.Video;

/// <summary>
/// Latest-frame-wins decode stage. <see cref="Submit"/> never blocks: if the decoder is busy,
/// an older pending frame is discarded and counted. Decoding runs on one dedicated STA thread
/// (WPF imaging objects are thread-affine).
/// </summary>
public sealed class FramePipeline : IDisposable
{
    private readonly Dictionary<VideoCodec, IVideoDecoder> _decoders;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private EncodedFrame? _pending;
    private long _dropped;
    private long _failed;

    public FramePipeline(params IVideoDecoder[] decoders)
    {
        _decoders = decoders.ToDictionary(d => d.Codec);
        _thread = new Thread(Run) { IsBackground = true, Name = "FramePipeline.Decode" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Raised on the decode thread. The handler owns the frame and must dispose it.</summary>
    public event Action<VideoFrame>? FrameDecoded;

    public long DroppedFrames => Interlocked.Read(ref _dropped);

    public long FailedFrames => Interlocked.Read(ref _failed);

    /// <summary>Takes ownership of <paramref name="frame"/>.</summary>
    public void Submit(EncodedFrame frame)
    {
        if (_stopping)
        {
            frame.Dispose();
            return;
        }
        var previous = Interlocked.Exchange(ref _pending, frame);
        if (previous is not null)
        {
            previous.Dispose();
            Interlocked.Increment(ref _dropped);
        }
        _signal.Set();
    }

    private void Run()
    {
        while (true)
        {
            _signal.WaitOne();
            if (_stopping)
                break;

            var frame = Interlocked.Exchange(ref _pending, null);
            if (frame is null)
                continue;

            using (frame)
            {
                if (!_decoders.TryGetValue(frame.Header.Codec, out var decoder))
                {
                    if (Interlocked.Increment(ref _failed) == 1)
                        Log.Error($"No decoder for codec {frame.Header.Codec}");
                    continue;
                }

                VideoFrame decoded;
                try
                {
                    decoded = decoder.Decode(frame);
                }
                catch (Exception ex)
                {
                    // A single corrupt JPEG must not kill the stream.
                    if (Interlocked.Increment(ref _failed) % 100 == 1)
                        Log.Error($"Decode failed (frame {frame.Header.FrameId}): {ex.Message}");
                    continue;
                }

                var handler = FrameDecoded;
                if (handler is null)
                    decoded.Dispose();
                else
                    handler(decoded);
            }
        }

        Interlocked.Exchange(ref _pending, null)?.Dispose();
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        foreach (var decoder in _decoders.Values)
            decoder.Dispose();
    }
}
