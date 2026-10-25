using System.Net;
using System.Net.Sockets;

namespace IPhoneWebcam.Core.Transport;

public sealed class WifiTransport : ITransport
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public TransportKind Kind => TransportKind.Wifi;

    public async Task<Stream> ConnectAsync(DeviceEndpoint endpoint, CancellationToken ct)
    {
        if (endpoint.Kind != TransportKind.Wifi || string.IsNullOrWhiteSpace(endpoint.Host))
            throw new ArgumentException("Wi-Fi endpoint requires a host", nameof(endpoint));

        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(IsLocal)
            ?? throw new InvalidOperationException($"{endpoint.Host} is not a local-network address; refusing to connect.");

        var client = new TcpClient(address.AddressFamily) { NoDelay = true };
        client.ReceiveBufferSize = 1 << 20;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(address, endpoint.Port, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client.GetStream();
    }

    /// <summary>Loopback, RFC1918, IPv4 link-local, and IPv6 ULA/link-local only — never the public internet.</summary>
    public static bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }
}
