package net.silow.airlens.encoder

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.Process
import net.silow.airlens.util.AirLog
import net.silow.airlens.util.DebugTuning
import net.silow.airlens.util.PipelineTiming
import android.view.Surface
import net.silow.airlens.net.EncodedFrame
import net.silow.airlens.net.EncodedFramePool
import net.silow.airlens.net.Protocol
import net.silow.airlens.util.runSync

data class EncoderSettings(
    val width: Int,
    val height: Int,
    val fps: Int,
    val bitrateBps: Int,
    val keyframeIntervalSec: Float,
)

/**
 * Hardware H.264/H.265 encoder with Surface input, in asynchronous callback mode on its own thread.
 *
 * Zero-copy contract: raw pixels arrive in GPU/gralloc buffers through [inputSurface]
 * (`createInputSurface()` between `configure()` and `start()`) and never touch the CPU. The only
 * copy is of the *compressed* access unit into a pooled [EncodedFrame], which has to happen anyway
 * because the output buffer must be released immediately or the encoder stalls.
 *
 * **Configure fallback ladder.** Optional low-latency keys are applied most-important first. If
 * `configure()` rejects the format, the least important remaining key is removed and a fresh
 * codec instance is tried. This exists because OMX encoders (e.g. HiSilicon on the Huawei P30)
 * make ACodec *fail* configure for some optional features they do not implement, notably
 * `prepend-sps-pps-to-idr-frames`. [appliedFeatures] records what actually stuck, for the device report.
 */
