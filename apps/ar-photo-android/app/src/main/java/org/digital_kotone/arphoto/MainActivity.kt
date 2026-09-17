package org.digital_kotone.arphoto

import android.net.Uri
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {
    private val modelLocation = mutableStateOf<String?>(null)
    private val modelRevision = mutableIntStateOf(0)
    private val playbackDataset = mutableStateOf<java.io.File?>(null)
    private val playbackRevision = mutableIntStateOf(0)
    private val message = mutableStateOf("选择预览或 AR；可导入自有 GLB 模型")

    private val pickModel = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
        if (uri == null) return@registerForActivityResult
        lifecycleScope.launch {
            message.value = "正在导入模型…"
            try {
                val file = withContext(Dispatchers.IO) { SubjectImporter.importGlb(this@MainActivity, uri) }
                modelLocation.value = "file://${file.absolutePath}"
                modelRevision.intValue++
                message.value = "已导入：${file.name}"
            } catch (error: Exception) {
                message.value = "导入失败：${error.message ?: "文件不受支持"}"
            }
        }
    }

    private val pickDataset = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
        if (uri == null) return@registerForActivityResult
        lifecycleScope.launch {
            message.value = "正在导入 ARCore 会话…"
            try {
                val file = withContext(Dispatchers.IO) { DatasetImporter.importMp4(this@MainActivity, uri) }
                playbackDataset.value = file
                playbackRevision.intValue++
                message.value = "已导入会话；切换到数据回放以验证"
            } catch (error: Exception) {
                message.value = "会话导入失败：${error.message ?: "文件不受支持"}"
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        SubjectImporter.savedModel(this)?.let {
            modelLocation.value = "file://${it.absolutePath}"
            message.value = "已恢复导入模型；可切换模式、调整并拍照"
        }
        playbackDataset.value = DatasetImporter.savedDataset(this)
        setContent {
            PhotoScreen(
                modelLocation = modelLocation.value,
                modelRevision = modelRevision.intValue,
                playbackDataset = playbackDataset.value,
                playbackRevision = playbackRevision.intValue,
                message = message.value,
                onMessage = { message.value = it },
                onPickModel = { pickModel.launch(arrayOf("model/gltf-binary", "application/octet-stream", "*/*")) },
                onPickDataset = { pickDataset.launch(arrayOf("video/mp4", "*/*")) },
                onCapture = {
                    val saved = PhotoStore.captureAndSave(this@MainActivity)
                    withContext(Dispatchers.Main) {
                        val result = saved?.let { "照片已保存：$it" } ?: "截图失败，请重试"
                        message.value = result
                        Toast.makeText(this@MainActivity, result, Toast.LENGTH_SHORT).show()
                    }
                },
            )
        }
    }
}
