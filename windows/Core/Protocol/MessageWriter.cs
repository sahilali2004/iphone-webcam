using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace IPhoneWebcam.Core.Protocol;

/// <summary>
/// Writes framed messages. Thread-safe: each message is written atomically, so control
/// messages from any thread can interleave safely with video frames.
/// </summary>
public sealed class MessageWriter(Stream stream)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private uint _seq;

    public async Task WriteAsync(
        MessageType type,
        ReadOnlyMemory<byte> part1,
        ReadOnlyMemory<byte> part2 = default,
        ushort flags = 0,
        CancellationToken ct = default)
    {
        var length = part1.Length + part2.Length;
        var total = MessageHeader.Size + length;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                new MessageHeader(type, flags, (uint)length, _seq++).Write(buffer);
                part1.Span.CopyTo(buffer.AsSpan(MessageHeader.Size));
                part2.Span.CopyTo(buffer.AsSpan(MessageHeader.Size + part1.Length));
                await stream.WriteAsync(buffer.AsMemory(0, total), ct).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Task WriteControlAsync<T>(MessageType type, T message, CancellationToken ct = default) =>
        WriteAsync(type, JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options), ct: ct);

    public Task WritePingAsync(long senderTimeUs, CancellationToken ct = default)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, senderTimeUs);
        return WriteAsync(MessageType.Ping, payload, ct: ct);
    }

    public Task WritePongAsync(long echoedTimeUs, long responderTimeUs, CancellationToken ct = default)
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(payload, echoedTimeUs);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), responderTimeUs);
        return WriteAsync(MessageType.Pong, payload, ct: ct);
    }
}
