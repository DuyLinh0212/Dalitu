import 'dart:convert';
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
import 'package:user_app/core/localization/app_strings.dart';
import 'package:user_app/core/theme/app_theme.dart';
import 'package:user_app/features/content/download_coordinator.dart';
import 'package:user_app/features/content/download_preferences.dart';
import 'package:user_app/features/content/downloads_screen.dart';
import 'package:user_app/features/content/local_download_manager.dart';
import 'package:user_app/features/creator/upload_controller.dart';
import 'auth_test.dart' show MemoryStore, jsonResponse, session;
import 'download_security_test.dart' show waitFor;

class PreviewManager extends LocalDownloadManager {
  PreviewManager(this.records)
    : super.test(rootDirectory: () async => Directory.systemTemp);
  final List<LocalDownload> records;
  @override
  List<LocalDownload> get items => records;
  @override
  Future<void> ensureLoaded() async {}
  @override
  Future<void> updateMetadata(
    String id, {
    String? thumbnailUrl,
    String? channelName,
    int? duration,
  }) async {}
}

class ScreenCoordinator extends DownloadCoordinator {
  ScreenCoordinator(super.auth, {required super.manager}) {
    preferences = const DownloadPreferences(
      wifiOnly: false,
      recommendations: false,
    );
    conditions.setState(connected: true, wifi: true);
  }
  @override
  Future<void> initialize() async {}
  @override
  Future<void> loadSuggestions() async {}
}

