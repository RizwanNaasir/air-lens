package net.silow.airlens.camera

import android.annotation.SuppressLint
import android.content.Context
import android.hardware.camera2.CameraAccessException
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CameraMetadata
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.CaptureResult
import android.hardware.camera2.TotalCaptureResult
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.Process
import net.silow.airlens.util.AirLog
import net.silow.airlens.util.DebugTuning
import android.view.Surface
import net.silow.airlens.util.asExecutor
import net.silow.airlens.util.runSync

/**
 * Camera2 device + session + repeating request for one output surface.
 *
 * Latency-oriented request settings:
 * - `TEMPLATE_RECORD` with a fixed `[fps, fps]` AE range when available (steady pacing).
 * - **EIS off**: digital stabilisation buffers frames (1-3 frames of latency) and costs ISP power.
 *   OIS stays on where the lens has it; it is free.
 * - FAST noise reduction and edge modes instead of HIGH_QUALITY.
 * - The request doubles as the session parameters so the HAL picks the right sensor mode up front
 *   (avoids a reconfiguration glitch at 60 fps on some devices).
 *
 * All camera callbacks run on one process-wide camera thread that is never quit. If a pipeline
 * closes while `openCamera` is still in flight, the late `onOpened` still gets delivered, sees
 * [closed] and closes the device. A quit looper would drop that callback and leave the camera
 * locked until the process died.
 */
class CameraController(context: Context, private val listener: Listener) {
    interface Listener {
        fun onCameraStarted()
        fun onCameraError(message: String)
    }

    private val manager = context.getSystemService(CameraManager::class.java)
    private val handler = cameraHandler
    private val executor = handler.asExecutor()

    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var builder: CaptureRequest.Builder? = null
    private lateinit var lens: LensOption
    private val camera: CameraDescriptor get() = lens.descriptor
    private lateinit var target: Surface
    @Volatile private var closed = false
    /** Manual focus, 0 = far (infinity) .. 1 = nearest; null = continuous autofocus. */
    @Volatile private var focus: Float? = null
    /** Where autofocus last put the lens, in diopters, so "fixed focus" can start from there. */
    @Volatile private var afDiopters: Float? = null

    /** Reads the lens position autofocus settled on; one field per frame, nothing else. */
    private val results = object : CameraCaptureSession.CaptureCallback() {
        override fun onCaptureCompleted(s: CameraCaptureSession, request: CaptureRequest, result: TotalCaptureResult) {
            if (focus == null) result.get(CaptureResult.LENS_FOCUS_DISTANCE)?.let { afDiopters = it }
        }
    }

    /** Autofocus's current position on the 0 (far) .. 1 (near) scale, or null if the HAL does not say. */
    fun autofocusPosition(): Float? {
        if (!::lens.isInitialized) return null
        val d = afDiopters ?: return null
        val min = camera.minFocusDiopters
        return if (min > 0f) (d / min).coerceIn(0f, 1f) else null
    }

    @SuppressLint("MissingPermission") // checked by the activity before the service starts
    fun open(lens: LensOption, target: Surface, fps: Int, focus: Float? = null) {
        this.lens = lens
        this.target = target
        this.focus = focus
        handler.post {
            if (closed) return@post
            try {
                manager.openCamera(lens.cameraId, executor, deviceCallback(fps))
            } catch (e: CameraAccessException) {
                listener.onCameraError("openCamera failed: ${e.reason} ${e.message}")
            } catch (e: SecurityException) {
                listener.onCameraError("camera permission missing or background access denied")
            } catch (e: IllegalArgumentException) {
                listener.onCameraError("camera ${lens.cameraId} does not exist")
            }
        }
    }

    private fun deviceCallback(fps: Int) = object : CameraDevice.StateCallback() {
        override fun onOpened(d: CameraDevice) {
            if (closed) {
                d.close()
                return
            }
            device = d
            try {
                createSession(d, fps)
            } catch (e: Exception) {
                listener.onCameraError("session setup failed: ${e.message}")
            }
        }

        override fun onDisconnected(d: CameraDevice) {
            d.close()
            device = null
            if (!closed) listener.onCameraError("camera disconnected (taken by another app or the camera service restarted)")
        }

        override fun onError(d: CameraDevice, error: Int) {
            d.close()
            device = null
            if (!closed) listener.onCameraError("camera error ${errorName(error)}")
        }
    }

    private fun createSession(d: CameraDevice, fps: Int) {
        val template = if (DebugTuning.recordTemplate == false) CameraDevice.TEMPLATE_PREVIEW else CameraDevice.TEMPLATE_RECORD
        val b = d.createCaptureRequest(template).apply {
            addTarget(target)
            applyLowLatencyControls(this, fps)
        }
        builder = b
        val output = OutputConfiguration(target).apply {
            // Stream from one sub-camera of a logical multi-camera (ultra-wide / telephoto).
            lens.physicalId?.let { setPhysicalCameraId(it) }
        }
        val config = SessionConfiguration(SessionConfiguration.SESSION_REGULAR, listOf(output), executor, sessionCallback)
        runCatching { config.sessionParameters = b.build() }
        // API 29+: ask before trying, so an unsupported lens/size gives a clear message instead of
        // a generic configure failure. Some HALs cannot answer the query; then just try.
        val supported = runCatching { d.isSessionConfigurationSupported(config) }.getOrNull()
        if (supported == false) {
            listener.onCameraError("${lens.label} cannot stream this size/frame rate on this device")
            return
        }
        d.createCaptureSession(config)
    }

