using System.Diagnostics;
using System.Net.Sockets;
using IPhoneWebcam.Core.Protocol;
using IPhoneWebcam.Core.Security;
using IPhoneWebcam.Core.Session;
using IPhoneWebcam.Core.Timing;
using IPhoneWebcam.Core.Transport;
using IPhoneWebcam.Core.Video;
using IPhoneWebcam.TestSender;
using Xunit;
using Xunit.Abstractions;

namespace IPhoneWebcam.Core.Tests;

/// <summary>Fake iPhone ⇄ PhoneSession ⇄ FramePipeline over real loopback TCP.</summary>
public sealed class EndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private readonly FakePhoneServer _server = new(new FakePhoneOptions { Port = 0 });
    private Task? _serverTask;

    private FakePhoneServer StartServer()
    {
        _serverTask = _server.RunAsync(_cts.Token);
        return _server;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server.Dispose();
    }

    private Task<Stream> ConnectAsync() =>
        new WifiTransport().ConnectAsync(new DeviceEndpoint(TransportKind.Wifi, "fake", "127.0.0.1", _server.Port), _cts.Token);

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task PairsThenStreamsAndDecodesAt30Fps(int width, int height)
    {
        StartServer();
        var codeShown = new TaskCompletionSource<string>();
        _server.PairingCodeShown += code => codeShown.TrySetResult(code);

        var store = new InMemoryPairingStore();
        await using var session = new PhoneSession(await ConnectAsync(), store, new SessionOptions { Stream = new("back", width, height, 30, "mjpeg", 0.7) });
        using var pipeline = new FramePipeline(new MjpegDecoder());
        session.FrameReceived += pipeline.Submit;
        session.PairingCodeRequired += () => _ = Task.Run(async () => await session.SubmitPairingCodeAsync(await codeShown.Task));

        const int target = 60;
        var timestamps = new List<long>();
        var sizes = new HashSet<(int, int)>();
        var done = new TaskCompletionSource();
        pipeline.FrameDecoded += frame =>
        {
            using (frame)
            {
                session.Stats.OnFrameDecoded(frame.DecodeUs);
                lock (timestamps)
                {
                    sizes.Add((frame.Width, frame.Height));
                    timestamps.Add(MonoClock.NowUs);
                    if (timestamps.Count == target)
                        done.TrySetResult();
                }
            }
        };

        var run = session.RunAsync(_cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var stats = session.Stats.Sample();

        var fps = (target - 1) / ((timestamps[^1] - timestamps[0]) / 1e6);
        output.WriteLine($"decoded fps={fps:F1} latency={stats.LatencyMs:F2}ms decode={stats.DecodeMs:F2}ms rtt={stats.RttMs:F2}ms bitrate={stats.BitrateMbps:F1}Mbps droppedPc={pipeline.DroppedFrames} droppedPhone={stats.DroppedOnPhone}");

        Assert.Equal([(width, height)], sizes);
        Assert.Equal(SessionState.Streaming, session.State);
        Assert.NotNull(store.GetKey(session.Device!.DeviceId));
        Assert.InRange(fps, 25, 35);
        Assert.NotNull(stats.LatencyMs);
        Assert.InRange(stats.LatencyMs!.Value, 0, 100);
        Assert.Equal(0, pipeline.FailedFrames);

        await session.DisconnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    /// <summary>Long-running stability check. Runs only when IPHONEWEBCAM_SOAK_SECONDS is set.</summary>
    [Fact]
    public async Task Soak()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("IPHONEWEBCAM_SOAK_SECONDS"), out var seconds))
            return;

        StartServer();
        var codeShown = new TaskCompletionSource<string>();
        _server.PairingCodeShown += code => codeShown.TrySetResult(code);
        await using var session = new PhoneSession(await ConnectAsync(), new InMemoryPairingStore());
        using var pipeline = new FramePipeline(new MjpegDecoder());
        session.FrameReceived += pipeline.Submit;
        session.PairingCodeRequired += () => _ = Task.Run(async () => await session.SubmitPairingCodeAsync(await codeShown.Task));
        long decoded = 0;
        pipeline.FrameDecoded += frame => { using (frame) { session.Stats.OnFrameDecoded(frame.DecodeUs); Interlocked.Increment(ref decoded); } };

        using var soakCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _cts.CancelAfter(TimeSpan.FromSeconds(seconds + 30));
        var run = session.RunAsync(soakCts.Token);
        var startMem = GC.GetTotalMemory(true);
        for (var t = 0; t < seconds / 10; t++)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted, $"session ended early: {run.Exception?.GetBaseException().Message}");
            var s = session.Stats.Sample();
            output.WriteLine($"t={t * 10 + 10,4}s fps={s.DecodeFps:F1} latency={s.LatencyMs:F2}ms decode={s.DecodeMs:F2}ms drops phone/pc={s.DroppedOnPhone}/{pipeline.DroppedFrames} managedMB={GC.GetTotalMemory(false) / 1e6:F0}");
            Assert.InRange(s.DecodeFps, 27, 33);
        }
        var growthMb = (GC.GetTotalMemory(true) - startMem) / 1e6;
        output.WriteLine($"decoded={decoded} managed growth={growthMb:F1}MB");
        Assert.InRange(growthMb, -50, 20);
        await session.DisconnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SecondConnectionReusesPairingWithoutCode()
    {
        StartServer();
        var codeShown = new TaskCompletionSource<string>();
        _server.PairingCodeShown += code => codeShown.TrySetResult(code);
        var store = new InMemoryPairingStore();

        // First connection: pair.
        await using (var first = new PhoneSession(await ConnectAsync(), store, new SessionOptions { AutoStart = false }))
        {
            var authorized = new TaskCompletionSource();
            first.PairingCodeRequired += () => _ = Task.Run(async () => await first.SubmitPairingCodeAsync(await codeShown.Task));
            first.StateChanged += s => { if (s == SessionState.Authorized) authorized.TrySetResult(); };
            var run = first.RunAsync(_cts.Token);
            await authorized.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await first.DisconnectAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        await WaitUntil(() => _server.ActiveClients == 0);

        // Second connection: stored key, no pairing prompt.
        await using var second = new PhoneSession(await ConnectAsync(), store, new SessionOptions { AutoStart = false });
        var prompted = false;
        var reauthorized = new TaskCompletionSource();
        second.PairingCodeRequired += () => prompted = true;
        second.StateChanged += s => { if (s == SessionState.Authorized) reauthorized.TrySetResult(); };
        var run2 = second.RunAsync(_cts.Token);
        await reauthorized.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(prompted);
        await second.DisconnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run2);
    }

    [Fact]
    public async Task WrongPairingCodeIsRejected()
    {
        StartServer();
        var codeShown = new TaskCompletionSource<string>();
        _server.PairingCodeShown += code => codeShown.TrySetResult(code);
        var store = new InMemoryPairingStore();
        await using var session = new PhoneSession(await ConnectAsync(), store);
        var failed = new TaskCompletionSource<string>();
        session.PairingFailed += reason => failed.TrySetResult(reason);
        session.PairingCodeRequired += () => _ = Task.Run(async () =>
        {
            var real = await codeShown.Task;
            await session.SubmitPairingCodeAsync(real == "000000" ? "111111" : "000000");
        });

        var run = session.RunAsync(_cts.Token);
        Assert.Equal("Incorrect code", await failed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(SessionState.AwaitingPairingCode, session.State);
        Assert.Null(store.GetKey(session.Device!.DeviceId));
        await session.DisconnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task UnauthorizedClientCannotStartStream()
    {
        StartServer();
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _server.Port);
        var stream = client.GetStream();
        await new MessageWriter(stream).WriteControlAsync(MessageType.StartStream, new StartStream("back", 1280, 720, 30, "mjpeg", 0.7));

        var reader = new MessageReader(stream);
        using var reply = await reader.ReadAsync(_cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MessageType.Error, reply.Header.Type);
        Assert.Equal(ErrorCodes.Unauthorized, ProtocolJson.Deserialize<ErrorMessage>(reply).Code);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task SessionTimesOutWhenPeerGoesSilent()
    {
        // A listener that accepts and then never sends anything.
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        var stream = await new WifiTransport().ConnectAsync(new DeviceEndpoint(TransportKind.Wifi, "silent", "127.0.0.1", port), _cts.Token);
        await using var session = new PhoneSession(stream, new InMemoryPairingStore(), new SessionOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(100),
            InactivityTimeout = TimeSpan.FromMilliseconds(500),
        });
        using var peer = await accept;

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => session.RunAsync(_cts.Token));
        Assert.InRange(sw.ElapsedMilliseconds, 400, 3000);
        listener.Stop();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Condition not met");
            await Task.Delay(20);
        }
    }
}
