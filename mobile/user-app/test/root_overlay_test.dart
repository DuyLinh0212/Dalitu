import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/testing.dart';
import 'package:user_app/app/mobile_app.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/core/localization/app_strings.dart';
import 'package:user_app/core/media/picture_in_picture.dart';
import 'package:user_app/core/network/network_status.dart';
import 'package:user_app/core/widgets/network_fallback_overlay.dart';
import 'package:user_app/features/creator/upload_controller.dart';

import 'auth_test.dart' show MemoryStore, jsonResponse;
import 'auth_widget_helpers.dart' show openSignInFromHome;

void main() {
  setUp(() {
    FlutterSecureStorage.setMockInitialValues({});
    NetworkStatus.instance.markAvailable();
  });
  tearDown(() => NetworkStatus.instance.markAvailable());

  testWidgets(
    'offline banner has an Overlay for its tooltip and can be closed',
    (tester) async {
      final client = MockClient((_) async => jsonResponse({'items': []}));
      final auth = AuthController(ApiClient(client: client), MemoryStore());
      await tester.pumpWidget(HuTubeApp(auth: auth));
      await tester.pumpAndSettle();
      NetworkStatus.instance.markUnavailable();
      NetworkStatus.instance.markUnavailable();
      await tester.pump();
      expect(tester.takeException(), isNull);
      final close = find.descendant(
        of: find.byType(NetworkFallbackOverlay),
        matching: find.byTooltip(AppStrings.t('common.close')),
      );
      expect(close, findsOneWidget);
      expect(Overlay.maybeOf(tester.element(close)), isNotNull);
      AppPictureInPicture.active.value = true;
      await tester.pump();
      expect(close, findsNothing);
      AppPictureInPicture.active.value = false;
      await tester.pump();
      expect(close, findsOneWidget);
      await tester.longPress(close);
      await tester.pump(const Duration(milliseconds: 200));
      expect(find.text(AppStrings.t('common.close')), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.tap(close);
      await tester.pumpAndSettle();
      expect(NetworkStatus.instance.showFallback, isFalse);
      expect(close, findsNothing);
      await openSignInFromHome(tester);
      expect(find.byKey(const ValueKey('Email')), findsOneWidget);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    },
  );

  testWidgets(
    'upload progress button opens details and can dismiss the upload',
    (tester) async {
      final client = MockClient((_) async => jsonResponse({'items': []}));
      final auth = AuthController(ApiClient(client: client), MemoryStore());
      await tester.pumpWidget(HuTubeApp(auth: auth));
      await tester.pumpAndSettle();
      final uploads = tester
          .widget<UploadScope>(find.byType(UploadScope))
          .notifier!;
      uploads.fileName = 'test-video.mp4';
      uploads.phase = UploadPhase.uploading;
      uploads.cancel();
      await tester.pump();
      AppPictureInPicture.active.value = true;
      await tester.pump();
      expect(find.text('test-video.mp4'), findsNothing);
      expect(uploads.visible, isTrue);
      AppPictureInPicture.active.value = false;
      await tester.pump();
      expect(tester.takeException(), isNull);
      final progress = find.byKey(const ValueKey('upload-progress-button'));
      expect(progress, findsOneWidget);
      await tester.tap(progress);
      await tester.pumpAndSettle();
      expect(find.text('test-video.mp4'), findsOneWidget);
      final close = find.byTooltip(AppStrings.t('parity.close'));
      expect(close, findsOneWidget);
      expect(Overlay.maybeOf(tester.element(close)), isNotNull);
      await tester.tap(close);
      await tester.pumpAndSettle();
      expect(uploads.visible, isTrue);
      await tester.tap(progress);
      await tester.pumpAndSettle();
      await tester.tap(
        find.widgetWithText(TextButton, AppStrings.t('parity.close')),
      );
      await tester.pumpAndSettle();
      expect(uploads.visible, isFalse);
      expect(find.text('test-video.mp4'), findsNothing);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    },
  );
}
