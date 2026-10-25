package local.phonewebcam.net

import android.content.Context
import android.util.Base64
import java.util.UUID

/**
 * Paired PCs: pcId → pairing key, in app-private SharedPreferences (sandboxed; backup disabled in the
 * manifest so keys never leave the device).
 */
class PairingStore(context: Context) {
    private val prefs = context.getSharedPreferences("pairings", Context.MODE_PRIVATE)

    fun key(pcId: String): ByteArray? =
        prefs.getString("key:$pcId", null)?.let { Base64.decode(it, Base64.NO_WRAP) }

    fun save(pcId: String, pcName: String, key: ByteArray) {
        prefs.edit()
            .putString("key:$pcId", Base64.encodeToString(key, Base64.NO_WRAP))
            .putString("name:$pcId", pcName)
            .apply()
    }

    fun remove(pcId: String) {
        prefs.edit().remove("key:$pcId").remove("name:$pcId").apply()
    }
}

/** Random per-install identifier advertised to PCs. Not a hardware ID. */
object DeviceIdentity {
    fun deviceId(context: Context): String {
        val prefs = context.getSharedPreferences("identity", Context.MODE_PRIVATE)
        return prefs.getString("deviceId", null) ?: UUID.randomUUID().toString().also {
            prefs.edit().putString("deviceId", it).apply()
        }
    }
}

/** Monotonic microseconds; same clock as CameraX frame timestamps when the sensor uses REALTIME. */
object MonoClock {
    val nowUs: Long get() = android.os.SystemClock.elapsedRealtimeNanos() / 1_000
}
