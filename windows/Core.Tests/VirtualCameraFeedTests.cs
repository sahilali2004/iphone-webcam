using System.Buffers.Binary;
using System.IO.Pipes;
using PhoneWebcam.Core.Protocol;
using PhoneWebcam.Core.Video;
using PhoneWebcam.Core.VirtualCamera;
using Xunit;

namespace PhoneWebcam.Core.Tests;

public class ColorConvertTests
{
    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 255;
        }
        return px;
    }

    [Theory]
    // BT.709 limited range reference values (same integer math as the virtual camera DLL).
    [InlineData(255, 255, 255, 235, 128, 128)]
    [InlineData(0, 0, 0, 16, 128, 128)]
    [InlineData(255, 0, 0, 63, 102, 240)]
    [InlineData(0, 255, 0, 172, 42, 26)]
    [InlineData(0, 0, 255, 32, 240, 118)]
    public void SolidColours(byte r, byte g, byte b, byte y, byte u, byte v)
    {
        const int w = 8, h = 4;
        var nv12 = new byte[w * h * 3 / 2];
        ColorConvert.Bgr32ToNv12(Solid(w, h, r, g, b), w, h, w * 4, nv12);
        Assert.All(nv12.AsSpan(0, w * h).ToArray(), value => Assert.Equal(y, value));
        for (var i = w * h; i < nv12.Length; i += 2)
        {
            Assert.Equal(u, nv12[i]);
            Assert.Equal(v, nv12[i + 1]);
        }
    }

    [Fact]
    public void RespectsSourceStride()
    {
        const int w = 4, h = 2, stride = 32;
        var src = new byte[stride * h];
        for (var row = 0; row < h; row++)
            for (var x = 0; x < w; x++)
                src[row * stride + x * 4 + 1] = 255; // green, padding left as zeros
        var nv12 = new byte[w * h * 3 / 2];
        ColorConvert.Bgr32ToNv12(src, w, h, stride, nv12);
        Assert.All(nv12.AsSpan(0, w * h).ToArray(), value => Assert.Equal(172, value));
    }

    [Fact]
    public void RejectsOddSizes()
    {
        Assert.Throws<ArgumentException>(() => ColorConvert.Bgr32ToNv12(new byte[12], 3, 1, 12, new byte[16]));
    }
}

public class FramePublisherTests
{
    [Fact]
    public async Task WritesHeaderAndNv12FrameToPipe()
    {
        var name = "PhoneWebcam.Test." + Guid.NewGuid().ToString("N");
        using var publisher = new FramePublisher(name);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.In);
        await client.ConnectAsync(5000);
        for (var i = 0; i < 50 && !publisher.IsCameraReading; i++) await Task.Delay(20);
        Assert.True(publisher.IsCameraReading);

        const int w = 16, h = 8;
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4) pixels[i + 2] = 255; // red
        using var frame = new VideoFrame(PixelLayout.Bgr32, w, h, w * 4, pixels, default(VideoFrameHeader), 0);
        publisher.Publish(frame);

        var header = new byte[FramePublisher.HeaderSize];
        await client.ReadExactlyAsync(header);
        Assert.Equal(FramePublisher.Magic, BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Equal((uint)w, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal((uint)h, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
        Assert.Equal(FramePublisher.FormatNv12, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)));
        Assert.Equal((uint)(w * h * 3 / 2), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)));
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24)));

        var payload = new byte[w * h * 3 / 2];
        await client.ReadExactlyAsync(payload);
        Assert.Equal(63, payload[0]);       // red luma
        Assert.Equal(240, payload[w * h + 1]); // red V
        Assert.Equal(1, publisher.SentFrames);
    }

    [Fact]
    public void IgnoresFramesWhenNoCameraIsReading()
    {
        using var publisher = new FramePublisher("PhoneWebcam.Test." + Guid.NewGuid().ToString("N"));
        using var frame = new VideoFrame(PixelLayout.Bgr32, 4, 2, 16, new byte[32], default(VideoFrameHeader), 0);
        publisher.Publish(frame);
        Assert.Equal(0, publisher.SentFrames);
        Assert.False(publisher.IsCameraReading);
    }
}
