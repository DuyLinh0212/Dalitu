import 'package:app_links/app_links.dart';
import 'package:better_native_video_player/better_native_video_player.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

import 'auth.dart';
import 'app/mobile_app.dart';
import 'core/localization/app_strings.dart';
import 'core/theme/theme_notifier.dart';

export 'app/mobile_app.dart' show HuTubeApp;
export 'core/widgets/app_logo.dart';
export 'core/widgets/app_shell.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  // Compose the video in the same Flutter scene as its controls. Android
  // activity PiP then resizes one surface instead of a hybrid SurfaceView
  // retaining the inline player's old bounds over the activity background.
  NativeVideoPlayerConfig.global = const NativeVideoPlayerConfig(
    androidTextureMode: true,
  );
  await SystemChrome.setPreferredOrientations([DeviceOrientation.portraitUp]);
  await Future.wait([AppStrings.restore(), ThemeNotifier.restore()]);
  final links = AppLinks();
  runApp(
    HuTubeApp(
      auth: AuthController(ApiClient(), SecureTokenStore()),
      links: links.uriLinkStream,
    ),
  );
}
