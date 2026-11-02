import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/features/notifications/notification_center.dart';
import 'package:user_app/features/notifications/notifications_screen.dart';

import 'auth_test.dart' show MemoryStore, jsonResponse, session;

const _base = 'https://notification-ui.test/api/v1';

http.Response _json(Object value, [int status = 200]) => http.Response(
  jsonEncode(value),
  status,
  headers: {'content-type': 'application/json'},
);

Map<String, dynamic> _notification(
  String id, {
  String title = 'Video ready',
  bool read = false,
}) => {
  'notificationId': id,
  'title': title,
  'content': 'Your upload is ready to watch.',
  'createdAt': '2026-06-01T12:00:00Z',
  'isRead': read,
};

Future<AuthController> _auth(MockClient client) async {
  final auth = AuthController(
    ApiClient(baseUrl: _base, client: client),
    MemoryStore('refresh'),
  );
  await auth.restore();
  return auth;
}

Future<void> _pumpNotifications(
  WidgetTester tester, {
  required AuthController auth,
  required NotificationCenter center,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: NotificationsScreen(auth: auth, notifications: center),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  setUp(ApiClient.invalidateReadCache);

  testWidgets('renders unread items and updates the shared unread count', (
    tester,
  ) async {
    final client = MockClient((request) async {
      if (request.url.path.endsWith('/auth/refresh')) {
        return jsonResponse(session());
      }
      expect(request.url.path, '/api/v1/notifications');
      expect(request.url.queryParameters, {'page': '1', 'pageSize': '20'});
      return _json({
        'items': [
          _notification('n-1'),
          _notification('n-2', title: 'Comment received', read: true),
        ],
        'unreadCount': 1,
      });
    });
    final auth = await _auth(client);
    final center = NotificationCenter(auth);
    await _pumpNotifications(tester, auth: auth, center: center);
    expect(find.text('Video ready'), findsOneWidget);
    expect(find.text('Comment received'), findsOneWidget);
    expect(find.text('1 chưa đọc'), findsOneWidget);
    expect(center.unreadCount, 1);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
    center.dispose();
    auth.dispose();
    client.close();
  });

  testWidgets('empty response renders the empty notification state', (
    tester,
  ) async {
    final client = MockClient((request) async {
      if (request.url.path.endsWith('/auth/refresh')) {
        return jsonResponse(session());
      }
      return _json({'items': [], 'unreadCount': 0});
    });
    final auth = await _auth(client);
    final center = NotificationCenter(auth);
    await _pumpNotifications(tester, auth: auth, center: center);
    expect(find.text('Chưa có thông báo.'), findsOneWidget);
    expect(find.text('Đọc tất cả'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
    center.dispose();
    auth.dispose();
    client.close();
  });

  testWidgets('mark all read updates the server, list and unread badge', (
    tester,
  ) async {
    var readAllCalls = 0;
    final client = MockClient((request) async {
      if (request.url.path.endsWith('/auth/refresh')) {
        return jsonResponse(session());
      }
      if (request.url.path.endsWith('/notifications/read-all')) {
        expect(request.method, 'POST');
        readAllCalls++;
        return _json({});
      }
      if (request.url.path.endsWith('/notifications')) {
        return _json({
          'items': [_notification('n-1'), _notification('n-2')],
          'unreadCount': 2,
        });
      }
      return _json({});
    });
    final auth = await _auth(client);
    final center = NotificationCenter(auth);
    await _pumpNotifications(tester, auth: auth, center: center);
    expect(find.text('2 chưa đọc'), findsOneWidget);
    await tester.tap(find.text('Đọc tất cả'));
    await tester.pumpAndSettle();
    expect(readAllCalls, 1);
    expect(center.unreadCount, 0);
    expect(find.text('2 chưa đọc'), findsNothing);
    expect(find.text('Đọc tất cả'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
    center.dispose();
    auth.dispose();
    client.close();
  });

  testWidgets(
    'opening an unread notification marks it read and refreshes count',
    (tester) async {
      final calls = <String>[];
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        calls.add('${request.method} ${request.url.path}');
        if (request.url.path.endsWith('/notifications/n-1/read')) {
          expect(request.method, 'PATCH');
          return _json({});
        }
        if (request.url.path.endsWith('/notifications') &&
            request.url.queryParameters['pageSize'] == '1') {
          return _json({'unreadCount': 0});
        }
        return _json({
          'items': [_notification('n-1')],
          'unreadCount': 1,
        });
      });
      final auth = await _auth(client);
      final center = NotificationCenter(auth);
      await _pumpNotifications(tester, auth: auth, center: center);
      await tester.tap(find.text('Video ready'));
      await tester.pumpAndSettle();
      expect(calls, contains('PATCH /api/v1/notifications/n-1/read'));
      expect(calls, contains('GET /api/v1/notifications'));
      expect(center.unreadCount, 0);
      expect(find.text('1 chưa đọc'), findsNothing);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
      center.dispose();
      auth.dispose();
      client.close();
    },
  );

  testWidgets('load more appends new items and removes duplicate ids', (
    tester,
  ) async {
    final pages = <String>[];
    final client = MockClient((request) async {
      if (request.url.path.endsWith('/auth/refresh')) {
        return jsonResponse(session());
      }
      final page = request.url.queryParameters['page']!;
      pages.add(page);
      if (page == '1') {
        return _json({
          'items': List.generate(
            20,
            (index) => _notification('n-$index', title: 'Notification $index'),
          ),
          'unreadCount': 20,
        });
      }
      return _json({
        'items': [
          _notification('n-19', title: 'Notification 19'),
          _notification('n-20', title: 'Notification 20'),
        ],
        'unreadCount': 20,
      });
    });
    final auth = await _auth(client);
    final center = NotificationCenter(auth);
    await _pumpNotifications(tester, auth: auth, center: center);
    await tester.scrollUntilVisible(
      find.text('Tải thêm'),
      400,
      scrollable: find.byType(Scrollable).last,
    );
    await tester.tap(find.text('Tải thêm'));
    await tester.pumpAndSettle();
    expect(pages, ['1', '2']);
    expect(find.text('Notification 19'), findsOneWidget);
    expect(find.text('Notification 20'), findsOneWidget);
    expect(find.text('Tải thêm'), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
    center.dispose();
    auth.dispose();
    client.close();
  });

  testWidgets('load failure offers a retry that restores the list', (
    tester,
  ) async {
    var listCalls = 0;
    final client = MockClient((request) async {
      if (request.url.path.endsWith('/auth/refresh')) {
        return jsonResponse(session());
      }
      listCalls++;
      if (listCalls == 1) return _json({'code': 'TEMPORARY'}, 503);
      return _json({
        'items': [_notification('n-1')],
        'unreadCount': 1,
      });
    });
    final auth = await _auth(client);
    final center = NotificationCenter(auth);
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: NotificationsScreen(auth: auth, notifications: center),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Không thể tải thông báo.'), findsOneWidget);
    await tester.tap(find.text('Thử lại'));
    await tester.pumpAndSettle();
    expect(listCalls, 2);
    expect(find.text('Video ready'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
    center.dispose();
    auth.dispose();
    client.close();
  });
}
