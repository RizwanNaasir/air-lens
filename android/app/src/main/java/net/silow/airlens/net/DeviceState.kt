package net.silow.airlens.net

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

/** Payload of DEVICE_STATE: what the PC may change (docs/PROTOCOL.md, "Remote control"). */
data class DeviceState(
    val device: String,
    /** The phone's stable id, the same one discovery announces. */
    val id: String,
    val lensKey: String?,
    val lenses: List<Lens>,
    /** Id of the matching preset, or "custom". */
    val quality: String,
    val presets: List<Preset>,
    val focus: Focus? = null,
    val heat: Heat? = null,
) {
    data class Lens(val key: String, val label: String, val kind: String)

    /** [value]: 0 far .. 1 near while manual, null while autofocus. */
    data class Focus(val supported: Boolean, val value: Float?)

    /** [lowered]: the preset label the stream was stepped down to because of heat, else null. */
    data class Heat(val tempC: Float, val level: String, val lowered: String?)

    data class Preset(
        val id: String,
        val label: String,
        val tag: String,
        val width: Int,
        val height: Int,
        val fps: Int,
        /** "h264" or "hevc". */
        val codec: String,
        val bitrateBps: Int,
        val direct: Boolean,
    )

    fun toJson(): ByteArray {
        val o = JSONObject()
        o.put("device", device)
        o.put("id", id)
        o.put("lens", lensKey ?: JSONObject.NULL)
        o.put("lenses", JSONArray().also { a -> lenses.forEach { a.put(JSONObject().put("key", it.key).put("label", it.label).put("kind", it.kind)) } })
        o.put("quality", quality)
        o.put(
            "presets",
            JSONArray().also { a ->
                presets.forEach { p ->
                    a.put(
                        JSONObject()
                            .put("id", p.id)
                            .put("label", p.label)
                            .put("tag", p.tag)
                            .put("width", p.width)
                            .put("height", p.height)
                            .put("fps", p.fps)
                            .put("codec", p.codec)
                            .put("bitrateBps", p.bitrateBps)
                            .put("direct", p.direct),
                    )
                }
            },
        )
        focus?.let { f ->
            o.put("focus", JSONObject().put("supported", f.supported).put("auto", f.value == null).put("value", f.value?.toDouble() ?: JSONObject.NULL))
        }
        heat?.let { h ->
            o.put("heat", JSONObject()
                .put("tempC", if (h.tempC.isNaN()) JSONObject.NULL else Math.round(h.tempC * 10) / 10.0)
                .put("level", h.level)
                .put("lowered", h.lowered ?: JSONObject.NULL))
        }
        return o.toString().toByteArray(Charsets.UTF_8)
    }
}

/**
 * Payload of CONTROL. Malformed JSON, unknown keys and empty values all mean "nothing".
 * [focus]: a number 0 (far) .. 1 (near) for manual focus; [focusAuto] for "focus": "auto";
 * [focusLock] for "focus": "lock", which fixes focus wherever autofocus has it right now.
 * [virtualCamera]: the PC's virtual camera is on, so the phone can skip drawing its own preview.
 */
data class ControlRequest(
    val lens: String? = null,
    val quality: String? = null,
    val focus: Float? = null,
    val focusAuto: Boolean = false,
    val virtualCamera: Boolean? = null,
    val focusLock: Boolean = false,
) {
    val isEmpty get() = lens == null && quality == null && focus == null && !focusAuto && !focusLock && virtualCamera == null

    companion object {
        fun parse(payload: ByteArray, length: Int = payload.size): ControlRequest = try {
            val o = JSONObject(String(payload, 0, length, Charsets.UTF_8))
            val focusValue = o.opt("focus")
            ControlRequest(
                lens = o.stringOrNull("lens"),
                quality = o.stringOrNull("quality"),
                focus = (focusValue as? Number)?.toFloat()?.coerceIn(0f, 1f),
                focusAuto = focusValue == "auto",
                focusLock = focusValue == "lock",
                virtualCamera = if (o.has("virtualCamera") && !o.isNull("virtualCamera")) o.optBoolean("virtualCamera") else null,
            )
        } catch (_: JSONException) {
            ControlRequest()
        }

        private fun JSONObject.stringOrNull(key: String): String? =
            if (!has(key) || isNull(key)) null else optString(key).ifEmpty { null }
    }
}
