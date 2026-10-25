package local.phonewebcam.ui

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import com.google.android.material.button.MaterialButton
import com.google.android.material.button.MaterialButtonToggleGroup
import java.net.Inet4Address
import java.net.NetworkInterface
import kotlin.math.abs
import local.phonewebcam.R
import local.phonewebcam.camera.CameraController
import local.phonewebcam.camera.CameraPosition
import local.phonewebcam.camera.JpegEncoder
import local.phonewebcam.camera.StreamResolution
import local.phonewebcam.net.DeviceIdentity
import local.phonewebcam.net.DeviceInfoTemplate
import local.phonewebcam.net.FrameGate
import local.phonewebcam.net.MonoClock
import local.phonewebcam.net.PairingStore
import local.phonewebcam.net.ServerListener
import local.phonewebcam.net.StreamServer
import local.phonewebcam.protocol.ErrorCodes
import local.phonewebcam.protocol.StreamStates
import local.phonewebcam.protocol.VideoCodec
import local.phonewebcam.protocol.VideoFrameFlags
import local.phonewebcam.protocol.VideoFrameHeader
import org.json.JSONObject

/**
 * Single screen: viewfinder + control panel. Also the coordinator: camera → gate → JPEG → server,
 * and the handler for commands from the PC (same behaviour as the iOS AppModel).
 */
class MainActivity : AppCompatActivity(), ServerListener {
    private lateinit var previewView: PreviewView
    private lateinit var emptyState: View
    private lateinit var pairingCard: View
    private lateinit var pairingCodeText: TextView
    private lateinit var tally: View
    private lateinit var statusTitle: TextView
    private lateinit var statusDetail: TextView
    private lateinit var cameraGroup: MaterialButtonToggleGroup
    private lateinit var resolutionGroup: MaterialButtonToggleGroup
    private lateinit var fpsText: TextView
    private lateinit var torchButton: MaterialButton
    private lateinit var errorText: TextView
    private lateinit var addressText: TextView
    private lateinit var startButton: MaterialButton

    private lateinit var server: StreamServer
    private lateinit var camera: CameraController
    private val gate = FrameGate()
    private val encoder = JpegEncoder()

    private var isRunning = false
    private var isStreaming = false
    private var inBackground = false
    private var connectedPc: String? = null
    private var pairingCode: String? = null
    private var torchOn = false
    private var position = CameraPosition.BACK
    private var resolution = StreamResolution.HD1080

    @Volatile private var fpsCount = 0
    private var fpsWindowStart = 0L
    private val fpsTicker = object : Runnable {
        override fun run() {
            val now = MonoClock.nowUs
            val seconds = (now - fpsWindowStart) / 1e6
            if (isRunning && seconds > 0) fpsText.text = getString(R.string.fps_value, fpsCount / seconds)
            fpsCount = 0
            fpsWindowStart = now
            fpsText.postDelayed(this, 1000)
        }
    }

