using System.Diagnostics;
using System.Net;
using PhoneWebcam.Core.Devices;
using PhoneWebcam.Core.Protocol;
using PhoneWebcam.Core.Security;
using PhoneWebcam.Core.Session;
using PhoneWebcam.Core.Transport;
using PhoneWebcam.TestSender;
using Xunit;
using Xunit.Abstractions;

namespace PhoneWebcam.Core.Tests;

public class AdbParsingTests
{
    [Fact]
    public void ParsesDevicesWithStates()
    {
        const string output = """
            List of devices attached
            R52N40438ER            device product:gta3xlwifixx model:SM_T510 device:gta3xlwifi transport_id:2
            emulator-5554          unauthorized transport_id:3
            ABC123                 offline
            * daemon started successfully

            """;
        var devices = AdbClient.ParseDevices(output);
        Assert.Equal(3, devices.Count);
        Assert.Equal(new AdbDevice("R52N40438ER", "SM T510", AdbDeviceState.Ready), devices[0]);
        Assert.Equal(AdbDeviceState.Unauthorized, devices[1].State);
        Assert.Equal("emulator-5554", devices[1].Model);
        Assert.Equal(AdbDeviceState.Offline, devices[2].State);
    }
}

public class DiscoveryTests
{
    [Fact]
    public async Task FakePhoneAnswersProbe()
    {
        var udpPort = 47000 + Random.Shared.Next(2000);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = new FakePhoneServer(new FakePhoneOptions { Port = 0, DiscoveryPort = udpPort, DeviceName = "Probe Tab", DeviceId = "dev-1" });
        _ = server.RunAsync(cts.Token);
        await Task.Delay(200);

        var replies = await Discovery.ProbeAsync(TimeSpan.FromMilliseconds(500), [new IPEndPoint(IPAddress.Loopback, udpPort)], cts.Token);
        var reply = Assert.Single(replies);
        Assert.Equal("dev-1", reply.DeviceId);
        Assert.Equal("Probe Tab", reply.Name);
        Assert.Equal(server.Port, reply.Port);
        Assert.Equal(IPAddress.Loopback, reply.Address);
    }

    [Fact]
    public async Task DeviceManagerListsWifiDevices()
    {
        var reply = new DiscoveryReply("dev-2", "Tab", IPAddress.Parse("192.168.1.20"), 47809);
        using var manager = new DeviceManager(null, _ => Task.FromResult<IReadOnlyList<DiscoveryReply>>([reply]));
        IReadOnlyList<AvailableDevice>? raised = null;
        manager.DevicesChanged += d => raised = d;
        var devices = await manager.ScanAsync(default);
        var device = Assert.Single(devices);
        Assert.Equal("wifi:dev-2", device.Key);
        Assert.Equal(new DeviceEndpoint(TransportKind.Wifi, "Tab", "192.168.1.20", 47809), device.Endpoint);
        Assert.True(device.IsUsable);
        Assert.Same(devices, raised);

        raised = null;
        await manager.ScanAsync(default);
        Assert.Null(raised); // unchanged list → no event
    }
}

/// <summary>WebcamController against the fake phone over loopback TCP.</summary>
public sealed class WebcamControllerTests(ITestOutputHelper output) : IDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(90));
    private readonly Dictionary<string, byte[]> _phonePairings = [];
    private readonly InMemoryPairingStore _pcPairings = new();
    private FakePhoneServer? _server;
    private int _port;

    private void StartPhone()
    {
        _server = new FakePhoneServer(new FakePhoneOptions { Port = _port, PairedPcs = _phonePairings, DeviceId = "fake-phone" });
        _ = _server.RunAsync(_cts.Token);
        _port = _server.Port;
        _server.PairingCodeShown += code => _lastCode = code;
    }

    private string? _lastCode;

    public void Dispose()
    {
        _cts.Cancel();
        _server?.Dispose();
    }

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 15)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task ConnectsPairsStreamsSwitchesAndReconnects()
    {
        StartPhone();
        await using var controller = new WebcamController(_pcPairings, _ => new WifiTransport());
        var frames = 0;
        var lastWidth = 0;
        controller.FrameReceived += f => { using (f) { frames++; lastWidth = f.Header.Width; } };
        controller.StatusChanged += (s, d) => output.WriteLine($"{s}: {d}");
        controller.PairingCodeRequired += () => _ = Task.Run(async () =>
        {
            await WaitFor(() => _lastCode is not null, "pairing code");
            await controller.SubmitPairingCodeAsync(_lastCode!);
        });

        // 1. Connect + pair → Connected, but not streaming until Start Webcam.
        await controller.SetTargetAsync(new DeviceEndpoint(TransportKind.Wifi, "Fake", "127.0.0.1", _port));
        await WaitFor(() => controller.Status == WebcamStatus.Connected, "connected");
        await Task.Delay(300);
        Assert.Equal(0, frames);

        // 2. Start Webcam → Live with frames.
        await controller.ApplySettingsAsync(new StartStream("back", 1920, 1080, 30, "mjpeg", 0.7));
        await controller.StartWebcamAsync();
        await WaitFor(() => controller.Status == WebcamStatus.Live && frames > 10, "live frames");
        Assert.Equal(1920, lastWidth);

        // 3. Change resolution while live.
        await controller.ApplySettingsAsync(new StartStream("back", 1280, 720, 30, "mjpeg", 0.7));
        await WaitFor(() => lastWidth == 1280, "720p frames");

        // 4. Phone app restarts: controller reconnects, re-authenticates with the stored key, resumes streaming.
        _server!.Dispose();
        await WaitFor(() => controller.Status == WebcamStatus.Retrying, "retrying");
        _lastCode = null;
        StartPhone();
        frames = 0;
        await WaitFor(() => controller.Status == WebcamStatus.Live && frames > 10, "resumed after restart", 20);
        Assert.Null(_lastCode); // no re-pairing needed
        Assert.Equal(1280, lastWidth); // settings kept

        // 5. Stop Webcam → stays connected, frames stop.
        await controller.StopWebcamAsync();
        await WaitFor(() => controller.Status == WebcamStatus.Connected, "stopped");
        var after = frames;
        await Task.Delay(500);
        Assert.InRange(frames - after, 0, 2);
    }
}
