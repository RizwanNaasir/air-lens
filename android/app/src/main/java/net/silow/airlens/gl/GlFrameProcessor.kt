package net.silow.airlens.gl

import android.graphics.SurfaceTexture
import android.opengl.EGL14
import android.opengl.EGLSurface
import android.opengl.GLES11Ext
import android.opengl.GLES20
import android.os.Handler
import android.os.HandlerThread
import android.os.Process
import net.silow.airlens.util.AirLog
import android.view.Surface
import net.silow.airlens.util.PipelineTiming
import net.silow.airlens.util.runSync
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.FloatBuffer
import java.util.concurrent.atomic.AtomicInteger

/**
 * The GPU stage between Camera2 and the encoder:
 *
 * ```
 * camera → SurfaceTexture (OES texture) → one textured quad (rotate / crop / mirror) → EGLSurface(encoder input)
 * ```
 *
 * Zero CPU copies: the camera buffer is sampled as an external texture and drawn straight into the
 * encoder's gralloc buffers. The encoded size stays fixed while the phone rotates, so rotation never
 * restarts the encoder.
 *
 * **Latest-frame latch.** `updateTexImage()` is FIFO. If this thread ever falls behind, every pending
 * buffer is latched and only the newest is drawn, so latency cannot pile up inside the GPU stage.
 *
 * **On-screen preview.** The same frame can also be drawn into the phone's preview surface
 * ([setPreview]) after the encoder has its copy, so the phone shows exactly what the PC gets. When
 * nothing is streamed, [toEncoder] = false makes the preview surface itself the output.
 *
 * All GL work runs on one dedicated thread. Public setters only write volatile fields.
 */
