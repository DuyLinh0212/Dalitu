import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/channel/models/channel_models.dart';
import 'package:user_app/core/localization/app_strings.dart';
import 'package:user_app/features/creator/creator_analytics_screen.dart';
import 'package:user_app/features/creator/creator_comments_screen.dart';
import 'package:user_app/features/creator/creator_content_screen.dart';

import 'auth_test.dart' show MemoryStore, jsonResponse, session;

const _base = 'https://creator-ui.test/api/v1';
const _channel = ChannelDetail(
  id: 'ch-1',
  name: 'Creator channel',
  handle: 'creator',
  subscriberCount: 12,
  videoCount: 2,
  viewCount: 400,
  isOwner: true,
  isSubscribed: false,
  createdAt: '2026-01-01T00:00:00Z',
  permissions: ['comment.manage'],
);

http.Response _json(Object value, [int status = 200]) => http.Response(
  jsonEncode(value),
  status,
  headers: {'content-type': 'application/json'},
);

Future<AuthController> _auth(MockClient client) async {
  final auth = AuthController(
    ApiClient(baseUrl: _base, client: client),
    MemoryStore('refresh'),
  );
  await auth.restore();
  return auth;
}

Map<String, dynamic> _comment({String status = 'visible'}) => {
  'commentId': 'cm-1',
  'videoId': 'v-1',
  'videoTitle': 'Creator video',
  'displayName': 'Viewer',
  'content': 'A useful comment',
  'status': status,
  'createdAt': '2026-07-01T10:00:00Z',
  'likes': 3,
  'replyCount': 1,
};

Map<String, dynamic> _video({
  String id = 'v-1',
  String title = 'Creator video',
  String moderationStatus = 'not_submitted',
  String visibility = 'private',
  String status = 'published',
  int views = 100,
  int likes = 20,
  int comments = 5,
  int watchSeconds = 400,
  double? averageRating = 4.5,
  int ratingCount = 10,
}) => {
  'videoId': id,
  'channelId': 'ch-1',
  'channelName': 'Creator channel',
  'channelHandle': 'creator',
  'title': title,
  'thumbnailUrl': null,
  'duration': 60,
  'visibility': visibility,
  'publishedAt': '2026-07-01T10:00:00Z',
  'description': 'A creator video',
  'videoUrl': '',
  'tags': ['dart'],
  'chapters': [],
  'moderationStatus': moderationStatus,
  'status': status,
  'stats': {
    'views': views,
    'likes': likes,
    'dislikes': 0,
    'comments': comments,
    'averageRating': averageRating,
    'ratingCount': ratingCount,
    'watchSeconds': watchSeconds,
  },
};

