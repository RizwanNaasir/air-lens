package net.silow.airlens.gl

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FrameTransformTest {
    private val m = FloatArray(16)
    private val eps = 1e-4f

    private fun assertMaps(u: Float, v: Float, expectedU: Float, expectedV: Float) {
        val (su, sv) = FrameTransform.apply(m, u, v)
        assertEquals("u for ($u,$v)", expectedU, su, eps)
        assertEquals("v for ($u,$v)", expectedV, sv, eps)
    }

    @Test
    fun sameAspectNoRotationIsIdentity() {
        FrameTransform.uvMatrix(16f / 9, 16f / 9, 0, false, ScaleMode.FILL, m)
        assertMaps(0f, 0f, 0f, 0f)
        assertMaps(1f, 1f, 1f, 1f)
        assertMaps(0.25f, 0.75f, 0.25f, 0.75f)
    }

    @Test
    fun clockwiseQuarterTurnPutsSourceLeftEdgeAtTop() {
        // Portrait-upright source (phone camera, natural orientation) into a landscape frame,
        // phone rotated 90° clockwise: the picture must turn clockwise to stay upright.
        FrameTransform.uvMatrix(9f / 16, 16f / 9, 90, false, ScaleMode.FILL, m)
        // Top-centre of the output samples the source's left-centre.
        assertMaps(0.5f, 1f, 0f, 0.5f)
        // Right-centre of the output samples the source's top-centre.
        assertMaps(1f, 0.5f, 0.5f, 1f)
        // Aspects match after rotation, so corners map to corners (no crop).
        assertMaps(0f, 0f, 1f, 0f)
    }

    @Test
    fun halfTurnFlipsBothAxes() {
        FrameTransform.uvMatrix(16f / 9, 16f / 9, 180, false, ScaleMode.FILL, m)
        assertMaps(0f, 0f, 1f, 1f)
        assertMaps(0.25f, 0.5f, 0.75f, 0.5f)
    }

    @Test
    fun fillCropsCentreOfWiderSource() {
        // 16:9 source into 4:3 output: only the middle 75% of the source width is used.
        FrameTransform.uvMatrix(16f / 9, 4f / 3, 0, false, ScaleMode.FILL, m)
        assertMaps(0f, 0.5f, 0.125f, 0.5f)
        assertMaps(1f, 0.5f, 0.875f, 0.5f)
        assertMaps(0.5f, 0f, 0.5f, 0f)
    }

    @Test
    fun fitLetterboxesTallerSourceOutsideUnitSquare() {
        // 4:3 source into 16:9 output with FIT: output edges sample outside [0,1] -> black bars.
        FrameTransform.uvMatrix(4f / 3, 16f / 9, 0, false, ScaleMode.FIT, m)
        val (left, _) = FrameTransform.apply(m, 0f, 0.5f)
        val (right, _) = FrameTransform.apply(m, 1f, 0.5f)
        assertTrue(left < 0f)
        assertTrue(right > 1f)
        assertMaps(0.5f, 1f, 0.5f, 1f) // full height used
    }

    @Test
    fun horizontalFlipMirrorsInOutputSpace() {
        FrameTransform.uvMatrix(16f / 9, 16f / 9, 0, true, ScaleMode.FILL, m)
        assertMaps(0f, 0.3f, 1f, 0.3f)
        assertMaps(1f, 0.3f, 0f, 0.3f)
    }

    @Test
    fun flipIsAppliedAfterRotation() {
        // With 90° rotation the flip must still mirror left-right of the *output*: the output's
        // left and right edges swap their source samples, while top/bottom stay put.
        FrameTransform.uvMatrix(1f, 1f, 90, true, ScaleMode.FILL, m)
        val unflipped = FloatArray(16).also { FrameTransform.uvMatrix(1f, 1f, 90, false, ScaleMode.FILL, it) }
        val a = FrameTransform.apply(m, 0f, 0.5f)
        val b = FrameTransform.apply(unflipped, 1f, 0.5f)
        assertEquals(b.first, a.first, eps)
        assertEquals(b.second, a.second, eps)
        assertMaps(0.5f, 1f, FrameTransform.apply(unflipped, 0.5f, 1f).first, FrameTransform.apply(unflipped, 0.5f, 1f).second)
    }

    @Test
    fun axisSwapDetection() {
        // Identity-ish ST matrix (no rotation) vs a 90° buffer transform.
        val identity = floatArrayOf(1f, 0f, 0f, 0f, 0f, -1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 1f)
        val rotated = floatArrayOf(0f, -1f, 0f, 0f, -1f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f)
        assertFalse(FrameTransform.isAxisSwapped(identity))
        assertTrue(FrameTransform.isAxisSwapped(rotated))
        assertEquals(1080f / 1920f, FrameTransform.uprightAspect(1920, 1080, rotated), eps)
        assertEquals(1920f / 1080f, FrameTransform.uprightAspect(1920, 1080, identity), eps)
    }

    @Test
    fun directModeRotationFollowsCameraJpegFormula() {
        // Back camera, sensor at 90°: natural portrait needs 90°, rotating the phone adds.
        assertEquals(90, FrameTransform.directModeRotation(90, false, 0))
        assertEquals(180, FrameTransform.directModeRotation(90, false, 90))
        assertEquals(0, FrameTransform.directModeRotation(90, false, 270))
        // Front camera, sensor at 270°: device rotation subtracts.
        assertEquals(270, FrameTransform.directModeRotation(270, true, 0))
        assertEquals(180, FrameTransform.directModeRotation(270, true, 90))
    }

    @Test
    fun multiplyMatchesComposition() {
        val a = FloatArray(16).also { FrameTransform.uvMatrix(16f / 9, 16f / 9, 90, false, ScaleMode.FILL, it) }
        val b = FloatArray(16).also { FrameTransform.uvMatrix(16f / 9, 4f / 3, 0, true, ScaleMode.FILL, it) }
        val ab = FloatArray(16)
        FrameTransform.multiply(a, b, ab)
        val (bu, bv) = FrameTransform.apply(b, 0.3f, 0.7f)
        val expected = FrameTransform.apply(a, bu, bv)
        val actual = FrameTransform.apply(ab, 0.3f, 0.7f)
        assertEquals(expected.first, actual.first, eps)
        assertEquals(expected.second, actual.second, eps)
    }
}
