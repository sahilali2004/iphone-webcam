package local.phonewebcam.camera

import android.content.Context
import android.util.Log
import android.util.Range
import android.util.Size
import android.view.Surface
import androidx.annotation.OptIn
import androidx.camera.camera2.interop.Camera2Interop
import androidx.camera.camera2.interop.ExperimentalCamera2Interop
import androidx.camera.core.Camera
import androidx.camera.core.CameraSelector
import androidx.camera.core.FocusMeteringAction
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.core.resolutionselector.AspectRatioStrategy
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleOwner
import android.hardware.camera2.CaptureRequest
import java.util.concurrent.Executors

enum class CameraPosition(val id: String, val label: String) {
    BACK("back", "Rear"),
    FRONT("front", "Front");

    companion object {
        fun fromId(id: String) = entries.firstOrNull { it.id == id }
    }
}

enum class StreamResolution(val label: String, val width: Int, val height: Int) {
    HD720("720p", 1280, 720),
    HD1080("1080p", 1920, 1080);

    companion object {
        fun of(width: Int, height: Int) = entries.firstOrNull { it.width == width && it.height == height }
    }
}

/**
 * CameraX: on-screen preview + ImageAnalysis frames for the PC (never the preview surface).
 * Output is rotated to the (locked landscape) display orientation by CameraX itself.
 */
class CameraController(private val context: Context) {
    /** Called on the analyzer thread for every frame. The callee must close the image. */
    var onFrame: ((ImageProxy) -> Unit)? = null

    private val analyzerExecutor = Executors.newSingleThreadExecutor { Thread(it, "camera-analyzer") }
    private var provider: ProcessCameraProvider? = null
    private var camera: Camera? = null

    val hasTorch: Boolean get() = camera?.cameraInfo?.hasFlashUnit() == true

    fun hasCamera(position: CameraPosition): Boolean =
        provider?.hasCamera(selector(position)) ?: true

    /** (Re)binds preview + analysis to [owner]. CameraX stops/starts the camera with the lifecycle. */
    fun bind(owner: LifecycleOwner, previewView: PreviewView, position: CameraPosition, resolution: StreamResolution,
             onResult: (Throwable?) -> Unit) {
        val future = ProcessCameraProvider.getInstance(context)
        future.addListener({
            try {
                val provider = future.get().also { provider = it }
                val rotation = previewView.display?.rotation ?: Surface.ROTATION_90
                val resolutionSelector = ResolutionSelector.Builder()
                    .setAspectRatioStrategy(AspectRatioStrategy.RATIO_16_9_FALLBACK_AUTO_STRATEGY)
                    .setResolutionStrategy(ResolutionStrategy(Size(resolution.width, resolution.height),
                        ResolutionStrategy.FALLBACK_RULE_CLOSEST_LOWER_THEN_HIGHER))
                    .build()

                val preview = Preview.Builder()
                    .setResolutionSelector(resolutionSelector)
                    .setTargetRotation(rotation)
                    .build()
                    .also { it.surfaceProvider = previewView.surfaceProvider }

                val analysisBuilder = ImageAnalysis.Builder()
                    .setResolutionSelector(resolutionSelector)
                    .setTargetRotation(rotation)
                    .setOutputImageRotationEnabled(true)
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888)
                lockFrameRate(analysisBuilder)
                val analysis = analysisBuilder.build()
                analysis.setAnalyzer(analyzerExecutor) { image ->
                    val handler = onFrame
                    if (handler == null) image.close() else handler(image)
                }

                provider.unbindAll()
                camera = provider.bindToLifecycle(owner, selector(position), preview, analysis)
                Log.i("Camera", "Bound ${position.id} ${resolution.label}; analysis ${analysis.resolutionInfo?.resolution}")
                onResult(null)
            } catch (e: Exception) {
                Log.e("Camera", "Bind failed", e)
                onResult(e)
            }
        }, ContextCompat.getMainExecutor(context))
    }

    fun unbind() {
        provider?.unbindAll()
        camera = null
    }

    fun setTorch(on: Boolean) {
        if (hasTorch) camera?.cameraControl?.enableTorch(on)
    }

    fun focus(previewView: PreviewView, x: Float, y: Float) {
        val point = previewView.meteringPointFactory.createPoint(x, y)
        camera?.cameraControl?.startFocusAndMetering(FocusMeteringAction.Builder(point).build())
    }

    private fun selector(position: CameraPosition) =
        if (position == CameraPosition.FRONT) CameraSelector.DEFAULT_FRONT_CAMERA else CameraSelector.DEFAULT_BACK_CAMERA

    @OptIn(ExperimentalCamera2Interop::class)
    private fun lockFrameRate(builder: ImageAnalysis.Builder) {
        Camera2Interop.Extender(builder).setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range(30, 30))
    }
}
