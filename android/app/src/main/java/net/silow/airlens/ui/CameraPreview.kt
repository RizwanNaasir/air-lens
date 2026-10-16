package net.silow.airlens.ui

import android.graphics.SurfaceTexture
import android.view.Surface
import android.view.TextureView
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import net.silow.airlens.stream.PreviewHub

/**
 * The view the camera picture is drawn into (by the idle preview or by the stream itself).
 * [onTarget] gets the surface when it is ready or resized, and null when it goes away, which
 * also happens when this leaves the composition.
 *
 * A TextureView rather than a SurfaceView: a SurfaceView punches a hole through the window, which
 * left the viewfinder's background undrawn (transparent). The extra GPU copy is irrelevant for a
 * preview, and the stream itself never waits on it.
 */
@Composable
fun CameraPreview(modifier: Modifier, onTarget: (PreviewHub.Target?) -> Unit) {
    val current by rememberUpdatedState(onTarget)
    AndroidView(
        modifier = modifier,
        factory = { ctx ->
            TextureView(ctx).apply {
                surfaceTextureListener = object : TextureView.SurfaceTextureListener {
                    private var surface: Surface? = null

                    override fun onSurfaceTextureAvailable(texture: SurfaceTexture, width: Int, height: Int) {
                        val s = Surface(texture)
                        surface = s
                        current(PreviewHub.Target(s, width, height))
                    }

                    override fun onSurfaceTextureSizeChanged(texture: SurfaceTexture, width: Int, height: Int) {
                        surface?.let { current(PreviewHub.Target(it, width, height)) }
                    }

                    override fun onSurfaceTextureDestroyed(texture: SurfaceTexture): Boolean {
                        current(null) // GL lets go of the surface before it is released
                        surface?.release()
                        surface = null
                        return true
                    }

                    override fun onSurfaceTextureUpdated(texture: SurfaceTexture) = Unit
                }
            }
        },
    )
}
