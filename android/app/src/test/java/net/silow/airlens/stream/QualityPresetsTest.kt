package net.silow.airlens.stream

import net.silow.airlens.camera.SizeFps
import net.silow.airlens.camera.VideoModeOption
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class QualityPresetsTest {
    private val both = listOf(VideoCodec.H264, VideoCodec.HEVC)

    private fun mode(w: Int, h: Int, fps: Int = 30, codecs: List<VideoCodec> = both, direct: Boolean = true) =
        VideoModeOption(w, h, fps, codecs, SizeFps(w, h, fps), direct)

    /** The P30 main lens: 2160p30, 1440p30, 1080p30, 720p30. */
    private val main = listOf(mode(3840, 2160), mode(2560, 1440), mode(1920, 1080), mode(1280, 720))

    @Test
    fun mainLensGetsBestBalancedAndFast() {
        val p = QualityPresets.forModes(main)
        assertEquals(listOf("best", "balanced", "fast"), p.map { it.id })
        assertEquals(listOf("4K", "1440p", "1080p"), p.map { it.label })
        assertEquals(listOf(VideoCodec.HEVC, VideoCodec.HEVC, VideoCodec.H264), p.map { it.codec })
        assertEquals(listOf(false, false, true), p.map { it.direct })
    }

    @Test
    fun telephotoKeepsItsOwnBestRate() {
        val tele = listOf(mode(3840, 2160, fps = 20), mode(2560, 1440, fps = 20), mode(1920, 1080))
        val p = QualityPresets.forModes(tele)
        assertEquals(20, p.first { it.id == "best" }.fps)
        assertEquals(30, p.first { it.id == "fast" }.fps)
    }

    @Test
    fun frontCameraHasNoBalancedStep() {
        val front = listOf(mode(1920, 1440), mode(1920, 1080))
        val p = QualityPresets.forModes(front)
        assertEquals(listOf("best", "fast"), p.map { it.id })
        assertEquals("1440p", p[0].label)
    }

    @Test
    fun encoderWithoutHevcFallsBackToH264() {
        val p = QualityPresets.forModes(listOf(mode(3840, 2160, codecs = listOf(VideoCodec.H264)), mode(1920, 1080, codecs = listOf(VideoCodec.H264))))
        assertTrue(p.all { it.codec == VideoCodec.H264 })
    }

    @Test
    fun aPhoneThatTopsOutAt1080pWithoutDirectModeHasOnePreset() {
        val p = QualityPresets.forModes(listOf(mode(1920, 1080, codecs = listOf(VideoCodec.H264), direct = false)))
        assertEquals(listOf("best"), p.map { it.id })
    }

    @Test
    fun noModesNoPresets() {
        assertTrue(QualityPresets.forModes(emptyList()).isEmpty())
    }

    @Test
    fun matchingFindsThePresetAndCustomOtherwise() {
        val presets = QualityPresets.forModes(main)
        val fast = presets.first { it.id == "fast" }.applyTo(StreamConfig())
        assertEquals("fast", QualityPresets.matching(fast, presets))
        assertTrue(fast.directMode)
        assertEquals(StreamConfig.BITRATE_MAX, fast.bitrateBps)
        assertEquals(QualityPresets.CUSTOM, QualityPresets.matching(fast.copy(bitrateBps = 20_000_000), presets))
        assertFalse(presets.first { it.id == "best" }.matches(fast))
    }
}
