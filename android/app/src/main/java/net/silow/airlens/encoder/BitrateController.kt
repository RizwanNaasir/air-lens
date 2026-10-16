package net.silow.airlens.encoder

import kotlin.math.roundToInt

/**
 * Estimates the **link budget** with AIMD, fed by two signals:
 *
 * - **Congestion** (sender queue overflow): multiply by 0.7, at most once per [cutCooldownMs] so one
 *   burst does not collapse the estimate in a single step.
 * - **Clean receiver reports**: after [recoveryDelayMs] without congestion, grow 5% per report
 *   back toward [maxBps].
 *
 * It does not decide how to *spend* the budget; [RatePolicy] does that (frame rate first or
 * bitrate first). Pure logic: callers supply the clock, which keeps it unit-testable.
 */
class BitrateController(
    private val maxBps: Int,
    private val minBps: Int = (maxBps / 8).coerceAtLeast(1_000_000),
    private val cutCooldownMs: Long = 500,
    private val recoveryDelayMs: Long = 2000,
) {
    private var budgetBps = maxBps
    private var lastCutMs = Long.MIN_VALUE / 2

    val targetBps: Int get() = budgetBps

    /** Returns the new budget if it changed. */
    fun onCongestion(nowMs: Long): Int? {
        if (nowMs - lastCutMs < cutCooldownMs) return null
        lastCutMs = nowMs
        return update(maxOf(minBps, (budgetBps * 0.7).roundToInt()))
    }

    /** Called for each receiver report with no drops. Returns the new budget if it changed. */
    fun onCleanReport(nowMs: Long): Int? {
        if (nowMs - lastCutMs < recoveryDelayMs) return null
        return update(minOf(maxBps, (budgetBps * 1.05).roundToInt() + 1))
    }

    private fun update(next: Int): Int? {
        if (next == budgetBps) return null
        budgetBps = next
        return next
    }
}
