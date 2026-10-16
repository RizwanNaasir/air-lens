package net.silow.airlens.util

import android.content.Context
import android.util.Log
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * `android.util.Log` plus a small rotating file in app-specific storage.
 *
 * Why: EMUI/HarmonyOS (e.g. the Huawei P30) drop third-party `Log.i`/`Log.d` unless "AP Log" is
 * enabled in a hidden service menu, which makes field debugging blind. The file is readable over
 * ADB without root:
 *
 * ```
 * adb pull /sdcard/Android/data/net.silow.airlens/files/airlens.log
 * ```
 *
 * Only lifecycle events, configuration choices and errors go here, never per-frame data, so writes
 * are rare and done synchronously. Capped at [MAX_BYTES] with one previous generation kept.
 * Before [init] (e.g. in JVM unit tests) only the platform log is used.
 */
object AirLog {
    private const val MAX_BYTES = 1_000_000L
    private var file: File? = null
    private val stamp = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US)

    fun init(context: Context) {
        file = context.getExternalFilesDir(null)?.let { File(it, "airlens.log") }
    }

    /** Writes [text] verbatim next to the log (e.g. the device report). Returns the path or null. */
    fun writeSidecar(name: String, text: String): String? = runCatching {
        val dir = file?.parentFile ?: return null
        File(dir, name).apply { writeText(text) }.absolutePath
    }.getOrNull()

    fun i(tag: String, msg: String) {
        Log.i(tag, msg)
        append("I", tag, msg, null)
    }

    fun w(tag: String, msg: String, t: Throwable? = null) {
        Log.w(tag, msg, t)
        append("W", tag, msg, t)
    }

    fun e(tag: String, msg: String, t: Throwable? = null) {
        Log.e(tag, msg, t)
        append("E", tag, msg, t)
    }

    @Synchronized
    private fun append(level: String, tag: String, msg: String, t: Throwable?) {
        val f = file ?: return
        runCatching {
            if (f.length() > MAX_BYTES) f.renameTo(File(f.parentFile, "airlens.log.1"))
            val line = buildString {
                append(stamp.format(Date())).append(' ').append(level).append(' ').append(tag).append(": ").append(msg)
                if (t != null) append(" | ").append(t.javaClass.simpleName).append(": ").append(t.message)
                append('\n')
            }
            f.appendText(line)
        }
    }
}
