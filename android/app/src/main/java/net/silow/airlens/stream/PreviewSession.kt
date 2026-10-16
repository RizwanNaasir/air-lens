package net.silow.airlens.stream

import android.content.Context
import net.silow.airlens.camera.CameraCapabilities
import net.silow.airlens.camera.CameraController
import net.silow.airlens.camera.LensOption
import net.silow.airlens.gl.GlFrameProcessor
import net.silow.airlens.util.AirLog

/**
 * The phone's preview while nothing is streaming: camera → the same GL stage the stream uses →
 * the on-screen surface. It shows exactly the frame the PC will get: the stream's shape, crop,
 * rotation and mirror. It runs at a small capture size (720p), so it costs little.
 *
 * Owned by the UI's [PreviewController], on the main thread. It must be closed before the service
 * opens the camera for streaming.
 */
class PreviewSession(
    context: Context,
    private val lens: LensOption,
    private val config: StreamConfig,
    private val target: PreviewHub.Target,
    rotation: Int,
    private val onError: (String) -> Unit,
) : CameraController.Listener {
    private val camera = CameraController(context, this)
    private var gl: GlFrameProcessor? = null
    @Volatile private var closed = false

    init {
        val d = lens.descriptor
        val (w, h) = if (config.outputWidth * 3 == config.outputHeight * 4) 960 to 720 else 1280 to 720
        val capture = CameraCapabilities.chooseCaptureSize(d, w, h, PREVIEW_FPS)
            ?: d.textureSizes.firstOrNull()
            ?: throw IllegalStateException("${lens.label} reports no output sizes")
        val processor = GlFrameProcessor(
            target.surface, target.width, target.height, capture.width, capture.height,
            toEncoder = false,
            pictureAspect = config.outputWidth.toFloat() / config.outputHeight,
        ) { t -> if (!closed) onError("preview: ${t.message}") }
        gl = processor
        processor.flipHorizontal = lens.isFront xor config.mirror
        processor.scaleMode = config.scaleMode
        setRotation(rotation)
        camera.open(lens, processor.start(), PREVIEW_FPS)
        AirLog.i(TAG, "preview ${lens.label}: capture $capture -> ${target.width}x${target.height}")
    }

    fun setRotation(deviceRotation: Int) {
        gl?.rotationDegrees = config.rotationLock ?: deviceRotation
    }

    /** Camera first (the producer), then GL. Blocking, a few tens of ms. */
    fun close() {
        if (closed) return
        closed = true
        runCatching { camera.close() }
        runCatching { gl?.release() }
        gl = null
    }

    override fun onCameraStarted() = Unit

    override fun onCameraError(message: String) {
        if (!closed) onError(message)
    }

    private companion object {
        const val TAG = "AirLens.Preview"
        const val PREVIEW_FPS = 30
    }
}
