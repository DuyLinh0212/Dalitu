import 'dart:async';

import 'package:better_native_video_player/better_native_video_player.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:user_app/core/media/picture_in_picture.dart';
import 'package:user_app/features/content/mini_player.dart';
import 'package:user_app/features/content/playback_session.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  const codec = StandardMethodCodec();
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

  Future<Object?> incoming(String method, [Object? arguments]) async {
    final response = Completer<Object?>();
    await messenger.handlePlatformMessage(
      'hutube/picture_in_picture',
      codec.encodeMethodCall(MethodCall(method, arguments)),
      (reply) =>
          response.complete(reply == null ? null : codec.decodeEnvelope(reply)),
    );
    return response.future;
  }

  testWidgets(
    'Android PiP keeps one Flutter texture across resize and restoration',
    (tester) async {
      final config = NativeVideoPlayerConfig.global;
      NativeVideoPlayerConfig.global = const NativeVideoPlayerConfig(
        androidTextureMode: true,
      );
      debugDefaultTargetPlatformOverride = TargetPlatform.android;
      tester.view.physicalSize = const Size(390, 844);
      tester.view.devicePixelRatio = 1;
      addTearDown(() {
        NativeVideoPlayerConfig.global = config;
        debugDefaultTargetPlatformOverride = null;
        tester.view.resetPhysicalSize();
        tester.view.resetDevicePixelRatio();
        messenger.setMockMethodCallHandler(
          const MethodChannel('native_video_player'),
          null,
        );
        messenger.setMockMethodCallHandler(AppPictureInPicture.channel, null);
      });
      final pipCalls = <MethodCall>[];
      messenger.setMockMethodCallHandler(AppPictureInPicture.channel, (
        call,
      ) async {
        pipCalls.add(call);
        return null;
      });
      var surfaces = 0;
      messenger.setMockMethodCallHandler(
        const MethodChannel('native_video_player'),
        (call) async {
          if (call.method == 'createTextureView') {
            surfaces++;
            final viewId = (call.arguments as Map)['viewId'];
            final channel = MethodChannel('native_video_player_$viewId');
            messenger.setMockMethodCallHandler(channel, (_) async => null);
            addTearDown(
              () => messenger.setMockMethodCallHandler(channel, null),
            );
            return {'textureId': 72};
          }
          return null;
        },
      );
      final playback = PlaybackSession()
        ..player = NativeVideoPlayerController(
          id: 775,
          showNativeControls: false,
          canStartPictureInPictureAutomatically: false,
        )
        ..videoId = 'video'
        ..ready = true
        ..minimized = true;
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(body: MiniPlayer(session: playback)),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.byType(Texture), findsOneWidget);
      // Automatic PiP must already have the source before Home is pressed;
      // no manual prepare/enter call should be needed to prime it.
      expect(AppPictureInPicture.active.value, isFalse);
      expect(
        pipCalls.where((call) => call.method == 'setSourceRect'),
        isNotEmpty,
      );
      expect(pipCalls.any((call) => call.method == 'enter'), isFalse);
      final surface = tester.element(find.byType(Texture));
      AppPictureInPicture.active.value = true;
      tester.view.physicalSize = const Size(220, 124);
      await tester.pumpAndSettle();
      expect(tester.element(find.byType(Texture)), same(surface));
      AppPictureInPicture.active.value = false;
      tester.view.physicalSize = const Size(390, 844);
      await tester.pumpAndSettle();
      expect(tester.element(find.byType(Texture)), same(surface));
      expect(surfaces, 1);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
      playback.dispose();
      await tester.pump();
      debugDefaultTargetPlatformOverride = null;
    },
  );

  test('native automatic entry does not wait for Dart preparation', () async {
    final owner = Object();
    var prepares = 0;
    AppPictureInPicture.bind(owner, () async {
      prepares++;
      return false;
    });
    addTearDown(() => AppPictureInPicture.unbind(owner));
    await incoming('modeChanged', true);
    expect(AppPictureInPicture.active.value, isTrue);
    expect(prepares, 0);
    await incoming('modeChanged', false);
    expect(AppPictureInPicture.active.value, isFalse);
  });

  testWidgets('PiP reports only the video bounds in physical pixels', (
    tester,
  ) async {
    tester.view.devicePixelRatio = 2;
    addTearDown(tester.view.resetDevicePixelRatio);
    final calls = <MethodCall>[];
    messenger.setMockMethodCallHandler(AppPictureInPicture.channel, (
      call,
    ) async {
      calls.add(call);
      return null;
    });
    addTearDown(
      () =>
          messenger.setMockMethodCallHandler(AppPictureInPicture.channel, null),
    );
    final surface = GlobalKey();
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Stack(
          children: [
            Positioned(
              left: 12,
              top: 24,
              width: 160,
              height: 90,
              child: SizedBox(key: surface),
            ),
          ],
        ),
      ),
    );
    final prepared = AppPictureInPicture.prepareSurface(surface);
    await tester.pump();
    expect(await prepared, isTrue);
    expect(calls.single.method, 'setSourceRect');
    expect(calls.single.arguments, {
      'left': 24,
      'top': 48,
      'right': 344,
      'bottom': 228,
    });

    final missing = AppPictureInPicture.prepareSurface(GlobalKey());
    await tester.pump();
    expect(await missing, isFalse);
    expect(calls, hasLength(1));
  });

  test(
    'native PiP lifecycle hides overlays before preparation and restores on exit',
    () async {
      final owner = Object();
      AppPictureInPicture.bind(owner, () async {
        expect(AppPictureInPicture.active.value, isTrue);
        return true;
      });
      addTearDown(() => AppPictureInPicture.unbind(owner));
      expect(await incoming('prepare'), isTrue);
      expect(AppPictureInPicture.active.value, isTrue);
      await incoming('modeChanged', false);
      expect(AppPictureInPicture.active.value, isFalse);
      await incoming('modeChanged', true);
      expect(AppPictureInPicture.active.value, isTrue);
    },
  );

  test(
    'failed preparation and stale owner cleanup do not leave PiP active',
    () async {
      final oldOwner = Object();
      final currentOwner = Object();
      AppPictureInPicture.bind(oldOwner, () async => true);
      AppPictureInPicture.bind(currentOwner, () async => false);
      AppPictureInPicture.unbind(oldOwner);
      expect(await incoming('prepare'), isFalse);
      expect(AppPictureInPicture.active.value, isFalse);
      AppPictureInPicture.unbind(currentOwner);
    },
  );
  test(
    'PiP fallback survives a watch route and throwing preparation restores overlays',
    () async {
      final mini = Object();
      final watch = Object();
      var fallbackCalls = 0;
      AppPictureInPicture.bindFallback(mini, () async {
        fallbackCalls++;
        return true;
      });
      AppPictureInPicture.bind(
        watch,
        () async => throw StateError('Preparation failed'),
      );
      expect(await incoming('prepare'), false);
      expect(AppPictureInPicture.active.value, false);
      expect(fallbackCalls, 0);
      AppPictureInPicture.unbind(watch);
      expect(await incoming('prepare'), true);
      expect(fallbackCalls, 1);
      AppPictureInPicture.unbind(mini);
      expect(AppPictureInPicture.active.value, false);
    },
  );
}
