import 'package:flutter/material.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/testing.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/core/media/device_conditions.dart';
import 'package:user_app/core/media/picture_in_picture.dart';
import 'package:user_app/core/storage/app_preferences.dart';
import 'package:user_app/features/content/content_models.dart';
import 'package:user_app/features/content/media_entitlements.dart';
import 'package:user_app/features/content/playback_session.dart';
import 'auth_test.dart' show MemoryStore, jsonResponse, session;

class LocalConditions extends DeviceConditions {
  @override
  Future<void> initialize() async {}
  @override
  Future<void> refresh() async {}
}

void main() {
  for (final enabled in [true, false]) {
    for (final permitted in [true, false]) {
      testWidgets(
        'Android registers automatic PiP: setting=$enabled permission=$permitted',
        (tester) async {
          debugDefaultTargetPlatformOverride = TargetPlatform.android;
          addTearDown(() => debugDefaultTargetPlatformOverride = null);
          FlutterSecureStorage.setMockInitialValues({
            AppPreferencesStore.playbackPipKey: '$enabled',
          });
          final messenger = tester.binding.defaultBinaryMessenger;
          final registrations = <bool>[];
          messenger.setMockMethodCallHandler(AppPictureInPicture.channel, (
            call,
          ) async {
            if (call.method == 'configure') {
              registrations.add((call.arguments as Map)['automatic'] == true);
            }
            return null;
          });
          messenger.setMockMethodCallHandler(
            const MethodChannel('native_video_player'),
            (call) async {
              if (call.method == 'setupControllerEventChannel') {
                final channel = MethodChannel(
                  'native_video_player_controller_${(call.arguments as Map)['controllerId']}',
                );
                messenger.setMockMethodCallHandler(channel, (_) async => null);
                addTearDown(
                  () => messenger.setMockMethodCallHandler(channel, null),
                );
              }
              return null;
            },
          );
          addTearDown(() {
            messenger.setMockMethodCallHandler(
              AppPictureInPicture.channel,
              null,
            );
            messenger.setMockMethodCallHandler(
              const MethodChannel('native_video_player'),
              null,
            );
          });
          final client = MockClient(
            (_) async => jsonResponse({
              'features': {'pip': permitted},
            }),
          );
          final auth = AuthController(ApiClient(client: client), MemoryStore())
            ..user = {'userId': 'one'};
          final conditions = LocalConditions();
          final playback = PlaybackSession(conditions: conditions);
          final entitlements = MediaEntitlements(pictureInPicture: permitted);
          await tester.runAsync(() async {
            await playback.start(
              auth: auth,
              video: VideoDetail.fromJson({'videoId': 'v', 'title': 'Video'}),
              videoRenditions: [],
              mediaEntitlements: entitlements,
              sourceUrl: 'https://video.test/video.mp4',
              resumeAt: 0,
            );
            playback.ready = true;
            playback.isPlaying = true;
            playback.updateMediaEntitlements(entitlements);
            await Future<void>.delayed(Duration.zero);
            expect(registrations.last, enabled && permitted);
            await const AppPreferencesStore().writePipEnabled(!enabled);
            await Future<void>.delayed(const Duration(milliseconds: 20));
            expect(
              registrations.last,
              !enabled && permitted,
              reason: 'Changing the setting reconfigures the current video',
            );
            playback.isPlaying = false;
            playback.updateMediaEntitlements(entitlements);
            await Future<void>.delayed(Duration.zero);
            expect(
              registrations.last,
              isFalse,
              reason: 'Pausing disarms automatic PiP',
            );
            playback.isPlaying = true;
            playback.updateMediaEntitlements(entitlements);
            await Future<void>.delayed(Duration.zero);
            expect(registrations.last, !enabled && permitted);
            playback.dispose();
            await Future<void>.delayed(Duration.zero);
            expect(
              registrations.last,
              isFalse,
              reason: 'Closing playback disarms automatic PiP',
            );
            conditions.dispose();
            auth.dispose();
            client.close();
          });
          debugDefaultTargetPlatformOverride = null;
        },
      );
    }
  }
  for (final mode in ['off', 'always', 'headphones']) {
    for (final permitted in [false, true]) {
      testWidgets('background policy $mode, entitlement=$permitted', (
        tester,
      ) async {
        FlutterSecureStorage.setMockInitialValues({
          AppPreferencesStore.backgroundPlaybackModeKey: mode,
          AppPreferencesStore.playbackPipKey: '${mode != 'off'}',
        });
        await tester.pumpWidget(const MaterialApp(home: Text('Native host')));
        final context = tester.element(find.text('Native host'));
        await tester.runAsync(() async {
          final commands = <String>[];
          final messenger = tester.binding.defaultBinaryMessenger;
          messenger.setMockMethodCallHandler(
            const MethodChannel('native_video_player'),
            (call) async {
              commands.add(call.method);
              if (call.method == 'setupControllerEventChannel') {
                messenger.setMockMethodCallHandler(
                  MethodChannel(
                    'native_video_player_controller_${(call.arguments as Map)['controllerId']}',
                  ),
                  (_) async => null,
                );
              }
              return null;
            },
          );
          messenger.setMockMethodCallHandler(
            const MethodChannel('native_video_player_44003'),
            (_) async => null,
          );
          final client = MockClient((_) async => jsonResponse({}));
          final auth = AuthController(ApiClient(client: client), MemoryStore())
            ..restoring = false
            ..user = Map<String, dynamic>.from(session()['user'] as Map);
          final conditions = LocalConditions()
            ..setState(connected: true, wifi: true);
          final playback = PlaybackSession(conditions: conditions);
          final native = await playback.start(
            auth: auth,
            video: VideoDetail.fromJson({
              'videoId': 'test',
              'title': 'Test',
              'duration': 100,
            }),
            videoRenditions: [],
            mediaEntitlements: MediaEntitlements(
              backgroundPlayback: permitted,
              pictureInPicture: permitted,
            ),
            sourceUrl: 'https://file.test/test',
            resumeAt: 0,
          );
          expect(native.allowsPictureInPicture, permitted);
          expect(
            native.canStartPictureInPictureAutomatically,
            defaultTargetPlatform != TargetPlatform.android &&
                permitted &&
                mode != 'off',
          );
          await native.onPlatformViewCreated(44003, context);
          playback.ready = true;
          playback.isPlaying = true;
          expect(playback.automaticPipEligible, permitted && mode != 'off');
          playback.isPlaying = false;
          expect(
            playback.automaticPipEligible,
            false,
            reason: 'A paused video does not automatically enter PiP',
          );
          playback.ready = false;
          playback.didChangeAppLifecycleState(AppLifecycleState.paused);
          playback.didChangeAppLifecycleState(AppLifecycleState.resumed);
          expect(
            identical(playback.player, native),
            isTrue,
            reason: 'Lock/unlock keeps the same player',
          );
          expect(
            playback.backgroundPlaybackEnabled,
            permitted && mode == 'always',
          );
          conditions.setState(connected: true, wifi: true, externalAudio: true);
          expect(
            playback.backgroundPlaybackEnabled,
            permitted && mode != 'off',
          );
          if (permitted && mode == 'headphones') {
            playback.didChangeAppLifecycleState(AppLifecycleState.paused);
            conditions.setState(
              connected: true,
              wifi: true,
              externalAudio: false,
            );
            await Future<void>.delayed(const Duration(milliseconds: 20));
            expect(playback.backgroundPlaybackEnabled, isFalse);
            expect(commands, contains('pause'));
            expect(playback.waitingForExternalAudio, isTrue);
            expect(await playback.setBackgroundPlaybackEnabled(false), isTrue);
            expect(playback.backgroundPlaybackRequested, isFalse);
            expect(await playback.setBackgroundPlaybackEnabled(true), isFalse);
            expect(playback.waitingForExternalAudio, isTrue);
          }
          if (permitted && mode == 'off') {
            expect(await playback.setBackgroundPlaybackEnabled(true), isTrue);
            expect(playback.backgroundPlaybackEnabled, isTrue);
            expect(await playback.setBackgroundPlaybackEnabled(false), isTrue);
          }
          if (permitted && mode == 'always') {
            await const AppPreferencesStore().writeBackgroundPlaybackMode(
              'off',
            );
            await Future<void>.delayed(const Duration(milliseconds: 20));
            expect(playback.backgroundPlaybackEnabled, isFalse);
          }
          if (!permitted) {
            expect(await playback.setBackgroundPlaybackEnabled(true), isFalse);
          }
          playback.dispose();
          conditions.dispose();
          auth.dispose();
          client.close();
        });
        await tester.pumpWidget(const SizedBox.shrink());
        await tester.pump();
        expect(tester.takeException(), isNull);
      });
    }
  }

  group('plan media permissions', () {
    test('reads background and PiP permissions from the plan feature map', () {
      final entitlements = MediaEntitlements.fromPayload({
        'features': {'background_play': true, 'pip': true},
      });

      expect(entitlements.backgroundPlayback, isTrue);
      expect(entitlements.pictureInPicture, isTrue);
    });

    test(
      'defaults to disabled media capabilities for missing or invalid data',
      () {
        expect(
          MediaEntitlements.fromPayload(const {}).backgroundPlayback,
          isFalse,
        );
        expect(
          MediaEntitlements.fromPayload({
            'features': 'not-a-map',
          }).pictureInPicture,
          isFalse,
        );
      },
    );
  });
}
