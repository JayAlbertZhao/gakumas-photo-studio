package org.digital_kotone.arphoto

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import com.google.ar.core.RecordingConfig
import com.google.ar.core.RecordingStatus
import com.google.ar.core.Session
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

/** Saves an ARCore dataset (camera frames and motion data), not just a screen video. */
internal class ArSessionRecorder(private val context: Context) {
    private var activeUri: Uri? = null
    val isRecording: Boolean get() = activeUri != null

    fun start(session: Session) {
        check(activeUri == null) { "AR 会话已在录制" }
        val name = "AR-Session-" + LocalDateTime.now().format(
            DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss-SSS")) + ".mp4"
        val values = ContentValues().apply {
            put(MediaStore.Video.Media.DISPLAY_NAME, name)
            put(MediaStore.Video.Media.MIME_TYPE, "video/mp4")
            put(MediaStore.Video.Media.RELATIVE_PATH, Environment.DIRECTORY_MOVIES + "/AR Photo")
            put(MediaStore.Video.Media.IS_PENDING, 1)
        }
        val resolver = context.contentResolver
        val uri = resolver.insert(MediaStore.Video.Media.EXTERNAL_CONTENT_URI, values)
            ?: error("无法创建 AR 会话文件")
        try {
            session.startRecording(RecordingConfig(session)
                .setMp4DatasetUri(uri)
                .setAutoStopOnPause(true))
            check(session.recordingStatus == RecordingStatus.OK) { "ARCore 未开始录制" }
            activeUri = uri
        } catch (error: Exception) {
            runCatching { resolver.delete(uri, null, null) }
            throw error
        }
    }

    /** Finishes the MP4 and makes it visible in Movies/AR Photo. */
    fun stop(session: Session): Uri? {
        val uri = activeUri ?: return null
        activeUri = null
        val resolver = context.contentResolver
        return try {
            // Auto-stop may already have happened when the Activity was paused.
            if (session.recordingStatus == RecordingStatus.OK) session.stopRecording()
            val bytes = resolver.openFileDescriptor(uri, "r")?.use { it.statSize } ?: 0L
            check(bytes > 0L) { "AR 会话文件为空" }
            val values = ContentValues().apply { put(MediaStore.Video.Media.IS_PENDING, 0) }
            check(resolver.update(uri, values, null, null) == 1) { "无法发布 AR 会话文件" }
            uri
        } catch (error: Exception) {
            runCatching { resolver.delete(uri, null, null) }
            null
        }
    }
}