    private val sessionCallback = object : CameraCaptureSession.StateCallback() {
        override fun onConfigured(s: CameraCaptureSession) {
            if (closed) {
                s.close()
                return
            }
            session = s
            try {
                s.setRepeatingRequest(builder!!.build(), results, handler)
                listener.onCameraStarted()
            } catch (e: Exception) {
                listener.onCameraError("setRepeatingRequest failed: ${e.message}")
            }
        }

        override fun onConfigureFailed(s: CameraCaptureSession) {
            if (!closed) listener.onCameraError("capture session configuration failed (unsupported size/fps?)")
        }
    }

    private fun applyLowLatencyControls(b: CaptureRequest.Builder, fps: Int) {
        CameraCapabilities.chooseFpsRange(camera.rawFpsRanges, fps)?.let {
            b.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it)
        }
        b.set(CaptureRequest.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO)
        applyFocus(b)
        b.set(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE, CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_OFF)
        if (CameraMetadata.LENS_OPTICAL_STABILIZATION_MODE_ON in camera.oisModes) {
            b.set(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE, CameraMetadata.LENS_OPTICAL_STABILIZATION_MODE_ON)
        }
        if (CameraMetadata.NOISE_REDUCTION_MODE_FAST in camera.noiseReductionModes) {
            b.set(CaptureRequest.NOISE_REDUCTION_MODE, CameraMetadata.NOISE_REDUCTION_MODE_FAST)
        }
        if (CameraMetadata.EDGE_MODE_FAST in camera.edgeModes) {
            b.set(CaptureRequest.EDGE_MODE, CameraMetadata.EDGE_MODE_FAST)
        }
        // Zoom-ratio lenses (API 30+): the HAL switches to the ultra-wide below 1x.
        val zoom = lens.zoomRatio
        if (zoom != null && Build.VERSION.SDK_INT >= 30) {
            b.set(CaptureRequest.CONTROL_ZOOM_RATIO, zoom)
        }
    }

    /**
     * Continuous autofocus, or a fixed distance when autofocus keeps hunting (low light, glass,
     * plain walls). Fixed focus sets AF off and LENS_FOCUS_DISTANCE in diopters, scaled from 0
     * (infinity) to the lens's nearest distance.
     */
    private fun applyFocus(b: CaptureRequest.Builder) {
        val f = focus
        if (f != null && camera.supportsManualFocus) {
            b.set(CaptureRequest.CONTROL_AF_MODE, CameraMetadata.CONTROL_AF_MODE_OFF)
            b.set(CaptureRequest.LENS_FOCUS_DISTANCE, f.coerceIn(0f, 1f) * camera.minFocusDiopters)
        } else if (CameraMetadata.CONTROL_AF_MODE_CONTINUOUS_VIDEO in camera.afModes) {
            b.set(CaptureRequest.CONTROL_AF_MODE, CameraMetadata.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
        }
    }

    /** Changes focus in the repeating request only: no session reconfiguration, no glitch. */
    fun setFocus(value: Float?) {
        focus = value
        handler.post {
            val s = session ?: return@post
            val b = builder ?: return@post
            applyFocus(b)
            runCatching { s.setRepeatingRequest(b.build(), results, handler) }
            AirLog.i(TAG, "focus -> ${value?.let { "manual %.2f".format(it) } ?: "auto"}")
        }
    }

    /**
     * Thermal step-down: changes only the AE range in the repeating request, so the session is
     * not reconfigured and the stream does not glitch.
     */
    fun setFpsCap(fps: Int) {
        handler.post {
            val s = session ?: return@post
            val b = builder ?: return@post
            CameraCapabilities.chooseFpsRange(camera.rawFpsRanges, fps)?.let {
                b.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it)
                runCatching { s.setRepeatingRequest(b.build(), results, handler) }
                AirLog.i(TAG, "fps cap -> $it")
            }
        }
    }

    /** Stops the camera. Safe to call at any point, including while the open is in flight. */
    fun close() {
        closed = true
        handler.runSync(CLOSE_TIMEOUT_MS) {
            runCatching { session?.close() }
            runCatching { device?.close() }
            session = null
            device = null
            builder = null
        }
    }

    private fun errorName(error: Int) = when (error) {
        ERROR_CAMERA_IN_USE -> "IN_USE"
        ERROR_MAX_CAMERAS_IN_USE -> "MAX_CAMERAS_IN_USE"
        ERROR_CAMERA_DISABLED -> "DISABLED_BY_POLICY"
        ERROR_CAMERA_DEVICE -> "DEVICE"
        ERROR_CAMERA_SERVICE -> "SERVICE"
        else -> error.toString()
    }

    private companion object {
        const val TAG = "AirLens.Camera"
        const val CLOSE_TIMEOUT_MS = 2000L
        const val ERROR_CAMERA_IN_USE = CameraDevice.StateCallback.ERROR_CAMERA_IN_USE
        const val ERROR_MAX_CAMERAS_IN_USE = CameraDevice.StateCallback.ERROR_MAX_CAMERAS_IN_USE
        const val ERROR_CAMERA_DISABLED = CameraDevice.StateCallback.ERROR_CAMERA_DISABLED
        const val ERROR_CAMERA_DEVICE = CameraDevice.StateCallback.ERROR_CAMERA_DEVICE
        const val ERROR_CAMERA_SERVICE = CameraDevice.StateCallback.ERROR_CAMERA_SERVICE

        val cameraHandler: Handler by lazy {
            val t = HandlerThread("AirLens-Camera", Process.THREAD_PRIORITY_DISPLAY)
            t.start()
            Handler(t.looper)
        }
    }
}