class VideoEncoder(
    private val choice: EncoderChoice,
    private val settings: EncoderSettings,
    private val pool: EncodedFramePool,
    private val sink: Sink,
) {
    interface Sink {
        /** Takes ownership of [frame]. Called on the encoder thread. */
        fun onEncodedFrame(frame: EncodedFrame)
        fun onEncoderError(error: Throwable)
    }

    private val thread = HandlerThread("AirLens-Encoder", Process.THREAD_PRIORITY_URGENT_DISPLAY).apply { start() }
    private val handler = Handler(thread.looper)
    private var codec: MediaCodec? = null
    private var pending: EncodedFrame? = null // a frame split across PARTIAL_FRAME buffers
    @Volatile private var released = false

    lateinit var inputSurface: Surface
        private set

    @Volatile var appliedFeatures: List<String> = emptyList()
        private set

    @Volatile var framesEncoded = 0L
        private set

    private class Feature(val name: String, val apply: (MediaFormat) -> Unit)

    /**
     * Far above the frame rate on purpose: it tells the codec to run at full clock instead of pacing
     * itself to the frame rate. Measured on the Huawei P30 at 4K30, this cut encoder delay from ~93 ms
     * to ~51 ms (glass-to-glass median 209 -> 182 ms). Codecs that reject it fall back via negotiation.
     */
    private val operatingRate: Int get() = DebugTuning.operatingRate ?: FAST_OPERATING_RATE

    private fun optionalFeatures(): List<Feature> = buildList {
        add(Feature("no-b-frames") { it.setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0) })
        choice.profile?.let { profile ->
            add(Feature("profile") { format ->
                format.setInteger(MediaFormat.KEY_PROFILE, profile)
                choice.level?.let { format.setInteger(MediaFormat.KEY_LEVEL, it) }
            })
        }
        if (choice.supportsCbr) {
            add(Feature("cbr") {
                it.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)
            })
        }
        add(Feature("realtime-priority") { it.setInteger(MediaFormat.KEY_PRIORITY, 0) })
        add(Feature("latency-1") { it.setInteger(MediaFormat.KEY_LATENCY, 1) })
        add(Feature("operating-rate") { it.setInteger(MediaFormat.KEY_OPERATING_RATE, operatingRate) })
        add(Feature("bt709-limited") {
            it.setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT709)
            it.setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_LIMITED)
            it.setInteger(MediaFormat.KEY_COLOR_TRANSFER, MediaFormat.COLOR_TRANSFER_SDR_VIDEO)
        })
        add(Feature("prepend-sps-pps") { it.setInteger(MediaFormat.KEY_PREPEND_HEADER_TO_SYNC_FRAMES, 1) })
        val name = choice.codecName.lowercase()
        if (name.contains("qti") || name.contains("qcom")) {
            add(Feature("qti-low-latency") { it.setInteger("vendor.qti-ext-enc-low-latency.enable", 1) })
        }
    }

    private fun baseFormat(): MediaFormat =
        MediaFormat.createVideoFormat(choice.mime, settings.width, settings.height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, settings.bitrateBps)
            setInteger(MediaFormat.KEY_FRAME_RATE, settings.fps)
            setFloat(MediaFormat.KEY_I_FRAME_INTERVAL, settings.keyframeIntervalSec)
        }

    /**
     * Configures and starts the codec. Returns the surface the camera or GL stage renders into.
     *
     * Feature negotiation, cached per codec + size + rate for the life of the process:
     * 1. Find the longest working prefix of [optionalFeatures] (most important first).
     * 2. Re-try each dropped feature on its own: OMX components usually reject one key, not all of
     *    them (on the P30, H.264 rejects only `profile` at 4K; CBR, priority and latency are fine).
     * 3. Configure for real with the surviving set.
     */
    fun start(): Surface {
        val all = optionalFeatures()
        val cacheKey = "${choice.codecName}/${settings.width}x${settings.height}@${settings.fps}/op$operatingRate"
        val cached = negotiated[cacheKey]?.let { names -> all.filter { it.name in names } }
        val chosen = cached ?: negotiate(all).also { set -> negotiated[cacheKey] = set.map { it.name } }

        val mc = MediaCodec.createByCodecName(choice.codecName)
        try {
            // The callback must be set before configure() for asynchronous mode.
            mc.setCallback(callback, handler)
            mc.configure(formatWith(chosen), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            inputSurface = mc.createInputSurface()
            mc.start()
        } catch (e: Exception) {
            runCatching { mc.release() }
            negotiated.remove(cacheKey)
            throw IllegalStateException("${choice.codecName} failed to start with ${chosen.map { it.name }}", e)
        }
        codec = mc
        appliedFeatures = chosen.map { it.name }
        AirLog.i(TAG, "${choice.codecName} ${settings.width}x${settings.height}@${settings.fps} " +
            "${settings.bitrateBps / 1000} kbps, features=$appliedFeatures, " +
            "rejected=${all.filterNot { it in chosen }.map { it.name }}${if (cached != null) " (cached)" else ""}")
        return inputSurface
    }

    private fun formatWith(features: List<Feature>): MediaFormat =
        baseFormat().also { f -> features.forEach { it.apply(f) } }

    private fun negotiate(all: List<Feature>): List<Feature> {
        var base: List<Feature>? = null
        var lastError: Throwable? = null
        for (drop in 0..all.size) {
            val candidate = all.dropLast(drop)
            val error = tryConfigure(candidate)
            if (error == null) {
                base = candidate
                break
            }
            lastError = error
        }
        if (base == null) throw IllegalStateException("${choice.codecName} rejected even the minimal format", lastError)
        var set: List<Feature> = base
        for (feature in all.filterNot { it in base }) {
            val with = all.filter { it in set || it === feature } // keep importance order
            if (tryConfigure(with) == null) set = with
        }
        return set
    }

    /** Configures a throwaway instance. Returns null on success, the error otherwise. */
    private fun tryConfigure(features: List<Feature>): Throwable? {
        val mc = MediaCodec.createByCodecName(choice.codecName)
        return try {
            mc.configure(formatWith(features), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            null
        } catch (e: Exception) {
            // IllegalArgumentException, MediaCodec.CodecException or IllegalStateException.
            AirLog.w(TAG, "configure rejected ${features.map { it.name }}: ${e.message}")
            e
        } finally {
            runCatching { mc.release() }
        }
    }

    /** Asks for an IDR as soon as possible. Rate limiting is the caller's job. */
    fun requestKeyframe() {
        handler.post {
            codec?.let { mc ->
                runCatching {
                    mc.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
                }
            }
        }
    }

    /** Dynamic bitrate change; no restart, takes effect within a few frames. */
    fun setBitrate(bps: Int) {
        handler.post {
            codec?.let { mc ->
                runCatching {
                    mc.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, bps) })
                }
            }
        }
    }

    /**
     * Stops and releases on the encoder thread (so no callback can run concurrently with teardown),
     * then quits the thread. Call after the producer (camera or GL) has stopped rendering into
     * [inputSurface]. Idempotent.
     */
    fun release() {
        if (released) return
        released = true
        val finished = handler.runSync(TEARDOWN_TIMEOUT_MS) {
            codec?.let { mc ->
                runCatching { mc.stop() }
                runCatching { mc.release() }
            }
            codec = null
            pending?.recycle()
            pending = null
        }
        if (!finished) AirLog.e(TAG, "encoder teardown timed out; the codec may leak until process exit")
        if (::inputSurface.isInitialized) runCatching { inputSurface.release() }
        thread.quitSafely()
    }

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) = Unit // surface input

        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            try {
                if (info.size > 0 && !released) {
                    val buffer = codec.getOutputBuffer(index)
                    if (buffer != null) {
                        buffer.position(info.offset)
                        buffer.limit(info.offset + info.size)
                        val frame = pending ?: pool.acquire().also {
                            it.ptsUs = info.presentationTimeUs
                            pending = it
                        }
                        frame.append(buffer, info.size)
                        if (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0) {
                            frame.flags = frame.flags or Protocol.FLAG_KEYFRAME
                        }
                        if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                            frame.flags = frame.flags or Protocol.FLAG_CODEC_CONFIG
                        }
                        if (info.flags and MediaCodec.BUFFER_FLAG_PARTIAL_FRAME == 0) {
                            pending = null
                            if (!frame.isConfig) {
                                framesEncoded++
                                PipelineTiming.onEncoded(frame.ptsUs)
                            }
                            sink.onEncodedFrame(frame)
                        }
                    }
                }
            } catch (t: Throwable) {
                pending?.recycle()
                pending = null
                sink.onEncoderError(t)
            } finally {
                // Always return the buffer, even when the copy failed: a leaked output buffer
                // stalls the encoder within a handful of frames.
                runCatching { codec.releaseOutputBuffer(index, false) }
            }
        }

        override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
            AirLog.e(TAG, "codec error (recoverable=${e.isRecoverable}, transient=${e.isTransient})", e)
            if (!e.isTransient) sink.onEncoderError(e)
        }

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) {
            AirLog.i(TAG, "output format: $format")
        }
    }

    private companion object {
        const val TAG = "AirLens.Encoder"
        const val TEARDOWN_TIMEOUT_MS = 2000L
        const val FAST_OPERATING_RATE = 240

        /** codec/size/rate -> feature names that configure() accepted. Saves ~0.4 s per restart. */
        val negotiated = java.util.concurrent.ConcurrentHashMap<String, List<String>>()
    }
}
