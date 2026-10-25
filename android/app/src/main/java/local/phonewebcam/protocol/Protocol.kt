package local.phonewebcam.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Wire protocol v1. Mirrors windows/Core/Protocol and docs/protocol.md. All integers little-endian. */
object MessageType {
    const val HELLO = 0x01
    const val DEVICE_INFO = 0x02
    const val PAIR_REQUEST = 0x03
    const val PAIR_CONFIRM = 0x04
    const val AUTH = 0x05
    const val AUTH_RESULT = 0x06

    const val START_STREAM = 0x10
    const val STOP_STREAM = 0x11
    const val STREAM_STATE = 0x12
    const val CAMERA_SWITCH = 0x13
    const val SET_RESOLUTION = 0x14
    const val SET_FPS = 0x15
    const val SET_QUALITY = 0x16
    const val SET_TORCH = 0x17
    const val START_AUDIO = 0x18
    const val STOP_AUDIO = 0x19
    const val AUDIO_STATE = 0x1A

    const val PING = 0x20
    const val PONG = 0x21

    const val DISCONNECT = 0x3F
    const val ERROR = 0x40

    const val VIDEO_FRAME = 0x80
    const val AUDIO_FRAME = 0x81

    fun isMedia(type: Int) = type >= 0x80
}

class ProtocolException(message: String) : Exception(message)

data class MessageHeader(val type: Int, val flags: Int, val length: Long, val seq: Long) {
    fun encode(): ByteArray = ByteBuffer.allocate(SIZE).order(ByteOrder.LITTLE_ENDIAN).apply {
        putShort(MAGIC.toShort())
        put(VERSION.toByte())
        put(type.toByte())
        putShort(flags.toShort())
        putShort(0)
        putInt(length.toInt())
        putInt(seq.toInt())
    }.array()

    companion object {
        const val SIZE = 16
        const val MAGIC = 0x5749 // bytes 'I' 'W'
        const val VERSION = 1
        const val MAX_CONTROL_LENGTH = 64L * 1024
        const val MAX_MEDIA_LENGTH = 8L * 1024 * 1024

        fun parse(bytes: ByteArray): MessageHeader {
            if (bytes.size < SIZE) throw ProtocolException("Header truncated")
            val b = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
            if (b.short.toInt() and 0xFFFF != MAGIC) throw ProtocolException("Bad magic")
            val version = b.get().toInt() and 0xFF
            if (version != VERSION) throw ProtocolException("Unsupported protocol version $version")
            val type = b.get().toInt() and 0xFF
            val flags = b.short.toInt() and 0xFFFF
            b.short
            val length = b.int.toLong() and 0xFFFFFFFFL
            val seq = b.int.toLong() and 0xFFFFFFFFL
            val max = if (MessageType.isMedia(type)) MAX_MEDIA_LENGTH else MAX_CONTROL_LENGTH
            if (length > max) throw ProtocolException("Payload too large for 0x${type.toString(16)}: $length bytes")
            return MessageHeader(type, flags, length, seq)
        }
    }
}

object VideoCodec {
    const val MJPEG = 1
    const val H264 = 2
    const val HEVC = 3
}

object VideoFrameFlags {
    const val KEYFRAME = 1
    const val MIRRORED = 2
    const val DISCONTINUITY = 4
}

data class VideoFrameHeader(
    val codec: Int,
    val flags: Int,
    val rotation: Int,
    val width: Int,
    val height: Int,
    val frameId: Long,
    val ptsUs: Long,
) {
    fun encode(): ByteArray = ByteBuffer.allocate(SIZE).order(ByteOrder.LITTLE_ENDIAN).apply {
        put(codec.toByte())
        put(flags.toByte())
        put(rotation.toByte())
        put(0)
        putShort(width.toShort())
        putShort(height.toShort())
        putInt(frameId.toInt())
        putLong(ptsUs)
    }.array()

    companion object {
        const val SIZE = 20
    }
}

object AudioCodec {
    const val PCM_S16LE = 1
}

/** 20-byte header at the start of an AUDIO_FRAME payload (docs/protocol.md §5a). */
data class AudioFrameHeader(val codec: Int, val channels: Int, val sampleRate: Long, val sequence: Long, val ptsUs: Long) {
    fun encode(): ByteArray = ByteBuffer.allocate(SIZE).order(ByteOrder.LITTLE_ENDIAN).apply {
        put(codec.toByte())
        put(channels.toByte())
        putShort(0)
        putInt(sampleRate.toInt())
        putInt(sequence.toInt())
        putLong(ptsUs)
    }.array()

    companion object {
        const val SIZE = 20
    }
}

/** Builds complete messages (header + payload). Not thread-safe; callers serialize writes. */
class MessageEncoder {
    private var seq = 0L

    fun message(type: Int, payload: ByteArray = ByteArray(0), flags: Int = 0): ByteArray {
        val header = MessageHeader(type, flags, payload.size.toLong(), seq).encode()
        seq = (seq + 1) and 0xFFFFFFFFL
        return header + payload
    }

    fun control(type: Int, json: org.json.JSONObject): ByteArray = message(type, json.toString().toByteArray(Charsets.UTF_8))

    fun ping(senderTimeUs: Long): ByteArray =
        message(MessageType.PING, ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(senderTimeUs).array())

    fun pong(echoed: Long, responder: Long): ByteArray =
        message(MessageType.PONG, ByteBuffer.allocate(16).order(ByteOrder.LITTLE_ENDIAN).putLong(echoed).putLong(responder).array())

    /** Header + frame header + bitstream in one buffer so it is written atomically. */
    fun videoFrame(header: VideoFrameHeader, data: ByteArray, dataLength: Int = data.size): ByteArray {
        val payloadLength = VideoFrameHeader.SIZE + dataLength
        val out = ByteArray(MessageHeader.SIZE + payloadLength)
        MessageHeader(MessageType.VIDEO_FRAME, 0, payloadLength.toLong(), seq).encode().copyInto(out)
        seq = (seq + 1) and 0xFFFFFFFFL
        header.encode().copyInto(out, MessageHeader.SIZE)
        data.copyInto(out, MessageHeader.SIZE + VideoFrameHeader.SIZE, 0, dataLength)
        return out
    }

    fun audioFrame(header: AudioFrameHeader, pcm: ByteArray, length: Int = pcm.size): ByteArray {
        val payload = ByteArray(AudioFrameHeader.SIZE + length)
        header.encode().copyInto(payload)
        pcm.copyInto(payload, AudioFrameHeader.SIZE, 0, length)
        return message(MessageType.AUDIO_FRAME, payload)
    }

    companion object {
        fun readPing(payload: ByteArray): Long {
            if (payload.size < 8) throw ProtocolException("PING truncated")
            return ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN).long
        }
    }
}

object StreamStates {
    const val STREAMING = "streaming"
    const val STOPPED = "stopped"
    const val PAUSED = "paused"
}

object AudioStates {
    const val STREAMING = "streaming"
    const val STOPPED = "stopped"
    const val DENIED = "denied"
}

object ErrorCodes {
    const val BUSY = "busy"
    const val UNAUTHORIZED = "unauthorized"
    const val UNSUPPORTED = "unsupported"
    const val CAMERA_UNAVAILABLE = "camera_unavailable"
    const val BAD_REQUEST = "bad_request"
}