    private val requestCamera = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) startCamera() else showError(getString(R.string.error_camera_permission))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        previewView = findViewById(R.id.preview)
        emptyState = findViewById(R.id.empty_state)
        pairingCard = findViewById(R.id.pairing_card)
        pairingCodeText = findViewById(R.id.pairing_code)
        tally = findViewById(R.id.tally)
        statusTitle = findViewById(R.id.status_title)
        statusDetail = findViewById(R.id.status_detail)
        cameraGroup = findViewById(R.id.camera_group)
        resolutionGroup = findViewById(R.id.resolution_group)
        fpsText = findViewById(R.id.fps_text)
        torchButton = findViewById(R.id.torch_button)
        errorText = findViewById(R.id.error_text)
        addressText = findViewById(R.id.address_text)
        startButton = findViewById(R.id.start_button)

        server = StreamServer(PairingStore(this), this)
        camera = CameraController(this)
        camera.onFrame = ::onCameraFrame

        cameraGroup.check(R.id.camera_back)
        resolutionGroup.check(R.id.res_1080)
        cameraGroup.addOnButtonCheckedListener { _, id, checked ->
            if (checked) setPosition(if (id == R.id.camera_front) CameraPosition.FRONT else CameraPosition.BACK)
        }
        resolutionGroup.addOnButtonCheckedListener { _, id, checked ->
            if (checked) setResolution(if (id == R.id.res_720) StreamResolution.HD720 else StreamResolution.HD1080)
        }
        torchButton.setOnClickListener { setTorch(!torchOn) }
        startButton.setOnClickListener { if (isRunning) stopCamera() else requestStart() }
        previewView.setOnTouchListener { v, event ->
            if (event.action == MotionEvent.ACTION_UP && isRunning) {
                camera.focus(previewView, event.x, event.y)
                v.performClick()
            }
            true
        }
        render()
    }

    override fun onStart() {
        super.onStart()
        if (inBackground) {
            inBackground = false
            updateFlow()
        }
    }

    override fun onStop() {
        super.onStop()
        // CameraX stops the camera with the lifecycle; tell the PC instead of going silent.
        if (isRunning && !isChangingConfigurations) {
            inBackground = true
            updateFlow()
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        if (isRunning) server.stop()
    }

    // MARK: user actions

    private fun requestStart() {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            startCamera()
        } else {
            requestCamera.launch(Manifest.permission.CAMERA)
        }
    }

    private fun startCamera() {
        showError(null)
        bindCamera {
            server.start(deviceInfo())
            isRunning = true
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            fpsWindowStart = MonoClock.nowUs
            fpsText.removeCallbacks(fpsTicker)
            fpsText.post(fpsTicker)
            render()
        }
    }

    private fun stopCamera() {
        gate.setStreaming(false)
        server.stop()
        camera.unbind()
        isRunning = false
        isStreaming = false
        connectedPc = null
        pairingCode = null
        setTorch(false)
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        fpsText.removeCallbacks(fpsTicker)
        render()
    }

    private fun bindCamera(then: () -> Unit = {}) {
        camera.bind(this, previewView, position, resolution) { error ->
            if (error != null) {
                showError(getString(R.string.error_camera_start, error.message ?: error.javaClass.simpleName))
                server.sendError(ErrorCodes.CAMERA_UNAVAILABLE, error.message ?: "Camera failed")
            } else {
                torchOn = false
                then()
                updateFlow()
                render()
            }
        }
    }

    private fun setPosition(value: CameraPosition) {
        if (value == position) return
        position = value
        cameraGroup.check(if (value == CameraPosition.FRONT) R.id.camera_front else R.id.camera_back)
        if (isRunning) bindCamera()
    }

    private fun setResolution(value: StreamResolution) {
        if (value == resolution) return
        resolution = value
        resolutionGroup.check(if (value == StreamResolution.HD720) R.id.res_720 else R.id.res_1080)
        if (isRunning) bindCamera()
    }

    private fun setTorch(on: Boolean) {
        torchOn = on && isRunning && camera.hasTorch
        camera.setTorch(torchOn)
        render()
    }

    // MARK: frame path (analyzer thread)

    private fun onCameraFrame(image: androidx.camera.core.ImageProxy) {
        fpsCount++
        val (frameId, send) = gate.capture()
        if (!send) {
            image.close()
            return
        }
        try {
            // Prefer the sensor timestamp when it shares our clock (REALTIME source); else time of arrival.
            val now = MonoClock.nowUs
            val sensorUs = image.imageInfo.timestamp / 1_000
            val ptsUs = if (abs(now - sensorUs) < 1_000_000) sensorUs else now
            val width = image.width
            val height = image.height
            val encodeStart = System.nanoTime()
            val (buffer, length) = encoder.encode(image, (gate.quality * 100).toInt())
            image.close()
            // Copy: the encoder reuses its buffer for the next frame, which may be encoded while this one is sent.
            val jpeg = buffer.copyOf(length)
            recordEncodeTime(System.nanoTime() - encodeStart, width, height)
            val header = VideoFrameHeader(VideoCodec.MJPEG, VideoFrameFlags.KEYFRAME, 0, width, height, frameId, ptsUs)
            server.sendFrame(header, jpeg, length) { gate.finished() }
        } catch (e: Exception) {
            Log.e("Frame", "Encode failed", e)
            image.close()
            gate.finished()
        }
    }

    private var encodeNanos = 0L
    private var encodeCount = 0

    /** Analyzer thread only. Logs the average JPEG encode time every ~5 s (the main 1080p bottleneck). */
    private fun recordEncodeTime(nanos: Long, width: Int, height: Int) {
        encodeNanos += nanos
        if (++encodeCount == 150) {
            Log.i("Frame", "JPEG encode ${width}x$height avg ${"%.1f".format(encodeNanos / encodeCount / 1e6)} ms")
            encodeNanos = 0
            encodeCount = 0
        }
    }

    // MARK: ServerListener (main thread)

    override fun onServerState(listening: Boolean, error: String?) {
        if (error != null && isRunning) showError(getString(R.string.error_network, error))
        else if (listening) showError(null)
        render()
    }

    override fun onPairingCode(code: String?) {
        pairingCode = code
        render()
    }

    override fun onClientChanged(pcName: String?) {
        connectedPc = pcName
        if (pcName == null) {
            isStreaming = false
            setTorch(false)
        }
        updateFlow()
        render()
    }

    override fun onStartStream(camera: String, width: Int, height: Int, quality: Double) {
        gate.quality = quality
        isStreaming = true
        val newPosition = CameraPosition.fromId(camera) ?: position
        val newResolution = StreamResolution.of(width, height) ?: resolution
        if (newPosition != position || newResolution != resolution) {
            position = newPosition
            resolution = newResolution
            cameraGroup.check(if (position == CameraPosition.FRONT) R.id.camera_front else R.id.camera_back)
            resolutionGroup.check(if (resolution == StreamResolution.HD720) R.id.res_720 else R.id.res_1080)
            bindCamera()
        } else {
            updateFlow()
        }
        render()
    }

    override fun onStopStream() {
        isStreaming = false
        updateFlow()
        render()
    }

    override fun onSwitchCamera(camera: String) {
        CameraPosition.fromId(camera)?.let(::setPosition)
    }

    override fun onSetResolution(width: Int, height: Int) {
        StreamResolution.of(width, height)?.let(::setResolution)
    }

    override fun onSetQuality(quality: Double) {
        gate.quality = quality
    }

    override fun onSetTorch(on: Boolean) {
        setTorch(on)
    }

    // MARK: state

    /** Single place that decides whether frames flow, and tells the PC. */
    private fun updateFlow() {
        gate.setStreaming(isRunning && isStreaming && !inBackground)
        if (connectedPc == null) return
        val state = JSONObject()
        when {
            !isStreaming -> state.put("state", StreamStates.STOPPED)
            inBackground -> state.put("state", StreamStates.PAUSED)
                .put("reason", getString(R.string.paused_background))
            else -> state.put("state", StreamStates.STREAMING)
        }
        state.put("camera", position.id).put("width", resolution.width).put("height", resolution.height)
            .put("fps", 30).put("codec", "mjpeg")
        server.sendStreamState(state)
    }

    private fun render() {
        val live = isStreaming && !inBackground && connectedPc != null
        tally.background.setTint(ContextCompat.getColor(this, when {
            live -> R.color.tally_live
            isRunning -> R.color.tally_busy
            else -> R.color.tally_idle
        }))
        statusTitle.setText(when {
            !isRunning -> R.string.status_off
            connectedPc != null && live -> R.string.status_live
            connectedPc != null -> R.string.status_connected
            pairingCode != null -> R.string.status_pairing
            else -> R.string.status_waiting
        })
        statusDetail.text = when {
            !isRunning -> getString(R.string.detail_off)
            connectedPc != null -> getString(R.string.detail_connected, connectedPc)
            pairingCode != null -> getString(R.string.detail_pairing)
            else -> getString(R.string.detail_waiting)
        }
        emptyState.visibility = if (isRunning) View.GONE else View.VISIBLE
        pairingCard.visibility = if (pairingCode != null) View.VISIBLE else View.GONE
        pairingCode?.let { pairingCodeText.text = getString(R.string.pairing_code_format, it.substring(0, 3), it.substring(3)) }
        torchButton.isEnabled = isRunning && camera.hasTorch
        torchButton.setIconResource(if (torchOn) R.drawable.ic_torch_on else R.drawable.ic_torch_off)
        startButton.setText(if (isRunning) R.string.stop_camera else R.string.start_camera)
        addressText.visibility = if (isRunning) View.VISIBLE else View.GONE
        addressText.text = getString(R.string.connect_hint, wifiAddress() ?: getString(R.string.no_wifi), StreamServer.PORT)
        if (!isRunning) fpsText.setText(R.string.fps_default)
    }

    private fun showError(message: String?) {
        errorText.text = message
        errorText.visibility = if (message == null) View.GONE else View.VISIBLE
    }

    private fun deviceInfo() = DeviceInfoTemplate(
        deviceId = DeviceIdentity.deviceId(this),
        deviceName = "${Build.MANUFACTURER.replaceFirstChar { it.uppercase() }} ${Build.MODEL}",
        model = Build.MODEL,
        osVersion = "Android ${Build.VERSION.RELEASE}",
        appVersion = packageManager.getPackageInfo(packageName, 0).versionName ?: "0.1.0",
        cameras = CameraPosition.entries.filter { camera.hasCamera(it) }.map { it.id to (it == CameraPosition.BACK) },
        formats = StreamResolution.entries.map { it.width to it.height },
    )

    /** IPv4 of the Wi-Fi interface, for manual connection from the PC. */
    private fun wifiAddress(): String? = try {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback && (it.name.startsWith("wlan") || it.name.startsWith("eth")) }
            .flatMap { it.inetAddresses.toList() }
            .firstOrNull { it is Inet4Address && it.isSiteLocalAddress }
            ?.hostAddress
    } catch (_: Exception) {
        null
    }
}
