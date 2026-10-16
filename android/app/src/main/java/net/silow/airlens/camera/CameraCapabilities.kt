package net.silow.airlens.camera

import android.content.Context
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CameraMetadata
import android.media.MediaCodec
import android.os.Build
import net.silow.airlens.util.AirLog
import android.util.Range
import net.silow.airlens.encoder.EncoderSelector
import net.silow.airlens.stream.VideoCodec
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

enum class Facing { BACK, FRONT, EXTERNAL }

data class SizeFps(val width: Int, val height: Int, val maxFps: Int) {
    val longSide get() = max(width, height)
    val shortSide get() = min(width, height)
    val area get() = width.toLong() * height
    override fun toString() = "${width}x$height@$maxFps"
}

data class CameraDescriptor(
    val id: String,
    val facing: Facing,
    val sensorOrientation: Int,
    val hardwareLevel: String,
    /** Raw ranges as the HAL reports them (some legacy HALs use 1/1000 fps units). */
    val rawFpsRanges: List<Range<Int>>,
    val maxAeFps: Int,
    /** SurfaceTexture outputs (GL mode). */
    val textureSizes: List<SizeFps>,
    /** MediaCodec outputs (direct mode). */
    val codecSizes: List<SizeFps>,
    val afModes: Set<Int>,
    val oisModes: Set<Int>,
    val noiseReductionModes: Set<Int>,
    val edgeModes: Set<Int>,
    /** Nearest focus in diopters (LENS_INFO_MINIMUM_FOCUS_DISTANCE); 0 = fixed focus, no manual focus. */
    val minFocusDiopters: Float = 0f,
) {
    val isFront get() = facing == Facing.FRONT
    val supportsManualFocus get() = minFocusDiopters > 0f
    val label get() = "${facing.name.lowercase().replaceFirstChar { it.uppercase() }} camera ($id)"
}

/** One entry in the resolution menu: something the camera, GL path and a HW encoder all support. */
data class VideoModeOption(
    val width: Int,
    val height: Int,
    val fps: Int,
    val codecs: List<VideoCodec>,
    val capture: SizeFps,
    val directCapable: Boolean,
) {
    val label get() = "${height}p$fps" + if (width * 3 == height * 4) " 4:3" else ""
}

/**
 * Runtime discovery of what this phone can actually stream. Nothing here is hard-coded to a
 * model: the Huawei P30 (4K30 / 1080p60 at best, HiSilicon encoders) and a 4K60 flagship go
 * through the same checks, and the menu only offers what passes all three:
 *
 * 1. the camera produces the size at that rate (`getOutputMinFrameDuration`),
 * 2. an AE target FPS range reaches the rate,
 * 3. a **hardware** encoder accepts size x rate.
 */
object CameraCapabilities {
    private const val TAG = "AirLens.Caps"

    /** Output presets offered in the menu, largest first. */
    private val PRESETS = listOf(
        3840 to 2160, 2560 to 1440, 1920 to 1080, 1280 to 720, // 16:9
        1920 to 1440, 1440 to 1080, 960 to 720, // 4:3
    )
    private val RATES = listOf(60, 30, 25, 24, 20, 15)