void main() {
  const capture = bool.fromEnvironment('CAPTURE_MOBILE_UI');
  setUp(() {
    FlutterSecureStorage.setMockInitialValues({});
    ApiClient.invalidateReadCache();
  });
  setUpAll(() async {
    if (!capture) return;
    var regular = File(
      'D:/flutter/flutter/bin/cache/artifacts/material_fonts/Roboto-Regular.ttf',
    );
    if (!await regular.exists()) regular = File('C:/Windows/Fonts/arial.ttf');
    for (final entry in {
      'Plus Jakarta Sans': regular,
      'MaterialIcons': File(
        'D:/flutter/flutter/bin/cache/artifacts/material_fonts/MaterialIcons-Regular.otf',
      ),
    }.entries) {
      final bytes = await entry.value.readAsBytes();
      await (FontLoader(
        entry.key,
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });

  for (final imageFails in [false, true]) {
    test(
      'cover metadata survives transfer and restart; imageFails=$imageFails',
      () async {
        final root = await Directory.systemTemp.createTemp('dalitu_cover_');
        final bytes = await File(
          'test/fixtures/thumbnail-frame.jpg',
        ).readAsBytes();
        final manager = LocalDownloadManager.test(
          rootDirectory: () async => root,
          clientFactory: () => MockClient((request) async {
            if (request.url.path.endsWith('/cover')) {
              return http.Response.bytes(
                imageFails ? [] : bytes,
                imageFails ? 503 : 200,
              );
            }
            return http.Response('video', 200);
          }),
        );
        try {
          await manager.bindUser(
            'owner',
            authorize: (_) async => 'https://media.test/video',
          );
          await manager.enqueue(
            id: 'one',
            videoId: 'video',
            title: 'Offline video',
            quality: '720p',
            url: '',
            fileSize: 5,
            thumbnailUrl: 'https://media.test/cover',
            channelName: 'Music',
            duration: 254,
          );
          await waitFor(() => manager.items.single.completed);
          if (!imageFails) {
            await waitFor(() => manager.items.single.thumbnailPath != null);
          }
          await manager.flush();
          final saved = manager.items.single;
          expect(saved.channelName, 'Music');
          expect(saved.duration, 254);
          if (!imageFails) {
            expect(await File(saved.thumbnailPath!).readAsBytes(), bytes);
          }
          await manager.bindUser(null);
          await manager.bindUser('owner');
          expect(manager.items.single.completed, isTrue);
          expect(manager.items.single.thumbnailUrl, 'https://media.test/cover');
          expect(manager.items.single.channelName, 'Music');
          expect(manager.items.single.duration, 254);
          expect(manager.items.single.thumbnailPath, saved.thumbnailPath);
          await manager.remove('one');
          if (saved.thumbnailPath != null) {
            expect(await File(saved.thumbnailPath!).exists(), isFalse);
          }
        } finally {
          await manager.bindUser(null);
          manager.dispose();
          await root.delete(recursive: true);
        }
      },
    );
  }

  test('old manifests remain readable without thumbnail metadata', () {
    final old = LocalDownload.fromJson({
      'id': 'old',
      'videoId': 'v',
      'status': 'paused',
    });
    expect(old.thumbnailUrl, isNull);
    expect(old.channelName, '');
    expect(old.duration, 0);
  });

  for (final dark in [false, true]) {
    for (final size in [
      const Size(320, 568),
      const Size(390, 844),
      const Size(640, 360),
    ]) {
      for (final scale in [1.0, 2.0]) {
        testWidgets(
          'downloads and upload card dark=$dark size=$size scale=$scale',
          (tester) async {
            tester.view.physicalSize = size;
            tester.view.devicePixelRatio = 1;
            addTearDown(tester.view.resetPhysicalSize);
            addTearDown(tester.view.resetDevicePixelRatio);
            final cover = File(
              'test/fixtures/thumbnail-frame.jpg',
            ).absolute.path;
            final records = [
              LocalDownload(
                id: 'one',
                videoId: 'v1',
                title: 'Alan Walker Style x Aeon KnoX – Where We Belong',
                quality: '720p',
                url: '',
                totalBytes: 200,
                status: 'completed',
                progress: 1,
                thumbnailPath: cover,
                channelName: 'Aeon KnoX',
                duration: 254,
              ),
              LocalDownload(
                id: 'two',
                videoId: 'v2',
                title: 'Những giai điệu yêu thích mỗi ngày',
                quality: '1080p',
                url: '',
                totalBytes: 200,
                status: 'downloading',
                progress: .42,
                thumbnailPath: cover,
                channelName: 'Dalitu Music',
                duration: 273,
              ),
            ];
            final client = MockClient((request) async {
              if (request.url.path.endsWith('/auth/refresh')) {
                return jsonResponse(session());
              }
              if (request.url.path.endsWith('/downloads')) {
                return http.Response(
                  jsonEncode([
                    {
                      'videoDownloadId': 'one',
                      'videoId': 'v1',
                      'title': records.first.title,
                      'quality': '720p',
                      'status': 'ready',
                    },
                    {
                      'videoDownloadId': 'remote',
                      'videoId': 'v3',
                      'title': 'Chuyến đi cuối tuần',
                      'quality': '480p',
                      'status': 'ready',
                    },
                  ]),
                  200,
                  headers: {'content-type': 'application/json'},
                );
              }
              return jsonResponse({
                'videoId': 'v3',
                'channelName': 'Travel',
                'duration': 150,
              });
            });
            final auth = AuthController(
              ApiClient(client: client),
              MemoryStore('token'),
            );
            await auth.restore();
            final manager = PreviewManager(records);
            final coordinator = ScreenCoordinator(auth, manager: manager);
            final uploads = UploadController(auth)
              ..fileName = 'chuyen-di-cuoi-tuan.mp4'
              ..phase = UploadPhase.uploading
              ..sent = 25165824
              ..total = 41943040;
            final boundary = GlobalKey();
            Future<void> mount() => tester.pumpWidget(
              RepaintBoundary(
                key: boundary,
                child: MaterialApp(
                  debugShowCheckedModeBanner: false,
                  theme: dark ? AppTheme.darkTheme : AppTheme.lightTheme,
                  builder: (context, child) => MediaQuery(
                    data: MediaQuery.of(
                      context,
                    ).copyWith(textScaler: TextScaler.linear(scale)),
                    child: child!,
                  ),
                  home: Scaffold(
                    body: Column(
                      children: [
                        Expanded(
                          child: DownloadsScreen(
                            auth: auth,
                            coordinator: coordinator,
                          ),
                        ),
                        UploadTray(controller: uploads),
                      ],
                    ),
                  ),
                ),
              ),
            );
            await tester.runAsync(() async {
              await mount();
              await Future<void>.delayed(const Duration(milliseconds: 80));
            });
            await tester.pump(const Duration(milliseconds: 100));
            if (find.byKey(const ValueKey('download-one')).evaluate().isEmpty) {
              await tester.scrollUntilVisible(
                find.byKey(const ValueKey('download-one')),
                100,
                scrollable: find.byType(Scrollable).first,
              );
            }
            expect(find.text(AppStrings.t('downloads.requests')), findsNothing);
            expect(
              find.text(AppStrings.t('downloads.localVideos')),
              findsNothing,
            );
            expect(find.byKey(const ValueKey('download-one')), findsOneWidget);
            expect(find.text(records.first.title), findsOneWidget);
            expect(find.byIcon(Icons.cloud_download_outlined), findsNothing);
            expect(find.byIcon(Icons.download_outlined), findsNothing);
            final progress = find.byKey(
              const ValueKey('upload-progress-button'),
            );
            expect(progress, findsOneWidget);
            await tester.tap(progress);
            await tester.pumpAndSettle();
            expect(find.text('chuyen-di-cuoi-tuan.mp4'), findsOneWidget);
            expect(find.text('60% · 24.0 MB / 40.0 MB'), findsOneWidget);
            for (final imageWidget in tester.widgetList<Image>(
              find.byType(Image),
            )) {
              await tester.runAsync(
                () => precacheImage(
                  imageWidget.image,
                  tester.element(find.byWidget(imageWidget)),
                ),
              );
            }
            await tester.pump();
            expect(tester.takeException(), isNull);
            expect(
              tester
                  .widgetList<RawImage>(find.byType(RawImage))
                  .where((image) => image.image != null),
              isNotEmpty,
            );

            if (capture && size.width == 390 && scale == 1) {
              await tester.runAsync(() async {
                final image =
                    await (boundary.currentContext!.findRenderObject()
                            as RenderRepaintBoundary)
                        .toImage();
                final data = await image.toByteData(
                  format: ui.ImageByteFormat.png,
                );
                final folder = Directory('../../outputs/downloads-redesign')
                  ..createSync(recursive: true);
                await File(
                  '${folder.path}/downloads-${dark ? 'dark' : 'light'}.png',
                ).writeAsBytes(data!.buffer.asUint8List());
                image.dispose();
              });
            }
            for (final phase in [
              UploadPhase.processing,
              UploadPhase.failed,
              UploadPhase.completed,
              UploadPhase.cancelled,
            ]) {
              uploads.phase = phase;
              uploads.error = phase == UploadPhase.failed
                  ? 'Không thể kết nối. Kiểm tra mạng rồi thử lại.'
                  : null;
              await mount();
              await tester.pump();
              expect(tester.takeException(), isNull, reason: '$phase');
              if (phase == UploadPhase.failed) {
                expect(find.text(AppStrings.t('parity.retry')), findsOneWidget);
              }
            }
            await tester.tap(find.byTooltip(AppStrings.t('parity.close')));
            await tester.pumpAndSettle();
            expect(
              find.byKey(const ValueKey('upload-progress-dialog')),
              findsNothing,
            );
            await tester.tap(progress);
            await tester.pumpAndSettle();
            await tester.tap(
              find.widgetWithText(TextButton, AppStrings.t('parity.close')),
            );
            await tester.pumpAndSettle();
            expect(
              find.byKey(const ValueKey('upload-progress-button')),
              findsNothing,
            );
            await tester.pumpWidget(const SizedBox.shrink());
            uploads.dispose();
            coordinator.dispose();
            manager.dispose();
            auth.dispose();
            client.close();
          },
        );
      }
    }
  }
}
