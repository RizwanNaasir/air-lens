package net.silow.airlens.stream

import net.silow.airlens.camera.VideoModeOption

/**
 * One choice on the landing screens: a resolution together with the settings that measured best
 * for it (docs/ARCHITECTURE.md §6.1). Every preset uses the encoder's maximum bitrate.
 */
data class QualityPreset(
    val id: String,
    /** Resolution as people say it: "4K", "1440p", "1080p". */
    val label: String,
    /** What it is for: "Best picture", "Balanced", "Lowest delay". */
    val tag: String,
    val width: Int,
    val height: Int,
    val fps: Int,
    val codec: VideoCodec,
    val direct: Boolean,
) {
    fun applyTo(c: StreamConfig): StreamConfig = c.copy(
        outputWidth = width,
        outputHeight = height,
        fps = fps,
        codec = codec,
        bitrateBps = StreamConfig.BITRATE_MAX,
        directMode = direct,
    )

    fun matches(c: StreamConfig): Boolean =
        c.outputWidth == width && c.outputHeight == height && c.fps == fps && c.codec == codec &&
            c.bitrateBps == StreamConfig.BITRATE_MAX && c.directMode == direct
}

/**
 * Builds the presets for one lens from what its camera, the GL path and the hardware encoder
 * support, so a lens with fewer modes simply offers fewer presets:
 *
 * - **best**: the largest mode, H.265 when the encoder has it (more picture per bit), GL path.
 * - **balanced**: the next size down that is still above 1080p (1440p on most phones), H.265.
 * - **fast**: 1080p (or the largest mode below it), H.264, direct mode when the camera can feed
 *   the encoder directly. Lowest delay, and it matches the virtual camera's 1080p output.
 */
object QualityPresets {
    const val BEST = "best"
    const val BALANCED = "balanced"
    const val FAST = "fast"
    const val CUSTOM = "custom"

    /** [modes] as returned by `CameraCapabilities.videoModes`: largest first. */
    fun forModes(modes: List<VideoModeOption>): List<QualityPreset> {
        val best = modes.firstOrNull() ?: return emptyList()
        val fast = modes.firstOrNull { it.height <= FAST_HEIGHT }
        val balanced = modes.firstOrNull { it.height < best.height && it.height > FAST_HEIGHT }
        return buildList {
            add(preset(BEST, "Best picture", best, VideoCodec.HEVC, direct = false))
            if (balanced != null) add(preset(BALANCED, "Balanced", balanced, VideoCodec.HEVC, direct = false))
            if (fast != null) {
                val p = preset(FAST, "Lowest delay", fast, VideoCodec.H264, direct = fast.directCapable)
                // A phone whose best mode already is 1080p H.264 on the direct path needs no second entry.
                if (none { it.copy(id = FAST, tag = p.tag) == p }) add(p)
            }
        }
    }

    /** The preset the running settings correspond to, or [CUSTOM] after changes in Advanced. */
    fun matching(config: StreamConfig, presets: List<QualityPreset>): String =
        presets.firstOrNull { it.matches(config) }?.id ?: CUSTOM

    fun label(height: Int): String = if (height == 2160) "4K" else "${height}p"

    private fun preset(id: String, tag: String, mode: VideoModeOption, preferred: VideoCodec, direct: Boolean) =
        QualityPreset(
            id = id,
            label = label(mode.height),
            tag = tag,
            width = mode.width,
            height = mode.height,
            fps = mode.fps,
            codec = if (preferred in mode.codecs) preferred else mode.codecs.first(),
            direct = direct,
        )

    private const val FAST_HEIGHT = 1080
}
