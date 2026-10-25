using System.Buffers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneWebcam.Core.Protocol;
using IPhoneWebcam.Core.Timing;

namespace IPhoneWebcam.Core.Video;

/// <summary>
/// JPEG → BGR32 via WIC (through WPF imaging). Simple and reliable for V1.
/// Later optimisation: WIC planar decode straight to NV12 to skip the RGB round-trip.
/// </summary>
public sealed class MjpegDecoder : IVideoDecoder
{
    public VideoCodec Codec => VideoCodec.Mjpeg;

    public unsafe VideoFrame Decode(EncodedFrame frame)
    {
        var start = MonoClock.NowUs;
        var data = frame.Data;

        BitmapSource source;
        fixed (byte* ptr = data.Span)
        {
            using var stream = new UnmanagedMemoryStream(ptr, data.Length);
            var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            source = decoder.Frames[0];
        }

        if (source.Format != PixelFormats.Bgr32 && source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = ArrayPool<byte>.Shared.Rent(stride * height);
        try
        {
            source.CopyPixels(pixels, stride, 0);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }

        return new VideoFrame(PixelLayout.Bgr32, width, height, stride, pixels, frame.Header, MonoClock.NowUs - start);
    }

    public void Dispose()
    {
    }
}
