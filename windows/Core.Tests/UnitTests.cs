using System.Net;
using IPhoneWebcam.Core.Protocol;
using IPhoneWebcam.Core.Security;
using IPhoneWebcam.Core.Session;
using IPhoneWebcam.Core.Timing;
using IPhoneWebcam.Core.Transport;
using Xunit;

namespace IPhoneWebcam.Core.Tests;

public class PairingCryptoTests
{
    [Fact]
    public void BothSidesDeriveSameKey_AndMacVerifies()
    {
        var phoneNonce = PairingCrypto.NewNonce();
        var pcNonce = PairingCrypto.NewNonce();
        var pcKey = PairingCrypto.DeriveKey("123456", phoneNonce, pcNonce);
        var phoneKey = PairingCrypto.DeriveKey("123456", phoneNonce, pcNonce);
        Assert.Equal(pcKey, phoneKey);
        Assert.Equal(PairingCrypto.KeySize, pcKey.Length);

        var authNonce = PairingCrypto.NewNonce();
        Assert.True(PairingCrypto.Matches(
            PairingCrypto.Mac(phoneKey, phoneNonce, authNonce, "pc-1"),
            PairingCrypto.Mac(pcKey, phoneNonce, authNonce, "pc-1")));
        Assert.False(PairingCrypto.Matches(
            PairingCrypto.Mac(phoneKey, phoneNonce, authNonce, "pc-1"),
            PairingCrypto.Mac(pcKey, phoneNonce, authNonce, "pc-2")));
    }

    [Fact]
    public void ProofDependsOnCode()
    {
        var a = PairingCrypto.NewNonce();
        var b = PairingCrypto.NewNonce();
        Assert.False(PairingCrypto.Matches(PairingCrypto.Proof("123456", a, b, "pc"), PairingCrypto.Proof("123457", a, b, "pc")));
    }

    [Fact]
    public void CodesAreSixDigits()
    {
        for (var i = 0; i < 100; i++)
            Assert.Matches("^[0-9]{6}$", PairingCrypto.NewCode());
    }

    [Fact]
    public void DpapiStore_PersistsPcIdAndKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), "iphonewebcam-test-" + Guid.NewGuid());
        try
        {
            var store = new DpapiPairingStore(dir);
            store.SaveKey("phone", [1, 2, 3]);
            var reopened = new DpapiPairingStore(dir);
            Assert.Equal(store.PcId, reopened.PcId);
            Assert.Equal(new byte[] { 1, 2, 3 }, reopened.GetKey("phone"));
            Assert.DoesNotContain("AQID", File.ReadAllText(Path.Combine(dir, "pairings.json"))); // base64 of raw key must not appear
            reopened.RemoveKey("phone");
            Assert.Null(new DpapiPairingStore(dir).GetKey("phone"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class ClockSyncTests
{
    [Fact]
    public void UsesLowestRttSample()
    {
        var clock = new ClockSync();
        // True offset = +1000. Sample 1: symmetric 200us RTT. Sample 2: 5ms RTT with asymmetric delay (bad estimate).
        clock.AddSample(t0: 0, t1: 1100, t2: 200);
        clock.AddSample(t0: 10_000, t1: 11_000 + 4_500, t2: 15_000);
        Assert.Equal(1000, clock.OffsetUs);
        Assert.Equal(5000, clock.RttUs);
        Assert.Equal(9_000, clock.ToLocal(10_000));
    }

    [Fact]
    public void ExpiresOldSamples()
    {
        var clock = new ClockSync(windowUs: 1_000);
        clock.AddSample(0, 1100, 200);       // offset 1000, rtt 200
        clock.AddSample(5_000, 7_500, 6_000); // offset 2000, rtt 1000; first sample now outside window
        Assert.Equal(2000, clock.OffsetUs);
    }

    [Fact]
    public void MonoClockIsMonotonic()
    {
        var a = MonoClock.NowUs;
        Thread.Sleep(5);
        var b = MonoClock.NowUs;
        Assert.InRange(b - a, 4_000, 500_000);
    }
}

public class StreamStatsTests
{
    [Fact]
    public void CountsFrameIdGapsAsPhoneDrops_ExceptAfterDiscontinuity()
    {
        var stats = new StreamStats();
        VideoFrameHeader H(uint id, VideoFrameFlags flags = VideoFrameFlags.Keyframe) => new(VideoCodec.Mjpeg, flags, 0, 1280, 720, id, 0);

        stats.OnFrameReceived(H(0), 100, 10_000);
        stats.OnFrameReceived(H(1), 100, 20_000);
        stats.OnFrameReceived(H(4), 100, null);                                       // 2 dropped
        stats.OnFrameReceived(H(100, VideoFrameFlags.Discontinuity), 100, null);      // not a drop

        var s = stats.Sample();
        Assert.Equal(2, s.DroppedOnPhone);
        Assert.Equal(15.0, s.LatencyMs);
        Assert.Equal(1280, s.Width);
    }
}

public class WifiTransportTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.25", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    public void IsLocal(string address, bool expected) =>
        Assert.Equal(expected, WifiTransport.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public async Task RefusesPublicAddresses()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WifiTransport().ConnectAsync(new DeviceEndpoint(TransportKind.Wifi, "x", "8.8.8.8"), default));
    }
}
