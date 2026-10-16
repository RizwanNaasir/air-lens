package net.silow.airlens.gl

import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

enum class ScaleMode {
    /** Crop to fill the output frame (no bars). */
    FILL,

    /** Fit the whole camera image inside the output, with black bars. */
    FIT,
}

/**
 * Pure math for the GL pass: no Android types, so it runs in local unit tests.
 *
 * Coordinate conventions (all y-up, matching GL texture space after the SurfaceTexture matrix):
 * - **Output UV**: (0,0) is the bottom-left of the encoded frame.
 * - **Upright UV**: the camera image as it looks upright in the device's *natural* orientation.
 *   Camera2 sets a buffer transform (sensor rotation, plus a horizontal flip for front cameras)
 *   that `SurfaceTexture.getTransformMatrix()` applies, so upright UV is what the ST matrix consumes.
 *
 * The final texture coordinate is `ST * UV * (u, v, 0, 1)`.
 */
object FrameTransform {
    /**
     * Builds the 4x4 column-major matrix mapping output UV to upright UV.
     *
     * @param uprightAspect width / height of the upright camera image.
     * @param outputAspect width / height of the encoded frame.
     * @param rotationDegrees clockwise rotation to apply to the picture (0, 90, 180 or 270).
     *   For an accelerometer-driven stream this is the device's clockwise rotation from natural
     *   orientation (`OrientationEventListener` degrees, snapped), which keeps the world upright.
     * @param flipHorizontal mirror the result left-right in output space.
     */
    fun uvMatrix(
        uprightAspect: Float,
        outputAspect: Float,
        rotationDegrees: Int,
        flipHorizontal: Boolean,
        mode: ScaleMode,
        out: FloatArray,
    ) {
        require(out.size >= 16)
        val rot = ((rotationDegrees % 360) + 360) % 360
        val quarterTurn = rot == 90 || rot == 270
        val a = uprightAspect

        // Output height H in source units (source height = 1, width = a). The output rectangle,
        // rotated back into the source, must fit inside it (FILL) or contain it (FIT).
        val h = when {
            !quarterTurn && mode == ScaleMode.FILL -> min(a / outputAspect, 1f)
            !quarterTurn -> max(a / outputAspect, 1f)
            mode == ScaleMode.FILL -> min(a, 1f / outputAspect)
            else -> max(a, 1f / outputAspect)
        }
        val w = outputAspect * h

        // Sampling direction is the inverse of the picture rotation: a picture rotated clockwise
        // by θ samples its source counter-clockwise by θ.
        val (cos, sin) = when (rot) {
            90 -> 0f to 1f
            180 -> -1f to 0f
            270 -> 0f to -1f
            else -> 1f to 0f
        }
        val fx = if (flipHorizontal) -1f else 1f

        // s = R * F * D * (u, v) + R * F * p0, then normalised: uv = N * s + 0.5.
        // D = diag(w, h), p0 = (-w/2, -h/2), F = diag(fx, 1), R = [[cos, -sin], [sin, cos]], N = diag(1/a, 1).
        val m00 = cos * fx * w
        val m01 = -sin * h
        val m10 = sin * fx * w
        val m11 = cos * h
        val px = fx * (-w / 2f)
        val py = -h / 2f
        val t0 = cos * px - sin * py
        val t1 = sin * px + cos * py

        out.fill(0f)
        out[0] = m00 / a
        out[1] = m10
        out[4] = m01 / a
        out[5] = m11
        out[10] = 1f
        out[12] = t0 / a + 0.5f
        out[13] = t1 + 0.5f
        out[15] = 1f
    }

    /**
     * True when the SurfaceTexture matrix swaps the u and v axes (a 90/270 degree buffer
     * transform), i.e. the upright image is the buffer turned sideways.
     */
    fun isAxisSwapped(stMatrix: FloatArray): Boolean = abs(stMatrix[0]) < abs(stMatrix[1])

    fun uprightAspect(bufferWidth: Int, bufferHeight: Int, stMatrix: FloatArray): Float =
        if (isAxisSwapped(stMatrix)) bufferHeight.toFloat() / bufferWidth else bufferWidth.toFloat() / bufferHeight

    /**
     * Direct (no-GL) mode: encoders ignore Camera2's buffer transform, so the encoded picture is in
     * raw sensor orientation. This is the clockwise rotation the *receiver* must apply.
     * Front cameras rotate the other way because their raw buffer is not mirrored.
     */
    fun directModeRotation(sensorOrientation: Int, isFrontFacing: Boolean, deviceRotation: Int): Int {
        val device = if (isFrontFacing) -deviceRotation else deviceRotation
        return ((sensorOrientation + device) % 360 + 360) % 360
    }

    /** out = a * b for 4x4 column-major matrices. [out] must not alias [a] or [b]. */
    fun multiply(a: FloatArray, b: FloatArray, out: FloatArray) {
        for (col in 0 until 4) {
            for (row in 0 until 4) {
                var sum = 0f
                for (k in 0 until 4) sum += a[k * 4 + row] * b[col * 4 + k]
                out[col * 4 + row] = sum
            }
        }
    }

    /** Applies a column-major 4x4 matrix to (u, v, 0, 1); returns (u', v'). */
    fun apply(m: FloatArray, u: Float, v: Float): Pair<Float, Float> =
        (m[0] * u + m[4] * v + m[12]) to (m[1] * u + m[5] * v + m[13])
}
