package com.hutube.user_app

import android.content.res.Configuration
import android.app.PictureInPictureUiState
import android.os.Build

import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine

class MainActivity : FlutterActivity() {
    private var mediaTools: MediaTools? = null
    private var deviceConditions: DeviceConditions? = null
    private var pictureInPicture: PictureInPictureHost? = null
    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        mediaTools = MediaTools(this, flutterEngine.dartExecutor.binaryMessenger)
        deviceConditions = DeviceConditions(this, flutterEngine.dartExecutor.binaryMessenger)
        pictureInPicture = PictureInPictureHost(this, flutterEngine.dartExecutor.binaryMessenger)
    }
    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        pictureInPicture?.onUserLeaveHint()
    }
    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        pictureInPicture?.onModeChanged(isInPictureInPictureMode)
    }
    override fun onPictureInPictureUiStateChanged(pipState: PictureInPictureUiState) {
        super.onPictureInPictureUiStateChanged(pipState)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.VANILLA_ICE_CREAM && pipState.isTransitioningToPip) {
            pictureInPicture?.onTransitionStarted()
        }
    }
    override fun onResume() {
        super.onResume()
        pictureInPicture?.onResume()
    }
    override fun onDestroy() {
        mediaTools?.close()
        mediaTools = null
        deviceConditions?.close()
        deviceConditions = null
        pictureInPicture?.close()
        pictureInPicture = null
        super.onDestroy()
    }
}
