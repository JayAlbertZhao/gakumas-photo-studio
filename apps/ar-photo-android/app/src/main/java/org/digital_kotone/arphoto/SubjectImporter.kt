package org.digital_kotone.arphoto

import android.content.Context
import android.net.Uri
import org.json.JSONObject
import java.io.DataInputStream
import java.io.File
import java.io.FileOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

internal object SubjectImporter {
    private const val MAX_BYTES = 80L * 1024L * 1024L
    private const val MAX_JSON_BYTES = 4 * 1024 * 1024

    fun savedModel(context: Context): File? =
        File(context.filesDir, "subject.glb").takeIf { file ->
            file.isFile && runCatching { validateGlb(file) }.isSuccess
        }

    /** Import only a self-contained GLB into private storage; never publish source assets. */
    fun importGlb(context: Context, uri: Uri): File {
        val target = File(context.filesDir, "subject.glb")
        val temporary = File(context.filesDir, "subject-importing.glb")
        try {
            context.contentResolver.openInputStream(uri)?.use { input ->
                FileOutputStream(temporary).use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var copied = 0L
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) break
                        copied += count
                        require(copied <= MAX_BYTES) { "GLB 超过 80 MB 限额" }
                        output.write(buffer, 0, count)
                    }
                }
            } ?: throw IllegalArgumentException("无法读取所选文件")
            validateGlb(temporary)
            // Same-directory rename replaces the previous import atomically on Android.
            // A rejected or interrupted import leaves the old model intact.
            require(temporary.renameTo(target)) { "模型写入失败" }
            return target
        } finally {
            temporary.delete()
        }
    }

    private fun validateGlb(file: File) {
        require(file.length() in 20..MAX_BYTES) { "GLB 文件长度无效或超过 80 MB" }
        DataInputStream(file.inputStream().buffered()).use { stream ->
            val header = ByteArray(20)
            stream.readFully(header)
            val bytes = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN)
            require(bytes.int == 0x46546c67) { "仅支持二进制 GLB" }
            require(bytes.int == 2) { "仅支持 glTF 2.0 GLB" }
            require(bytes.int.toLong() == file.length()) { "GLB 声明长度与文件不一致" }
            val jsonLength = bytes.int
            require(jsonLength in 1..MAX_JSON_BYTES && jsonLength.toLong() <= file.length() - 20) {
                "GLB JSON 块长度无效"
            }
            require(bytes.int == 0x4e4f534a) { "GLB 缺少 JSON 块" }
            val jsonBytes = ByteArray(jsonLength)
            stream.readFully(jsonBytes)
            val json = JSONObject(String(jsonBytes, Charsets.UTF_8))
            for (section in arrayOf("buffers", "images")) {
                val entries = json.optJSONArray(section) ?: continue
                for (index in 0 until entries.length()) {
                    val uri = entries.optJSONObject(index)?.optString("uri").orEmpty()
                    require(uri.isEmpty() || uri.startsWith("data:")) {
                        "GLB 引用了外部文件；请导出自包含模型"
                    }
                }
            }
        }
    }
}
