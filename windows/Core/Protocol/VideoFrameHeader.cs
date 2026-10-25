using System.Buffers.Binary;

namespace PhoneWebcam.Core.Protocol;

public enum VideoCodec : byte
{
    Mjpeg = 1,
    H264 = 2,
    Hevc = 3,
}

[Flags]
public enum VideoFrameFlags : byte
{
    None = 0,
    Keyframe = 1 << 0,
    Mirrored = 1 << 1,
    Discontinuity = 1 << 2,
}

/// <summary>20-byte header at the start of a VIDEO_FRAME payload. See docs/protocol.md §5.</summary>
public readonly record struct VideoFrameHeader(
    VideoCodec Codec,
    VideoFrameFlags Flags,
    byte Rotation,
    ushort Width,
    ushort Height,
    uint FrameId,
    ulong PtsUs)
{
    public const int Size = 20;

    public void Write(Span<byte> dest)
    {
        dest[0] = (byte)Codec;
        dest[1] = (byte)Flags;
        dest[2] = Rotation;
        dest[3] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[4..], Width);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[6..], Height);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..], FrameId);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[12..], PtsUs);
    }

    public static VideoFrameHeader Parse(ReadOnlySpan<byte> src)
    {
        if (src.Length < Size)
            throw new ProtocolException("Video frame header truncated");
        return new VideoFrameHeader(
            (VideoCodec)src[0],
            (VideoFrameFlags)src[1],
            src[2],
            BinaryPrimitives.ReadUInt16LittleEndian(src[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(src[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(src[12..]));
    }
}