void main() {
  setUp(() {
    AppStrings.currentLang.value = 'vi';
    ApiClient.invalidateReadCache();
  });

  testWidgets('creator comments filter by visibility and sort order', (
    tester,
  ) async {
    final queries = <Map<String, String>>[];
    final client = MockClient((request) async {
      if (request.url.path.endsWith('/auth/refresh')) {
        return jsonResponse(session());
      }
      queries.add(request.url.queryParameters);
      final status = request.url.queryParameters['status'] ?? 'visible';
      return _json({
        'items': [_comment(status: status)],
        'page': 1,
        'pageSize': 50,
        'total': 1,
      });
    });
    final auth = await _auth(client);
    await tester.pumpWidget(
      MaterialApp(
        home: CreatorCommentsScreen(auth: auth, channel: _channel),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('A useful comment'), findsOneWidget);
    await tester.tap(find.byType(DropdownButtonFormField<String>));
    await tester.pumpAndSettle();
    await tester.tap(find.text(AppStrings.t('creator.commentSortOldest')).last);
    await tester.pumpAndSettle();
    await tester.tap(find.text(AppStrings.t('creator.hiddenComments')));
    await tester.pumpAndSettle();
    expect(queries.first['status'], 'visible');
    expect(queries.first['sort'], 'newest');
    expect(queries[1]['status'], 'visible');
    expect(queries[1]['sort'], 'oldest');
    expect(queries.last['status'], 'hidden');
    expect(queries.last['sort'], 'oldest');
    expect(find.text('A useful comment'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
    auth.dispose();
    client.close();
  });

  testWidgets('creator can hide a comment and it leaves the visible filter', (
    tester,
  ) async {
    Map<String, dynamic>? visibilityBody;
    final requests = <String>[];
    final client = MockClient((request) async {
      if (request.url.path.endsWith('/auth/refresh')) {
        return jsonResponse(session());
      }
      requests.add('${request.method} ${request.url.path}');
      if (request.url.path.endsWith('/comments/cm-1/visibility')) {
        visibilityBody = jsonDecode(request.body) as Map<String, dynamic>;
        return _json({});
      }
      return _json({
        'items': [_comment()],
        'page': 1,
        'pageSize': 50,
        'total': 1,
      });
    });
    final auth = await _auth(client);
    await tester.pumpWidget(
      MaterialApp(
        home: CreatorCommentsScreen(auth: auth, channel: _channel),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byType(PopupMenuButton<String>));
    await tester.pumpAndSettle();
    await tester.tap(find.text(AppStrings.t('creator.hideComment')));
    await tester.pumpAndSettle();
    expect(visibilityBody, {'hidden': true, 'reason': null});
    expect(requests, contains('PATCH /api/v1/comments/cm-1/visibility'));
    expect(find.text('A useful comment'), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
    auth.dispose();
    client.close();
  });

  for (final action in [
    (
      title: 'submit a draft for moderation',
      moderationStatus: 'not_submitted',
      visibility: 'private',
      actionLabel: 'creator.submitReview',
      endpoint: '/videos/v-1/submit-moderation',
    ),
    (
      title: 'publish an approved private video',
      moderationStatus: 'approved',
      visibility: 'private',
      actionLabel: 'creator.publishVideo',
      endpoint: '/videos/v-1/publish',
    ),
  ]) {
    testWidgets('creator can ${action.title}', (tester) async {
      final requests = <String>[];
      var listReads = 0;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        requests.add('${request.method} ${request.url.path}');
        if (request.url.path.endsWith('/videos/manage')) {
          listReads++;
          return _json({
            'items': [
              _video(
                moderationStatus: action.moderationStatus,
                visibility: action.visibility,
              ),
            ],
            'page': 1,
            'pageSize': 50,
            'total': 1,
          });
        }
        return _json(
          _video(moderationStatus: 'approved', visibility: 'public'),
        );
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        MaterialApp(
          home: CreatorContentScreen(auth: auth, channel: _channel),
        ),
      );
      await tester.pumpAndSettle();
      final actionLabel = AppStrings.t(action.actionLabel);
      if (action.actionLabel == 'creator.submitReview') {
        await tester.ensureVisible(find.text(actionLabel));
        await tester.pumpAndSettle();
        await tester.tap(find.text(actionLabel));
      } else {
        await tester.ensureVisible(find.byTooltip(actionLabel));
        await tester.pumpAndSettle();
        await tester.tap(find.byTooltip(actionLabel));
      }
      await tester.pumpAndSettle();
      expect(requests, contains('POST /api/v1${action.endpoint}'));
      expect(listReads, 2);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });
  }

  testWidgets(
    'creator analytics derives weighted rating and ranks top videos',
    (tester) async {
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        expect(request.url.path, '/api/v1/studio/channels/ch-1/metrics');
        return _json([
          _video(),
          _video(
            id: 'v-2',
            title: 'More viewed video',
            views: 300,
            likes: 30,
            comments: 10,
            watchSeconds: 900,
            averageRating: 3,
            ratingCount: 2,
          ),
        ]);
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        MaterialApp(
          home: CreatorAnalyticsScreen(auth: auth, channel: _channel),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('4.25 / 5'), findsOneWidget);
      await tester.drag(find.byType(ListView), const Offset(0, -900));
      await tester.pumpAndSettle();
      expect(find.text('More viewed video'), findsOneWidget);
      expect(find.text(AppStrings.t('analytics.topVideos')), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    },
  );
}
