package net.silow.airlens.encoder

import net.silow.airlens.power.ThermalLevel
import kotlin.math.min
import kotlin.math.roundToInt

/** What to protect when the link or the phone's temperature cannot sustain the configured stream. */
enum class RatePriority {
    /**
     * Keep every frame's quality (bits per frame) and give up frame rate first:
     * 30 → 24 → 20 → 15 fps. Only below the floor are bits per frame reduced.
     */
    QUALITY,

    /** Keep the frame rate and reduce the bitrate first (classic video-call behaviour). */
    SMOOTHNESS,
}

data class RateDecision(val fps: Int, val bitrateBps: Int)

/**
 * Turns three inputs into one (fps, bitrate) target:
 *
 * - the **configured** stream (fps and the bitrate at that fps),
 * - the **link budget** from [BitrateController] (AIMD on sender-queue overflow / receiver reports),
 * - the **thermal level** from the thermal governor.
 *
 * In [RatePriority.QUALITY] mode lowering fps is also the better thermal lever: it cuts ISP, GPU,
 * encoder *and* radio work together, while a bitrate cut only relieves the radio.
 *
 * Frame rates snap to rates the camera can hold steadily (AE ranges with lower == upper, e.g.
 * 15/20/24/25/30 on the Huawei P30), so motion cadence stays even. Pure logic, unit-tested.
 */
object RatePolicy {
    const val MIN_FPS = 15

    fun decide(
        priority: RatePriority,
        configuredFps: Int,
        configuredBitrateBps: Int,
        linkBudgetBps: Int,
        thermal: ThermalLevel,
        steadyFps: List<Int>,
    ): RateDecision = when (priority) {
        RatePriority.QUALITY -> {
            val bitsPerFrame = configuredBitrateBps.toDouble() / configuredFps
            val thermalFps = when (thermal) {
                ThermalLevel.NORMAL -> configuredFps
                ThermalLevel.WARM -> min(24, configuredFps)
                ThermalLevel.HOT -> min(20, configuredFps)
                ThermalLevel.CRITICAL -> min(MIN_FPS, configuredFps)
            }
            val linkFps = linkBudgetBps / bitsPerFrame
            val fps = snapDown(min(thermalFps.toDouble(), linkFps), steadyFps, configuredFps)
            RateDecision(fps, min(linkBudgetBps, (bitsPerFrame * fps).roundToInt()))
        }
        RatePriority.SMOOTHNESS -> {
            val fps = thermal.fpsCap?.let { min(it, configuredFps) } ?: configuredFps
            RateDecision(fps, min(linkBudgetBps, (configuredBitrateBps * thermal.bitrateFactor).roundToInt()))
        }
    }

    /** Highest steady rate not above [target], never below [MIN_FPS] (or the lowest candidate). */
    fun snapDown(target: Double, steadyFps: List<Int>, configuredFps: Int): Int {
        val candidates = (steadyFps + configuredFps)
            .filter { it in MIN_FPS..configuredFps }
            .distinct()
            .sorted()
        return candidates.lastOrNull { it <= target + 0.01 } ?: candidates.firstOrNull() ?: configuredFps
    }
}
