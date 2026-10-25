namespace IPhoneWebcam.Core.Transport;

public enum TransportKind
{
    Wifi,
    Usb,
}

/// <summary>Where to reach an iPhone. Wi-Fi uses Host/Port; USB uses UsbDeviceId (usbmuxd DeviceID).</summary>
public sealed record DeviceEndpoint(TransportKind Kind, string DisplayName, string? Host = null, int Port = DeviceEndpoint.DefaultPort, int? UsbDeviceId = null)
{
    public const int DefaultPort = 47809;

    public override string ToString() => Kind == TransportKind.Wifi ? $"{DisplayName} ({Host}:{Port})" : $"{DisplayName} (USB)";
}

/// <summary>Produces a connected byte stream to the iPhone app. Everything above this is transport-agnostic.</summary>
public interface ITransport
{
    TransportKind Kind { get; }

    Task<Stream> ConnectAsync(DeviceEndpoint endpoint, CancellationToken ct);
}
