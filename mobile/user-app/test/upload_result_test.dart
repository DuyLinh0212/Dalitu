import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/testing.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/core/localization/app_strings.dart';
import 'package:user_app/core/theme/app_theme.dart';
import 'package:user_app/features/content/content_models.dart';
import 'package:user_app/features/creator/upload_controller.dart';

import 'auth_test.dart' show MemoryStore, jsonResponse;

void main() {
  setUpAll(() async {
    if (!const bool.fromEnvironment('CAPTURE_UPLOAD_TRAY')) return;
    for (final font in {
      'Plus Jakarta Sans': 'Roboto-Regular.ttf',
      'MaterialIcons': 'MaterialIcons-Regular.otf',
    }.entries) {
      final bytes = await File(
        '../../.work/flutter-ci/bin/cache/artifacts/material_fonts/${font.value}',
      ).readAsBytes();
      await (FontLoader(
        font.key,
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });
  testWidgets('saved upload stays compact and readable with large text', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(390, 844);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final auth = AuthController(
      ApiClient(client: MockClient((_) async => jsonResponse({}))),
      MemoryStore(),
    );
    final controller = UploadController(auth)
      ..phase = UploadPhase.completed
      ..fileName = '20250727_104315.mp4'
      ..video = VideoDetail.fromJson({
        'videoId': 'saved',
        'status': 'processing',
        'moderationStatus': 'pending',
      });
    final capture = GlobalKey();
    for (final scale in [1.0, 2.0]) {
      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.lightTheme,
          home: MediaQuery(
            data: MediaQueryData(
              size: const Size(390, 844),
              textScaler: TextScaler.linear(scale),
            ),
            child: Scaffold(
              body: Align(
                alignment: Alignment.bottomCenter,
                child: RepaintBoundary(
                  key: capture,
                  child: UploadTray(controller: controller),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(
        tester
            .getSize(find.byKey(const ValueKey('upload-progress-card')))
            .height,
        lessThanOrEqualTo(scale == 1 ? 80 : 130),
      );
      expect(
        find.text(AppStrings.t('uploadTray.savedPending')),
        findsOneWidget,
      );
      expect(find.byTooltip(AppStrings.t('parity.close')), findsOneWidget);
      expect(tester.takeException(), isNull);
      if (scale == 1 && const bool.fromEnvironment('CAPTURE_UPLOAD_TRAY')) {
        await tester.runAsync(() async {
          final image =
              await (capture.currentContext!.findRenderObject()
                      as RenderRepaintBoundary)
                  .toImage(pixelRatio: 2);
          final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
          final folder = Directory('../../outputs/upload-tray')
            ..createSync(recursive: true);
          await File(
            '${folder.path}/compact-upload.png',
          ).writeAsBytes(bytes!.buffer.asUint8List());
          image.dispose();
        });
      }
    }
    await tester.pumpWidget(const SizedBox.shrink());
    controller.dispose();
    auth.dispose();
  });
  for (final entry in [
    ('processing', 'not_submitted', 'uploadTray.savedProcessing'),
    ('processing', 'pending', 'uploadTray.savedPending'),
    ('failed', 'pending', 'uploadTray.savedFailed'),
    ('published', 'approved', 'uploadTray.published'),
  ]) {
    testWidgets(
      'saved video reports ${entry.$1}/${entry.$2} and releases upload',
      (tester) async {
        final client = MockClient((_) async => jsonResponse({}));
        final auth = AuthController(ApiClient(client: client), MemoryStore())
          ..user = {'userId': 'one'};
        final controller = UploadController(auth);
        await tester.pumpWidget(
          MaterialApp(
            home: Scaffold(body: UploadTray(controller: controller)),
          ),
        );
        expect(
          controller.start(
            name: 'video.mp4',
            task: (control, key, progress) async {
              progress(6, 6);
              return VideoDetail.fromJson({
                'videoId': 'saved',
                'status': entry.$1,
                'moderationStatus': entry.$2,
              });
            },
          ),
          isTrue,
        );
        await tester.pumpAndSettle();
        expect(controller.phase, UploadPhase.completed);
        expect(controller.busy, isFalse);
        expect(find.text(AppStrings.t(entry.$3)), findsOneWidget);
        expect(find.text(AppStrings.t('uploadTray.processing')), findsNothing);
        if (entry.$1 != 'published') {
          expect(find.text(AppStrings.t('uploadTray.published')), findsNothing);
        }
        await tester.pumpWidget(const SizedBox.shrink());
        controller.dispose();
        auth.dispose();
        client.close();
      },
    );
  }
}
