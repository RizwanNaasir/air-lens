package net.silow.airlens.stream

import android.content.Context
import android.content.SharedPreferences
import android.os.Bundle
import androidx.core.content.edit
import net.silow.airlens.encoder.RatePriority
import net.silow.airlens.gl.ScaleMode
import net.silow.airlens.net.Protocol

enum class VideoCodec { H264, HEVC }

enum class PowerMode {
    /** Foreground, black overlay, brightness 0, no wakelock. Best for Wi-Fi and OLED phones. */
    STEALTH,

    /** Foreground service + partial wakelock with the screen off. Best for USB or LCD phones. */
    SCREEN_OFF,
}

/**
 * Everything one streaming pipeline needs. Output size is explicit (not an enum) so presets,
 * aspect ratios and custom sizes all reduce to the same two numbers.
 *
 * Defaults are quality-first: the UI picks the largest mode the selected lens supports on first
 * run, [bitrateBps] = [BITRATE_MAX] uses the encoder's ceiling, and [priority] gives up frame rate
 * before bits per frame.
 */
data class StreamConfig(
    /** [net.silow.airlens.camera.LensOption.key]; null selects the default (main/auto) back lens. */
    val lensKey: String? = null,
    val outputWidth: Int = 1920,
    val outputHeight: Int = 1080,
    val fps: Int = 30,
    val codec: VideoCodec = VideoCodec.H264,
    /** Target bitrate, or [BITRATE_MAX] for the selected encoder's advertised maximum. */
    val bitrateBps: Int = BITRATE_MAX,
    val priority: RatePriority = RatePriority.QUALITY,
    val keyframeIntervalSec: Float = 4f,
    /** Whether the picture should look like a mirror to the viewer. */
    val mirror: Boolean = false,
    val scaleMode: ScaleMode = ScaleMode.FILL,
    /** Fixed clockwise device rotation for mounts, or null to follow the accelerometer. */
    val rotationLock: Int? = null,
    /** Camera renders straight into the encoder (no GL pass). Rotation is then done on the PC. */
    val directMode: Boolean = false,
    val powerMode: PowerMode = PowerMode.STEALTH,
    val port: Int = Protocol.DEFAULT_PORT,
) {
    /** Encoders want even dimensions; anything outside sane bounds is clamped. */
    fun normalized(): StreamConfig = copy(
        outputWidth = (outputWidth and 1.inv()).coerceIn(320, 4096),
        outputHeight = (outputHeight and 1.inv()).coerceIn(240, 4096),
        fps = fps.coerceIn(15, 60),
        bitrateBps = if (bitrateBps == BITRATE_MAX) BITRATE_MAX else bitrateBps.coerceIn(1_000_000, MAX_SANE_BITRATE),
        rotationLock = rotationLock?.let { ((it / 90) * 90).mod(360) },
    )

    /** The bitrate to configure for an encoder whose ceiling is [encoderMaxBps]. */
    fun resolveBitrate(encoderMaxBps: Int): Int {
        val ceiling = encoderMaxBps.coerceAtMost(MAX_SANE_BITRATE)
        return if (bitrateBps == BITRATE_MAX) ceiling else bitrateBps.coerceAtMost(ceiling)
    }

    private fun write(put: (String, Any?) -> Unit) {
        put(K_LENS, lensKey)
        put(K_WIDTH, outputWidth)
        put(K_HEIGHT, outputHeight)
        put(K_FPS, fps)
        put(K_CODEC, codec.name)
        put(K_BITRATE, bitrateBps)
        put(K_PRIORITY, priority.name)
        put(K_GOP, keyframeIntervalSec)
        put(K_MIRROR, mirror)
        put(K_SCALE, scaleMode.name)
        put(K_ROTATION_LOCK, rotationLock ?: -1)
        put(K_DIRECT, directMode)
        put(K_POWER, powerMode.name)
        put(K_PORT, port)
    }

    fun toBundle(): Bundle = Bundle().also { b ->
        write { k, v ->
            when (v) {
                null -> b.remove(k)
                is String -> b.putString(k, v)
                is Int -> b.putInt(k, v)
                is Float -> b.putFloat(k, v)
                is Boolean -> b.putBoolean(k, v)
            }
        }
    }

    fun save(prefs: SharedPreferences) = prefs.edit {
        write { k, v ->
            when (v) {
                null -> remove(k)
                is String -> putString(k, v)
                is Int -> putInt(k, v)
                is Float -> putFloat(k, v)
                is Boolean -> putBoolean(k, v)
            }
        }
    }

    companion object {
        /** Sentinel: use the encoder's maximum bitrate. */
        const val BITRATE_MAX = 0

        /** Above this, transport and receiver buffers stop making sense for a webcam. */
        const val MAX_SANE_BITRATE = 150_000_000

        private const val K_LENS = "lens"
        private const val K_WIDTH = "width"
        private const val K_HEIGHT = "height"
        private const val K_FPS = "fps"
        private const val K_CODEC = "codec"
        private const val K_BITRATE = "bitrate"
        private const val K_PRIORITY = "priority"
        private const val K_GOP = "gop"
        private const val K_MIRROR = "mirror"
        private const val K_SCALE = "scale"
        private const val K_ROTATION_LOCK = "rotationLock"
        private const val K_DIRECT = "direct"
        private const val K_POWER = "power"
        private const val K_PORT = "port"

        private inline fun <reified E : Enum<E>> parse(name: String?, default: E): E =
            name?.let { n -> enumValues<E>().firstOrNull { it.name == n } } ?: default

        private fun read(
            getString: (String) -> String?,
            getInt: (String, Int) -> Int,
            getFloat: (String, Float) -> Float,
            getBoolean: (String, Boolean) -> Boolean,
        ): StreamConfig {
            val d = StreamConfig()
            return StreamConfig(
                lensKey = getString(K_LENS),
                outputWidth = getInt(K_WIDTH, d.outputWidth),
                outputHeight = getInt(K_HEIGHT, d.outputHeight),
                fps = getInt(K_FPS, d.fps),
                codec = parse(getString(K_CODEC), d.codec),
                bitrateBps = getInt(K_BITRATE, d.bitrateBps),
                priority = parse(getString(K_PRIORITY), d.priority),
                keyframeIntervalSec = getFloat(K_GOP, d.keyframeIntervalSec),
                mirror = getBoolean(K_MIRROR, d.mirror),
                scaleMode = parse(getString(K_SCALE), d.scaleMode),
                rotationLock = getInt(K_ROTATION_LOCK, -1).takeIf { it >= 0 },
                directMode = getBoolean(K_DIRECT, d.directMode),
                powerMode = parse(getString(K_POWER), d.powerMode),
                port = getInt(K_PORT, d.port),
            ).normalized()
        }

        fun fromBundle(b: Bundle): StreamConfig =
            read({ b.getString(it) }, { k, d -> b.getInt(k, d) }, { k, d -> b.getFloat(k, d) }, { k, d -> b.getBoolean(k, d) })

        fun load(prefs: SharedPreferences): StreamConfig =
            read({ prefs.getString(it, null) }, prefs::getInt, prefs::getFloat, prefs::getBoolean)

        /** False until the user (or the first-run default) has saved a configuration. */
        fun isSaved(prefs: SharedPreferences): Boolean = prefs.contains(K_WIDTH)

        fun prefs(context: Context): SharedPreferences =
            context.getSharedPreferences("stream_config", Context.MODE_PRIVATE)
    }
}
