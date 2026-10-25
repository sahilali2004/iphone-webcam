package local.phonewebcam.camera

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.util.Log
import local.phonewebcam.net.MonoClock

/**
 * Captures the microphone as 16-bit PCM in 20 ms chunks and hands each chunk to [onChunk] (on its own thread).
 * VOICE_COMMUNICATION enables the device's echo cancellation / noise suppression where available,
 * which is what a call needs.
 */
class AudioStreamer(private val sampleRate: Int, private val channels: Int, private val onChunk: (ByteArray, Int, Long) -> Unit) {
    @Volatile private var running = false
    private var thread: Thread? = null

    /** Caller must hold RECORD_AUDIO. Returns false if the microphone couldn't be opened. */
    @SuppressLint("MissingPermission")
    fun start(): Boolean {
        if (running) return true
        val channelMask = if (channels == 2) AudioFormat.CHANNEL_IN_STEREO else AudioFormat.CHANNEL_IN_MONO
        val chunkBytes = sampleRate / 50 * channels * 2
        val minBuffer = AudioRecord.getMinBufferSize(sampleRate, channelMask, AudioFormat.ENCODING_PCM_16BIT)
        if (minBuffer <= 0) return false
        val record = try {
            AudioRecord(MediaRecorder.AudioSource.VOICE_COMMUNICATION, sampleRate, channelMask, AudioFormat.ENCODING_PCM_16BIT,
                maxOf(minBuffer, chunkBytes * 4))
        } catch (e: Exception) {
            Log.e("Audio", "AudioRecord failed", e)
            return false
        }
        if (record.state != AudioRecord.STATE_INITIALIZED) {
            record.release()
            return false
        }
        running = true
        thread = Thread({
            val buffer = ByteArray(chunkBytes)
            record.startRecording()
            Log.i("Audio", "Microphone started ${sampleRate}Hz ${channels}ch")
            try {
                while (running) {
                    var filled = 0
                    while (filled < chunkBytes && running) {
                        val n = record.read(buffer, filled, chunkBytes - filled)
                        if (n <= 0) break
                        filled += n
                    }
                    if (filled == chunkBytes) onChunk(buffer.copyOf(), filled, MonoClock.nowUs)
                }
            } finally {
                record.stop()
                record.release()
                Log.i("Audio", "Microphone stopped")
            }
        }, "audio").apply { start() }
        return true
    }

    fun stop() {
        running = false
        thread?.join(500)
        thread = null
    }
}
