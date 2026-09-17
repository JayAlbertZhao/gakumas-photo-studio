package org.digital_kotone.arphoto

import com.google.ar.core.Pose
import dev.romainguy.kotlin.math.Quaternion
import io.github.sceneview.math.Position
import io.github.sceneview.node.ModelNode
import kotlin.math.cos
import kotlin.math.sin

/** Apply the complete ARCore anchor transform while keeping the model's feet offset local. */
internal fun applyAnchorPose(node: ModelNode, alignment: Position, anchorPose: Pose, yawDegrees: Float) {
    val point = anchorPose.transformPoint(floatArrayOf(alignment.x, alignment.y, alignment.z))
    val halfYaw = Math.toRadians(yawDegrees.toDouble()).toFloat() * 0.5f
    val rotation = anchorPose.compose(Pose.makeRotation(0f, sin(halfYaw), 0f, cos(halfYaw)))
    node.position = Position(point[0], point[1], point[2])
    node.quaternion = Quaternion(rotation.qx(), rotation.qy(), rotation.qz(), rotation.qw())
}
