package org.digital_kotone.arphoto

import android.content.Context
import android.net.Uri
import java.io.File
import java.io.FileOutputStream

/** Keeps one local ARCore recording for repeatable playback; no network or repo assets. */
object DatasetImporter {
    private const val MAX_BYTES = 256L * 1024L * 1024L

    fun savedDataset(context: Context): File? =
        File(context.filesDir, "session-playback.mp4").takeIf { it.isFile && looksLikeMp4(it) }

    fun importMp4(context: Context, uri: Uri): File {
        val target = File(context.filesDir, "session-playback.mp4")
        val temporary = File(context.filesDir, "session-importing.mp4")
        try {
            context.contentResolver.openInputStream(uri)?.use { input ->
                FileOutputStream(temporary).use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var copied = 0L
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) break
                        copied += count
                        require(copied <= MAX_BYTES) { "会话 MP4 超过 256 MB 限额" }
                        output.write(buffer, 0, count)
                    }
                }
            } ?: throw IllegalArgumentException("无法读取所选文件")
            require(looksLikeMp4(temporary)) { "文件不是有效的 MP4 容器" }
            // ARCore performs the definitive dataset validation when playback starts.
            require(temporary.renameTo(target)) { "回放文件写入失败" }
            return target
        } finally {
            temporary.delete()
        }
    }

    private fun looksLikeMp4(file: File): Boolean {
        if (file.length() !in 16..MAX_BYTES) return false
        return file.inputStream().use { stream ->
            val header = ByteArray(12)
            stream.read(header) == header.size &&
                header.copyOfRange(4, 8).contentEquals("ftyp".toByteArray(Charsets.US_ASCII))
        }
    }
}
