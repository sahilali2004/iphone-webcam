using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneWebcam.Core.Diagnostics;
using IPhoneWebcam.Core.Timing;

namespace IPhoneWebcam.TestSender;

/// <param name="Sequence">Capture counter; gaps mean frames the source produced but nobody consumed.</param>
/// <param name="CapturedUs">Monotonic capture time (µs).</param>
public sealed record JpegImage(byte[] Data, int Width, int Height, uint Sequence = 0, long CapturedUs = 0);

/// <summary>
/// Push-style frame source, like a camera callback: <see cref="NextAsync"/> completes when the next
/// frame is captured. Sources own their pacing and sequence numbering.
/// </summary>
public interface IJpegSource : IDisposable
{
    ValueTask<JpegImage> NextAsync(CancellationToken ct);
}

/// <summary>
/// Pre-renders a looping set of test-pattern frames (WPF on an STA thread), then serves them with no
/// per-frame cost, so the sender's timing isn't distorted by encoding.
/// </summary>
public sealed class SyntheticJpegSource : IJpegSource
{
    private readonly JpegImage[] _frames;
    private readonly long _periodUs;
    private long _nextUs;
    private uint _sequence;

    public SyntheticJpegSource(int width, int height, int fps, double quality, int frameCount = 60)
    {
        _periodUs = 1_000_000L / fps;
        JpegImage[]? frames = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                frames = Enumerable.Range(0, frameCount).Select(i => Render(width, height, fps, quality, i, frameCount)).ToArray();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new InvalidOperationException("Failed to render test pattern", error);
        _frames = frames!;
        _nextUs = MonoClock.NowUs;
    }

    /// <summary>Deadline-paced; if the consumer falls behind, missed periods are skipped (sequence jumps).</summary>
    public async ValueTask<JpegImage> NextAsync(CancellationToken ct)
    {
        var wait = _nextUs - MonoClock.NowUs;
        if (wait > 1000)
            await Task.Delay(TimeSpan.FromMicroseconds(wait), ct).ConfigureAwait(false);

        var now = MonoClock.NowUs;
        if (now - _nextUs > _periodUs)
        {
            var missed = (now - _nextUs) / _periodUs;
            _sequence += (uint)missed;
            _nextUs += missed * _periodUs;
        }
        _nextUs += _periodUs;

        var sequence = _sequence++;
        return _frames[sequence % _frames.Length] with { Sequence = sequence, CapturedUs = now };
    }

    public void Dispose()
    {
    }

    private static JpegImage Render(int width, int height, int fps, double quality, int index, int count)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bars = new[] { Colors.White, Colors.Yellow, Colors.Cyan, Colors.Lime, Colors.Magenta, Colors.Red, Colors.Blue, Colors.Black };
            var barWidth = width / (double)bars.Length;
            for (var i = 0; i < bars.Length; i++)
                dc.DrawRectangle(new SolidColorBrush(bars[i]), null, new Rect(i * barWidth, 0, barWidth + 1, height * 0.6));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 28)), null, new Rect(0, height * 0.6, width, height * 0.4));

            // Sweeping marker makes dropped/repeated frames visible by eye.
            var x = (width - 40) * index / (double)(count - 1);
            dc.DrawRectangle(Brushes.OrangeRed, null, new Rect(x, height * 0.6, 40, height * 0.08));

            var typeface = new Typeface("Segoe UI");
            var dpi = 1.0;
            var title = new FormattedText("TEST SENDER", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, height * 0.08, Brushes.White, dpi);
            var info = new FormattedText($"{width}×{height} · {fps} FPS · frame {index:00}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, height * 0.05, Brushes.LightGray, dpi);
            dc.DrawText(title, new Point(width * 0.05, height * 0.7));
            dc.DrawText(info, new Point(width * 0.05, height * 0.82));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp((int)(quality * 100), 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return new JpegImage(ms.ToArray(), width, height);
    }
}

/// <summary>
/// Real-time MJPEG from ffmpeg's lavfi testsrc2 (needs ffmpeg on PATH). Realistic frame sizes and content.
/// Splits the concatenated JPEG stream on SOI/EOI markers (0xFF bytes in entropy data are stuffed, so EOI is unambiguous).
/// </summary>
public sealed class FfmpegJpegSource : IJpegSource
{
    private readonly Process _process;
    private readonly Thread _reader;
    private readonly int _width;
    private readonly int _height;
    private readonly Channel<JpegImage> _frames = Channel.CreateBounded<JpegImage>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private uint _sequence;

    public FfmpegJpegSource(int width, int height, int fps, double quality)
    {
        _width = width;
        _height = height;
        // ffmpeg -q:v: 2 (best) .. 31 (worst)
        var q = Math.Clamp((int)Math.Round(31 - quality * 29), 2, 31);
        _process = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-hide_banner -loglevel error -re -f lavfi -i testsrc2=size={width}x{height}:rate={fps} -pix_fmt yuvj420p -q:v {q} -f mjpeg pipe:1")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start ffmpeg");
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ffmpeg reader" };
        _reader.Start();
    }

    public ValueTask<JpegImage> NextAsync(CancellationToken ct) => _frames.Reader.ReadAsync(ct);

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
    }

    private void ReadLoop()
    {
        var stream = _process.StandardOutput.BaseStream;
        var frame = new MemoryStream();
        var inFrame = false;
        var prev = -1;
        int b;
        try
        {
            while ((b = stream.ReadByte()) != -1)
            {
                if (!inFrame)
                {
                    if (prev == 0xFF && b == 0xD8)
                    {
                        inFrame = true;
                        frame.SetLength(0);
                        frame.WriteByte(0xFF);
                        frame.WriteByte(0xD8);
                    }
                }
                else
                {
                    frame.WriteByte((byte)b);
                    if (prev == 0xFF && b == 0xD9)
                    {
                        _frames.Writer.TryWrite(new JpegImage(frame.ToArray(), _width, _height, _sequence++, MonoClock.NowUs));
                        inFrame = false;
                        b = -1;
                    }
                }
                prev = b;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        _frames.Writer.TryComplete(new EndOfStreamException("ffmpeg exited"));
        Log.Debug("ffmpeg source ended");
    }
}
