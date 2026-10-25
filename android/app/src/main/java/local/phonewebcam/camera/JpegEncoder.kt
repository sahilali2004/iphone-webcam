package local.phonewebcam.camera

import android.graphics.ImageFormat
import android.graphics.Rect
import android.graphics.YuvImage
import androidx.camera.core.ImageProxy
import java.io.ByteArrayOutputStream

/**
 * YUV_420_888 → NV21 → JPEG via YuvImage (libjpeg-turbo in the platform).
 * Buffers are reused across frames; call from one thread only (the analyzer thread).
 */
class JpegEncoder {
    private class Output(size: Int) : ByteArrayOutputStream(size) {
        val bytes: ByteArray get() = buf
    }

    private var nv21 = ByteArray(0)
    private var uPlane = ByteArray(0)
    private var vPlane = ByteArray(0)
    private val output = Output(512 * 1024)

    /** Returns (buffer, length). The buffer is reused by the next call: copy or send before then. */
    fun encode(image: ImageProxy, quality: Int): Pair<ByteArray, Int> {
        val width = image.width
        val height = image.height
        toNv21(image, width, height)
        output.reset()
        YuvImage(nv21, ImageFormat.NV21, width, height, null)
            .compressToJpeg(Rect(0, 0, width, height), quality.coerceIn(10, 100), output)
        return output.bytes to output.size()
    }

    private fun toNv21(image: ImageProxy, width: Int, height: Int) {
        val ySize = width * height
        val needed = ySize + ySize / 2
        if (nv21.size != needed) nv21 = ByteArray(needed)

        val (yP, uP, vP) = image.planes.let { Triple(it[0], it[1], it[2]) }

        // Y: copy row by row when rows are padded.
        val yBuf = yP.buffer.duplicate().apply { rewind() }
        if (yP.rowStride == width) {
            yBuf.get(nv21, 0, ySize)
        } else {
            for (row in 0 until height) {
                yBuf.position(row * yP.rowStride)
                yBuf.get(nv21, row * width, width)
            }
        }

        // Chroma: NV21 wants interleaved V,U at half resolution.
        val uBuf = uP.buffer.duplicate().apply { rewind() }
        val vBuf = vP.buffer.duplicate().apply { rewind() }
        if (uPlane.size < uBuf.remaining()) uPlane = ByteArray(uBuf.remaining())
        if (vPlane.size < vBuf.remaining()) vPlane = ByteArray(vBuf.remaining())
        val uLen = uBuf.remaining()
        val vLen = vBuf.remaining()
        uBuf.get(uPlane, 0, uLen)
        vBuf.get(vPlane, 0, vLen)

        val chromaH = height / 2
        val chromaW = width / 2
        val rowStride = uP.rowStride
        val pixelStride = uP.pixelStride
        var o = ySize
        for (row in 0 until chromaH) {
            val base = row * rowStride
            for (col in 0 until chromaW) {
                val i = base + col * pixelStride
                nv21[o++] = if (i < vLen) vPlane[i] else 0
                nv21[o++] = if (i < uLen) uPlane[i] else 0
            }
        }
    }
}
