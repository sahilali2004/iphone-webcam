using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PhoneWebcam.Core.Diagnostics;
using PhoneWebcam.Core.Transport;

namespace PhoneWebcam.Core.Devices;

/// <summary>A phone that answered a Wi-Fi discovery probe.</summary>
public sealed record DiscoveryReply(string DeviceId, string Name, IPAddress Address, int Port);

/// <summary>
/// Wi-Fi discovery: the PC sends the probe "PWDISCOVER1" to UDP port 47810 (broadcast on every local IPv4
/// network); phones running Phone Webcam reply with JSON {deviceId, name, port}. See docs/protocol.md §8.
/// Only private/link-local replies are accepted.
/// </summary>
public static class Discovery
{
    public const int Port = 47810;
    public static readonly byte[] Probe = "PWDISCOVER1"u8.ToArray();

    private sealed record ReplyJson(string DeviceId, string Name, int Port);

    public static async Task<IReadOnlyList<DiscoveryReply>> ProbeAsync(TimeSpan listenFor, IEnumerable<IPEndPoint>? targets = null, CancellationToken ct = default)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        foreach (var target in targets ?? BroadcastTargets())
        {
            try
            {
                await socket.SendAsync(Probe, target, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                Log.Debug($"Discovery probe to {target} failed: {ex.SocketErrorCode}");
            }
        }

        var replies = new Dictionary<string, DiscoveryReply>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(listenFor);
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(window.Token).ConfigureAwait(false);
                if (!WifiTransport.IsLocal(result.RemoteEndPoint.Address)) continue;
                try
                {
                    var json = JsonSerializer.Deserialize<ReplyJson>(Encoding.UTF8.GetString(result.Buffer), Protocol.ProtocolJson.Options);
                    if (json is { DeviceId.Length: > 0 } && json.Port is > 0 and < 65536)
                        replies[json.DeviceId] = new DiscoveryReply(json.DeviceId, json.Name, result.RemoteEndPoint.Address, json.Port);
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        return [.. replies.Values];
    }

    /// <summary>Limited broadcast plus the directed broadcast of each up IPv4 interface (some routers drop one or the other).</summary>
    public static IEnumerable<IPEndPoint> BroadcastTargets()
    {
        yield return new IPEndPoint(IPAddress.Broadcast, Port);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPEndPoint(new IPAddress(broadcast), Port);
            }
        }
    }

    /// <summary>Builds the reply a phone sends (used by TestSender and tests).</summary>
    public static byte[] BuildReply(string deviceId, string name, int port) =>
        JsonSerializer.SerializeToUtf8Bytes(new ReplyJson(deviceId, name, port), Protocol.ProtocolJson.Options);
}
