import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/features/content/content_service.dart';

import 'auth_test.dart' show MemoryStore, jsonResponse, session;

const _base = 'https://content-contract.test/api/v1';

http.Response _list([List<dynamic> items = const []]) => http.Response(
  jsonEncode(items),
  200,
  headers: {'content-type': 'application/json'},
);

Future<AuthController> _auth(MockClient client, {bool signedIn = true}) async {
  final auth = AuthController(
    ApiClient(baseUrl: _base, client: client),
    MemoryStore(signedIn ? 'refresh' : null),
  );
  await auth.restore();
  return auth;
}

typedef _Call = Future<void> Function(AuthController auth);
typedef _Scenario = ({
  String name,
  String method,
  String path,
  Map<String, String> query,
  Map<String, dynamic>? body,
  bool listResponse,
  bool publicRequest,
  _Call invoke,
});

void main() {
  group('Content service API contracts', () {
    final scenarios = <_Scenario>[
      (
        name: 'anonymous home feed',
        method: 'GET',
        path: '/feed/home',
        query: {'page': '2', 'pageSize': '10', 'sort': 'popular'},
        body: null,
        listResponse: false,
        publicRequest: true,
        invoke: (auth) async {
          await ContentService(
            auth,
          ).feed(explore: false, page: 2, pageSize: 10, sort: 'popular');
        },
      ),
      (
        name: 'explore feed filters',
        method: 'GET',
        path: '/feed/explore',
        query: {
          'page': '3',
          'pageSize': '12',
          'categoryId': 'cat-1',
          'tag': 'learn dart',
          'sort': 'newest',
        },
        body: null,
        listResponse: false,
        publicRequest: true,
        invoke: (auth) async {
          await ContentService(auth).feed(
            explore: true,
            page: 3,
            pageSize: 12,
            categoryId: 'cat-1',
            tag: 'learn dart',
          );
        },
      ),
      (
        name: 'video search filters and trims the query',
        method: 'GET',
        path: '/videos/search',
        query: {
          'q': 'summer video',
          'categoryId': 'cat-1',
          'tag': 'travel',
          'channelId': 'ch-1',
          'dateRange': 'month',
          'duration': 'short',
          'sort': 'views',
          'page': '2',
          'pageSize': '15',
        },
        body: null,
        listResponse: false,
        publicRequest: true,
        invoke: (auth) async {
          await ContentService(auth).searchVideos(
            query: ' summer video ',
            categoryId: 'cat-1',
            tag: 'travel',
            channelId: 'ch-1',
            dateRange: 'month',
            duration: 'short',
            page: 2,
            pageSize: 15,
            sort: 'views',
          );
        },
      ),
      (
        name: 'explore hub',
        method: 'GET',
        path: '/feed/explore-hub',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: true,
        invoke: (auth) async => ContentService(auth).exploreHub(),
      ),
      (
        name: 'category catalogue',
        method: 'GET',
        path: '/categories',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: true,
        invoke: (auth) async => ContentService(auth).categories(),
      ),
      (
        name: 'signed-in subscriptions feed',
        method: 'GET',
        path: '/feed/subscriptions',
        query: {'page': '2', 'pageSize': '7'},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ContentService(auth).subscriptionsFeed(page: 2, pageSize: 7),
      ),
      (
        name: 'public video detail',
        method: 'GET',
        path: '/videos/video%2F1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: true,
        invoke: (auth) async => ContentService(auth).detail('video/1'),
      ),
      (
        name: 'public playback manifest',
        method: 'GET',
        path: '/videos/v-1/playback',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: true,
        invoke: (auth) async => ContentService(auth).playback('v-1'),
      ),
      (
        name: 'public comments pagination and sort',
        method: 'GET',
        path: '/videos/v-1/comments',
        query: {'page': '2', 'pageSize': '20', 'sort': 'top'},
        body: null,
        listResponse: false,
        publicRequest: true,
        invoke: (auth) async =>
            ContentService(auth).comments('v-1', page: 2, sort: 'top'),
      ),
      (
        name: 'public replies pagination',
        method: 'GET',
        path: '/comments/cm%2F1/replies',
        query: {'page': '2', 'pageSize': '5'},
        body: null,
        listResponse: false,
        publicRequest: true,
        invoke: (auth) async =>
            ContentService(auth).replies('cm/1', page: 2, pageSize: 5),
      ),
      (
        name: 'watch progress saves history preference',
        method: 'PUT',
        path: '/videos/v-1/watch-progress',
        query: const {},
        body: {'watchedSeconds': 45, 'saveHistory': false},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ContentService(auth).progress('v-1', 45, saveHistory: false),
      ),
      (
        name: 'set video reaction',
        method: 'PUT',
        path: '/videos/v-1/reaction',
        query: const {},
        body: {'type': 'like'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).react('v-1', 'like'),
      ),
      (
        name: 'remove video reaction',
        method: 'DELETE',
        path: '/videos/v-1/reaction',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).react('v-1', null),
      ),
      (
        name: 'set video rating',
        method: 'PUT',
        path: '/videos/v-1/rating',
        query: const {},
        body: {'score': 4},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).rate('v-1', 4),
      ),
      (
        name: 'remove video rating',
        method: 'DELETE',
        path: '/videos/v-1/rating',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).rate('v-1', null),
      ),
      (
        name: 'share video records mobile method',
        method: 'POST',
        path: '/videos/v-1/share',
        query: const {},
        body: {'method': 'mobile'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).share('v-1'),
      ),
      (
        name: 'create top-level comment',
        method: 'POST',
        path: '/videos/v-1/comments',
        query: const {},
        body: {'content': 'A helpful comment', 'parentCommentId': null},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ContentService(auth).createComment('v-1', 'A helpful comment'),
      ),
      (
        name: 'create reply with parent id',
        method: 'POST',
        path: '/videos/v-1/comments',
        query: const {},
        body: {'content': 'Reply', 'parentCommentId': 'cm-1'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(
          auth,
        ).createComment('v-1', 'Reply', parentCommentId: 'cm-1'),
      ),
      (
        name: 'update comment trims text',
        method: 'PATCH',
        path: '/comments/cm-1',
        query: const {},
        body: {'content': 'Updated comment'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ContentService(auth).updateComment('cm-1', ' Updated comment '),
      ),
      (
        name: 'delete comment',
        method: 'DELETE',
        path: '/comments/cm-1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).deleteComment('cm-1'),
      ),
      (
        name: 'set comment reaction',
        method: 'PUT',
        path: '/comments/cm-1/reaction',
        query: const {},
        body: {'type': 'like'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ContentService(auth).reactComment('cm-1', 'like'),
      ),
      (
        name: 'remove comment reaction',
        method: 'DELETE',
        path: '/comments/cm-1/reaction',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).reactComment('cm-1', null),
      ),
      (
        name: 'pin creator comment',
        method: 'PUT',
        path: '/comments/cm-1/pin',
        query: const {},
        body: {'enabled': true},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(
          auth,
        ).setCommentCreatorState('cm-1', enabled: true, pin: true),
      ),
      (
        name: 'remove creator heart from comment',
        method: 'PUT',
        path: '/comments/cm-1/creator-heart',
        query: const {},
        body: {'enabled': false},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(
          auth,
        ).setCommentCreatorState('cm-1', enabled: false, pin: false),
      ),
      (
        name: 'hide comment with moderation reason',
        method: 'PUT',
        path: '/comments/cm-1/visibility',
        query: const {},
        body: {'hidden': true, 'reason': 'spam'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(
          auth,
        ).setCommentVisibility('cm-1', true, reason: 'spam'),
      ),
      (
        name: 'load download qualities',
        method: 'GET',
        path: '/videos/v-1/download-options',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).downloadOptions('v-1'),
      ),
      (
        name: 'request video download',
        method: 'POST',
        path: '/videos/v-1/downloads',
        query: const {},
        body: {'quality': '720p'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ContentService(auth).createDownload('v-1', '720p'),
      ),
      (
        name: 'list user downloads',
        method: 'GET',
        path: '/downloads',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).downloads(),
      ),
      (
        name: 'retry a failed download',
        method: 'POST',
        path: '/downloads/dl-1/retry',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ContentService(auth).updateDownload('dl-1', 'retry'),
      ),
      (
        name: 'delete a download',
        method: 'DELETE',
        path: '/downloads/dl-1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).deleteDownload('dl-1'),
      ),
      (
        name: 'delete selected downloads',
        method: 'DELETE',
        path: '/downloads',
        query: const {},
        body: {
          'ids': ['dl-1', 'dl-2'],
        },
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ContentService(auth).deleteDownloads(['dl-1', 'dl-2']),
      ),
      (
        name: 'load public violation types',
        method: 'GET',
        path: '/violation-types',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: true,
        invoke: (auth) async => ContentService(auth).violationTypes(),
      ),
      (
        name: 'report comment with selected reason',
        method: 'POST',
        path: '/comments/cm-1/report',
        query: const {},
        body: {'violationTypeId': 'spam', 'description': 'Repeated spam'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ContentService(auth).reportComment('cm-1', 'spam', 'Repeated spam'),
      ),
      (
        name: 'history filters and sorting',
        method: 'GET',
        path: '/library/history',
        query: {
          'page': '3',
          'pageSize': '20',
          'q': ' learn Flutter ',
          'sort': 'oldest',
          'dateRange': 'week',
        },
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).history(
          page: 3,
          query: ' learn Flutter ',
          sort: 'oldest',
          dateRange: 'week',
        ),
      ),
      (
        name: 'liked videos filter by rating',
        method: 'GET',
        path: '/library/liked',
        query: {'page': '2', 'pageSize': '20', 'rating': '5'},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ContentService(auth).liked(page: 2, rating: 5),
      ),
    ];

    for (final scenario in scenarios) {
      for (final fail in [false, true]) {
        test(
          '${scenario.name}: ${fail ? 'server errors surface' : 'request succeeds'}',
          () async {
            ApiClient.invalidateReadCache();
            var requests = 0;
            final client = MockClient((request) async {
              if (request.url.path.endsWith('/auth/refresh')) {
                return jsonResponse(session());
              }
              requests++;
              expect(request.method, scenario.method);
              expect(request.url.path, '/api/v1${scenario.path}');
              expect(request.url.queryParameters, scenario.query);
              if (scenario.publicRequest) {
                expect(request.headers, isNot(contains('Authorization')));
              } else {
                expect(request.headers['Authorization'], startsWith('Bearer '));
              }
              if (scenario.body != null) {
                expect(jsonDecode(request.body), scenario.body);
              }
              if (fail) return jsonResponse({'code': 'CONTENT_FAILED'}, 503);
              if (scenario.path.endsWith('/download-options')) {
                return http.Response(
                  jsonEncode([
                    {
                      'quality': '720p',
                      'width': 1280,
                      'height': 720,
                      'fileSize': 1024,
                      'url': 'https://cdn.test/video-720p.mp4',
                    },
                  ]),
                  200,
                  headers: {'content-type': 'application/json'},
                );
              }
              if (scenario.listResponse) return _list();
              return jsonResponse({});
            });
            final auth = await _auth(client, signedIn: !scenario.publicRequest);
            if (fail) {
              await expectLater(
                scenario.invoke(auth),
                throwsA(
                  isA<ApiFailure>().having(
                    (error) => error.status,
                    'status',
                    503,
                  ),
                ),
              );
            } else {
              await scenario.invoke(auth);
            }
            expect(requests, 1);
            auth.dispose();
            client.close();
          },
        );
      }
    }
  });
}
