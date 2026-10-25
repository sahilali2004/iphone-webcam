package local.phonewebcam.net

import android.os.Handler
import android.os.Looper
import android.util.Base64
import android.util.Log
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.IOException
import java.net.Inet6Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import local.phonewebcam.protocol.ErrorCodes
import local.phonewebcam.protocol.MessageEncoder
import local.phonewebcam.protocol.MessageHeader
import local.phonewebcam.protocol.MessageType
import local.phonewebcam.protocol.PairingCrypto
import local.phonewebcam.protocol.ProtocolException
import local.phonewebcam.protocol.VideoFrameHeader
import org.json.JSONArray
import org.json.JSONObject

private const val TAG = "StreamServer"

/** Events from the server, always delivered on the main thread. */
interface ServerListener {
    fun onServerState(listening: Boolean, error: String?)
    fun onPairingCode(code: String?)
    /** A PC finished authenticating (name), or the authenticated PC went away (null). */
    fun onClientChanged(pcName: String?)
    fun onStartStream(camera: String, width: Int, height: Int, quality: Double)
    fun onStopStream()
    fun onSwitchCamera(camera: String)
    fun onSetResolution(width: Int, height: Int)
    fun onSetQuality(quality: Double)
    fun onSetTorch(on: Boolean)
}

/** Static parts of DEVICE_INFO supplied by the app. */
data class DeviceInfoTemplate(
    val deviceId: String,
    val deviceName: String,
    val model: String,
    val osVersion: String,
    val appVersion: String,
    val cameras: List<Pair<String, Boolean>>,
    val formats: List<Pair<Int, Int>>,
)

/**
 * TCP listener on port 47809 (all interfaces: Wi-Fi, and loopback for `adb forward` over USB).
 * Accepts one PC at a time. Wire protocol: docs/protocol.md.
 */
class StreamServer(private val pairings: PairingStore, private val listener: ServerListener) {
    companion object {
        const val PORT = 47809
    }

    private val main = Handler(Looper.getMainLooper())
    private var serverSocket: ServerSocket? = null
    @Volatile private var client: ClientSession? = null
    @Volatile var deviceInfo: DeviceInfoTemplate? = null

    fun start(info: DeviceInfoTemplate) {
        deviceInfo = info
        if (serverSocket != null) return
        val socket = try {
            ServerSocket().apply {
                reuseAddress = true
                bind(InetSocketAddress(PORT))
            }
        } catch (e: IOException) {
            Log.e(TAG, "Listen failed", e)
            post { listener.onServerState(false, e.message ?: "Port $PORT unavailable") }
            return
        }
        serverSocket = socket
        Thread({ acceptLoop(socket) }, "accept").start()
        Log.i(TAG, "Listening on port $PORT")
        post { listener.onServerState(true, null) }
    }

    fun stop() {
        client?.close("user", notifyPeer = true)
        client = null
        try {
            serverSocket?.close()
        } catch (_: IOException) {
        }
        serverSocket = null
        post { listener.onServerState(false, null) }
    }

    /** Hands an encoded frame to the connected PC; [done] runs once written (or dropped). */
    fun sendFrame(header: VideoFrameHeader, data: ByteArray, length: Int, done: () -> Unit) {
        val c = client
        if (c == null || !c.authorized) {
            done()
            return
        }
        c.send(after = done) { it.videoFrame(header, data, length) }
    }

    fun sendStreamState(state: JSONObject) {
        client?.send { it.control(MessageType.STREAM_STATE, state) }
    }

    fun sendError(code: String, message: String) {
        client?.send { it.control(MessageType.ERROR, JSONObject().put("code", code).put("message", message)) }
    }

    internal fun post(block: () -> Unit) {
        main.post(block)
    }

