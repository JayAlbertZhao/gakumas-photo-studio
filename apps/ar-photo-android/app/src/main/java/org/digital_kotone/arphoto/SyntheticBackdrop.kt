package org.digital_kotone.arphoto

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke

/** An original, deterministic room so the entire photo flow works without a camera. */
@Composable
internal fun SyntheticBackdrop() {
    Canvas(Modifier.fillMaxSize()) {
        val horizon = size.height * 0.65f
        drawRect(brush = Brush.verticalGradient(listOf(Color(0xFFF6F3FA), Color(0xFFEAE3F1)),
            endY = horizon))
        drawRect(brush = Brush.verticalGradient(listOf(Color(0xFFD7CEE5), Color(0xFFBCB3D1)),
            startY = horizon, endY = size.height),
            topLeft = Offset(0f, horizon), size = Size(size.width, size.height - horizon))

        val windowLeft = size.width * 0.08f
        val windowTop = size.height * 0.12f
        val windowWidth = size.width * 0.24f
        val windowHeight = size.height * 0.28f
        drawRect(Color(0xFFD8E8EE), Offset(windowLeft, windowTop), Size(windowWidth, windowHeight))
        drawRect(Color(0xFFB8B0CF), Offset(windowLeft, windowTop), Size(windowWidth, windowHeight),
            style = Stroke(width = 5f))
        drawLine(Color(0xFFB8B0CF),
            Offset(windowLeft + windowWidth * 0.5f, windowTop),
            Offset(windowLeft + windowWidth * 0.5f, windowTop + windowHeight), 4f)
        drawLine(Color(0xFFB8B0CF),
            Offset(windowLeft, windowTop + windowHeight * 0.55f),
            Offset(windowLeft + windowWidth, windowTop + windowHeight * 0.55f), 4f)

        drawLine(Color(0xFFB8AFC9), Offset(0f, horizon), Offset(size.width, horizon), 5f)
        val vanishing = Offset(size.width * 0.5f, horizon)
        for (step in 0..6) {
            drawLine(Color(0x33FFFFFF), vanishing,
                Offset(size.width * step / 6f, size.height), 2f)
        }
        for (fraction in listOf(0.72f, 0.82f, 0.94f)) {
            val y = size.height * fraction
            val spread = (y - horizon) / (size.height - horizon)
            val left = size.width * (0.5f - 0.5f * spread)
            val right = size.width - left
            val line = Path().apply {
                moveTo(left, y)
                lineTo(right, y)
            }
            drawPath(line, Color(0x33FFFFFF), style = Stroke(width = 2f))
        }
    }
}
