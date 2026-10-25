package local.phonewebcam.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Path
import android.util.AttributeSet
import android.view.View

/** Camera-viewfinder corner brackets drawn over the preview (matches the Windows app). */
class ViewfinderCorners @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {
    private val density = resources.displayMetrics.density
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = 0xD8FFFFFF.toInt()
        style = Paint.Style.STROKE
        strokeWidth = 2 * density
    }
    private val path = Path()

    override fun onDraw(canvas: Canvas) {
        val inset = 14 * density
        val arm = 22 * density
        val w = width.toFloat()
        val h = height.toFloat()
        path.reset()
        for ((x, y, dx, dy) in listOf(
            floatArrayOf(inset, inset, 1f, 1f),
            floatArrayOf(w - inset, inset, -1f, 1f),
            floatArrayOf(w - inset, h - inset, -1f, -1f),
            floatArrayOf(inset, h - inset, 1f, -1f),
        )) {
            path.moveTo(x, y + dy * arm)
            path.lineTo(x, y)
            path.lineTo(x + dx * arm, y)
        }
        canvas.drawPath(path, paint)
    }
}
