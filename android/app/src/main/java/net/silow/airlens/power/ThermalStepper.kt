package net.silow.airlens.power

/**
 * The second heat lever, after frame rate (RatePolicy already drops to 24/20/15 fps): while the
 * phone stays HOT, step the resolution down one quality preset (4K → 1440p → 1080p); once it has
 * been NORMAL for a while, step back up one. Smaller frames cut ISP, GPU and encoder work, which
 * frame rate alone cannot do enough of at 4K.
 *
 * The holds keep it from oscillating: each step restarts the encoder (about a second of pause),
 * so it must happen rarely. CRITICAL steps down after a quarter of the hold. Pure logic, tested.
 */
class ThermalStepper(
    private val hotHoldMs: Long = 60_000L,
    private val coolHoldMs: Long = 300_000L,
    private val maxSteps: Int = 2,
) {
    /** How many presets below the chosen one the stream currently runs. */
    var steps = 0
        private set

    private var hotSince = NONE
    private var coolSince = NONE
    private var lastChange = NONE

    /** Feed the current level about once a second. Returns true when [steps] changed. */
    fun update(level: ThermalLevel, nowMs: Long): Boolean {
        val hot = level >= ThermalLevel.HOT
        val cool = level == ThermalLevel.NORMAL
        hotSince = if (hot) (if (hotSince == NONE) nowMs else hotSince) else NONE
        coolSince = if (cool) (if (coolSince == NONE) nowMs else coolSince) else NONE

        val hold = if (level == ThermalLevel.CRITICAL) hotHoldMs / 4 else hotHoldMs
        val sinceChange = if (lastChange == NONE) Long.MAX_VALUE else nowMs - lastChange
        if (hot && steps < maxSteps && nowMs - hotSince >= hold && sinceChange >= hold) {
            steps++
            lastChange = nowMs
            hotSince = nowMs // the next step needs another full hold at this level
            return true
        }
        if (cool && steps > 0 && nowMs - coolSince >= coolHoldMs) {
            steps--
            lastChange = nowMs
            coolSince = nowMs
            return true
        }
        return false
    }

    fun reset() {
        steps = 0
        hotSince = NONE
        coolSince = NONE
        lastChange = NONE
    }

    private companion object {
        const val NONE = Long.MIN_VALUE
    }
}
