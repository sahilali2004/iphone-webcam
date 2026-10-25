package local.phonewebcam

import java.io.File
import local.phonewebcam.protocol.AudioFrameHeader
import local.phonewebcam.protocol.MessageEncoder
import local.phonewebcam.protocol.MessageHeader
import local.phonewebcam.protocol.PairingCrypto
import local.phonewebcam.protocol.ProtocolException
import local.phonewebcam.protocol.VideoFrameHeader
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Checks tests/protocol-vectors/vectors.json, generated from the C# implementation and also checked by
 * the Windows and Swift suites, so Android agrees with the PC byte for byte.
 */
class VectorTests {
    private val vectors: JSONObject by lazy {
        // Unit tests run with the module directory (android/app) as working directory.
        var dir: File? = File(System.getProperty("user.dir")!!).absoluteFile
        while (dir != null && !File(dir, "tests/protocol-vectors/vectors.json").exists()) dir = dir.parentFile
        JSONObject(File(dir!!, "tests/protocol-vectors/vectors.json").readText())
    }

    private fun hex(s: String) = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }

    @Test
    fun pairingMatchesWindows() {
        val v = vectors.getJSONObject("pairing")
        val phone = hex(v.getString("phoneNonce"))
        val pc = hex(v.getString("pcNonce"))
        val code = v.getString("code")
        val pcId = v.getString("pcId")

        assertArrayEquals(hex(v.getString("proof")), PairingCrypto.proof(code, phone, pc, pcId))
        val key = PairingCrypto.deriveKey(code, phone, pc)
        assertArrayEquals(hex(v.getString("pairKey")), key)
        assertArrayEquals(hex(v.getString("mac")), PairingCrypto.mac(key, phone, pc, pcId))
    }

    @Test
    fun headersMatchWindows() {
        val mh = vectors.getJSONObject("messageHeader")
        val header = MessageHeader(mh.getInt("type"), mh.getInt("flags"), mh.getLong("length"), mh.getLong("seq"))
        assertArrayEquals(hex(mh.getString("bytes")), header.encode())
        assertEquals(header, MessageHeader.parse(header.encode()))

        val vf = vectors.getJSONObject("videoFrameHeader")
        val frame = VideoFrameHeader(vf.getInt("codec"), vf.getInt("flags"), vf.getInt("rotation"),
            vf.getInt("width"), vf.getInt("height"), vf.getLong("frameId"), vf.getLong("ptsUs"))
        assertArrayEquals(hex(vf.getString("bytes")), frame.encode())

        val af = vectors.getJSONObject("audioFrameHeader")
        val audio = AudioFrameHeader(af.getInt("codec"), af.getInt("channels"), af.getLong("sampleRate"), af.getLong("sequence"), af.getLong("ptsUs"))
        assertArrayEquals(hex(af.getString("bytes")), audio.encode())
    }
}

class ProtocolTests {
    @Test(expected = ProtocolException::class)
    fun rejectsBadMagic() {
        val bytes = MessageHeader(0x20, 0, 8, 0).encode()
        bytes[0] = 0
        MessageHeader.parse(bytes)
    }

    @Test
    fun enforcesLimits() {
        val big = MessageHeader(0x01, 0, MessageHeader.MAX_CONTROL_LENGTH + 1, 0).encode()
        assertTrue(runCatching { MessageHeader.parse(big) }.isFailure)
        val bigVideo = MessageHeader(0x80, 0, MessageHeader.MAX_CONTROL_LENGTH + 1, 0).encode()
        assertEquals(0x80, MessageHeader.parse(bigVideo).type)
    }

    @Test
    fun encoderFramesAndSequences() {
        val enc = MessageEncoder()
        val a = enc.control(0x12, JSONObject().put("state", "streaming"))
        val b = enc.ping(-5)
        assertEquals(0L, MessageHeader.parse(a).seq)
        assertEquals(1L, MessageHeader.parse(b).seq)
        assertEquals(-5L, MessageEncoder.readPing(b.copyOfRange(16, b.size)))

        val frame = enc.videoFrame(VideoFrameHeader(1, 1, 0, 1280, 720, 7, 99), byteArrayOf(9, 9, 9, 0), dataLength = 3)
        val header = MessageHeader.parse(frame)
        assertEquals(0x80, header.type)
        assertEquals((VideoFrameHeader.SIZE + 3).toLong(), header.length)
        assertEquals(16 + 20 + 3, frame.size)
    }

    @Test
    fun codesAndCompare() {
        repeat(100) { assertTrue(PairingCrypto.newCode().matches(Regex("^[0-9]{6}$"))) }
        assertTrue(PairingCrypto.matches(byteArrayOf(1, 2), byteArrayOf(1, 2)))
        assertFalse(PairingCrypto.matches(byteArrayOf(1, 2), byteArrayOf(1, 3)))
    }
}
