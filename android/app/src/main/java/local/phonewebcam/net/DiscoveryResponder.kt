package local.phonewebcam.net

import android.content.Context
import android.net.wifi.WifiManager
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.SocketException
import org.json.JSONObject

/**
 * Answers the PC's Wi-Fi discovery probe ("PWDISCOVER1" on UDP 47810) with {deviceId, name, port}.
 * Holds a multicast lock while running: some Wi-Fi drivers drop broadcast packets in power-save otherwise.
 * Only replies to private / link-local senders. See docs/protocol.md §8.
 */
class DiscoveryResponder(context: Context, private val deviceId: String, private val name: String, private val port: Int) {
    companion object {
        const val DISCOVERY_PORT = 47810
        private val PROBE = "PWDISCOVER1".toByteArray(Charsets.US_ASCII)
        private const val TAG = "Discovery"
    }

    private val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    private var lock: WifiManager.MulticastLock? = null
    @Volatile private var socket: DatagramSocket? = null

    fun start() {
        if (socket != null) return
        val s = try {
            DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(DISCOVERY_PORT))
            }
        } catch (e: SocketException) {
            Log.w(TAG, "Discovery port unavailable: ${e.message}")
            return
        }
        socket = s
        lock = wifi.createMulticastLock("PhoneWebcam.discovery").apply {
            setReferenceCounted(false)
            acquire()
        }
        Thread({ loop(s) }, "discovery").start()
        Log.i(TAG, "Answering discovery on UDP $DISCOVERY_PORT")
    }

    fun stop() {
        socket?.close()
        socket = null
        lock?.release()
        lock = null
    }

    private fun loop(s: DatagramSocket) {
        val reply = JSONObject().put("deviceId", deviceId).put("name", name).put("port", port).toString().toByteArray(Charsets.UTF_8)
        val buffer = ByteArray(64)
        while (!s.isClosed) {
            val packet = DatagramPacket(buffer, buffer.size)
            try {
                s.receive(packet)
            } catch (e: Exception) {
                break
            }
            val from = packet.address
            if (!(from.isSiteLocalAddress || from.isLinkLocalAddress || from.isLoopbackAddress)) continue
            if (packet.length != PROBE.size || !buffer.copyOf(packet.length).contentEquals(PROBE)) continue
            try {
                s.send(DatagramPacket(reply, reply.size, packet.socketAddress))
            } catch (e: Exception) {
                Log.w(TAG, "Reply failed: ${e.message}")
            }
        }
    }
}
