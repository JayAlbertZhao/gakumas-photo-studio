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
import com.google.ar.core.ArCoreApk
import com.google.ar.core.exceptions.UnavailableUserDeclinedInstallationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {
    private var pendingArReady: (() -> Unit)? = null
    private var pendingArFailure: ((String) -> Unit)? = null
    private var waitingForArCoreInstall = false
    private var arRequestId = 0
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

    /** Do not create an AR scene until the optional ARCore service is usable. */
    private fun ensureArCoreReady(onReady: () -> Unit, onFailure: (String) -> Unit) {
        val requestId = ++arRequestId
        pendingArReady = onReady
        pendingArFailure = onFailure
        waitingForArCoreInstall = false
        try {
            ArCoreApk.getInstance().checkAvailabilityAsync(this) { availability ->
                if (requestId != arRequestId || isDestroyed) return@checkAvailabilityAsync
                if (!availability.isSupported) {
                    failArRequest("此设备暂不支持 ARCore；可继续使用合成预览")
                    return@checkAvailabilityAsync
                }
                // A manually disabled ARCore APK can still be present on disk.
                // Starting its native session in that state crashed the emulator.
                val serviceDisabled = runCatching {
                    !packageManager.getApplicationInfo("com.google.ar.core", 0).enabled
                }.getOrDefault(false)
                if (serviceDisabled) {
                    failArRequest("Google Play Services for AR 已停用；请在系统设置启用")
                    return@checkAvailabilityAsync
                }
                requestArCoreInstall(userRequested = true)
            }
        } catch (error: Exception) {
            failArRequest("无法检查 ARCore：${error.message ?: "请稍后重试"}")
        }
    }

    private fun requestArCoreInstall(userRequested: Boolean) {
        try {
            when (ArCoreApk.getInstance().requestInstall(this, userRequested)) {
                ArCoreApk.InstallStatus.INSTALLED -> {
                    waitingForArCoreInstall = false
                    val ready = pendingArReady
                    pendingArReady = null
                    pendingArFailure = null
                    ready?.invoke()
                }
                ArCoreApk.InstallStatus.INSTALL_REQUESTED -> waitingForArCoreInstall = true
            }
        } catch (_: UnavailableUserDeclinedInstallationException) {
            failArRequest("未安装 Google Play Services for AR；仍可使用合成预览")
        } catch (error: Exception) {
            failArRequest("ARCore 无法准备就绪：${error.message ?: "请检查设备支持情况"}")
        }
    }

    private fun failArRequest(message: String) {
        waitingForArCoreInstall = false
        pendingArReady = null
        val failure = pendingArFailure
        pendingArFailure = null
        failure?.invoke(message)
    }

    private fun cancelArCoreRequest() {
        arRequestId++
        waitingForArCoreInstall = false
        pendingArReady = null
        pendingArFailure = null
    }

    override fun onResume() {
        super.onResume()
        if (waitingForArCoreInstall) requestArCoreInstall(userRequested = false)
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
                onEnsureArReady = { onReady, onFailure -> ensureArCoreReady(onReady, onFailure) },
                onCancelArRequest = { cancelArCoreRequest() },
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
