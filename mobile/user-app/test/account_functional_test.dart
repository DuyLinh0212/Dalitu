import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:image_picker/image_picker.dart';
import 'package:user_app/account/models/account_models.dart';
import 'package:user_app/account/screens/active_sessions_screen.dart';
import 'package:user_app/account/screens/change_password_screen.dart';
import 'package:user_app/account/screens/edit_profile_screen.dart';
import 'package:user_app/account/screens/login_history_screen.dart';
import 'package:user_app/account/screens/notification_settings_screen.dart';
import 'package:user_app/account/services/account_service.dart';
import 'package:user_app/account/state/account_controller.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/core/localization/app_strings.dart';
import 'package:user_app/features/notifications/notification_center.dart';

import 'auth_test.dart' show MemoryStore, jsonResponse, session;

const _base = 'https://mobile.test/api/v1';

Map<String, dynamic> _profile({String name = 'Linh'}) => {
  'userId': 'user-1',
  'username': 'linh',
  'email': 'linh@example.test',
  'displayName': name,
  'avatarUrl': null,
  'bio': 'Bio',
  'country': 'Việt Nam',
  'emailVerified': true,
  'createdAt': '2026-01-01T00:00:00Z',
};

Future<AuthController> _auth(MockClient client) async {
  final auth = AuthController(
    ApiClient(baseUrl: _base, client: client),
    MemoryStore('refresh'),
  );
  await auth.restore();
  return auth;
}

Widget _app(Widget child) => MaterialApp(home: child);

