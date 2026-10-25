using IPhoneWebcam.Core.Protocol;
using Xunit;

namespace IPhoneWebcam.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Header_RoundTrips_AndHasDocumentedLayout()
    {
        var header = new MessageHeader(MessageType.StartStream, 0x0102, 1234, 0xA0B0C0D0);
        var buffer = new byte[MessageHeader.Size];
        header.Write(buffer);

        Assert.Equal(new byte[] { 0x49, 0x57, 1, 0x10, 0x02, 0x01, 0, 0, 0xD2, 0x04, 0, 0, 0xD0, 0xC0, 0xB0, 0xA0 }, buffer);
        Assert.Equal(header, MessageHeader.Parse(buffer));
    }

    [Fact]
    public void Header_RejectsBadMagic()
    {
        var buffer = new byte[MessageHeader.Size];
        new MessageHeader(MessageType.Ping, 0, 8, 0).Write(buffer);
        buffer[0] = 0x00;
        Assert.Throws<ProtocolException>(() => MessageHeader.Parse(buffer));
    }

    [Fact]
    public void Header_RejectsUnknownVersion()
    {
        var buffer = new byte[MessageHeader.Size];
        new MessageHeader(MessageType.Ping, 0, 8, 0).Write(buffer);
        buffer[2] = 2;
        Assert.Throws<ProtocolException>(() => MessageHeader.Parse(buffer));
    }

    [Fact]
    public void Header_EnforcesControlAndMediaLimits()
    {
        var buffer = new byte[MessageHeader.Size];
        new MessageHeader(MessageType.Hello, 0, MessageHeader.MaxControlLength + 1, 0).Write(buffer);
        Assert.Throws<ProtocolException>(() => MessageHeader.Parse(buffer));

        // The same size is fine for a video frame.
        new MessageHeader(MessageType.VideoFrame, 0, MessageHeader.MaxControlLength + 1, 0).Write(buffer);
        Assert.Equal(MessageType.VideoFrame, MessageHeader.Parse(buffer).Type);

        new MessageHeader(MessageType.VideoFrame, 0, MessageHeader.MaxMediaLength + 1, 0).Write(buffer);
        Assert.Throws<ProtocolException>(() => MessageHeader.Parse(buffer));
    }

    [Fact]
    public void VideoFrameHeader_RoundTrips()
    {
        var header = new VideoFrameHeader(VideoCodec.Mjpeg, VideoFrameFlags.Keyframe | VideoFrameFlags.Mirrored, 1, 1920, 1080, 42, 123_456_789_012);
        var buffer = new byte[VideoFrameHeader.Size];
        header.Write(buffer);
        Assert.Equal(header, VideoFrameHeader.Parse(buffer));
    }

    [Fact]
    public async Task WriterAndReader_PreserveOrderTypesAndPayloads()
    {
        var stream = new MemoryStream();
        var writer = new MessageWriter(stream);
        await writer.WriteControlAsync(MessageType.Hello, new Hello(1, "pc", "My PC", "0.1.0"));
        await writer.WritePingAsync(-5);
        await writer.WriteAsync(MessageType.VideoFrame, new byte[] { 1, 2 }, new byte[] { 3, 4, 5 });
        await writer.WritePongAsync(7, 9);

        stream.Position = 0;
        var reader = new MessageReader(stream);

        using (var hello = await reader.ReadAsync(default))
        {
            Assert.Equal(MessageType.Hello, hello.Header.Type);
            Assert.Equal(0u, hello.Header.Seq);
            Assert.Equal(new Hello(1, "pc", "My PC", "0.1.0"), ProtocolJson.Deserialize<Hello>(hello));
        }
        using (var ping = await reader.ReadAsync(default))
            Assert.Equal(-5, ProtocolJson.ReadPing(ping));
        using (var video = await reader.ReadAsync(default))
        {
            Assert.Equal(2u, video.Header.Seq);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, video.Payload.ToArray());
        }
        using (var pong = await reader.ReadAsync(default))
            Assert.Equal((7L, 9L), ProtocolJson.ReadPong(pong));

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync(default));
    }

    [Fact]
    public void ControlJson_UsesCamelCaseAndOmitsNulls()
    {
        var json = System.Text.Encoding.UTF8.GetString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new StreamState("streaming", Width: 1280), ProtocolJson.Options));
        Assert.Contains("\"state\":\"streaming\"", json);
        Assert.Contains("\"width\":1280", json);
        Assert.DoesNotContain("reason", json);
    }
}
