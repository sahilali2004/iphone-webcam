import CryptoKit
import Foundation

/// Pairing and authentication primitives. Mirrors windows/Core/Security/PairingCrypto.cs and docs/protocol.md §4.1.
public enum PairingCrypto {
    public static let nonceSize = 16
    public static let keySize = 32

    /// SystemRandomNumberGenerator is a CSPRNG on Apple platforms (arc4random_buf).
    public static func newNonce() -> Data {
        var rng = SystemRandomNumberGenerator()
        return Data((0..<nonceSize).map { _ in UInt8.random(in: .min ... .max, using: &rng) })
    }

    /// Six uniformly distributed decimal digits.
    public static func newCode() -> String {
        String(format: "%06d", Int.random(in: 0..<1_000_000))
    }

    public static func proof(code: String, phoneNonce: Data, pcNonce: Data, pcId: String) -> Data {
        hmac(key: Data(code.utf8), "pair-v1".data + phoneNonce + pcNonce + pcId.data)
    }

    public static func deriveKey(code: String, phoneNonce: Data, pcNonce: Data) -> Data {
        let key = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: Data(code.utf8)),
            salt: phoneNonce + pcNonce,
            info: "iphonewebcam pairkey v1".data,
            outputByteCount: keySize)
        return key.withUnsafeBytes { Data($0) }
    }

    public static func mac(pairKey: Data, phoneNonce: Data, pcNonce: Data, pcId: String) -> Data {
        hmac(key: pairKey, "auth-v1".data + phoneNonce + pcNonce + pcId.data)
    }

    /// Constant-time comparison.
    public static func matches(_ a: Data, _ b: Data) -> Bool {
        guard a.count == b.count else { return false }
        var diff: UInt8 = 0
        for (x, y) in zip(a, b) { diff |= x ^ y }
        return diff == 0
    }

    private static func hmac(key: Data, _ message: Data) -> Data {
        Data(HMAC<SHA256>.authenticationCode(for: message, using: SymmetricKey(data: key)))
    }
}

private extension String {
    var data: Data { Data(utf8) }
}
