package com.hutube.user_app

import android.app.Activity
import android.app.PictureInPictureParams
import android.content.pm.PackageManager
import android.graphics.Rect
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Rational
import io.flutter.plugin.common.BinaryMessenger
import io.flutter.plugin.common.MethodChannel

class PictureInPictureHost(private val activity: Activity, messenger: BinaryMessenger) {
    private val channel = MethodChannel(messenger, "hutube/picture_in_picture")
    private var automatic = false
    private var sourceRect: Rect? = null
    private val handler = Handler(Looper.getMainLooper())
    init {
        channel.setMethodCallHandler { call, result ->
            when (call.method) {
                "available" -> result.success(available())
                "setSourceRect" -> {
                    val left = call.argument<Int>("left")
                    val top = call.argument<Int>("top")
                    val right = call.argument<Int>("right")
                    val bottom = call.argument<Int>("bottom")
                    if (left == null || top == null || right == null || bottom == null || right <= left || bottom <= top) {
                        result.error("INVALID_SOURCE_RECT", "Video bounds must be non-empty", null)
                    } else {
                        sourceRect = Rect(left, top, right, bottom)
                        updateParams()
                        result.success(null)
                    }
                }
                "enter" -> {
                    val entered = enter()
                    if (!entered) onModeChanged(false)
                    result.success(entered)
                }
                "configure" -> {
                    automatic = call.argument<Boolean>("automatic") == true
                    updateParams()
                    result.success(null)
                }
                else -> result.notImplemented()
            }
        }
    }
    private fun available() = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
        activity.packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)
    private fun params(): PictureInPictureParams {
        val builder = PictureInPictureParams.Builder()
        sourceRect?.let { rect ->
            // Leave rounding room inside Android's supported aspect-ratio range.
            val aspect = (rect.width().toDouble() / rect.height()).coerceIn(1.0 / 2.38, 2.38)
            builder.setAspectRatio(Rational((aspect * 10000).toInt(), 10000))
                .setSourceRectHint(rect)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            builder.setAutoEnterEnabled(automatic && sourceRect != null)
                .setSeamlessResizeEnabled(false)
        }
        return builder.build()
    }
    private fun updateParams() {
        if (!available() || activity.isFinishing) return
        try { activity.setPictureInPictureParams(params()) } catch (_: Exception) { }
    }
    private fun enter(): Boolean {
        if (!available() || activity.isFinishing || sourceRect == null) return false
        return try { activity.enterPictureInPictureMode(params()) } catch (_: Exception) { false }
    }
    fun onUserLeaveHint() {
        if (!automatic || sourceRect == null || !available()) return
        // Do not wait for a Dart callback or a Flutter frame after Home: the
        // activity can already be paused. Its video bounds were cached on screen.
        onTransitionStarted()
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S && !enter()) onModeChanged(false)
    }
    fun onTransitionStarted() {
        channel.invokeMethod("modeChanged", true)
        handler.removeCallbacksAndMessages(null)
        handler.postDelayed({
            if (!activity.isInPictureInPictureMode) onModeChanged(false)
        }, 1500)
    }
    fun onModeChanged(active: Boolean) {
        handler.removeCallbacksAndMessages(null)
        channel.invokeMethod("modeChanged", active)
    }
    fun onResume() {
        if (available() && !activity.isInPictureInPictureMode) onModeChanged(false)
    }
    fun close() { automatic = false; handler.removeCallbacksAndMessages(null); channel.setMethodCallHandler(null) }
}
