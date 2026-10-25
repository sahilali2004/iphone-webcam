using System.Buffers;

namespace PhoneWebcam.Core.Protocol;

/// <summary>Reads framed messages from a stream. Not thread-safe: one reader loop per stream.</summary>
public sealed class MessageReader(Stream stream)
{
    private readonly byte[] _header = new byte[MessageHeader.Size];

    /// <exception cref="EndOfStreamException">Peer closed the connection.</exception>
    /// <exception cref="ProtocolException">Malformed header.</exception>
    public async ValueTask<Message> ReadAsync(CancellationToken ct)
    {
        await stream.ReadExactlyAsync(_header, ct).ConfigureAwait(false);
        var header = MessageHeader.Parse(_header);

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, (int)header.Length));
        try
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(0, (int)header.Length), ct).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        return new Message(header, buffer);
    }
}