class GlFrameProcessor(
    private val encoderSurface: Surface,
    private val outputWidth: Int,
    private val outputHeight: Int,
    @Volatile private var cameraBufferWidth: Int,
    @Volatile private var cameraBufferHeight: Int,
    /** False when the output is the on-screen preview: no presentation time, no latency probe. */
    private val toEncoder: Boolean = true,
    /** Shape of the picture; the stream's shape, also when the output is a preview of other size. */
    private val pictureAspect: Float = outputWidth.toFloat() / outputHeight,
    private val onError: (Throwable) -> Unit,
) {
    private val thread = HandlerThread("AirLens-GL", Process.THREAD_PRIORITY_URGENT_DISPLAY).apply { start() }
    private val handler = Handler(thread.looper)

    private lateinit var egl: EglCore
    private lateinit var eglSurface: EGLSurface
    private lateinit var surfaceTexture: SurfaceTexture
    private lateinit var cameraSurface: Surface
    private var program = 0
    private var textureId = 0
    private var aPosition = 0
    private var aTexCoord = 0
    private var uTexMatrix = 0
    private var uUvMatrix = 0

    // Preallocated: nothing on the per-frame path allocates.
    private val stMatrix = FloatArray(16)
    private val uvMatrix = FloatArray(16)
    private val quadPositions = floatBuffer(-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f)
    private val quadTexCoords = floatBuffer(0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f)
    private val pendingFrames = AtomicInteger()
    private val drawRunnable = Runnable { drawFrame() }
    private var previewSurface: EGLSurface? = null
    private var previewWidth = 0
    private var previewHeight = 0

    @Volatile var rotationDegrees = 0
    @Volatile var flipHorizontal = false
    @Volatile var scaleMode = ScaleMode.FILL

    @Volatile var framesRendered = 0L
        private set

    /** Frames latched but not drawn because a newer one was already waiting. */
    @Volatile var framesSkipped = 0L
        private set

    @Volatile private var released = false

    /** Initialises EGL on the GL thread and returns the Surface the camera should render into. */
    fun start(): Surface {
        var failure: Throwable? = null
        val ok = handler.runSync(INIT_TIMEOUT_MS) {
            try {
                init()
            } catch (t: Throwable) {
                failure = t
            }
        }
        failure?.let { throw IllegalStateException("GL init failed", it) }
        check(ok) { "GL init timed out" }
        return cameraSurface
    }

    private fun init() {
        egl = EglCore()
        eglSurface = egl.createWindowSurface(encoderSurface)
        egl.makeCurrent(eglSurface)

        program = buildProgram(VERTEX_SHADER, FRAGMENT_SHADER)
        aPosition = GLES20.glGetAttribLocation(program, "aPosition")
        aTexCoord = GLES20.glGetAttribLocation(program, "aTexCoord")
        uTexMatrix = GLES20.glGetUniformLocation(program, "uTexMatrix")
        uUvMatrix = GLES20.glGetUniformLocation(program, "uUvMatrix")

        val tex = IntArray(1)
        GLES20.glGenTextures(1, tex, 0)
        textureId = tex[0]
        GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, textureId)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_CLAMP_TO_EDGE)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE)

        surfaceTexture = SurfaceTexture(textureId).apply {
            setDefaultBufferSize(cameraBufferWidth, cameraBufferHeight)
            // Delivered on this thread's looper. Each callback is exactly one queued buffer.
            setOnFrameAvailableListener({
                if (pendingFrames.getAndIncrement() == 0) handler.post(drawRunnable)
            }, handler)
        }
        cameraSurface = Surface(surfaceTexture)
        AirLog.i(TAG, "GL ready: camera ${cameraBufferWidth}x$cameraBufferHeight -> encoder ${outputWidth}x$outputHeight")
    }

    private fun drawFrame() {
        if (released) return
        val pending = pendingFrames.getAndSet(0)
        if (pending == 0) return
        try {
            repeat(pending) { surfaceTexture.updateTexImage() }
            framesSkipped += pending - 1
            surfaceTexture.getTransformMatrix(stMatrix)

            FrameTransform.uvMatrix(
                uprightAspect = FrameTransform.uprightAspect(cameraBufferWidth, cameraBufferHeight, stMatrix),
                outputAspect = pictureAspect,
                rotationDegrees = rotationDegrees,
                flipHorizontal = flipHorizontal,
                mode = scaleMode,
                out = uvMatrix,
            )

            drawQuad(outputWidth, outputHeight)
            if (toEncoder) {
                PipelineTiming.onGlLatched(surfaceTexture.timestamp)
                egl.setPresentationTime(eglSurface, surfaceTexture.timestamp)
                if (!egl.swapBuffers(eglSurface)) {
                    throw IllegalStateException("eglSwapBuffers failed; encoder surface abandoned")
                }
            } else {
                egl.swapBuffers(eglSurface) // false once the view is gone; the owner closes us then
            }
            framesRendered++
            drawPreview()
        } catch (t: Throwable) {
            if (!released) onError(t)
        }
    }

    /**
     * The encoder already has this frame; a slow display can delay only the preview copy. A broken
     * preview (the view went away between callbacks) is dropped, never reported as a stream error.
     */
    private fun drawPreview() {
        val preview = previewSurface ?: return
        try {
            egl.makeCurrent(preview)
            drawQuad(previewWidth, previewHeight)
            egl.swapBuffers(preview)
        } catch (t: Throwable) {
            AirLog.w(TAG, "preview dropped: ${t.message}")
            runCatching { egl.releaseSurface(preview) }
            previewSurface = null
        } finally {
            egl.makeCurrent(eglSurface)
        }
    }

    private fun drawQuad(viewportWidth: Int, viewportHeight: Int) {
        GLES20.glViewport(0, 0, viewportWidth, viewportHeight)
        GLES20.glClearColor(0f, 0f, 0f, 1f)
        GLES20.glClear(GLES20.GL_COLOR_BUFFER_BIT)
        GLES20.glUseProgram(program)
        GLES20.glActiveTexture(GLES20.GL_TEXTURE0)
        GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, textureId)
        GLES20.glUniformMatrix4fv(uTexMatrix, 1, false, stMatrix, 0)
        GLES20.glUniformMatrix4fv(uUvMatrix, 1, false, uvMatrix, 0)
        GLES20.glEnableVertexAttribArray(aPosition)
        GLES20.glVertexAttribPointer(aPosition, 2, GLES20.GL_FLOAT, false, 0, quadPositions)
        GLES20.glEnableVertexAttribArray(aTexCoord)
        GLES20.glVertexAttribPointer(aTexCoord, 2, GLES20.GL_FLOAT, false, 0, quadTexCoords)
        GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4)
    }

    /**
     * Attaches (or with null, detaches) the on-screen preview. Blocks until the GL thread has
     * switched, so a surface the view is about to destroy is never drawn into afterwards.
     */
    fun setPreview(surface: Surface?, width: Int, height: Int) {
        if (released) return
        handler.runSync(INIT_TIMEOUT_MS) {
            if (!::egl.isInitialized) return@runSync
            previewSurface?.let { runCatching { egl.releaseSurface(it) } }
            previewSurface = null
            if (surface != null && surface.isValid && width > 0 && height > 0) {
                previewSurface = runCatching { egl.createWindowSurface(surface) }
                    .onFailure { AirLog.w(TAG, "preview surface rejected: ${it.message}") }
                    .getOrNull()
                previewWidth = width
                previewHeight = height
                previewSurface?.let {
                    // Never wait for the display: the preview must not slow the encoder down.
                    egl.makeCurrent(it)
                    EGL14.eglSwapInterval(egl.display, 0)
                    egl.makeCurrent(eglSurface)
                }
            }
        }
    }

    /**
     * Lens switch: the next camera session may deliver a different buffer size. Call while no
     * camera is attached (between closing the old session and opening the new one). The encoder
     * and its surface are untouched, so the PC sees a short pause instead of a new stream.
     */
    fun setCameraBufferSize(width: Int, height: Int) {
        handler.runSync(INIT_TIMEOUT_MS) {
            cameraBufferWidth = width
            cameraBufferHeight = height
            if (::surfaceTexture.isInitialized) surfaceTexture.setDefaultBufferSize(width, height)
        }
    }

    /**
     * Releases GL objects, the SurfaceTexture and EGL on the GL thread, then quits it. Call after
     * the camera has stopped rendering into the camera surface. Idempotent.
     */
    fun release() {
        if (released) return
        released = true
        handler.removeCallbacks(drawRunnable)
        val finished = handler.runSync(TEARDOWN_TIMEOUT_MS) {
            if (::surfaceTexture.isInitialized) {
                surfaceTexture.setOnFrameAvailableListener(null)
                runCatching { surfaceTexture.release() }
            }
            if (::cameraSurface.isInitialized) runCatching { cameraSurface.release() }
            if (::egl.isInitialized) {
                runCatching {
                    if (program != 0) GLES20.glDeleteProgram(program)
                    if (textureId != 0) GLES20.glDeleteTextures(1, intArrayOf(textureId), 0)
                }
                previewSurface?.let { runCatching { egl.releaseSurface(it) } }
                previewSurface = null
                if (::eglSurface.isInitialized) runCatching { egl.releaseSurface(eglSurface) }
                runCatching { egl.release() }
            }
        }
        if (!finished) AirLog.e(TAG, "GL teardown timed out")
        thread.quitSafely()
    }

    private fun buildProgram(vertex: String, fragment: String): Int {
        val vs = compile(GLES20.GL_VERTEX_SHADER, vertex)
        val fs = compile(GLES20.GL_FRAGMENT_SHADER, fragment)
        val p = GLES20.glCreateProgram()
        GLES20.glAttachShader(p, vs)
        GLES20.glAttachShader(p, fs)
        GLES20.glLinkProgram(p)
        val status = IntArray(1)
        GLES20.glGetProgramiv(p, GLES20.GL_LINK_STATUS, status, 0)
        GLES20.glDeleteShader(vs)
        GLES20.glDeleteShader(fs)
        check(status[0] == GLES20.GL_TRUE) { "link failed: ${GLES20.glGetProgramInfoLog(p)}" }
        return p
    }

    private fun compile(type: Int, source: String): Int {
        val shader = GLES20.glCreateShader(type)
        GLES20.glShaderSource(shader, source)
        GLES20.glCompileShader(shader)
        val status = IntArray(1)
        GLES20.glGetShaderiv(shader, GLES20.GL_COMPILE_STATUS, status, 0)
        check(status[0] == GLES20.GL_TRUE) { "shader compile failed: ${GLES20.glGetShaderInfoLog(shader)}" }
        return shader
    }

    private companion object {
        const val TAG = "AirLens.GL"
        const val INIT_TIMEOUT_MS = 3000L
        const val TEARDOWN_TIMEOUT_MS = 2000L

        // Both varyings are affine in the vertex attributes, so computing them per vertex is exact.
        const val VERTEX_SHADER = """
            uniform mat4 uTexMatrix;
            uniform mat4 uUvMatrix;
            attribute vec4 aPosition;
            attribute vec4 aTexCoord;
            varying vec2 vTex;
            varying vec2 vUv;
            void main() {
                gl_Position = aPosition;
                vec4 upright = uUvMatrix * aTexCoord;
                vUv = upright.xy;
                vTex = (uTexMatrix * upright).xy;
            }
        """

        // highp where available: mediump texcoords (~11-bit mantissa) are visibly off by a texel at 4K.
        const val FRAGMENT_SHADER = """
            #extension GL_OES_EGL_image_external : require
            #ifdef GL_FRAGMENT_PRECISION_HIGH
            precision highp float;
            #else
            precision mediump float;
            #endif
            uniform samplerExternalOES sTexture;
            varying vec2 vTex;
            varying vec2 vUv;
            void main() {
                if (vUv.x < 0.0 || vUv.x > 1.0 || vUv.y < 0.0 || vUv.y > 1.0) {
                    gl_FragColor = vec4(0.0, 0.0, 0.0, 1.0); // FIT-mode bars
                } else {
                    gl_FragColor = texture2D(sTexture, vTex);
                }
            }
        """

        fun floatBuffer(vararg values: Float): FloatBuffer =
            ByteBuffer.allocateDirect(values.size * 4).order(ByteOrder.nativeOrder()).asFloatBuffer().apply {
                put(values)
                position(0)
            }
    }
}
