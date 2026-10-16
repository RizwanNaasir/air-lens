package net.silow.airlens.stream

import android.content.Context
import net.silow.airlens.util.AirLog
import android.view.Surface
import net.silow.airlens.camera.CameraCapabilities
import net.silow.airlens.camera.CameraController
import net.silow.airlens.camera.LensOption
import net.silow.airlens.camera.SizeFps
import net.silow.airlens.encoder.EncoderChoice
import net.silow.airlens.encoder.EncoderSelector
import net.silow.airlens.encoder.EncoderSettings
import net.silow.airlens.encoder.RateDecision
import net.silow.airlens.encoder.RatePriority
import net.silow.airlens.encoder.VideoEncoder
import net.silow.airlens.gl.FrameTransform
import net.silow.airlens.gl.GlFrameProcessor
import net.silow.airlens.net.EncodedFrame
import net.silow.airlens.net.EncodedFramePool
import net.silow.airlens.net.Protocol
import net.silow.airlens.net.StreamInfo
import net.silow.airlens.net.StreamServer
import kotlin.math.max
import kotlin.math.min

/**
 * One camera → (GL) → encoder chain for a fixed output size and codec. The service builds a new
 * pipeline (and a new stream epoch) whenever any of those change, or to recover from an error.
 * The server outlives pipelines, so the PC stays connected across restarts.
 *
 * Lens switches do *not* need a new pipeline in GL mode: only the camera session is replaced,
 * while the GL stage and encoder keep running at the same output size.
 *
 * Teardown order matters and is fixed in [close]: the camera stops producing first, then the GL
 * stage (consumer of the camera, producer for the encoder), then the encoder.
 */
