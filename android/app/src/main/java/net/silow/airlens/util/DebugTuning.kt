package net.silow.airlens.util

/**
 * Debug-build overrides for latency experiments, set from ADB through MainActivity's debug command:
 *
 * ```
 * adb shell am start -n net.silow.airlens/.MainActivity --ez autostart true --es template preview --ei oprate 240
 * ```
 *
 * Release builds never set these, so the defaults below are what ships.
 */
object DebugTuning {
    /** Camera request template: true = TEMPLATE_RECORD, false = TEMPLATE_PREVIEW. */
    @Volatile var recordTemplate: Boolean? = null

    /** MediaCodec KEY_OPERATING_RATE; null = the shipped default. */
    @Volatile var operatingRate: Int? = null
}
