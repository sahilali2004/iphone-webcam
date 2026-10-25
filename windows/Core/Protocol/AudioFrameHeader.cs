using System.Buffers.Binary;

namespace PhoneWebcam.Core.Protocol;

public enum AudioCodec : byte
{
    /// <summary>Signed 16-bit little-endian PCM, interleaved.</summary>
    PcmS16Le = 1,
}

/// <summary>20-byte header at the start of an AUDIO_FRAME payload. See docs/protocol.md §5a.</summary>
public readonly record struct AudioFrameHeader(AudioCodec Codec, byte Channels, uint SampleRate, uint Sequence, ulong PtsUs)
{
    public const int Size = 20;

    public void Write(Span<byte> dest)
    {
        dest[0] = (byte)Codec;
        dest[1] = Channels;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..], Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[12..], PtsUs);
    }

    public static AudioFrameHeader Parse(ReadOnlySpan<byte> src)
    {
        if (src.Length < Size)
            throw new ProtocolException("Audio frame header truncated");
        return new AudioFrameHeader(
            (AudioCodec)src[0],
            src[1],
            BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(src[12..]));
    }
}
