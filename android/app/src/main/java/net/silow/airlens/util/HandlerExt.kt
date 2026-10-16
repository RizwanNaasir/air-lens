package net.silow.airlens.util

import android.os.Handler
import android.os.Looper
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executor
import java.util.concurrent.TimeUnit

/**
 * Runs [block] on this handler's thread and waits up to [timeoutMs] for it to finish.
 * Runs inline when already on that thread. Returns false on timeout (the block may still run later)
 * or if the looper has already quit.
 */
fun Handler.runSync(timeoutMs: Long, block: () -> Unit): Boolean {
    if (Looper.myLooper() == looper) {
        block()
        return true
    }
    val done = CountDownLatch(1)
    val posted = post {
        try {
            block()
        } finally {
            done.countDown()
        }
    }
    return posted && done.await(timeoutMs, TimeUnit.MILLISECONDS)
}

fun Handler.asExecutor(): Executor = Executor { post(it) }