    /**
     * Reads one camera's (or physical sub-camera's) capabilities. [fallbackRanges] supplies AE
     * ranges for physical cameras, whose characteristics usually omit them because the capture
     * request goes to the logical camera.
     */
    fun describe(id: String, c: CameraCharacteristics, fallbackRanges: List<Range<Int>> = emptyList()): CameraDescriptor? {
        val map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: return null
        val rawRanges = c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.toList()
            ?.takeIf { it.isNotEmpty() } ?: fallbackRanges
        val maxAe = rawRanges.maxOfOrNull { normalizeFps(it.upper, rawRanges) } ?: 30

        fun sizesFor(klass: Class<*>): List<SizeFps> =
            map.getOutputSizes(klass).orEmpty().map { size ->
                val minDurationNs = runCatching { map.getOutputMinFrameDuration(klass, size) }.getOrDefault(0L)
                val sensorFps = if (minDurationNs > 0) (1e9 / minDurationNs).roundToInt() else maxAe
                SizeFps(size.width, size.height, min(sensorFps, maxAe))
            }.sortedByDescending { it.area }

        return CameraDescriptor(
            id = id,
            facing = when (c.get(CameraCharacteristics.LENS_FACING)) {
                CameraMetadata.LENS_FACING_FRONT -> Facing.FRONT
                CameraMetadata.LENS_FACING_BACK -> Facing.BACK
                else -> Facing.EXTERNAL
            },
            sensorOrientation = c.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90,
            hardwareLevel = when (c.get(CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL)) {
                CameraMetadata.INFO_SUPPORTED_HARDWARE_LEVEL_LEGACY -> "LEGACY"
                CameraMetadata.INFO_SUPPORTED_HARDWARE_LEVEL_LIMITED -> "LIMITED"
                CameraMetadata.INFO_SUPPORTED_HARDWARE_LEVEL_FULL -> "FULL"
                CameraMetadata.INFO_SUPPORTED_HARDWARE_LEVEL_3 -> "LEVEL_3"
                CameraMetadata.INFO_SUPPORTED_HARDWARE_LEVEL_EXTERNAL -> "EXTERNAL"
                else -> "UNKNOWN"
            },
            rawFpsRanges = rawRanges,
            maxAeFps = maxAe,
            textureSizes = sizesFor(SurfaceTexture::class.java),
            codecSizes = sizesFor(MediaCodec::class.java),
            afModes = c.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES)?.toSet().orEmpty(),
            oisModes = c.get(CameraCharacteristics.LENS_INFO_AVAILABLE_OPTICAL_STABILIZATION)?.toSet().orEmpty(),
            noiseReductionModes = c.get(CameraCharacteristics.NOISE_REDUCTION_AVAILABLE_NOISE_REDUCTION_MODES)?.toSet().orEmpty(),
            edgeModes = c.get(CameraCharacteristics.EDGE_AVAILABLE_EDGE_MODES)?.toSet().orEmpty(),
            minFocusDiopters = c.get(CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE) ?: 0f,
        )
    }

    /** Some legacy HALs report fps ranges in 1/1000 fps. Detect that per camera and undo it. */
    fun normalizeFps(value: Int, ranges: List<Range<Int>>): Int =
        if (ranges.any { it.upper > 1000 }) value / 1000 else value

    /**
     * Picks the AE range for [fps]: exact `[fps, fps]` (steady frame pacing) first, then the range
     * ending at fps with the highest floor, then the fastest range below fps.
     * Returns the *raw* range, ready to put in a capture request.
     */
    fun chooseFpsRange(ranges: List<Range<Int>>, fps: Int): Range<Int>? {
        if (ranges.isEmpty()) return null
        val norm = ranges.map { it to Range(normalizeFps(it.lower, ranges), normalizeFps(it.upper, ranges)) }
        norm.firstOrNull { it.second.lower == fps && it.second.upper == fps }?.let { return it.first }
        norm.filter { it.second.upper == fps }.maxByOrNull { it.second.lower }?.let { return it.first }
        return norm.filter { it.second.upper <= fps }.maxByOrNull { it.second.upper }?.first ?: norm.first().first
    }

    /**
     * Camera buffer size for GL mode: same long/short aspect as the output, at least as large,
     * fast enough, and otherwise as small as possible (smaller buffers = less ISP and GPU power).
     */
    fun chooseCaptureSize(camera: CameraDescriptor, outWidth: Int, outHeight: Int, fps: Int): SizeFps? {
        val outLong = max(outWidth, outHeight)
        val outShort = min(outWidth, outHeight)
        val outAspect = outLong.toFloat() / outShort
        val fastEnough = camera.textureSizes.filter { it.maxFps >= fps }
        fun aspectOf(s: SizeFps) = s.longSide.toFloat() / s.shortSide
        return fastEnough
            .filter { abs(aspectOf(it) - outAspect) < 0.02f && it.longSide >= outLong }
            .minByOrNull { it.area }
            ?: fastEnough.filter { it.longSide >= outLong && it.shortSide >= outShort }.minByOrNull { it.area }
            ?: fastEnough.maxByOrNull { it.area }
    }

    /**
     * For each preset: a 60 fps variant if the lens has one, plus the best rate up to 30 fps it can
     * hold at *native* resolution. Resolution is never traded for frame rate here; a lens whose 4K
     * mode only runs at 20 fps (the P30 telephoto) is offered "2160p20" rather than an upscaled 4K30.
     */
    fun videoModes(camera: CameraDescriptor): List<VideoModeOption> = buildList {
        for ((w, h) in PRESETS) {
            for (fps in RATES) {
                if (fps > camera.maxAeFps) continue
                val capture = chooseCaptureSize(camera, w, h, fps) ?: continue
                // Upscaling a smaller sensor mode is never worth the bitrate; try a lower rate instead.
                if (capture.longSide < max(w, h) || capture.shortSide < min(w, h)) continue
                val codecs = VideoCodec.entries.filter { codec ->
                    EncoderSelector.select(codec, w, h, fps)?.isHardware == true
                }
                if (codecs.isEmpty()) continue
                val direct = camera.codecSizes.any { it.width == w && it.height == h && it.maxFps >= fps }
                add(VideoModeOption(w, h, fps, codecs, capture, direct))
                if (fps <= 30) break // best rate ≤ 30 found; lower rates for this size add nothing
            }
        }
    }

    /** Human-readable report for the Device report screen and `adb logcat -s AirLens.Caps`. */
    fun report(context: Context): String = buildString {
        appendLine("AirLens device report")
        appendLine("Device: ${Build.MANUFACTURER} ${Build.MODEL} (${Build.DEVICE}), Android ${Build.VERSION.RELEASE} / API ${Build.VERSION.SDK_INT}")
        appendLine("SoC: ${socDescription()}")
        val manager = context.getSystemService(CameraManager::class.java)
        appendLine("Camera IDs visible to apps: ${runCatching { manager.cameraIdList.joinToString() }.getOrDefault("?")}")
        appendLine()
        for (lens in LensCatalog.lenses(context)) {
            val cam = lens.descriptor
            val route = when {
                lens.physicalId != null -> "physical ${lens.physicalId} of logical ${lens.cameraId}"
                lens.zoomRatio != null -> "zoom ratio ${lens.zoomRatio} on ${lens.cameraId}"
                else -> "camera ${lens.cameraId}"
            }
            appendLine("${lens.label} — $route, ${"%.0f".format(lens.equivalentFocalMm)} mm eq.")
            appendLine("  level=${cam.hardwareLevel}, sensorOrientation=${cam.sensorOrientation}")
            appendLine("  AE fps ranges: ${cam.rawFpsRanges.joinToString { "[${it.lower},${it.upper}]" }} (max ${cam.maxAeFps})")
            appendLine("  GL sizes ≥720p: ${cam.textureSizes.filter { it.shortSide >= 720 }.joinToString()}")
            appendLine("  Direct sizes ≥720p: ${cam.codecSizes.filter { it.shortSide >= 720 }.joinToString()}")
            appendLine("  OIS: ${if (1 in cam.oisModes) "yes" else "no"}, fast NR: ${if (1 in cam.noiseReductionModes) "yes" else "no"}")
            val modes = videoModes(cam)
            appendLine("  Streamable: ${if (modes.isEmpty()) "none" else modes.joinToString { "${it.label} ${it.codecs.joinToString("/")}${if (it.directCapable) " (direct)" else ""}" }}")
        }
        appendLine()
        for (codec in VideoCodec.entries) {
            val mime = EncoderSelector.mimeFor(codec)
            val hw = EncoderSelector.hardwareEncoders(mime)
            appendLine("$codec HW encoders: ${if (hw.isEmpty()) "none" else hw.joinToString { it.name }}")
            hw.forEach { info ->
                val vc = info.getCapabilitiesForType(mime).videoCapabilities ?: return@forEach
                appendLine("  ${info.name}: up to ${vc.supportedWidths.upper}x${vc.supportedHeights.upper}, " +
                    "1080p60=${vc.areSizeAndRateSupported(1920, 1080, 60.0)}, 2160p30=${vc.areSizeAndRateSupported(3840, 2160, 30.0)}, " +
                    "2160p60=${vc.areSizeAndRateSupported(3840, 2160, 60.0)}")
            }
        }
    }.also { text ->
        // A file as well as logcat: EMUI hides third-party info logs. Pull with
        // adb pull /sdcard/Android/data/net.silow.airlens/files/device-report.txt
        val path = AirLog.writeSidecar("device-report.txt", text)
        AirLog.i(TAG, "device report generated${path?.let { " -> $it" } ?: ""}")
        text.lineSequence().forEach { android.util.Log.i(TAG, it) }
    }
}

/** Build.SOC_* exists from API 31; older releases (e.g. the P30 on API 29) fall back to board names. */
private fun socDescription(): String =
    if (Build.VERSION.SDK_INT >= 31) "${Build.SOC_MANUFACTURER} ${Build.SOC_MODEL}" else "${Build.HARDWARE} (${Build.BOARD})"