void main() {
  group('Account service contracts', () {
    final cases =
        <
          ({
            String name,
            String method,
            String path,
            Map<String, dynamic>? body,
            Future<void> Function(AccountService) invoke,
          })
        >[
          (
            name: 'loads the current profile',
            method: 'GET',
            path: '/account/profile',
            body: null,
            invoke: (service) async => service.getProfile(),
          ),
          (
            name: 'trims profile fields before saving',
            method: 'PATCH',
            path: '/account/profile',
            body: {'displayName': 'New name', 'bio': 'New bio'},
            invoke: (service) async => service.updateProfile(
              displayName: ' New name ',
              bio: ' New bio ',
            ),
          ),
          (
            name: 'changes password without sending the confirmation field',
            method: 'POST',
            path: '/account/change-password',
            body: {
              'currentPassword': 'old-pass',
              'newPassword': 'new-pass-123',
            },
            invoke: (service) async => service.changePassword(
              currentPassword: 'old-pass',
              newPassword: 'new-pass-123',
              confirmPassword: 'new-pass-123',
            ),
          ),
          (
            name: 'loads notification preferences',
            method: 'GET',
            path: '/account/notifications',
            body: null,
            invoke: (service) async => service.getNotificationSettings(),
          ),
          (
            name: 'saves all notification preference fields',
            method: 'PUT',
            path: '/account/notifications',
            body: const NotificationSettingModel(
              inAppEnabled: false,
              emailEnabled: false,
              newVideoEnabled: false,
              commentReplyEnabled: false,
              reportResultEnabled: false,
              moderationEnabled: false,
              planEnabled: false,
              recommendationEnabled: false,
              mentionEnabled: false,
              channelActivityEnabled: false,
              paymentEnabled: false,
            ).toJson(),
            invoke: (service) async => service.updateNotificationSettings(
              const NotificationSettingModel(
                inAppEnabled: false,
                emailEnabled: false,
                newVideoEnabled: false,
                commentReplyEnabled: false,
                reportResultEnabled: false,
                moderationEnabled: false,
                planEnabled: false,
                recommendationEnabled: false,
                mentionEnabled: false,
                channelActivityEnabled: false,
                paymentEnabled: false,
              ),
            ),
          ),
          (
            name: 'loads account preferences',
            method: 'GET',
            path: '/account/settings',
            body: null,
            invoke: (service) async => service.getPreferences(),
          ),
          (
            name: 'saves account preferences',
            method: 'PUT',
            path: '/account/settings',
            body: const UserPreferencesModel(
              theme: 'dark',
              defaultPlaybackQuality: '720p',
              autoplayNext: false,
              keepSubscriptionsPrivate: true,
              keepPlaylistsPrivate: false,
              location: 'Hà Nội',
            ).toJson(),
            invoke: (service) async => service.updatePreferences(
              const UserPreferencesModel(
                theme: 'dark',
                defaultPlaybackQuality: '720p',
                autoplayNext: false,
                keepSubscriptionsPrivate: true,
                keepPlaylistsPrivate: false,
                location: 'Hà Nội',
              ),
            ),
          ),
          (
            name: 'loads only session maps and ignores malformed list entries',
            method: 'GET',
            path: '/auth/sessions',
            body: null,
            invoke: (service) async => service.getSessions(),
          ),
          (
            name: 'requests the selected login history page',
            method: 'GET',
            path: '/auth/login-history',
            body: null,
            invoke: (service) async =>
                service.getLoginHistory(page: 3, pageSize: 7),
          ),
          (
            name: 'revokes one session using DELETE',
            method: 'DELETE',
            path: '/auth/sessions/session-2',
            body: null,
            invoke: (service) async => service.revokeSession('session-2'),
          ),
          (
            name: 'revokes other sessions using POST',
            method: 'POST',
            path: '/auth/logout-others',
            body: null,
            invoke: (service) async => service.revokeOtherSessions(),
          ),
          (
            name: 'revokes all sessions using POST',
            method: 'POST',
            path: '/auth/logout-all',
            body: null,
            invoke: (service) async => service.revokeAllSessions(),
          ),
        ];

    for (final scenario in cases) {
      test('AccountService ${scenario.name}', () async {
        http.Request? captured;
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          captured = request;
          if (request.url.path.endsWith('/auth/sessions')) {
            return httpResponse({
              'items': [
                {'sessionId': 's1'},
                'bad',
              ],
            });
          }
          if (request.url.path.endsWith('/account/profile')) {
            return jsonResponse(_profile(name: 'New name'));
          }
          if (request.url.path.endsWith('/auth/login-history')) {
            return jsonResponse({
              'items': [],
              'page': 3,
              'pageSize': 7,
              'total': 21,
            });
          }
          return jsonResponse({});
        });
        final auth = await _auth(client);
        final service = AccountService(auth);
        await scenario.invoke(service);
        expect(captured, isNotNull);
        expect(captured!.method, scenario.method);
        expect(captured!.url.path, '/api/v1${scenario.path}');
        if (scenario.path == '/auth/login-history') {
          expect(captured!.url.queryParameters, {'page': '3', 'pageSize': '7'});
        }
        if (scenario.body != null) {
          expect(jsonDecode(captured!.body), scenario.body);
        }
        auth.dispose();
        client.close();
      });
    }

    test(
      'profile country update writes the profile before preferences',
      () async {
        final writes = <String>[];
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          writes.add('${request.method} ${request.url.path}');
          if (request.url.path.endsWith('/account/profile')) {
            return jsonResponse(_profile(name: 'Updated'));
          }
          if (request.url.path.endsWith('/account/settings')) {
            if (request.method == 'GET') {
              return jsonResponse({'theme': 'dark', 'location': 'Cũ'});
            }
            expect(jsonDecode(request.body)['location'], 'Nhật Bản');
          }
          return jsonResponse({});
        });
        final auth = await _auth(client);
        final updated = await AccountService(
          auth,
        ).updateProfile(displayName: 'Updated', country: ' Nhật Bản ');
        expect(updated.displayName, 'Updated');
        expect(auth.user?['displayName'], 'Updated');
        expect(writes, [
          'PATCH /api/v1/account/profile',
          'GET /api/v1/account/settings',
          'PUT /api/v1/account/settings',
        ]);
        auth.dispose();
        client.close();
      },
    );

    for (final image in [
      ('avatar.jpg', 'image/jpeg'),
      ('avatar.jpeg', 'image/jpeg'),
      ('avatar.png', 'image/png'),
      ('avatar.webp', 'image/webp'),
      ('avatar.gif', 'image/gif'),
    ]) {
      test('avatar upload accepts ${image.$1} with ${image.$2}', () async {
        http.Request? upload;
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          upload = request;
          return jsonResponse({
            ..._profile(),
            'avatarUrl': 'https://cdn.test/a',
          });
        });
        final auth = await _auth(client);
        final profile = await AccountService(auth).uploadAvatar(
          XFile.fromData(
            Uint8List.fromList([1, 2, 3]),
            name: image.$1,
            mimeType: image.$2,
          ),
        );
        expect(upload!.url.path, '/api/v1/account/avatar');
        expect(
          upload!.headers['content-type'],
          contains('multipart/form-data'),
        );
        expect(
          utf8.decode(upload!.bodyBytes, allowMalformed: true).toLowerCase(),
          contains('content-type: ${image.$2}'),
        );
        expect(profile.avatarUrl, 'https://cdn.test/a');
        expect(auth.user?['avatarUrl'], 'https://cdn.test/a');
        auth.dispose();
        client.close();
      });
    }

    test('unsupported avatar types fail before any upload request', () async {
      var uploadCount = 0;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        uploadCount++;
        return jsonResponse(_profile());
      });
      final auth = await _auth(client);
      await expectLater(
        AccountService(auth).uploadAvatar(
          XFile.fromData(
            Uint8List.fromList([1]),
            name: 'avatar.svg',
            mimeType: 'image/svg+xml',
          ),
        ),
        throwsA(
          isA<ApiFailure>().having(
            (error) => error.code,
            'code',
            'INVALID_FILE_TYPE',
          ),
        ),
      );
      expect(uploadCount, 0);
      auth.dispose();
      client.close();
    });

    test('oversized avatar fails before any upload request', () async {
      var uploadCount = 0;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        uploadCount++;
        return jsonResponse(_profile());
      });
      final auth = await _auth(client);
      await expectLater(
        AccountService(auth).uploadAvatar(
          XFile.fromData(
            Uint8List(5 * 1024 * 1024 + 1),
            name: 'large.png',
            mimeType: 'image/png',
          ),
        ),
        throwsA(
          isA<ApiFailure>().having(
            (error) => error.code,
            'code',
            'FILE_TOO_LARGE',
          ),
        ),
      );
      expect(uploadCount, 0);
      auth.dispose();
      client.close();
    });
  });

  group('Account screen workflows', () {
    testWidgets('edit profile blocks blank display name locally', (
      tester,
    ) async {
      tester.view.physicalSize = const Size(800, 1200);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      var accountWrites = 0;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        accountWrites++;
        return jsonResponse(_profile());
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        _app(
          EditProfileScreen(
            auth: auth,
            profile: UserProfile.fromJson(_profile()),
          ),
        ),
      );
      await tester.enterText(find.byType(TextFormField).first, '   ');
      await tester.tap(find.text(AppStrings.t('editProfile.saveBtn')));
      await tester.pumpAndSettle();
      expect(accountWrites, 0);
      expect(
        find.text(AppStrings.t('editProfile.displayNameRequired')),
        findsOneWidget,
      );
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    testWidgets('edit profile saves trimmed values and selected country', (
      tester,
    ) async {
      tester.view.physicalSize = const Size(800, 1200);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      http.Request? profileRequest;
      Map<String, dynamic>? settingsRequest;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/account/profile')) {
          profileRequest = request;
          return jsonResponse(_profile(name: 'Linh mới'));
        }
        if (request.url.path.endsWith('/account/settings')) {
          if (request.method == 'GET') {
            return jsonResponse({'location': 'Việt Nam'});
          }
          settingsRequest = jsonDecode(request.body) as Map<String, dynamic>;
        }
        return jsonResponse({});
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        _app(
          Navigator(
            onGenerateRoute: (_) => MaterialPageRoute<void>(
              builder: (_) => EditProfileScreen(
                auth: auth,
                profile: UserProfile.fromJson(_profile()),
              ),
            ),
          ),
        ),
      );
      await tester.enterText(find.byType(TextFormField).first, ' Linh mới ');
      await tester.enterText(find.byType(TextFormField).last, '  Bio mới  ');
      await tester.tap(find.byType(DropdownButtonFormField<String>));
      await tester.pumpAndSettle();
      await tester.tap(
        find.text(AppStrings.t('editProfile.countryJapan')).last,
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text(AppStrings.t('editProfile.saveBtn')));
      await tester.pumpAndSettle();
      expect(jsonDecode(profileRequest!.body), {
        'displayName': 'Linh mới',
        'bio': 'Bio mới',
      });
      expect(settingsRequest?['location'], 'Nhật Bản');
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    for (final validation in [
      (
        name: 'missing current password',
        values: ['', 'NewPass123', 'NewPass123'],
      ),
      (
        name: 'new password under eight characters',
        values: ['Current123', 'short', 'short'],
      ),
      (
        name: 'confirmation mismatch',
        values: ['Current123', 'NewPass123', 'OtherPass123'],
      ),
    ]) {
      testWidgets('password screen rejects ${validation.name} before the API', (
        tester,
      ) async {
        var writes = 0;
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          writes++;
          return jsonResponse({});
        });
        final auth = await _auth(client);
        await tester.pumpWidget(_app(ChangePasswordScreen(auth: auth)));
        final fields = find.byType(TextFormField);
        for (var index = 0; index < validation.values.length; index++) {
          await tester.enterText(fields.at(index), validation.values[index]);
        }
        await tester.tap(find.text(AppStrings.t('password.updateBtn')));
        await tester.pumpAndSettle();
        expect(writes, 0);
        expect(find.byType(TextFormField), findsNWidgets(3));
        await tester.pumpWidget(const SizedBox.shrink());
        auth.dispose();
        client.close();
      });
    }

    testWidgets(
      'password screen sends one change request and shows API failure',
      (tester) async {
        var payload = <String, dynamic>{};
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          payload = jsonDecode(request.body) as Map<String, dynamic>;
          return jsonResponse({'code': 'INVALID_CURRENT_PASSWORD'}, 400);
        });
        final auth = await _auth(client);
        await tester.pumpWidget(_app(ChangePasswordScreen(auth: auth)));
        final fields = find.byType(TextFormField);
        await tester.enterText(fields.at(0), 'Current123');
        await tester.enterText(fields.at(1), 'NewPassword123');
        await tester.enterText(fields.at(2), 'NewPassword123');
        await tester.tap(find.text(AppStrings.t('password.updateBtn')));
        await tester.pumpAndSettle();
        expect(payload, {
          'currentPassword': 'Current123',
          'newPassword': 'NewPassword123',
        });
        expect(find.byType(ChangePasswordScreen), findsOneWidget);
        expect(find.byType(ErrorWidget), findsNothing);
        await tester.pumpWidget(const SizedBox.shrink());
        auth.dispose();
        client.close();
      },
    );

    testWidgets('notification setting switch persists the changed value', (
      tester,
    ) async {
      final writes = <Map<String, dynamic>>[];
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/account/notifications')) {
          if (request.method == 'GET') {
            return jsonResponse({'newVideoEnabled': true});
          }
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          writes.add(body);
          return jsonResponse(body);
        }
        return jsonResponse({});
      });
      final auth = await _auth(client);
      final notifications = NotificationCenter(auth);
      await tester.pumpWidget(
        _app(
          NotificationSettingsScreen(auth: auth, notifications: notifications),
        ),
      );
      await tester.pumpAndSettle();
      final switches = find.byType(Switch);
      await tester.tap(switches.first);
      await tester.pumpAndSettle();
      expect(writes, hasLength(1));
      expect(writes.single['newVideoEnabled'], isFalse);
      await tester.pumpWidget(const SizedBox.shrink());
      notifications.dispose();
      auth.dispose();
      client.close();
    });

    testWidgets('login history paginates, masks IPv4 and retries errors', (
      tester,
    ) async {
      final requests = <Uri>[];
      var failFirst = true;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        requests.add(request.url);
        if (failFirst) {
          failFirst = false;
          return jsonResponse({'code': 'TEMPORARY'}, 503);
        }
        final page = int.parse(request.url.queryParameters['page']!);
        return jsonResponse({
          'items': page == 1
              ? [
                  {
                    'deviceName': 'iPhone',
                    'platform': 'mobile',
                    'ipAddress': '192.168.1.22',
                    'loginAt': '2026-01-01T00:00:00Z',
                  },
                ]
              : [
                  {
                    'deviceName': 'Laptop',
                    'platform': 'web',
                    'ipAddress': '10.2.3.4',
                    'loginAt': '2026-01-02T00:00:00Z',
                  },
                ],
          'page': page,
          'pageSize': 1,
          'total': 2,
        });
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        _app(LoginHistoryScreen(service: AccountService(auth))),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.byTooltip(AppStrings.t('common.retry')));
      await tester.pumpAndSettle();
      expect(find.text('iPhone'), findsOneWidget);
      expect(find.textContaining('192.168.•••.•••'), findsOneWidget);
      await tester.tap(find.text(AppStrings.t('account.nextPage')));
      await tester.pumpAndSettle();
      expect(find.text('Laptop'), findsOneWidget);
      expect(requests.map((uri) => uri.queryParameters['page']), [
        '1',
        '1',
        '2',
      ]);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    testWidgets(
      'active session screen removes a revoked device from the list',
      (tester) async {
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          if (request.url.path.endsWith('/auth/sessions')) {
            return jsonResponse({
              'items': [
                {
                  'sessionId': 's1',
                  'deviceName': 'Current phone',
                  'isCurrent': true,
                },
                {
                  'sessionId': 's2',
                  'deviceName': 'Old tablet',
                  'isCurrent': false,
                },
              ],
            });
          }
          if (request.url.path.endsWith('/auth/sessions/s2')) {
            return jsonResponse({});
          }
          return jsonResponse({});
        });
        final auth = await _auth(client);
        final controller = AccountController(AccountService(auth));
        await controller.loadSessions();
        await tester.pumpWidget(
          _app(
            ActiveSessionsScreen(
              controller: controller,
              onLogoutAll: () async {},
              onLogout: () async {},
            ),
          ),
        );
        expect(find.text('Old tablet'), findsOneWidget);
        await controller.revokeSession('s2');
        await tester.pumpAndSettle();
        expect(find.text('Old tablet'), findsNothing);
        expect(find.text('Current phone'), findsOneWidget);
        await tester.pumpWidget(const SizedBox.shrink());
        controller.dispose();
        auth.dispose();
        client.close();
      },
    );
  });
}

http.Response httpResponse(Map<String, dynamic> value) => http.Response(
  jsonEncode(value),
  200,
  headers: {'content-type': 'application/json'},
);
