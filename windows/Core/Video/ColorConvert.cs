namespace PhoneWebcam.Core.Video;

/// <summary>Pixel-format conversion for the virtual camera feed.</summary>
public static class ColorConvert
{
    /// <summary>
    /// BGRX (4 bytes/pixel) → NV12, BT.709 limited range: the format and colorimetry the virtual camera
    /// advertises (MF_MT_YUV_MATRIX = BT.709, nominal range 16–235). Chroma is the 2×2 average.
    /// Width and height must be even. <paramref name="nv12"/> must hold width*height*3/2 bytes.
    /// </summary>
    public static unsafe void Bgr32ToNv12(ReadOnlySpan<byte> bgrx, int width, int height, int stride, Span<byte> nv12)
    {
        if ((width | height) % 2 != 0) throw new ArgumentException("Width and height must be even");
        if (nv12.Length < width * height * 3 / 2) throw new ArgumentException("NV12 buffer too small", nameof(nv12));
        if (bgrx.Length < stride * height) throw new ArgumentException("Source buffer too small", nameof(bgrx));

        fixed (byte* src = bgrx)
        fixed (byte* dst = nv12)
        {
            var s = (nint)src;
            var d = (nint)dst;
            // Rows are independent in pairs, so split across cores; 1080p drops from ~8 ms to ~2 ms.
            Parallel.For(0, height / 2, pair => ConvertRowPair((byte*)s, (byte*)d, width, height, stride, pair));
        }
    }

    private static unsafe void ConvertRowPair(byte* src, byte* dst, int width, int height, int stride, int pair)
    {
        var row0 = src + (pair * 2) * stride;
        var row1 = row0 + stride;
        var y0 = dst + (pair * 2) * width;
        var y1 = y0 + width;
        var uv = dst + width * height + pair * width;

        for (var x = 0; x < width; x += 2)
        {
            var p00 = row0 + x * 4;
            var p01 = p00 + 4;
            var p10 = row1 + x * 4;
            var p11 = p10 + 4;

            y0[x] = Luma(p00);
            y0[x + 1] = Luma(p01);
            y1[x] = Luma(p10);
            y1[x + 1] = Luma(p11);

            var b = (p00[0] + p01[0] + p10[0] + p11[0] + 2) >> 2;
            var g = (p00[1] + p01[1] + p10[1] + p11[1] + 2) >> 2;
            var r = (p00[2] + p01[2] + p10[2] + p11[2] + 2) >> 2;
            uv[x] = (byte)(128 + ((-26 * r - 86 * g + 112 * b + 128) >> 8));
            uv[x + 1] = (byte)(128 + ((112 * r - 102 * g - 10 * b + 128) >> 8));
        }
    }

    private static unsafe byte Luma(byte* p) => (byte)(16 + ((47 * p[2] + 157 * p[1] + 16 * p[0] + 128) >> 8));
}