    private fun acceptLoop(socket: ServerSocket) {
        while (!socket.isClosed) {
            val s = try {
                socket.accept()
            } catch (e: IOException) {
                break
            }
            if (!isLocal(s.inetAddress)) {
                Log.w(TAG, "Rejected non-local connection from ${s.inetAddress}")
                s.close()
                continue
            }
            if (client != null) {
                Log.w(TAG, "Rejected second client ${s.inetAddress}")
                Thread {
                    try {
                        s.getOutputStream().write(MessageEncoder().control(MessageType.ERROR,
                            JSONObject().put("code", ErrorCodes.BUSY).put("message", "Another PC is connected")))
                    } catch (_: IOException) {
                    }
                    s.close()
                }.start()
                continue
            }
            Log.i(TAG, "Client connected: ${s.inetAddress}")
            val session = ClientSession(s, this, pairings, listener)
            client = session
            session.start()
        }
    }

    internal fun clientClosed(session: ClientSession) {
        if (client !== session) return
        client = null
        val wasAuthorized = session.authorized
        post {
            listener.onPairingCode(null)
            if (wasAuthorized) listener.onClientChanged(null)
        }
    }

    private fun isLocal(address: InetAddress): Boolean {
        if (address.isLoopbackAddress || address.isSiteLocalAddress || address.isLinkLocalAddress) return true
        // IPv6 unique-local fc00::/7
        return address is Inet6Address && (address.address[0].toInt() and 0xFE) == 0xFC
    }
}

