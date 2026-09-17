package org.digital_kotone.arphoto

import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import com.google.ar.core.Pose
import io.github.sceneview.NodeScope
import io.github.sceneview.loaders.MaterialLoader
import io.github.sceneview.math.Position
import io.github.sceneview.math.Rotation
import io.github.sceneview.math.colorOf
import io.github.sceneview.model.ModelInstance
import io.github.sceneview.node.ModelNode as ModelNodeImpl
import kotlin.math.sin

@Composable
internal fun NodeScope.PhotoSubject(
    imported: ModelInstance?,
    materialLoader: MaterialLoader,
    wave: Boolean,
    animationName: String?,
    size: Float,
    yaw: Float,
    importedPosition: Position,
    anchorPose: Pose? = null,
    onImportedNodeReady: ((ModelNodeImpl, Position) -> Unit)? = null,
) {
    if (imported != null) {
        ModelNode(
            modelInstance = imported,
            scaleToUnits = 1.6f * size,
            centerOrigin = Position(0f, -1f, 0f),
            position = importedPosition,
            rotation = if (anchorPose == null) Rotation(y = yaw) else Rotation(0f),
            animationName = animationName,
            autoAnimate = true,
            // Apply before the model enters Filament's scene. Declarative changes to
            // the parent node do not reliably move loaded GLB renderables in 4.25.
            apply = {
                val offset = position
                if (anchorPose != null) {
                    applyAnchorPose(this, offset, anchorPose, yaw)
                } else {
                    position = Position(offset.x + importedPosition.x,
                        offset.y + importedPosition.y, offset.z + importedPosition.z)
                }
                onImportedNodeReady?.invoke(this, offset)
            },
        )
        return
    }

    // Original low-poly stand-in, synthesized in code rather than copied from a game.
    val skin = remember(materialLoader) {
        materialLoader.createColorInstance(colorOf(0xFFFFD4B8.toInt()), metallic = 0f, roughness = 0.9f)
    }
    val hair = remember(materialLoader) {
        materialLoader.createColorInstance(colorOf(0xFF463746.toInt()), metallic = 0f, roughness = 0.8f)
    }
    val uniform = remember(materialLoader) {
        materialLoader.createColorInstance(colorOf(0xFF576AA0.toInt()), metallic = 0f, roughness = 0.8f)
    }
    val accent = remember(materialLoader) {
        materialLoader.createColorInstance(colorOf(0xFFFFB550.toInt()), metallic = 0f, roughness = 0.8f)
    }
    val shoes = remember(materialLoader) {
        materialLoader.createColorInstance(colorOf(0xFF2D3045.toInt()), metallic = 0f, roughness = 0.8f)
    }
    var phase by remember { mutableFloatStateOf(0f) }
    LaunchedEffect(wave) {
        if (!wave) { phase = 0f; return@LaunchedEffect }
        while (true) withFrameNanos { time -> phase = time / 1_000_000_000f }
    }

    SphereNode(radius = 0.17f, position = Position(0f, 1.42f, -0.04f), materialInstance = hair)
    SphereNode(radius = 0.15f, position = Position(0f, 1.40f, 0.03f), materialInstance = skin)
    SphereNode(radius = 0.08f, position = Position(-0.11f, 1.51f, 0.10f), materialInstance = hair)
    SphereNode(radius = 0.08f, position = Position(0.07f, 1.51f, 0.10f), materialInstance = hair)
    SphereNode(radius = 0.016f, position = Position(-0.05f, 1.41f, 0.17f), materialInstance = shoes)
    SphereNode(radius = 0.016f, position = Position(0.05f, 1.41f, 0.17f), materialInstance = shoes)
    CylinderNode(radius = 0.16f, height = 0.42f,
        position = Position(0f, 1.04f, 0f), materialInstance = uniform)
    ConeNode(radius = 0.28f, height = 0.36f,
        position = Position(0f, 0.64f, 0f), materialInstance = uniform)
    CylinderNode(radius = 0.055f, height = 0.44f,
        position = Position(-0.11f, 0.25f, 0f), materialInstance = skin)
    CylinderNode(radius = 0.055f, height = 0.44f,
        position = Position(0.11f, 0.25f, 0f), materialInstance = skin)
    SphereNode(radius = 0.09f, position = Position(-0.11f, 0.04f, 0.06f), materialInstance = shoes)
    SphereNode(radius = 0.09f, position = Position(0.11f, 0.04f, 0.06f), materialInstance = shoes)
    Node(position = Position(-0.22f, 1.22f, 0f),
        rotation = Rotation(z = 20f + if (wave) sin(phase * 4f) * 12f else 0f)) {
        CylinderNode(radius = 0.045f, height = 0.40f,
            position = Position(-0.07f, -0.19f, 0f), materialInstance = skin)
    }
    Node(position = Position(0.22f, 1.22f, 0f),
        rotation = Rotation(z = -55f + if (wave) sin(phase * 7f) * 25f else 0f)) {
        CylinderNode(radius = 0.045f, height = 0.40f,
            position = Position(0.07f, -0.19f, 0f), materialInstance = skin)
    }
    SphereNode(radius = 0.055f, position = Position(0f, 1.01f, 0.16f), materialInstance = accent)
}
