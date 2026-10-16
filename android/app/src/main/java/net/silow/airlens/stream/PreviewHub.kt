package net.silow.airlens.stream

import android.view.Surface
import java.util.concurrent.CopyOnWriteArraySet

/**
 * Hands the on-screen preview surface from the UI to the running stream. The service and the
 * activity share one process, so a plain object is enough.
 *
 * Only set while streaming; before that the UI's own [PreviewSession] draws into the surface.
 * [set] runs the listeners synchronously, so when the view destroys its surface the stream has
 * let go of it before [set] returns.
 */
object PreviewHub {
    class Target(val surface: Surface, val width: Int, val height: Int)

    private val listeners = CopyOnWriteArraySet<(Target?) -> Unit>()

    @Volatile var target: Target? = null
        private set

    fun set(next: Target?) {
        if (next === target) return
        target = next
        listeners.forEach { it(next) }
    }

    /** The listener is called right away with the current target. */
    fun add(listener: (Target?) -> Unit) {
        listeners.add(listener)
        listener(target)
    }

    fun remove(listener: (Target?) -> Unit) {
        listeners.remove(listener)
    }
}