/** Phone side of one PC session: handshake, pairing/auth, keepalive, command dispatch, frame sending. */
internal class ClientSession(
    private val socket: Socket,
    private val server: StreamServer,
    private val pairings: PairingStore,
    private val listener: ServerListener,
) {
    companion object {
        private const val INACTIVITY_TIMEOUT_US = 3_000_000L
        private const val PAIRING_LOCKOUT_MS = 30_000L
        @Volatile private var pairingLockedUntil = 0L
        private val ALLOWED_BEFORE_AUTH = setOf(
            MessageType.HELLO, MessageType.PAIR_REQUEST, MessageType.PAIR_CONFIRM, MessageType.AUTH,
            MessageType.PING, MessageType.PONG, MessageType.DISCONNECT,
        )
    }

    private val writer = Executors.newSingleThreadExecutor { Thread(it, "session-writer") }
    private val timer: ScheduledExecutorService = Executors.newSingleThreadScheduledExecutor { Thread(it, "session-keepalive") }
    private val encoder = MessageEncoder() // used only on the writer thread
    private val out = BufferedOutputStream(socket.getOutputStream(), 64 * 1024)
    private val closed = AtomicBoolean(false)
    private val phoneNonce = PairingCrypto.newNonce()
    private var pcId: String? = null
    private var pcName = "PC"
    private var pairingCode: String? = null
    private var failedAttempts = 0
    @Volatile private var lastInboundUs = MonoClock.nowUs

    @Volatile var authorized = false
        private set

    fun start() {
        socket.tcpNoDelay = true
        socket.sendBufferSize = 1 shl 20
        Thread({ readLoop() }, "session-reader").start()
        timer.scheduleWithFixedDelay({ keepAlive() }, 1, 1, TimeUnit.SECONDS)
    }

    /** Queues a message. [build] runs on the writer thread so sequence numbers stay in order. */
    fun send(after: (() -> Unit)? = null, build: (MessageEncoder) -> ByteArray) {
        if (closed.get()) {
            after?.invoke()
            return
        }
        try {
            writer.execute {
                try {
                    out.write(build(encoder))
                    out.flush()
                } catch (e: IOException) {
                    close("send failed", notifyPeer = false)
                } finally {
                    after?.invoke()
                }
            }
        } catch (e: java.util.concurrent.RejectedExecutionException) {
            after?.invoke()
        }
    }

    fun close(reason: String, notifyPeer: Boolean) {
        if (!closed.compareAndSet(false, true)) return
        Log.i(TAG, "Closing session: $reason")
        timer.shutdownNow()
        if (notifyPeer) {
            // Best effort, bounded: the writer may be mid-frame.
            writer.execute {
                try {
                    out.write(encoder.control(MessageType.DISCONNECT, JSONObject().put("reason", reason)))
                    out.flush()
                } catch (_: IOException) {
                }
            }
        }
        writer.shutdown()
        try {
            writer.awaitTermination(300, TimeUnit.MILLISECONDS)
        } catch (_: InterruptedException) {
        }
        try {
            socket.close()
        } catch (_: IOException) {
        }
        server.clientClosed(this)
    }

    private fun readLoop() {
        val input = DataInputStream(socket.getInputStream().buffered())
        val headerBytes = ByteArray(MessageHeader.SIZE)
        try {
            while (!closed.get()) {
                input.readFully(headerBytes)
                val header = MessageHeader.parse(headerBytes)
                val payload = ByteArray(header.length.toInt())
                input.readFully(payload)
                lastInboundUs = MonoClock.nowUs
                try {
                    if (!handle(header.type, payload)) return
                } catch (e: org.json.JSONException) {
                    Log.w(TAG, "Malformed 0x${header.type.toString(16)}: ${e.message}")
                    sendError(ErrorCodes.BAD_REQUEST, "Malformed message")
                }
            }
        } catch (e: IOException) {
            close(if (e is java.io.EOFException) "peer closed" else "receive failed", notifyPeer = false)
        } catch (e: ProtocolException) {
            Log.e(TAG, "Protocol error: ${e.message}")
            close("protocol error", notifyPeer = false)
        }
    }

    /** Returns false when the session should stop reading. */
    private fun handle(type: Int, payload: ByteArray): Boolean {
        if (!authorized && type !in ALLOWED_BEFORE_AUTH) {
            Log.w(TAG, "Unauthorized 0x${type.toString(16)}; closing")
            sendError(ErrorCodes.UNAUTHORIZED, "Not authorized")
            close("unauthorized", notifyPeer = false)
            return false
        }
        val json by lazy { JSONObject(String(payload, Charsets.UTF_8)) }

        when (type) {
            MessageType.HELLO -> {
                pcId = json.getString("pcId")
                pcName = json.optString("pcName", "PC")
                Log.i(TAG, "HELLO from $pcName")
                sendDeviceInfo()
            }
            MessageType.PAIR_REQUEST -> {
                if (System.currentTimeMillis() < pairingLockedUntil) {
                    sendAuthResult(false, "Too many attempts. Try again in 30 seconds.")
                } else {
                    val code = PairingCrypto.newCode()
                    pairingCode = code
                    failedAttempts = 0
                    server.post { listener.onPairingCode(code) }
                }
            }
            MessageType.PAIR_CONFIRM -> handlePairConfirm(json)
            MessageType.AUTH -> {
                val id = pcId
                val key = id?.let(pairings::key)
                val pcNonce = b64(json.getString("pcNonce"))
                val mac = b64(json.getString("mac"))
                if (id == null || key == null || pcNonce == null || mac == null ||
                    !PairingCrypto.matches(PairingCrypto.mac(key, phoneNonce, pcNonce, id), mac)
                ) {
                    sendAuthResult(false, "Unknown PC")
                } else {
                    authorize()
                }
            }
            MessageType.START_STREAM -> {
                val camera = json.optString("camera", "back")
                val w = json.optInt("width", 1920)
                val h = json.optInt("height", 1080)
                val q = json.optDouble("quality", 0.7)
                server.post { listener.onStartStream(camera, w, h, q) }
            }
            MessageType.STOP_STREAM -> server.post { listener.onStopStream() }
            MessageType.CAMERA_SWITCH -> {
                val camera = json.getString("camera")
                server.post { listener.onSwitchCamera(camera) }
            }
            MessageType.SET_RESOLUTION -> {
                val w = json.getInt("width")
                val h = json.getInt("height")
                server.post { listener.onSetResolution(w, h) }
            }
            MessageType.SET_QUALITY -> {
                val q = json.getDouble("quality")
                server.post { listener.onSetQuality(q) }
            }
            MessageType.SET_TORCH -> {
                val on = json.getBoolean("on")
                server.post { listener.onSetTorch(on) }
            }
            MessageType.SET_FPS -> sendError(ErrorCodes.UNSUPPORTED, "Only 30 fps is supported")
            MessageType.PING -> {
                val echoed = MessageEncoder.readPing(payload)
                send { it.pong(echoed, MonoClock.nowUs) }
            }
            MessageType.PONG -> Unit
            MessageType.DISCONNECT -> {
                Log.i(TAG, "PC disconnected: ${json.optString("reason")}")
                close("peer disconnected", notifyPeer = false)
                return false
            }
            MessageType.ERROR -> Log.e(TAG, "PC reported ${json.optString("code")}: ${json.optString("message")}")
            else -> Log.w(TAG, "Ignoring message type 0x${type.toString(16)}")
        }
        return true
    }

    private fun handlePairConfirm(json: JSONObject) {
        val code = pairingCode
        val id = pcId
        val pcNonce = b64(json.getString("pcNonce"))
        val proof = b64(json.getString("proof"))
        if (code == null || id == null || pcNonce == null || proof == null) {
            sendAuthResult(false, "No pairing in progress")
            return
        }
        if (!PairingCrypto.matches(PairingCrypto.proof(code, phoneNonce, pcNonce, id), proof)) {
            failedAttempts++
            if (failedAttempts >= 3) {
                pairingCode = null
                pairingLockedUntil = System.currentTimeMillis() + PAIRING_LOCKOUT_MS
                server.post { listener.onPairingCode(null) }
                sendAuthResult(false, "Too many attempts. Try again in 30 seconds.")
            } else {
                sendAuthResult(false, "Incorrect code")
            }
            return
        }
        pairings.save(id, pcName, PairingCrypto.deriveKey(code, phoneNonce, pcNonce))
        pairingCode = null
        Log.i(TAG, "Paired with $pcName")
        server.post { listener.onPairingCode(null) }
        authorize()
    }

    private fun authorize() {
        authorized = true
        sendAuthResult(true, null)
        val name = pcName
        server.post { listener.onClientChanged(name) }
    }

    private fun sendDeviceInfo() {
        val t = server.deviceInfo ?: return
        val info = JSONObject()
            .put("deviceId", t.deviceId)
            .put("deviceName", t.deviceName)
            .put("model", t.model)
            .put("osVersion", t.osVersion)
            .put("appVersion", t.appVersion)
            .put("nonce", Base64.encodeToString(phoneNonce, Base64.NO_WRAP))
            .put("paired", pcId?.let(pairings::key) != null)
            .put("cameras", JSONArray(t.cameras.map { JSONObject().put("id", it.first).put("torch", it.second) }))
            .put("formats", JSONArray(t.formats.map { JSONObject().put("w", it.first).put("h", it.second).put("fps", JSONArray(listOf(30))) }))
            .put("codecs", JSONArray(listOf("mjpeg")))
        send { it.control(MessageType.DEVICE_INFO, info) }
    }

    private fun sendAuthResult(ok: Boolean, reason: String?) {
        val json = JSONObject().put("ok", ok)
        if (reason != null) json.put("reason", reason)
        send { it.control(MessageType.AUTH_RESULT, json) }
    }

    private fun sendError(code: String, message: String) {
        send { it.control(MessageType.ERROR, JSONObject().put("code", code).put("message", message)) }
    }

    /** Ping every second; drop the link after 3 s of silence (protocol.md §6). */
    private fun keepAlive() {
        if (closed.get()) return
        if (MonoClock.nowUs - lastInboundUs > INACTIVITY_TIMEOUT_US) {
            close("timeout", notifyPeer = false)
            return
        }
        send { it.ping(MonoClock.nowUs) }
    }

    private fun b64(s: String): ByteArray? = try {
        Base64.decode(s, Base64.DEFAULT)
    } catch (_: IllegalArgumentException) {
        null
    }
}
