using System.Buffers;
using PhoneWebcam.Core.Protocol;

namespace PhoneWebcam.Core.Video;

/// <summary>A compressed frame as received. Owns the underlying message buffer.</summary>
public sealed class EncodedFrame(VideoFrameHeader header, Message message, long receivedUs) : IDisposable
{
    public VideoFrameHeader Header { get; } = header;

    public ReadOnlyMemory<byte> Data => message.Payload[VideoFrameHeader.Size..];

    /// <summary>Local monotonic time the frame finished arriving.</summary>
    public long ReceivedUs { get; } = receivedUs;

    public void Dispose() => message.Dispose();
}

public enum PixelLayout
{
    /// <summary>32-bit B,G,R,X per pixel (alpha ignored).</summary>
    Bgr32,

    /// <summary>Full-resolution Y plane followed by interleaved half-resolution UV plane.</summary>
    Nv12,
}

/// <summary>A decoded frame. Pixel buffer is pooled; dispose when done.</summary>
public sealed class VideoFrame : IDisposable
{
    private byte[]? _pixels;

    public VideoFrame(PixelLayout layout, int width, int height, int stride, byte[] pixels, VideoFrameHeader source, long decodeUs)
    {
        Layout = layout;
        Width = width;
        Height = height;
        Stride = stride;
        _pixels = pixels;
        Source = source;
        DecodeUs = decodeUs;
    }

    public PixelLayout Layout { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public VideoFrameHeader Source { get; }

    public long DecodeUs { get; }

    public int ByteLength => Layout == PixelLayout.Nv12 ? Stride * Height * 3 / 2 : Stride * Height;

    public ReadOnlySpan<byte> Pixels =>
        (_pixels ?? throw new ObjectDisposedException(nameof(VideoFrame))).AsSpan(0, ByteLength);

    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null)
            ArrayPool<byte>.Shared.Return(pixels);
    }
}
