package local.phonewebcam.protocol

import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** Pairing and authentication primitives. Mirrors windows/Core/Security/PairingCrypto.cs and docs/protocol.md §4.1. */
object PairingCrypto {
    const val NONCE_SIZE = 16
    const val KEY_SIZE = 32
    private val random = SecureRandom()

    fun newNonce(): ByteArray = ByteArray(NONCE_SIZE).also(random::nextBytes)

    /** Six uniformly distributed decimal digits. */
    fun newCode(): String = "%06d".format(random.nextInt(1_000_000))

    fun proof(code: String, phoneNonce: ByteArray, pcNonce: ByteArray, pcId: String): ByteArray =
        hmac(code.toByteArray(Charsets.US_ASCII), "pair-v1".ascii() + phoneNonce + pcNonce + pcId.toByteArray(Charsets.UTF_8))

    /** HKDF-SHA256 (RFC 5869). One Expand block suffices for a 32-byte key. */
    fun deriveKey(code: String, phoneNonce: ByteArray, pcNonce: ByteArray): ByteArray {
        val prk = hmac(phoneNonce + pcNonce, code.toByteArray(Charsets.US_ASCII))
        return hmac(prk, "iphonewebcam pairkey v1".ascii() + byteArrayOf(1)).copyOf(KEY_SIZE)
    }

    fun mac(pairKey: ByteArray, phoneNonce: ByteArray, pcNonce: ByteArray, pcId: String): ByteArray =
        hmac(pairKey, "auth-v1".ascii() + phoneNonce + pcNonce + pcId.toByteArray(Charsets.UTF_8))

    /** Constant-time comparison. */
    fun matches(a: ByteArray, b: ByteArray): Boolean = MessageDigest.isEqual(a, b)

    private fun hmac(key: ByteArray, message: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run {
            init(SecretKeySpec(key, "HmacSHA256"))
            doFinal(message)
        }

    private fun String.ascii() = toByteArray(Charsets.US_ASCII)
}
