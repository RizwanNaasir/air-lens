package net.silow.airlens.net

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DeviceStateTest {
    @Test
    fun deviceStateSerialisesTheDocumentedFields() {
        val state = DeviceState(
            device = "HUAWEI P30",
            id = "3f9a0c1d2b4e5f60",
            lensKey = "2",
            lenses = listOf(DeviceState.Lens("3", "Ultra-wide", "ultra_wide"), DeviceState.Lens("2", "Main", "wide")),
            quality = "best",
            presets = listOf(DeviceState.Preset("best", "4K", "Best picture", 3840, 2160, 30, "hevc", 80_000_000, false)),
        )
        val o = JSONObject(String(state.toJson(), Charsets.UTF_8))
        assertEquals("HUAWEI P30", o.getString("device"))
        assertEquals("3f9a0c1d2b4e5f60", o.getString("id"))
        assertEquals("2", o.getString("lens"))
        assertEquals("ultra_wide", o.getJSONArray("lenses").getJSONObject(0).getString("kind"))
        assertEquals("best", o.getString("quality"))
        val p = o.getJSONArray("presets").getJSONObject(0)
        assertEquals(3840, p.getInt("width"))
        assertEquals("hevc", p.getString("codec"))
        assertEquals(80_000_000, p.getInt("bitrateBps"))
        assertEquals(false, p.getBoolean("direct"))
    }

    @Test
    fun controlRequestParsesLensAndQuality() {
        val r = ControlRequest.parse("""{"lens":"3","quality":"fast","future":1}""".toByteArray())
        assertEquals("3", r.lens)
        assertEquals("fast", r.quality)
    }

    @Test
    fun focusAndHeatAreSerialised() {
        val state = DeviceState("P", "id", "2", emptyList(), "best", emptyList(),
            focus = DeviceState.Focus(supported = true, value = 0.25f),
            heat = DeviceState.Heat(44.06f, "hot", "1440p"))
        val o = JSONObject(String(state.toJson(), Charsets.UTF_8))
        assertEquals(false, o.getJSONObject("focus").getBoolean("auto"))
        assertEquals(0.25, o.getJSONObject("focus").getDouble("value"), 1e-6)
        assertEquals(44.1, o.getJSONObject("heat").getDouble("tempC"), 1e-6)
        assertEquals("1440p", o.getJSONObject("heat").getString("lowered"))
    }

    @Test
    fun controlCarriesFocusAndTheVirtualCameraFlag() {
        val manual = ControlRequest.parse("""{"focus":0.4,"virtualCamera":true}""".toByteArray())
        assertEquals(0.4f, manual.focus!!, 1e-6f)
        assertEquals(true, manual.virtualCamera)
        assertTrue(ControlRequest.parse("""{"focus":"auto"}""".toByteArray()).focusAuto)
        val lock = ControlRequest.parse("""{"focus":"lock"}""".toByteArray())
        assertTrue(lock.focusLock && !lock.focusAuto && lock.focus == null && !lock.isEmpty)
        assertEquals(1f, ControlRequest.parse("""{"focus":7}""".toByteArray()).focus!!, 1e-6f)
    }

    @Test
    fun malformedOrEmptyControlIsIgnored() {
        assertTrue(ControlRequest.parse("not json".toByteArray()).isEmpty)
        assertTrue(ControlRequest.parse("""{"lens":null,"quality":""}""".toByteArray()).isEmpty)
        val buf = """{"lens":"1"}xxxx""".toByteArray()
        assertEquals("1", ControlRequest.parse(buf, 12).lens)
        assertNull(ControlRequest.parse("{}".toByteArray()).quality)
    }
}
