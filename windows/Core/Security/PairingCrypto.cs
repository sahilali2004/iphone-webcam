using System.Security.Cryptography;
using System.Text;

namespace IPhoneWebcam.Core.Security;

/// <summary>Pairing and authentication primitives. See docs/protocol.md §4.1.</summary>
public static class PairingCrypto
{
    public const int NonceSize = 16;
    public const int KeySize = 32;

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    /// <summary>Six random decimal digits, uniformly distributed.</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static byte[] Proof(string code, byte[] phoneNonce, byte[] pcNonce, string pcId) =>
        HMACSHA256.HashData(Encoding.ASCII.GetBytes(code), Concat("pair-v1"u8, phoneNonce, pcNonce, Encoding.UTF8.GetBytes(pcId)));

    public static byte[] DeriveKey(string code, byte[] phoneNonce, byte[] pcNonce) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.ASCII.GetBytes(code),
            KeySize,
            Concat(phoneNonce, pcNonce),
            "iphonewebcam pairkey v1"u8.ToArray());

    public static byte[] Mac(byte[] pairKey, byte[] phoneNonce, byte[] pcNonce, string pcId) =>
        HMACSHA256.HashData(pairKey, Concat("auth-v1"u8, phoneNonce, pcNonce, Encoding.UTF8.GetBytes(pcId)));

    public static bool Matches(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        CryptographicOperations.FixedTimeEquals(expected, actual);

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c = default, ReadOnlySpan<byte> d = default)
    {
        var result = new byte[a.Length + b.Length + c.Length + d.Length];
        var span = result.AsSpan();
        a.CopyTo(span);
        b.CopyTo(span[a.Length..]);
        c.CopyTo(span[(a.Length + b.Length)..]);
        d.CopyTo(span[(a.Length + b.Length + c.Length)..]);
        return result;
    }
}
