package net.silow.airlens.stream

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import net.silow.airlens.encoder.RatePriority
import net.silow.airlens.power.ThermalLevel

enum class StreamState { IDLE, STARTING, STREAMING, RECOVERING, ERROR }

/** Snapshot of the service for the UI. Immutable; the service publishes a new one on each change. */
data class StreamStatus(
    val state: StreamState = StreamState.IDLE,
    val message: String? = null,
    val clientAddress: String? = null,
    /** `SystemClock.elapsedRealtime()` when the current PC connected; 0 when none is. */
    val clientSinceMs: Long = 0,
    val lensKey: String? = null,
    val lensLabel: String? = null,
    val width: Int = 0,
    val height: Int = 0,
    val fps: Int = 0,
    val codecName: String? = null,
    val encoderFeatures: List<String> = emptyList(),
    /** Current target after rate policy. */
    val bitrateBps: Int = 0,
    /** Bitrate at full frame rate ("Max" resolved against the encoder ceiling). */
    val maxBitrateBps: Int = 0,
    val priority: RatePriority = RatePriority.QUALITY,
    val thermal: ThermalLevel = ThermalLevel.NORMAL,
    val batteryTempC: Float = Float.NaN,
    val framesEncoded: Long = 0,
    val framesSent: Long = 0,
    val framesDropped: Long = 0,
    val glFramesSkipped: Long = 0,
    /** Frames currently checked out of the pool. Should hover near the queue depth; growth = leak. */
    val poolOutstanding: Int = 0,
    val wakeLockHeld: Boolean = false,
    val addresses: List<String> = emptyList(),
    val port: Int = 0,
    val powerMode: PowerMode = PowerMode.STEALTH,
    /** The PC's virtual camera is on: the phone skips its own preview unless asked to show it. */
    val pcVirtualCamera: Boolean = false,
    /** Manual focus 0 (far) .. 1 (near), or null for autofocus. */
    val focus: Float? = null,
    val manualFocusSupported: Boolean = false,
    /** Preset label the stream was stepped down to because the phone is hot, else null. */
    val heatLowered: String? = null,
    /** Bumped when the service changes the saved settings (e.g. the PC picked a lens or quality). */
    val configRevision: Int = 0,
)

object StreamStatusBus {
    private val state = MutableStateFlow(StreamStatus())
    val status: StateFlow<StreamStatus> = state.asStateFlow()

    fun update(transform: (StreamStatus) -> StreamStatus) = state.update(transform)
}