class StreamPipeline(
    private val context: Context,
    private val config: StreamConfig,
    lens: LensOption,
    private val pool: EncodedFramePool,
    private val server: StreamServer,
    private val epoch: Int,
    private val listener: Listener,
) : VideoEncoder.Sink, CameraController.Listener {

    interface Listener {
        /** Camera thread. */
        fun onPipelineStarted(pipeline: StreamPipeline)

        /** Any thread. The listener closes the pipeline; the pipeline never closes itself. */
        fun onPipelineError(pipeline: StreamPipeline, reason: String)
    }

    @Volatile var lens: LensOption = lens
        private set

    private var encoder: VideoEncoder? = null
    private var gl: GlFrameProcessor? = null
    @Volatile private var camera: CameraController? = null
    private lateinit var cameraTarget: Surface
    @Volatile private var deviceRotation = 0
    @Volatile private var closed = false
    @Volatile private var appliedFps = 0
    @Volatile private var appliedBitrate = 0
    /** Manual focus (0 far .. 1 near) or null for autofocus; kept across lens-switch reopens. */
    @Volatile var focus: Float? = null
        private set
    /** Draws the stream into the phone's preview while the UI shows one (GL mode only). */
    private val previewListener: (PreviewHub.Target?) -> Unit = { t -> gl?.setPreview(t?.surface, t?.width ?: 0, t?.height ?: 0) }

    var encoderChoice: EncoderChoice? = null
        private set

    /** Bitrate at the full configured frame rate ("Max" resolved against the encoder's ceiling). */
    var configuredBitrateBps = 0
        private set

    /** Frame rate the current lens can deliver at the chosen capture size (≤ configured fps). */
    @Volatile var configuredFps = config.fps
        private set

    /** Steady frame rates (fixed AE ranges) the current lens can hold, for [net.silow.airlens.encoder.RatePolicy]. */
    @Volatile var steadyFps: List<Int> = emptyList()
        private set

    val currentFps: Int get() = appliedFps
    val currentBitrateBps: Int get() = appliedBitrate
    val framesEncoded: Long get() = encoder?.framesEncoded ?: 0
    val glFramesSkipped: Long get() = gl?.framesSkipped ?: 0
    val encoderFeatures: List<String> get() = encoder?.appliedFeatures.orEmpty()

    private val width get() = config.outputWidth
    private val height get() = config.outputHeight

    fun start() {
        val choice = EncoderSelector.select(config.codec, width, height, config.fps)
            ?.takeIf { it.isHardware }
            ?: throw IllegalStateException("no hardware ${config.codec} encoder for ${width}x$height@${config.fps}")
        encoderChoice = choice
        configuredBitrateBps = config.resolveBitrate(choice.maxBitrateBps)

        val capture = if (config.directMode) null else captureSizeFor(lens)
        updateLensRates(capture)

        val enc = VideoEncoder(
            choice,
            EncoderSettings(width, height, configuredFps, configuredBitrateBps, config.keyframeIntervalSec),
            pool,
            this,
        )
        encoder = enc
        appliedBitrate = configuredBitrateBps
        appliedFps = configuredFps
        server.configureForBitrate(configuredBitrateBps)
        // Before the encoder can emit anything, so the PC flushes its decoder ahead of the new config.
        server.updateStreamInfo(streamInfo())
        val encoderSurface = enc.start()

        cameraTarget = if (capture == null) {
            encoderSurface
        } else {
            val processor = GlFrameProcessor(encoderSurface, width, height, capture.width, capture.height) { t ->
                fail("GL: ${t.message}")
            }
            gl = processor
            applyTransform()
            processor.start().also { PreviewHub.add(previewListener) }
        }
        AirLog.i(TAG, "start ${lens.label}: ${width}x$height@$configuredFps, ${configuredBitrateBps / 1_000_000} Mbps, " +
            "capture=${capture ?: "direct"}, steady=$steadyFps")
        camera = CameraController(context, this).also { it.open(lens, cameraTarget, configuredFps, focus) }
    }

    /** Where autofocus has the lens now (0 far .. 1 near), or null if unknown. */
    fun autofocusPosition(): Float? = camera?.autofocusPosition()

    /** Null = autofocus. Ignored by lenses without a focus motor. */
    fun setFocus(value: Float?) {
        focus = value
        camera?.setFocus(value)
    }

    /** Must be set before [start]: the pipeline opens the camera with it. */
    fun initialFocus(value: Float?) {
        focus = value
    }

    /**
     * Replaces the camera session with [newLens]. Returns false when this pipeline cannot do it in
     * place (direct mode and the new lens lacks the exact encoder size); the caller then rebuilds.
     */
    fun switchLens(newLens: LensOption): Boolean {
        if (closed) return false
        if (config.directMode && newLens.descriptor.codecSizes.none { it.width == width && it.height == height }) {
            return false
        }
        camera?.close()
        lens = newLens
        val capture = gl?.let { processor ->
            captureSizeFor(newLens).also { processor.setCameraBufferSize(it.width, it.height) }
        }
        updateLensRates(capture)
        appliedFps = min(appliedFps, configuredFps)
        applyTransform()
        if (config.directMode) server.updateStreamInfo(streamInfo())
        camera = CameraController(context, this).also { it.open(newLens, cameraTarget, appliedFps, focus) }
        AirLog.i(TAG, "lens switched to ${newLens.label}, capture=${capture ?: "direct"}, fps<=$configuredFps")
        return true
    }

    /** Clockwise device rotation from [net.silow.airlens.power.OrientationTracker]. */
    fun setDeviceRotation(degrees: Int) {
        deviceRotation = degrees
        applyTransform()
        // Direct mode cannot rotate on the phone; tell the receiver instead (same epoch, no flush).
        if (config.directMode && !closed) server.updateStreamInfo(streamInfo())
    }

    /** Applies a [net.silow.airlens.encoder.RatePolicy] decision; no-ops when nothing changed. */
    fun applyRate(decision: RateDecision) {
        if (closed) return
        if (decision.bitrateBps != appliedBitrate) {
            appliedBitrate = decision.bitrateBps
            encoder?.setBitrate(decision.bitrateBps)
            server.configureForBitrate(decision.bitrateBps)
        }
        val fps = min(decision.fps, configuredFps)
        if (fps != appliedFps) {
            appliedFps = fps
            camera?.setFpsCap(fps)
        }
    }

    fun requestKeyframe() {
        encoder?.requestKeyframe()
    }

    fun close() {
        if (closed) return
        closed = true
        PreviewHub.remove(previewListener)
        runCatching { camera?.close() }.onFailure { AirLog.w(TAG, "camera close", it) }
        runCatching { gl?.release() }.onFailure { AirLog.w(TAG, "gl release", it) }
        runCatching { encoder?.release() }.onFailure { AirLog.w(TAG, "encoder release", it) }
        camera = null
        gl = null
        encoder = null
    }

    private fun updateLensRates(capture: SizeFps?) {
        val d = lens.descriptor
        val sizeFps = capture?.maxFps
            ?: d.codecSizes.firstOrNull { it.width == width && it.height == height }?.maxFps
            ?: config.fps
        configuredFps = min(config.fps, sizeFps)
        steadyFps = d.rawFpsRanges
            .map { CameraCapabilities.normalizeFps(it.lower, d.rawFpsRanges) to CameraCapabilities.normalizeFps(it.upper, d.rawFpsRanges) }
            .filter { (lo, hi) -> lo == hi && hi <= configuredFps }
            .map { it.second }
            .distinct()
            .sorted()
    }

    private fun applyTransform() {
        val processor = gl ?: return
        processor.rotationDegrees = config.rotationLock ?: deviceRotation
        // Camera2 already mirrors front cameras for SurfaceTexture consumers; flip again unless a
        // mirror image is wanted, and flip back cameras only when it is.
        processor.flipHorizontal = lens.isFront xor config.mirror
        processor.scaleMode = config.scaleMode
    }

    /**
     * Camera buffer size for GL mode. In quality-first mode a native-resolution buffer at a lower
     * frame rate beats an upscaled one at full rate (e.g. the P30 telephoto: 4K at 20 fps rather
     * than 1080p30 stretched to 4K).
     */
    private fun captureSizeFor(lens: LensOption): SizeFps {
        val d = lens.descriptor
        fun covers(s: SizeFps) = s.longSide >= max(width, height) && s.shortSide >= min(width, height)
        val atFullRate = CameraCapabilities.chooseCaptureSize(d, width, height, config.fps)
        if (atFullRate != null && covers(atFullRate)) return atFullRate
        if (config.priority == RatePriority.QUALITY) {
            CameraCapabilities.chooseCaptureSize(d, width, height, RATE_FLOOR)?.takeIf(::covers)?.let { return it }
        }
        return atFullRate
            ?: CameraCapabilities.chooseCaptureSize(d, width, height, RATE_FLOOR)
            ?: d.textureSizes.firstOrNull()
            ?: throw IllegalStateException("${lens.label} reports no output sizes")
    }

    private fun streamInfo() = StreamInfo(
        codec = if (config.codec == VideoCodec.HEVC) Protocol.CODEC_HEVC else Protocol.CODEC_H264,
        width = width,
        height = height,
        fps = configuredFps,
        bitrateBps = configuredBitrateBps,
        rotationDegrees = if (config.directMode) {
            FrameTransform.directModeRotation(lens.descriptor.sensorOrientation, lens.isFront, config.rotationLock ?: deviceRotation)
        } else {
            0
        },
        fullRange = false,
        mirrored = config.directMode && config.mirror,
        epoch = epoch,
    )

    private fun fail(reason: String) {
        if (!closed) listener.onPipelineError(this, reason)
    }

    // VideoEncoder.Sink — encoder thread
    override fun onEncodedFrame(frame: EncodedFrame) {
        if (closed) frame.recycle() else server.submit(frame)
    }

    override fun onEncoderError(error: Throwable) = fail("encoder: ${error.message ?: error}")

    // CameraController.Listener — camera thread
    override fun onCameraStarted() {
        if (!closed) listener.onPipelineStarted(this)
    }

    override fun onCameraError(message: String) = fail(message)

    private companion object {
        const val TAG = "AirLens.Pipeline"
        const val RATE_FLOOR = 15
    }
}
