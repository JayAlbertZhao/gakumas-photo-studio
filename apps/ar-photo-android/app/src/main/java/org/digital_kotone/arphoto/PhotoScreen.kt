package org.digital_kotone.arphoto

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.google.ar.core.Anchor
import com.google.ar.core.Config
import com.google.ar.core.Frame
import com.google.ar.core.Plane
import com.google.ar.core.PlaybackStatus
import com.google.ar.core.Session
import io.github.sceneview.SceneView
import io.github.sceneview.SurfaceType
import io.github.sceneview.ar.ARSceneView
import io.github.sceneview.math.Position
import io.github.sceneview.math.Rotation
import io.github.sceneview.math.Scale
import io.github.sceneview.node.Node
import io.github.sceneview.rememberCameraNode
import io.github.sceneview.rememberEngine
import io.github.sceneview.rememberMaterialLoader
import io.github.sceneview.rememberModelInstance
import io.github.sceneview.rememberModelLoader
import io.github.sceneview.rememberOnGestureListener
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import java.util.concurrent.atomic.AtomicReference
import java.io.File

@Composable
internal fun PhotoScreen(
    modelLocation: String?,
    modelRevision: Int,
    playbackDataset: File?,
    playbackRevision: Int,
    message: String,
    onMessage: (String) -> Unit,
    onPickModel: () -> Unit,
    onPickDataset: () -> Unit,
    onCapture: suspend () -> Unit,
) {
    val engine = rememberEngine()
    val context = LocalContext.current
    val recorder = remember(context) { ArSessionRecorder(context) }
    val modelLoader = rememberModelLoader(engine)
    val materialLoader = rememberMaterialLoader(engine)
    val previewCamera = rememberCameraNode(engine) {
        position = Position(0f, 1.0f, 4.6f)
    }
    var arMode by rememberSaveable { mutableStateOf(false) }
    var replayMode by rememberSaveable { mutableStateOf(false) }
    var replayRun by rememberSaveable { mutableIntStateOf(0) }
    // Each mode owns a separate scene. Recreate the Filament ModelInstance when
    // crossing scenes or replacing the same app-private file with a new GLB.
    // The named URL overload is required for file:// locations.
    var modelTransformRevision by remember { mutableIntStateOf(0) }
    var pendingModelTransformRevision by remember { mutableIntStateOf(0) }
    // Imported GLBs need a fresh instance for visible transform changes. Coalesce
    // quick successive UI actions so a slow file load cannot race another one.
    LaunchedEffect(pendingModelTransformRevision) {
        if (pendingModelTransformRevision != modelTransformRevision) {
            delay(250)
            modelTransformRevision = pendingModelTransformRevision
        }
    }
    val imported = key(arMode, modelRevision, modelTransformRevision) {
        rememberModelInstance(modelLoader = modelLoader, fileLocation = modelLocation ?: "")
    }
    val animationNames = remember(imported) {
        imported?.animator?.let { animator ->
            (0 until animator.animationCount).map { animator.getAnimationName(it) }
        } ?: emptyList()
    }

    var scale by rememberSaveable { mutableFloatStateOf(1f) }
    var yaw by rememberSaveable { mutableFloatStateOf(0f) }
    var wave by rememberSaveable { mutableStateOf(true) }
    var animationIndex by rememberSaveable { mutableIntStateOf(0) }
    var previewX by rememberSaveable { mutableFloatStateOf(0f) }
    var previewY by rememberSaveable { mutableFloatStateOf(0f) }
    var previewWidth by remember { mutableIntStateOf(1) }
    var previewHeight by remember { mutableIntStateOf(1) }
    val currentPreviewWidth = rememberUpdatedState(previewWidth)
    val currentPreviewHeight = rememberUpdatedState(previewHeight)
    var chromeVisible by remember { mutableStateOf(true) }
    var captureInProgress by remember { mutableStateOf(false) }
    var sessionReady by remember { mutableStateOf(false) }
    var recording by remember { mutableStateOf(false) }
    var playbackFinished by remember { mutableStateOf(false) }
    var anchor by remember { mutableStateOf<Anchor?>(null) }
    val latestFrame = remember { AtomicReference<Frame?>(null) }
    val currentSession = remember { AtomicReference<Session?>(null) }
    val scope = rememberCoroutineScope()

    // SceneView recreates the ARCore Session when changing live/playback mode.
    // Stop playback/recording before its disposal closes the native session.
    fun pauseForModeSwitch() {
        currentSession.get()?.let { session ->
            if (recorder.isRecording) {
                val saved = recorder.stop(session)
                recording = false
                onMessage(if (saved != null) "AR 会话已保存：$saved" else "AR 会话录制未能保存")
            }
            runCatching { session.pause() }
        }
    }

    LaunchedEffect(modelLocation, modelRevision) { animationIndex = 0 }
    LaunchedEffect(playbackDataset) { if (playbackDataset == null) replayMode = false }
    DisposableEffect(arMode, replayMode, playbackRevision, replayRun) {
        onDispose {
            currentSession.getAndSet(null)?.let { session ->
                if (recorder.isRecording) recorder.stop(session)
            }
            sessionReady = false
            recording = false
            playbackFinished = false
            anchor?.detach()
            anchor = null
            latestFrame.set(null)
        }
    }

    MaterialTheme {
        Box(Modifier.fillMaxSize().background(Color(0xFFEDE9F3))) {
            if (arMode) {
                key(replayMode, playbackRevision, replayRun) { ARSceneView(
                    modifier = Modifier.fillMaxSize(),
                    surfaceType = SurfaceType.TextureSurface,
                    engine = engine,
                    modelLoader = modelLoader,
                    materialLoader = materialLoader,
                    playbackDataset = if (replayMode) playbackDataset else null,
                    onSessionCreated = { currentSession.set(it) },
                    onSessionResumed = {
                        currentSession.set(it)
                        sessionReady = true
                    },
                    onSessionPaused = { session ->
                        sessionReady = false
                        if (recorder.isRecording) {
                            val saved = recorder.stop(session)
                            recording = false
                            onMessage(if (saved != null) "AR 会话已保存：$saved" else "AR 会话录制未能保存")
                        }
                    },
                    onSessionUpdated = { session, frame ->
                        latestFrame.set(frame)
                        if (replayMode && !playbackFinished &&
                            session.playbackStatus == PlaybackStatus.FINISHED) {
                            playbackFinished = true
                            onMessage("会话回放已结束；点击重新播放可再试放置与拍照")
                        }
                    },
                    onSessionFailed = { error ->
                        sessionReady = false
                        onMessage("AR 无法启动：${error.message ?: "请检查 ARCore 和相机权限"}")
                    },
                    onPlaybackFailed = { error ->
                        sessionReady = false
                        onMessage("会话回放失败：${error.message ?: "请检查是否为 ARCore 数据集"}")
                    },
                    sessionConfiguration = { _, config ->
                        config.planeFindingMode = Config.PlaneFindingMode.HORIZONTAL
                        config.instantPlacementMode = Config.InstantPlacementMode.DISABLED
                    },
                    onGestureListener = rememberOnGestureListener(onSingleTapConfirmed = { event, _ ->
                        val hit = latestFrame.get()?.hitTest(event)?.firstOrNull { result ->
                            val plane = result.trackable as? Plane
                            plane != null && plane.isPoseInPolygon(result.hitPose)
                        }
                        if (hit != null) {
                            val next = hit.createAnchor()
                            anchor?.detach()
                            anchor = next
                            if (imported != null) pendingModelTransformRevision++
                            onMessage("角色已放置；再次轻触可重新放置")
                        } else {
                            onMessage("尚未命中水平面；等待检测后轻触地面")
                        }
                    }),
                ) {
                    anchor?.let { placed ->
                        AnchorNode(anchor = placed) {
                            Node(rotation = Rotation(y = if (imported == null) yaw else 0f),
                                scale = Scale(if (imported == null) scale else 1f)) {
                                PhotoSubject(imported, materialLoader, wave,
                                    animationNames.getOrNull(animationIndex), scale, yaw,
                                    Position(0f, 0f, 0f))
                            }
                        }
                    }
                } }
            } else {
                SyntheticBackdrop()
                SceneView(
                    modifier = Modifier.fillMaxSize().onSizeChanged { size ->
                        previewWidth = size.width
                        previewHeight = size.height
                    },
                    surfaceType = SurfaceType.TextureSurface,
                    isOpaque = false,
                    engine = engine,
                    modelLoader = modelLoader,
                    materialLoader = materialLoader,
                    cameraNode = previewCamera,
                    cameraManipulator = null,
                    autoCenterContent = false,
                    onGestureListener = rememberOnGestureListener(onSingleTapConfirmed = { event, _ ->
                        val width = currentPreviewWidth.value
                        val height = currentPreviewHeight.value
                        if (width > 0 && height > 0) {
                            previewX = ((event.x / width) - 0.5f) * 2.6f
                            previewY = (0.5f - (event.y / height)) * 3.4f
                            if (imported != null) pendingModelTransformRevision++
                            onMessage("已在合成场景放置角色；轻触可重新放置")
                        }
                    }),
                ) {
                    Node(position = if (imported == null) Position(previewX, previewY, 0f)
                        else Position(0f, 0f, 0f),
                        rotation = Rotation(y = if (imported == null) yaw else 0f),
                        scale = Scale(if (imported == null) scale else 1f)) {
                        PhotoSubject(imported, materialLoader, wave,
                            animationNames.getOrNull(animationIndex), scale, yaw,
                            Position(previewX, previewY, 0f))
                    }
                }
            }

            if (chromeVisible) {
                Column(
                    modifier = Modifier.align(Alignment.TopCenter).fillMaxWidth()
                        .statusBarsPadding()
                        .padding(16.dp).background(Color(0xDDEEEDF4), RoundedCornerShape(18.dp))
                        .padding(14.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Row(modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically) {
                        Text("AR Photo", style = MaterialTheme.typography.titleLarge)
                        OutlinedButton(onClick = { chromeVisible = false }) { Text("收起控件") }
                    }
                    Text(message, style = MaterialTheme.typography.bodySmall)
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilterChip(selected = !arMode, onClick = {
                            if (arMode) pauseForModeSwitch()
                            arMode = false; replayMode = false
                        }, label = { Text("合成预览") })
                        FilterChip(selected = arMode && !replayMode, onClick = {
                            if (arMode && replayMode) pauseForModeSwitch()
                            arMode = true; replayMode = false
                        }, label = { Text("真实 AR") })
                        FilterChip(selected = arMode && replayMode, onClick = {
                            if (playbackDataset == null) onPickDataset()
                            else {
                                if (arMode && !replayMode) pauseForModeSwitch()
                                arMode = true; replayMode = true
                            }
                        }, label = { Text("数据回放") })
                    }
                    if (arMode) Text(if (replayMode) "回放录制的相机与传感器数据；轻触检测到的平面放置。" else "扫描水平面并轻触放置；无设备可用合成预览。")
                    if (arMode && !replayMode) Text("录制会话包含周围环境画面和传感器数据，仅保存在本机。")
                    if (modelLocation != null && imported == null) Text("模型加载中；加载失败时显示合成角色。")
                }
                Column(
                    modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth()
                        .navigationBarsPadding()
                        .padding(16.dp).background(Color(0xEAF6F4F9), RoundedCornerShape(18.dp))
                        .padding(14.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedButton(onClick = onPickModel) { Text("导入 GLB") }
                        OutlinedButton(onClick = {
                            anchor?.detach(); anchor = null; scale = 1f; yaw = 0f
                            if (imported != null) pendingModelTransformRevision++
                            previewX = 0f; previewY = 0f
                        }) { Text("重置") }
                        Button(onClick = {
                            scope.launch {
                                captureInProgress = true
                                chromeVisible = false
                                try {
                                    withFrameNanos { }
                                    withFrameNanos { }
                                    onCapture()
                                } finally {
                                    chromeVisible = true
                                    captureInProgress = false
                                }
                            }
                        }) { Text("拍照") }
                    }
                    if (arMode) {
                        OutlinedButton(onClick = onPickDataset) { Text("导入会话 MP4") }
                    }
                    if (arMode && replayMode) {
                        OutlinedButton(onClick = {
                            pauseForModeSwitch()
                            replayRun++
                            onMessage("正在从头播放会话；等待平面检测")
                        }) { Text(if (playbackFinished) "重新播放会话" else "从头播放会话") }
                    }
                    if (arMode && !replayMode) {
                        OutlinedButton(enabled = sessionReady, onClick = {
                            val session = currentSession.get()
                            if (session == null) {
                                onMessage("等待 ARCore 会话就绪")
                            } else if (recorder.isRecording) {
                                val saved = recorder.stop(session)
                                recording = false
                                onMessage(if (saved != null) "AR 会话已保存：$saved" else "AR 会话录制未能保存")
                            } else {
                                runCatching { recorder.start(session) }
                                    .onSuccess {
                                        recording = true
                                        onMessage("正在录制 AR 会话；扫描场景后点停止录制")
                                    }
                                    .onFailure { error ->
                                        onMessage("无法录制 AR 会话：${error.message ?: "请检查 ARCore"}")
                                    }
                            }
                        }) { Text(if (recording) "停止录制会话" else "录制 AR 会话") }
                    }
                    Text("大小 ${"%.1f".format(scale)}×")
                    Slider(value = scale, onValueChange = {
                        scale = it
                    }, onValueChangeFinished = {
                        if (imported != null) pendingModelTransformRevision++
                    }, valueRange = 0.3f..2.5f)
                    Text("旋转 ${yaw.toInt()}°")
                    Slider(value = yaw, onValueChange = {
                        yaw = it
                    }, onValueChangeFinished = {
                        if (imported != null) pendingModelTransformRevision++
                    }, valueRange = 0f..360f)
                    if (animationNames.isNotEmpty()) {
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp),
                            verticalAlignment = Alignment.CenterVertically) {
                            OutlinedButton(onClick = {
                                animationIndex = (animationIndex - 1 + animationNames.size) % animationNames.size
                            }) { Text("上一段") }
                            Text(animationNames.getOrElse(animationIndex) { "动画" }, modifier = Modifier.width(96.dp))
                            OutlinedButton(onClick = {
                                animationIndex = (animationIndex + 1) % animationNames.size
                            }) { Text("下一段") }
                        }
                    } else if (imported == null) {
                        FilterChip(selected = wave, onClick = { wave = !wave }, label = { Text("占位角色挥手") })
                    }
                }
            } else if (!captureInProgress) {
                OutlinedButton(onClick = { chromeVisible = true },
                    modifier = Modifier.align(Alignment.TopEnd).statusBarsPadding()
                        .padding(16.dp)) { Text("显示控件") }
            }
        }
    }
}
