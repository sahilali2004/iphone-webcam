using System.Net;
using IPhoneWebcam.Core.Diagnostics;
using IPhoneWebcam.TestSender;

// Fake iPhone for Phase 3: speaks the real wire protocol so the Windows receiver can be tested without a phone.
//   IPhoneWebcam.TestSender [--port 47809] [--lan] [--ffmpeg]
//     --lan     listen on all interfaces (default: loopback only)
//     --ffmpeg  use ffmpeg testsrc2 (realistic MJPEG sizes) instead of the built-in pattern

var port = 47809;
var lan = false;
var ffmpeg = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            port = int.Parse(args[++i]);
            break;
        case "--lan":
            lan = true;
            break;
        case "--ffmpeg":
            ffmpeg = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument {args[i]}");
            return 2;
    }
}

Log.Written += entry => Console.WriteLine(entry);

using var server = new FakePhoneServer(new FakePhoneOptions
{
    Bind = lan ? IPAddress.Any : IPAddress.Loopback,
    Port = port,
    SourceFactory = ffmpeg
        ? (w, h, fps, q) => new FfmpegJpegSource(w, h, fps, q)
        : (w, h, fps, q) => new SyntheticJpegSource(w, h, fps, q),
});
server.PairingCodeShown += code => Console.WriteLine($"\n    PAIRING CODE:  {code[..3]} {code[3..]}\n");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
Console.WriteLine("Press Ctrl+C to stop.");
await server.RunAsync(cts.Token);
return 0;
