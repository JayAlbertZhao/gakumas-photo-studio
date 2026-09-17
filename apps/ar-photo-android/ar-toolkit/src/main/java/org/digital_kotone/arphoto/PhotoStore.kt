package org.digital_kotone.arphoto

import android.content.ContentValues
import android.graphics.Bitmap
import android.os.Build
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import android.view.PixelCopy
import androidx.activity.ComponentActivity
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter
import kotlin.coroutines.resume

object PhotoStore {
    /** Captures the composited window, including the camera TextureView and 3D subject. */
    suspend fun captureAndSave(activity: ComponentActivity): String? {
        val width = activity.window.decorView.width
        val height = activity.window.decorView.height
        if (width < 1 || height < 1) return null
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        val result = try {
            suspendCancellableCoroutine<Int> { continuation ->
                PixelCopy.request(activity.window, bitmap, { code ->
                    if (continuation.isActive) continuation.resume(code)
                }, Handler(Looper.getMainLooper()))
            }
        } catch (error: Exception) {
            bitmap.recycle()
            return null
        }
        if (result != PixelCopy.SUCCESS) {
            bitmap.recycle()
            return null
        }
        val insets = activity.window.decorView.rootWindowInsets
        val bars = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            insets?.getInsets(android.view.WindowInsets.Type.systemBars())
        } else null
        val left = bars?.left ?: 0
        val top = bars?.top ?: insets?.systemWindowInsetTop ?: 0
        val right = bars?.right ?: 0
        val bottom = bars?.bottom ?: insets?.systemWindowInsetBottom ?: 0
        val photo = if (width > left + right && height > top + bottom) {
            Bitmap.createBitmap(bitmap, left, top, width - left - right, height - top - bottom)
        } else bitmap
        return withContext(Dispatchers.IO) {
            try {
                val name = "AR-Photo-" + LocalDateTime.now().format(
                    DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss-SSS")) + ".png"
                val values = ContentValues().apply {
                    put(MediaStore.Images.Media.DISPLAY_NAME, name)
                    put(MediaStore.Images.Media.MIME_TYPE, "image/png")
                    put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/AR Photo")
                    put(MediaStore.Images.Media.IS_PENDING, 1)
                }
                val resolver = activity.contentResolver
                val uri = resolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values)
                    ?: return@withContext null
                try {
                    resolver.openOutputStream(uri)?.use { output ->
                        check(photo.compress(Bitmap.CompressFormat.PNG, 100, output))
                    } ?: error("无法打开相册文件")
                    values.clear()
                    values.put(MediaStore.Images.Media.IS_PENDING, 0)
                    resolver.update(uri, values, null, null)
                    uri.toString()
                } catch (error: Exception) {
                    runCatching { resolver.delete(uri, null, null) }
                    null
                }
            } catch (error: Exception) {
                null
            } finally {
                if (photo !== bitmap) photo.recycle()
                bitmap.recycle()
            }
        }
    }
}
