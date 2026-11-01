import 'dart:async';
import 'dart:io';
import 'package:flutter/material.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:better_native_video_player/better_native_video_player.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/account/services/account_service.dart';
import 'package:user_app/account/state/account_controller.dart';
import 'package:user_app/channel/screens/create_channel_screen.dart';
import 'package:user_app/core/theme/app_theme.dart';
import 'package:user_app/core/localization/app_strings.dart';
import 'package:user_app/features/content/content_models.dart';
import 'package:user_app/features/content/home_screen.dart';
import 'package:user_app/features/content/local_download_manager.dart';
import 'package:user_app/features/content/offline_player_screen.dart';
import 'package:user_app/features/content/media_entitlements.dart';
import 'package:user_app/features/content/playback_session.dart';
import 'package:user_app/features/content/mini_player.dart';
import 'package:user_app/features/content/video_card.dart';
import 'package:user_app/features/notifications/notification_center.dart';
import 'package:user_app/features/notifications/notifications_screen.dart';
import 'package:user_app/features/search/search_screen.dart';
import 'package:user_app/features/subscriptions/subscriptions_screen.dart';
import 'auth_test.dart' show MemoryStore, jsonResponse, session;
import 'download_security_test.dart' show waitFor;
import 'package:go_router/go_router.dart';
import 'package:user_app/features/content/content_service.dart';
import 'package:user_app/features/content/watch_screen.dart';

Map<String, dynamic> video(String id) => {
  'videoId': id,
  'title': 'Video $id',
  'channelName': 'Channel',
  'channelHandle': 'channel',
  'duration': 100,
};
Map<String, dynamic> page(List<Map<String, dynamic>> items) => {
  'items': items,
  'page': 1,
  'pageSize': 20,
  'total': items.length,
};
Widget app(Widget child) => MaterialApp(
  theme: AppTheme.lightTheme,
  home: Scaffold(body: child),
);

class CountingAuth extends AuthController {
  CountingAuth(super.api, super.store);
  int listeners = 0;
  @override
  void addListener(VoidCallback listener) {
    listeners++;
    super.addListener(listener);
  }

  @override
  void removeListener(VoidCallback listener) {
    listeners--;
    super.removeListener(listener);
  }
}

