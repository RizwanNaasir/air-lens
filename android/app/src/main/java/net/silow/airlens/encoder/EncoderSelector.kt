package net.silow.airlens.encoder

import android.media.MediaCodecInfo
import android.media.MediaCodecInfo.CodecProfileLevel
import android.media.MediaCodecList
import android.media.MediaFormat
import net.silow.airlens.util.AirLog
import net.silow.airlens.stream.VideoCodec

/** What [EncoderSelector] found for one mime type and size/rate. */
data class EncoderChoice(
    val codecName: String,
    val mime: String,
    val isHardware: Boolean,
    /** Best supported profile, or null to let the codec decide. */
    val profile: Int?,
    /**
     * Highest level the codec advertises for [profile]. Always sent together with the profile:
     * Android's OMX glue (ACodec) rejects an AVC/HEVC profile without a level, which is why the
     * Huawei P30 fell back to Constrained Baseline before.
     */
    val level: Int?,
    val supportsCbr: Boolean,
    /** True when the codec claimed the exact size and rate, false when only the size was accepted. */
    val rateConfirmed: Boolean,
    /** Upper end of the codec's advertised bitrate range (e.g. 60 Mbps H.264 / 80 Mbps HEVC on Kirin 980). */
    val maxBitrateBps: Int,
)

/**
 * Picks an encoder by capability, never by name, so the same code works on HiSilicon (P30),
 * Qualcomm, Exynos, MediaTek and Tensor devices.
 *
 * Preference: hardware over software, exact size+rate support over size-only, non-alias over
 * alias. Some HALs under-report achievable rates, so a size-only match is kept as a fallback and
 * flagged instead of being discarded.
 */
object EncoderSelector {
    private const val TAG = "AirLens.Encoder"

    fun mimeFor(codec: VideoCodec): String = when (codec) {
        VideoCodec.H264 -> MediaFormat.MIMETYPE_VIDEO_AVC
        VideoCodec.HEVC -> MediaFormat.MIMETYPE_VIDEO_HEVC
    }

    fun select(codec: VideoCodec, width: Int, height: Int, fps: Int): EncoderChoice? {
        val mime = mimeFor(codec)
        val candidates = encodersFor(mime).mapNotNull { info ->
            val caps = runCatching { info.getCapabilitiesForType(mime) }.getOrNull() ?: return@mapNotNull null
            val video = caps.videoCapabilities ?: return@mapNotNull null
            if (MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface !in caps.colorFormats) return@mapNotNull null
            val exact = runCatching { video.areSizeAndRateSupported(width, height, fps.toDouble()) }.getOrDefault(false)
            val sizeOnly = exact || runCatching { video.isSizeSupported(width, height) }.getOrDefault(false)
            if (!sizeOnly) return@mapNotNull null
            EncoderChoice(
                codecName = info.name,
                mime = mime,
                isHardware = info.isHardwareAccelerated && !info.isSoftwareOnly,
                profile = bestProfile(mime, caps),
                level = bestProfile(mime, caps)?.let { p -> caps.profileLevels.filter { it.profile == p }.maxOfOrNull { it.level } },
                supportsCbr = caps.encoderCapabilities
                    ?.isBitrateModeSupported(MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR) == true,
                rateConfirmed = exact,
                maxBitrateBps = video.bitrateRange.upper,
            ) to info.isAlias
        }
        val best = candidates
            .sortedWith(
                compareByDescending<Pair<EncoderChoice, Boolean>> { it.first.isHardware }
                    .thenByDescending { it.first.rateConfirmed }
                    .thenBy { it.second },
            )
            .firstOrNull()
            ?.first
        if (best == null) {
            AirLog.w(TAG, "no $mime encoder for ${width}x$height@$fps")
        } else if (!best.rateConfirmed) {
            AirLog.w(TAG, "${best.codecName} accepts ${width}x$height but does not confirm $fps fps; trying anyway")
        }
        return best
    }

    /** Hardware encoders for [mime], for the device report and the codec menu. */
    fun hardwareEncoders(mime: String): List<MediaCodecInfo> =
        encodersFor(mime).filter { it.isHardwareAccelerated && !it.isSoftwareOnly }

    /** Building the list costs tens of milliseconds and it never changes while the process lives. */
    private val allEncoders: List<MediaCodecInfo> by lazy {
        MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { it.isEncoder }
    }

    private fun encodersFor(mime: String): List<MediaCodecInfo> =
        allEncoders.filter { info ->
            info.isEncoder && info.supportedTypes.any { it.equals(mime, ignoreCase = true) }
        }

    /**
     * H.264 High compresses ~10-15% better than Main at the same latency (no B-frames either way).
     * HEVC Main is the only 8-bit profile we want; Main10 would need a 10-bit surface path.
     */
    private fun bestProfile(mime: String, caps: MediaCodecInfo.CodecCapabilities): Int? {
        val offered = caps.profileLevels.map { it.profile }.toSet()
        val order = when (mime) {
            MediaFormat.MIMETYPE_VIDEO_AVC -> listOf(
                CodecProfileLevel.AVCProfileHigh,
                CodecProfileLevel.AVCProfileMain,
                CodecProfileLevel.AVCProfileBaseline,
            )
            MediaFormat.MIMETYPE_VIDEO_HEVC -> listOf(CodecProfileLevel.HEVCProfileMain)
            else -> emptyList()
        }
        return order.firstOrNull { it in offered }
    }
}
