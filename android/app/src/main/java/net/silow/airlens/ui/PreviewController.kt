package net.silow.airlens.ui

import android.content.Context
import android.os.Handler
import android.os.Looper
import net.silow.airlens.camera.LensOption
import net.silow.airlens.power.OrientationTracker
import net.silow.airlens.stream.PreviewHub
import net.silow.airlens.stream.PreviewSession
import net.silow.airlens.stream.StreamConfig
import net.silow.airlens.util.AirLog

/**
 * Decides who draws into the on-screen preview: the UI's own [PreviewSession] while nothing is
 * streaming, the stream's GL stage (through [PreviewHub]) while it is. Main thread only.
 *
 * The camera can have one owner at a time, so [releaseCamera] must run before the service is asked
 * to stream. After a stop the service may still be closing the camera; the idle preview then fails
 * to open and simply retries a moment later.
 */
class PreviewController(private val context: Context) {
    private val main = Handler(Looper.getMainLooper())
    private val orientation = OrientationTracker(context) { r -> session?.setRotation(r) }
    private var target: PreviewHub.Target? = null
    private var streaming = false
    private var lens: LensOption? = null
    private var config: StreamConfig? = null
    private var session: PreviewSession? = null
    private var sessionKey: List<Any?>? = null
    private var retries = 0
    private var closed = false
    private val retry = Runnable { reconcile() }

    /** From the SurfaceView's callbacks: a new surface, or null when it is destroyed. */
    fun onSurface(next: PreviewHub.Target?) {
        target = next
        retries = 0
        reconcile()
    }

    fun update(streaming: Boolean, lens: LensOption?, config: StreamConfig) {
        if (lens?.key != this.lens?.key) retries = 0
        this.streaming = streaming
        this.lens = lens
        this.config = config
        reconcile()
    }

    /** Right before asking the service to stream: frees the camera for it. Blocking, briefly. */
    fun releaseCamera() {
        streaming = true
        reconcile()
    }

    fun close() {
        closed = true
        main.removeCallbacks(retry)
        stopSession()
        PreviewHub.set(null)
    }

    private fun reconcile() {
        if (closed) return
        main.removeCallbacks(retry)
        if (streaming) {
            stopSession()
            PreviewHub.set(target)
            return
        }
        PreviewHub.set(null)
        val t = target
        val l = lens
        val c = config
        if (t == null || l == null || c == null) {
            stopSession()
            return
        }
        val key = listOf(t, l.key, c.outputWidth, c.outputHeight, c.mirror, c.scaleMode, c.rotationLock)
        if (session != null && key == sessionKey) return
        stopSession()
        orientation.start()
        session = runCatching { PreviewSession(context, l, c, t, orientation.rotation, ::onSessionError) }
            .onFailure { AirLog.w(TAG, "preview start failed: ${it.message}") }
            .getOrNull()
        sessionKey = key
    }

    /** Camera or GL thread. */
    private fun onSessionError(message: String) {
        main.post {
            if (closed) return@post
            AirLog.w(TAG, "preview: $message")
            stopSession()
            if (retries++ < MAX_RETRIES) main.postDelayed(retry, RETRY_MS)
        }
    }

    private fun stopSession() {
        session?.close()
        session = null
        sessionKey = null
        orientation.stop()
    }

    private companion object {
        const val TAG = "AirLens.Preview"
        const val MAX_RETRIES = 6
        const val RETRY_MS = 700L
    }
}