void main() {
  setUp(() => FlutterSecureStorage.setMockInitialValues({}));

  for (final mini in [false, true]) {
    testWidgets(
      'progress updates do not rebuild the native video widget: mini=$mini',
      (tester) async {
        debugDefaultTargetPlatformOverride = TargetPlatform.windows;
        addTearDown(() => debugDefaultTargetPlatformOverride = null);
        tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
          const MethodChannel('native_video_player'),
          (_) async => null,
        );
        final client = MockClient(
          (request) async => request.url.path.endsWith('/videos/perf')
              ? jsonResponse(video('perf'))
              : jsonResponse(page([])),
        );
        final auth = AuthController(ApiClient(client: client), MemoryStore());
        final playback = PlaybackSession()
          ..videoId = 'perf'
          ..player = NativeVideoPlayerController(id: 9001)
          ..ready = true
          ..minimized = mini
          ..duration = const Duration(seconds: 120);
        await tester.pumpWidget(
          app(
            mini
                ? MiniPlayer(session: playback)
                : WatchScreen(auth: auth, playback: playback, videoId: 'perf'),
          ),
        );
        await tester.pumpAndSettle();
        final before = tester.element(find.byType(NativeVideoPlayer));
        final previousCallback = debugOnRebuildDirtyWidget;
        var builds = 0;
        debugOnRebuildDirtyWidget = (element, builtOnce) {
          if (element.widget is NativeVideoPlayer) builds++;
          previousCallback?.call(element, builtOnce);
        };
        try {
          for (var tick = 1; tick <= 40; tick++) {
            playback.position = Duration(milliseconds: tick * 250);
            playback.notifyListeners();
            await tester.pump(const Duration(milliseconds: 250));
          }
        } finally {
          debugOnRebuildDirtyWidget = previousCallback;
        }
        expect(
          builds,
          0,
          reason: 'Progress belongs to controls, not the native surface',
        );
        expect(tester.element(find.byType(NativeVideoPlayer)), same(before));
        if (mini) {
          expect(
            tester
                .widget<LinearProgressIndicator>(
                  find.byType(LinearProgressIndicator),
                )
                .value,
            closeTo(10 / playback.duration.inSeconds, .0001),
          );
        } else {
          expect(find.text('0:10'), findsOneWidget);
        }
        await tester.pumpWidget(const SizedBox.shrink());
        playback.dispose();
        auth.dispose();
        client.close();
        await tester.pump();
        debugDefaultTargetPlatformOverride = null;
      },
    );
  }

  testWidgets('offline player unregisters its account listener when closed', (
    tester,
  ) async {
    final client = MockClient((_) async => jsonResponse({}));
    final auth = CountingAuth(ApiClient(client: client), MemoryStore());
    const download = LocalDownload(
      id: 'offline',
      videoId: 'video',
      title: 'Offline',
      quality: '720p',
      url: '',
      totalBytes: 0,
      status: 'completed',
      progress: 1,
    );
    await tester.pumpWidget(
      app(OfflinePlayerScreen(auth: auth, download: download)),
    );
    await tester.pumpAndSettle();
    expect(auth.listeners, 1);
    await tester.pumpWidget(const SizedBox.shrink());
    expect(auth.listeners, 0);
    auth.dispose();
    client.close();
  });

  test(
    'account request finishing after controller disposal does not notify a disposed object',
    () async {
      final response = Completer<http.Response>();
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        return response.future;
      });
      final auth = AuthController(
        ApiClient(client: client),
        MemoryStore('refresh'),
      );
      await auth.restore();
      final account = AccountController(AccountService(auth));
      final loading = account.loadProfile();
      account.dispose();
      response.complete(jsonResponse({'userId': 'user-1'}));
      await loading;
      auth.dispose();
      client.close();
    },
  );

  testWidgets(
    'home shows public content while recommendations are pending and builds lazily',
    (tester) async {
      final ranking = Completer<http.Response>();
      final requests = <String>[];
      final client = MockClient((request) async {
        requests.add(request.url.path);
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/feed/recommended')) {
          return ranking.future;
        }
        if (request.url.queryParameters['sort'] == 'popular') {
          return jsonResponse(
            page(List.generate(20, (i) => video('public-$i'))),
          );
        }
        return jsonResponse(page([]));
      });
      final auth = AuthController(
        ApiClient(client: client),
        MemoryStore('refresh'),
      );
      await auth.restore();
      await tester.pumpWidget(app(HomeScreen(auth: auth)));
      await tester.pumpAndSettle();
      expect(requests.any((p) => p.endsWith('/feed/explore')), isTrue);
      expect(find.text('Video public-0'), findsOneWidget);
      expect(find.byType(VideoCardTile).evaluate().length, lessThan(20));
      ranking.complete(
        jsonResponse(page([video('public-0'), video('ranked')])),
      );
      await tester.pumpAndSettle();
      expect(find.text('Video ranked'), findsOneWidget);
      expect(find.text('Video public-0'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    },
  );

  testWidgets(
    'home refresh discards a late response from the previous request',
    (tester) async {
      final oldHome = Completer<http.Response>();
      var homeCalls = 0;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/feed/home')) {
          homeCalls++;
          return homeCalls == 1
              ? oldHome.future
              : jsonResponse(page([video('fresh')]));
        }
        return jsonResponse(page([video('public')]));
      });
      final auth = AuthController(ApiClient(client: client), MemoryStore());
      await tester.pumpWidget(app(HomeScreen(auth: auth)));
      await tester.pumpAndSettle();
      final refresh = tester
          .state<RefreshIndicatorState>(find.byType(RefreshIndicator))
          .show();
      await tester.pumpAndSettle();
      await refresh;
      expect(find.text('Video fresh'), findsOneWidget);
      oldHome.complete(jsonResponse(page([video('stale')])));
      await tester.pumpAndSettle();
      expect(find.text('Video fresh'), findsOneWidget);
      expect(find.text('Video stale'), findsNothing);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    },
  );

  testWidgets(
    'search shows video results before optional channel lookup completes',
    (tester) async {
      final channel = Completer<http.Response>();
      final client = MockClient((request) async {
        if (request.url.path.contains('/channels/')) return channel.future;
        return jsonResponse(page([video('search-result')]));
      });
      final auth = AuthController(ApiClient(client: client), MemoryStore());
      await tester.pumpWidget(app(SearchScreen(auth: auth)));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField).first, 'music');
      await tester.testTextInput.receiveAction(TextInputAction.search);
      await tester.pumpAndSettle();
      expect(find.text('Video search-result'), findsOneWidget);
      channel.complete(jsonResponse({'code': 'NOT_FOUND'}, 404));
      await tester.pumpAndSettle();
      expect(find.text('Video search-result'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    },
  );

  testWidgets(
    'handle availability ignores responses for previously typed handles',
    (tester) async {
      final oldHandle = Completer<http.Response>();
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/channels/me')) {
          return jsonResponse({'code': 'CHANNEL_NOT_FOUND'}, 404);
        }
        return request.url.queryParameters['handle'] == 'oldhandle'
            ? oldHandle.future
            : jsonResponse({'isAvailable': true});
      });
      final auth = AuthController(ApiClient(client: client), MemoryStore());
      await tester.pumpWidget(app(CreateChannelScreen(auth: auth)));
      await tester.pumpAndSettle();
      final handle = find.byType(TextFormField).at(1);
      await tester.enterText(handle, 'oldhandle');
      await tester.pump(const Duration(milliseconds: 450));
      await tester.enterText(handle, 'newhandle');
      await tester.pump(const Duration(milliseconds: 450));
      await tester.pump();
      expect(find.byIcon(Icons.check_circle), findsOneWidget);
      oldHandle.complete(jsonResponse({'isAvailable': false}));
      await tester.pumpAndSettle();
      expect(find.byIcon(Icons.check_circle), findsOneWidget);
      expect(find.byIcon(Icons.cancel), findsNothing);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    },
  );

  testWidgets('notification load-more does not issue duplicate requests', (
    tester,
  ) async {
    final next = Completer<http.Response>();
    var moreCalls = 0;
    final client = MockClient((request) async {
      if (request.url.path.endsWith('/auth/refresh')) {
        return jsonResponse(session());
      }
      if (request.url.queryParameters['page'] == '2') {
        moreCalls++;
        return next.future;
      }
      return jsonResponse({
        'items': List.generate(
          20,
          (i) => {
            'notificationId': 'n$i',
            'title': 'Notice $i',
            'isRead': true,
          },
        ),
      });
    });
    final auth = AuthController(
      ApiClient(client: client),
      MemoryStore('refresh'),
    );
    await auth.restore();
    final center = NotificationCenter(auth);
    await tester.pumpWidget(
      app(NotificationsScreen(auth: auth, notifications: center)),
    );
    await tester.pumpAndSettle();
    final loadMore = find.widgetWithText(
      TextButton,
      AppStrings.t('notifications.loadMore'),
    );
    await tester.scrollUntilVisible(
      loadMore,
      400,
      scrollable: find.byType(Scrollable).first,
    );
    await tester.tap(loadMore);
    await tester.pump();
    await tester.tap(loadMore);
    await tester.pump();
    expect(moreCalls, 1);
    next.complete(
      jsonResponse({
        'items': [
          {'notificationId': 'n19', 'title': 'Notice 19', 'isRead': true},
          {'notificationId': 'n20', 'title': 'Notice 20', 'isRead': true},
        ],
      }),
    );
    await tester.pumpAndSettle();
    expect(find.text('Notice 19'), findsOneWidget);
    await tester.pumpWidget(const SizedBox.shrink());
    center.dispose();
    auth.dispose();
    client.close();
  });

  testWidgets(
    'rapid playback starts preserve only the latest video and dismiss cancels pending start',
    (tester) async {
      await tester.runAsync(() async {
        final messenger = tester.binding.defaultBinaryMessenger;
        messenger.setMockMethodCallHandler(
          const MethodChannel('native_video_player'),
          (call) async {
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
        final client = MockClient((_) async => jsonResponse({}));
        final auth = AuthController(ApiClient(client: client), MemoryStore());
        final playback = PlaybackSession();
        Future<dynamic> start(String id) => playback.start(
          auth: auth,
          video: VideoDetail.fromJson(video(id)),
          videoRenditions: [],
          mediaEntitlements: const MediaEntitlements.none(),
          sourceUrl: 'https://video.test/$id',
          resumeAt: 0,
        );
        final first = expectLater(start('first'), throwsStateError);
        await start('latest');
        await first;
        expect(playback.videoId, 'latest');
        final oldId = playback.player!.id;
        final other = start('other');
        await other;
        final again = start('latest');
        await again;
        expect(playback.player!.id, isNot(oldId));
        final cancelled = expectLater(start('cancelled'), throwsStateError);
        final dismissing = playback.dismiss();
        await dismissing;
        await cancelled;
        expect(playback.hasVideo, isFalse);
        playback.dispose();
        auth.dispose();
        client.close();
      });
      await tester.pump();
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('failed final progress save never escapes as an unhandled error', (
    tester,
  ) async {
    await tester.runAsync(() async {
      final messenger = tester.binding.defaultBinaryMessenger;
      messenger.setMockMethodCallHandler(
        const MethodChannel('native_video_player'),
        (call) async {
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
      var saves = 0;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/watch-progress')) saves++;
        return jsonResponse({'code': 'UNAVAILABLE'}, 503);
      });
      final auth = AuthController(
        ApiClient(client: client),
        MemoryStore('refresh'),
      );
      await auth.restore();
      final playback = PlaybackSession();
      await playback.start(
        auth: auth,
        video: VideoDetail.fromJson(video('playing')),
        videoRenditions: [],
        mediaEntitlements: const MediaEntitlements.none(),
        sourceUrl: 'https://video.test/a',
        resumeAt: 0,
      );
      playback.position = const Duration(seconds: 9);
      final dismissing = playback.dismiss();
      await dismissing;
      await waitFor(() => saves == 1);
      await Future<void>.delayed(const Duration(milliseconds: 10));
      expect(saves, 1);
      playback.dispose();
      auth.dispose();
      client.close();
    });
    await tester.pump();
    expect(tester.takeException(), isNull);
  });

  testWidgets('switching subscription channels ignores an older response', (
    tester,
  ) async {
    final oldChannel = Completer<http.Response>();
    final client = MockClient((request) async {
      if (request.url.path.endsWith('/auth/refresh')) {
        return jsonResponse(session());
      }
      if (request.url.path.endsWith('/subscriptions')) {
        return http.Response(
          '[{"channelId":"a","name":"Channel A","handle":"a"},{"channelId":"b","name":"Channel B","handle":"b"}]',
          200,
          headers: {'content-type': 'application/json'},
        );
      }
      if (request.url.queryParameters['channelId'] == 'a') {
        return oldChannel.future;
      }
      return jsonResponse(
        page([
          video(
            request.url.queryParameters['channelId'] == 'b'
                ? 'new-channel'
                : 'initial',
          ),
        ]),
      );
    });
    final auth = AuthController(
      ApiClient(client: client),
      MemoryStore('refresh'),
    );
    await auth.restore();
    await tester.pumpWidget(app(SubscriptionsScreen(auth: auth)));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Channel A').first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Channel B').first);
    await tester.pumpAndSettle();
    expect(find.text('Video new-channel'), findsOneWidget);
    oldChannel.complete(jsonResponse(page([video('old-channel')])));
    await tester.pumpAndSettle();
    expect(find.text('Video old-channel'), findsNothing);
    expect(find.text('Video new-channel'), findsOneWidget);
    await tester.pumpWidget(const SizedBox.shrink());
    auth.dispose();
    client.close();
  });

  testWidgets(
    'native time events throttle Flutter rebuilds and latch completion once',
    (tester) async {
      await tester.pumpWidget(app(const Text('Player host')));
      final context = tester.element(find.text('Player host'));
      await tester.runAsync(() async {
        final messenger = tester.binding.defaultBinaryMessenger;
        messenger.setMockMethodCallHandler(
          const MethodChannel('native_video_player'),
          (call) async {
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
          const MethodChannel('native_video_player_44001'),
          (_) async => null,
        );
        final client = MockClient((_) async => jsonResponse({}));
        final auth = AuthController(ApiClient(client: client), MemoryStore());
        final playback = PlaybackSession();
        final controller = await playback.start(
          auth: auth,
          video: VideoDetail.fromJson(video('time-events')),
          videoRenditions: [],
          mediaEntitlements: const MediaEntitlements.none(),
          sourceUrl: 'https://video.test/time-events',
          resumeAt: 0,
        );
        await controller.onPlatformViewCreated(44001, context);
        await Future<void>.delayed(const Duration(milliseconds: 30));
        Future<void> tick(int position) async {
          tester.binding.channelBuffers.push(
            'native_video_player_44001',
            const StandardMethodCodec().encodeSuccessEnvelope({
              'event': 'timeUpdated',
              'position': position,
              'duration': 100000,
            }),
            (_) {},
          );
          await Future<void>.delayed(Duration.zero);
        }

        await tick(0);
        var rebuilds = 0;
        var completions = 0;
        playback.addListener(() => rebuilds++);
        playback.onVideoCompleted = () => completions++;
        for (var ms = 25; ms <= 1000; ms += 25) {
          await tick(ms);
        }
        expect(playback.position, const Duration(seconds: 1));
        expect(rebuilds, inInclusiveRange(1, 5));
        await tick(99650);
        await tick(99700);
        await tick(99900);
        expect(completions, 1);
        await tick(0);
        await tick(99900);
        expect(
          completions,
          1,
          reason: 'Native reset must not rearm completion',
        );
        playback.ready = true;
        await playback.seekTo(Duration.zero);
        await tick(0);
        await tick(99900);
        expect(completions, 2);
        await playback.seekTo(Duration.zero);
        await tick(0);
        // Native completion can arrive without a tick in the final 400ms.
        tester.binding.channelBuffers.push(
          'native_video_player_44001',
          const StandardMethodCodec().encodeSuccessEnvelope({
            'event': 'completed',
          }),
          (_) {},
        );
        await Future<void>.delayed(Duration.zero);
        expect(completions, 3);
        await playback.dismiss();
        playback.dispose();
        auth.dispose();
        client.close();
      });
      await tester.pumpWidget(const SizedBox.shrink());
      await tester.pump();
      expect(tester.takeException(), isNull);
    },
  );

  test('removing an earlier queue item preserves current video index', () {
    final playback = PlaybackSession();
    playback.setQueue(
      List.generate(3, (i) => VideoCard.fromJson(video('$i'))),
      initialIndex: 1,
    );
    playback.removeFromQueue(0);
    expect(playback.queue[playback.queueIndex].id, '1');
    expect(playback.nextVideoCard!.id, '2');
    playback.dispose();
  });

  test(
    'chunked download does not flush a manifest or rebuild UI for each chunk',
    () async {
      final root = await Directory.systemTemp.createTemp('hutube_throttle_');
      var directoryCalls = 0;
      var notifications = 0;
      final manager = LocalDownloadManager.test(
        rootDirectory: () async {
          directoryCalls++;
          return root;
        },
        clientFactory: () => MockClient.streaming(
          (request, body) async => http.StreamedResponse(
            Stream.fromIterable(List.generate(1000, (_) => [42])),
            200,
            contentLength: 1000,
          ),
        ),
      );
      manager.addListener(() => notifications++);
      try {
        await manager.bindUser(
          'owner',
          authorize: (_) async => 'https://video.test/download',
        );
        await manager.enqueue(
          id: 'file',
          videoId: 'video',
          title: 'Video',
          quality: '720p',
          url: '',
          fileSize: 1000,
        );
        await waitFor(() => manager.items.single.completed);
        await manager.flush();
        expect(await File(manager.items.single.filePath!).length(), 1000);
        expect(directoryCalls, lessThan(12));
        expect(notifications, lessThan(20));
      } finally {
        await manager.bindUser(null);
        await manager.flush();
        manager.dispose();
        await root.delete(recursive: true);
      }
    },
  );

  group('watch loading budget', () {
    test('slow inference uses catalogue within the watch budget', () async {
      final ranking = Completer<http.Response>();
      var resolved = 0;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/feed/recommended')) {
          return ranking.future;
        }
        expect(request.url.path, endsWith('/feed/resolve'));
        resolved++;
        return jsonResponse({
          'items': [
            {'videoId': 'fallback'},
          ],
          'page': 1,
          'pageSize': 20,
          'total': 1,
        });
      });
      final auth = AuthController(
        ApiClient(client: client),
        MemoryStore('refresh'),
      );
      await auth.restore();
      final watch = ContentService(
        auth,
        recommendationApi: ApiClient(client: client),
      );
      final result = await watch
          .feed(
            explore: false,
            recommendationBudget: const Duration(milliseconds: 20),
          )
          .timeout(const Duration(seconds: 1));
      expect(result.items.single.id, 'fallback');
      expect(resolved, 1);
      ranking.complete(jsonResponse({'items': []}));
      auth.dispose();
      client.close();
    });

    testWidgets(
      'watch starts suggestions before playback finishes and caches filters on the left',
      (tester) async {
        FlutterSecureStorage.setMockInitialValues({});
        tester.view.physicalSize = const Size(375, 812);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        final pendingPlayback = Completer<http.Response>();
        var feedCalls = 0, channelCalls = 0;
        final client = MockClient((request) async {
          final path = request.url.path;
          if (path.endsWith('/videos/video')) {
            return jsonResponse({
              'videoId': 'video',
              'channelId': 'channel',
              'title': 'Video',
            });
          }
          if (path.endsWith('/playback')) return pendingPlayback.future;
          if (path.endsWith('/feed/home')) feedCalls++;
          if (path.endsWith('/videos/search')) channelCalls++;
          return jsonResponse({
            'items': [
              {'videoId': 'other', 'title': 'Other'},
            ],
            'page': 1,
            'total': 1,
          });
        });
        final auth = AuthController(ApiClient(client: client), MemoryStore())
          ..restoring = false;
        final playback = PlaybackSession()
          ..videoId = 'video'
          ..ready = true;
        final router = GoRouter(
          routes: [
            GoRoute(
              path: '/',
              builder: (_, _) =>
                  WatchScreen(auth: auth, playback: playback, videoId: 'video'),
            ),
          ],
        );
        await tester.pumpWidget(
          MaterialApp.router(theme: AppTheme.lightTheme, routerConfig: router),
        );
        await tester.pump(const Duration(milliseconds: 100));
        expect(
          feedCalls,
          1,
          reason: 'Recommendations must not wait for playback metadata',
        );
        pendingPlayback.complete(jsonResponse({}));
        await tester.pumpAndSettle();
        final all = find.widgetWithText(ChoiceChip, AppStrings.t('parity.all'));
        final channel = find.widgetWithText(
          ChoiceChip,
          AppStrings.t('parity.channel'),
        );
        await tester.scrollUntilVisible(
          all,
          200,
          scrollable: find.byType(Scrollable).first,
        );
        expect(tester.getTopLeft(all).dx, 20);
        await tester.tap(channel);
        await tester.pumpAndSettle();
        await tester.tap(all);
        await tester.pumpAndSettle();
        await tester.tap(channel);
        await tester.pumpAndSettle();
        expect(feedCalls, 1);
        expect(channelCalls, 1);
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox.shrink());
        router.dispose();
        playback.dispose();
        auth.dispose();
        client.close();
      },
    );
  });
}
