import 'package:user_app/features/plans/mobile_plans_screen.dart';
import 'package:better_native_video_player/better_native_video_player.dart';
import 'package:flutter/foundation.dart';
import 'package:user_app/core/media/picture_in_picture.dart';
import 'package:user_app/features/content/mini_player.dart';
import 'dart:io';
import 'dart:ui' as ui;
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/channel/models/channel_models.dart';
import 'package:user_app/core/localization/app_strings.dart';
import 'package:user_app/core/storage/app_preferences.dart';
import 'package:user_app/core/theme/app_theme.dart';
import 'package:user_app/features/account/background_download_settings_screen.dart';
import 'package:user_app/features/content/playback_session.dart';
import 'package:user_app/features/content/watch_screen.dart';
import 'package:user_app/features/creator/video_upload_screen.dart';
import 'auth_test.dart' show MemoryStore, jsonResponse, session;

void main() {
  const capture = bool.fromEnvironment('CAPTURE_MOBILE_UI');
  setUp(() {
    FlutterSecureStorage.setMockInitialValues({});
    ApiClient.invalidateReadCache();
    AppStrings.currentLang.value = 'vi';
  });
  setUpAll(() async {
    if (!capture) return;
    for (final font in {
      'Plus Jakarta Sans': 'C:/Windows/Fonts/arial.ttf',
      'MaterialIcons':
          'D:/flutter/flutter/bin/cache/artifacts/material_fonts/MaterialIcons-Regular.otf',
    }.entries) {
      final bytes = await File(font.value).readAsBytes();
      await (FontLoader(
        font.key,
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });
  for (final scenario in ['allowed', 'denied', 'network']) {
    testWidgets('automatic PiP setting checks plan before saving: $scenario', (
      tester,
    ) async {
      final client = MockClient((r) async {
        if (r.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (r.url.path.endsWith('/my-plan')) {
          return scenario == 'network'
              ? jsonResponse({'code': 'SERVER_ERROR'}, 500)
              : jsonResponse({
                  'features': {'pip': scenario == 'allowed'},
                });
        }
        return jsonResponse({});
      });
      final auth = AuthController(
        ApiClient(client: client),
        MemoryStore('refresh'),
      );
      await auth.restore();
      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.lightTheme,
          home: BackgroundDownloadSettingsScreen(auth: auth),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('automatic-pip-toggle')));
      await tester.pumpAndSettle();
      expect(
        await const AppPreferencesStore().readPipEnabled(),
        scenario == 'allowed',
      );
      if (scenario == 'denied') {
        expect(find.text(AppStrings.t('media.planTitle')), findsOneWidget);
        expect(
          find.widgetWithText(FilledButton, AppStrings.t('media.viewPlans')),
          findsOneWidget,
        );
      }
      if (scenario == 'network') {
        expect(find.text(AppStrings.t('media.checkFailed')), findsOneWidget);
        expect(find.text(AppStrings.t('media.planTitle')), findsNothing);
      }
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
    });
  }
  testWidgets(
    'dislike shows server count, updates after reaction, and locked PiP explains the required plan',
    (tester) async {
      var dislikes = 7;
      var reactions = 0;
      final client = MockClient((r) async {
        final path = r.url.path;
        if (path.endsWith('/auth/refresh')) return jsonResponse(session());
        if (path.endsWith('/plans/my-plan')) {
          return jsonResponse({'features': {}});
        }
        if (path.endsWith('/videos/v')) {
          return jsonResponse({
            'videoId': 'v',
            'title': 'Một video hay',
            'channelId': 'c',
            'channelName': 'Dalitu',
            'stats': {'likes': 52, 'dislikes': dislikes},
            'viewerState': {},
          });
        }
        if (r.method == 'PUT' && path.endsWith('/reaction')) {
          reactions++;
          dislikes++;
          return jsonResponse({
            'likes': 52,
            'dislikes': dislikes,
            'myReaction': 'dislike',
          });
        }
        return jsonResponse({'items': []});
      });
      final auth = AuthController(
        ApiClient(client: client),
        MemoryStore('refresh'),
      );
      await auth.restore();
      final playback = PlaybackSession()
        ..videoId = 'v'
        ..ready = true;
      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.lightTheme,
          home: WatchScreen(auth: auth, playback: playback, videoId: 'v'),
        ),
      );
      await tester.pumpAndSettle();
      final dislike = find.ancestor(
        of: find.byIcon(Icons.thumb_down_outlined),
        matching: find.byType(ActionChip),
      );
      expect(
        find.descendant(of: dislike, matching: find.text('7')),
        findsOneWidget,
      );
      expect(
        find.widgetWithText(ActionChip, AppStrings.t('watch.dislike')),
        findsNothing,
      );
      await tester.ensureVisible(dislike);
      await tester.tap(dislike);
      await tester.pumpAndSettle();
      expect(reactions, 1);
      expect(
        find.descendant(of: dislike, matching: find.text('8')),
        findsOneWidget,
      );
      final pip = find.widgetWithText(
        ActionChip,
        AppStrings.t('watch.pipAction'),
      );
      await tester.ensureVisible(pip);
      await tester.tap(pip);
      await tester.pumpAndSettle();
      expect(find.text(AppStrings.t('media.planTitle')), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
      playback.dispose();
      auth.dispose();
    },
  );
  testWidgets(
    'system PiP expands the mini-player with one native surface and restores it',
    (tester) async {
      debugDefaultTargetPlatformOverride = TargetPlatform.windows;
      addTearDown(() => debugDefaultTargetPlatformOverride = null);
      final messenger = tester.binding.defaultBinaryMessenger;
      messenger.setMockMethodCallHandler(
        const MethodChannel('native_video_player'),
        (_) async => null,
      );
      final playback = PlaybackSession()
        ..player = NativeVideoPlayerController(id: 551)
        ..videoId = 'video'
        ..title = 'Video mini'
        ..ready = true
        ..minimized = true;
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(body: MiniPlayer(session: playback)),
        ),
      );
      await tester.pumpAndSettle();
      final element = tester.element(find.byType(NativeVideoPlayer));
      expect(find.text('Video mini'), findsOneWidget);
      AppPictureInPicture.active.value = true;
      await tester.pumpAndSettle();
      expect(find.byType(NativeVideoPlayer), findsOneWidget);
      expect(tester.element(find.byType(NativeVideoPlayer)), same(element));
      expect(find.text('Video mini'), findsNothing);
      AppPictureInPicture.active.value = false;
      await tester.pumpAndSettle();
      expect(find.text('Video mini'), findsOneWidget);
      expect(playback.minimized, true);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
      playback.dispose();
      debugDefaultTargetPlatformOverride = null;
    },
  );
  for (final fromGate in [true, false]) {
    testWidgets(
      'View plans opens a Material page and Back restores settings, gate=$fromGate',
      (tester) async {
        final client = MockClient((r) async {
          if (r.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          if (r.url.path.endsWith('/plans')) {
            return http.Response(
              '[{"planId":"free","name":"FREE","price":0,"storageLimit":3221225472,"maxMembers":1,"features":{}}]',
              200,
            );
          }
          if (r.url.path.endsWith('/my-plan')) {
            return jsonResponse({'name': 'FREE', 'features': {}});
          }
          return http.Response('[]', 200);
        });
        final auth = AuthController(
          ApiClient(client: client),
          MemoryStore('refresh'),
        );
        await auth.restore();
        await tester.pumpWidget(
          MaterialApp(
            theme: AppTheme.lightTheme,
            home: BackgroundDownloadSettingsScreen(auth: auth),
          ),
        );
        await tester.pumpAndSettle();
        if (fromGate) {
          await tester.tap(find.byKey(const ValueKey('automatic-pip-toggle')));
        }
        await tester.pumpAndSettle();
        final viewPlans = find.text(AppStrings.t('media.viewPlans'));
        await tester.ensureVisible(viewPlans.last);
        await tester.tap(viewPlans.last);
        await tester.pumpAndSettle();
        expect(find.byType(MobilePlansScreen), findsOneWidget);
        final pageContext = tester.element(find.byType(MobilePlansScreen));
        expect(Scaffold.maybeOf(pageContext), isNotNull);
        final textStyle = DefaultTextStyle.of(pageContext).style;
        expect(textStyle.decorationStyle, isNot(TextDecorationStyle.double));
        expect(find.byType(BackButton), findsOneWidget);
        expect(tester.takeException(), isNull);
        await tester.tap(find.byType(BackButton));
        await tester.pumpAndSettle();
        expect(find.byType(BackgroundDownloadSettingsScreen), findsOneWidget);
        expect(find.byType(MobilePlansScreen), findsNothing);
        await tester.pumpWidget(const SizedBox.shrink());
        auth.dispose();
      },
    );
  }
  testWidgets(
    'watch PiP preserves the native surface through shrinking and restoration',
    (tester) async {
      debugDefaultTargetPlatformOverride = TargetPlatform.windows;
      tester.view.physicalSize = const Size(390, 844);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final messenger = tester.binding.defaultBinaryMessenger;
      messenger.setMockMethodCallHandler(
        const MethodChannel('native_video_player'),
        (_) async => null,
      );
      final client = MockClient(
        (r) async => r.url.path.endsWith('/videos/v')
            ? jsonResponse({
                'videoId': 'v',
                'title': 'PiP restore video',
                'channelName': 'Channel',
              })
            : jsonResponse({'items': []}),
      );
      final auth = AuthController(ApiClient(client: client), MemoryStore());
      final playback = PlaybackSession()
        ..videoId = 'v'
        ..ready = true
        ..player = NativeVideoPlayerController(id: 997);
      await tester.pumpWidget(
        MaterialApp(
          home: WatchScreen(auth: auth, playback: playback, videoId: 'v'),
        ),
      );
      await tester.pumpAndSettle();
      final surface = tester.element(find.byType(NativeVideoPlayer));
      AppPictureInPicture.active.value = true;
      await tester.pumpAndSettle();
      tester.view.physicalSize = const Size(220, 124);
      await tester.pumpAndSettle();
      expect(tester.element(find.byType(NativeVideoPlayer)), same(surface));
      expect(find.byType(ActionChip), findsNothing);
      tester.view.physicalSize = const Size(390, 844);
      AppPictureInPicture.active.value = false;
      await tester.pump(const Duration(milliseconds: 600));
      await tester.pumpAndSettle();
      expect(tester.element(find.byType(NativeVideoPlayer)), same(surface));
      expect(find.byType(ActionChip), findsWidgets);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
      playback.dispose();
      auth.dispose();
      debugDefaultTargetPlatformOverride = null;
    },
  );
  for (final dark in [false, true]) {
    for (final size in [
      const Size(320, 568),
      const Size(390, 844),
      const Size(640, 360),
    ]) {
      for (final scale in [1.0, 2.0]) {
        testWidgets(
          'upload and media settings dark=$dark size=$size scale=$scale',
          (tester) async {
            tester.view.physicalSize = size;
            tester.view.devicePixelRatio = 1;
            addTearDown(tester.view.resetPhysicalSize);
            addTearDown(tester.view.resetDevicePixelRatio);
            final client = MockClient((r) async {
              final p = r.url.path;
              if (p.endsWith('/auth/refresh')) return jsonResponse(session());
              if (p.endsWith('/categories') || p.endsWith('/playlists')) {
                return http.Response('[]', 200);
              }
              if (p.endsWith('/plans/my-plan')) {
                return jsonResponse({
                  'features': {'pip': true, 'background_play': true},
                });
              }
              return jsonResponse({'items': []});
            });
            final auth = AuthController(
              ApiClient(client: client),
              MemoryStore('refresh'),
            );
            await auth.restore();
            final channel = ChannelDetail.fromJson({
              'channelId': 'c',
              'name': 'Dalitu Studio',
              'handle': 'dalitu',
              'isOwner': true,
            });
            for (final entry in <String, Widget>{
              'upload': VideoUploadScreen(auth: auth, channel: channel),
              'settings': BackgroundDownloadSettingsScreen(auth: auth),
            }.entries) {
              final boundary = GlobalKey();
              await tester.pumpWidget(
                MaterialApp(
                  theme: dark ? AppTheme.darkTheme : AppTheme.lightTheme,
                  builder: (context, child) => MediaQuery(
                    data: MediaQuery.of(
                      context,
                    ).copyWith(textScaler: TextScaler.linear(scale)),
                    child: RepaintBoundary(key: boundary, child: child!),
                  ),
                  home: entry.value,
                ),
              );
              await tester.pumpAndSettle();
              expect(tester.takeException(), isNull, reason: entry.key);
              if (capture && size.width == 390 && scale == 1) {
                await tester.runAsync(() async {
                  final image =
                      await (boundary.currentContext!.findRenderObject()
                              as RenderRepaintBoundary)
                          .toImage();
                  final bytes = await image.toByteData(
                    format: ui.ImageByteFormat.png,
                  );
                  final folder = Directory('../../outputs/media-upload-refresh')
                    ..createSync(recursive: true);
                  await File(
                    '${folder.path}/${entry.key}-${dark ? 'dark' : 'light'}.png',
                  ).writeAsBytes(bytes!.buffer.asUint8List());
                  image.dispose();
                });
              }
              if (entry.key == 'upload') {
                await tester.scrollUntilVisible(
                  find.text(AppStrings.t('upload.optionsSection')),
                  160,
                  scrollable: find.byType(Scrollable).first,
                );
                final optionsTitle = find.text(
                  AppStrings.t('upload.optionsSection'),
                );
                final scrolling = tester.state<ScrollableState>(
                  find.byType(Scrollable).first,
                );
                await scrolling.position.ensureVisible(
                  tester.renderObject(optionsTitle),
                  alignment: .15,
                );
                await tester.pumpAndSettle();
                await tester.tap(optionsTitle);
                await tester.pumpAndSettle();
                expect(
                  find.text(AppStrings.t('parity.language')),
                  findsWidgets,
                );
                final language = find.byKey(const ValueKey('language-vi'));
                final comments = find.widgetWithText(
                  SwitchListTile,
                  AppStrings.t('parity.comments'),
                );
                expect(
                  tester.getTopLeft(comments).dy -
                      tester.getBottomLeft(language).dy,
                  greaterThanOrEqualTo(16),
                );
                if (capture && size.width == 390 && scale == 1) {
                  await tester.runAsync(() async {
                    final image =
                        await (boundary.currentContext!.findRenderObject()
                                as RenderRepaintBoundary)
                            .toImage();
                    final bytes = await image.toByteData(
                      format: ui.ImageByteFormat.png,
                    );
                    final folder = Directory(
                      '../../outputs/media-upload-refresh',
                    )..createSync(recursive: true);
                    await File(
                      '${folder.path}/upload-options-${dark ? 'dark' : 'light'}.png',
                    ).writeAsBytes(bytes!.buffer.asUint8List());
                    image.dispose();
                  });
                }
                final submit = find.widgetWithText(
                  FilledButton,
                  AppStrings.t('upload.uploadAction'),
                );
                expect(submit, findsOneWidget);
                expect(tester.takeException(), isNull);
              }
              await tester.pumpWidget(const SizedBox.shrink());
              await tester.pumpAndSettle();
            }
            auth.dispose();
          },
        );
      }
    }
  }
}
