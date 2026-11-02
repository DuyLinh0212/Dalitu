import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/channel/services/channel_service.dart';
import 'package:user_app/features/creator/creator_service.dart';
import 'package:user_app/features/feedback/feedback_survey_service.dart';
import 'package:user_app/features/playlists/playlist_service.dart';

import 'auth_test.dart' show MemoryStore, jsonResponse, session;

const _base = 'https://mobile-services.test/api/v1';

http.Response _list([List<dynamic> values = const []]) => http.Response(
  jsonEncode(values),
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
  group('Creator service request contracts', () {
    final scenarios = <_Scenario>[
      (
        name: 'video metrics',
        method: 'GET',
        path: '/studio/channels/ch-1/metrics',
        query: {'includeStats': 'true'},
        body: null,
        listResponse: true,
        publicRequest: false,
        invoke: (auth) async {
          await CreatorService(auth).videoMetrics('ch-1');
        },
      ),
      (
        name: 'upload preflight sends selected media metadata',
        method: 'POST',
        path: '/videos/upload-preflight',
        query: const {},
        body: {
          'channelId': 'ch-1',
          'fileSize': 2048,
          'duration': 91,
          'contentType': 'video/mp4',
          'sourceQuality': '1080p',
        },
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async {
          await CreatorService(auth).preflight(
            channelId: 'ch-1',
            fileSize: 2048,
            duration: 91,
            contentType: 'video/mp4',
            quality: '1080p',
          );
        },
      ),
      (
        name: 'managed video filters and pagination',
        method: 'GET',
        path: '/videos/manage',
        query: {
          'channelId': 'ch-1',
          'status': 'ready',
          'visibility': 'private',
          'search': 'summer mix',
          'page': '3',
          'pageSize': '50',
        },
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async {
          await CreatorService(auth).managedVideos(
            'ch-1',
            page: 3,
            status: 'ready',
            visibility: 'private',
            search: 'summer mix',
          );
        },
      ),
      (
        name: 'video metadata update includes explicit clear flag',
        method: 'PATCH',
        path: '/videos/video%2F1',
        query: const {},
        body: {
          'title': 'Edited title',
          'description': 'Edited description',
          'categoryId': null,
          'clearCategory': true,
          'languageCode': null,
          'visibility': 'unlisted',
          'ageRestricted': null,
          'thumbnailUrl': null,
          'tags': ['music', 'live'],
          'chapters': [
            {'startSeconds': 30, 'title': 'Verse'},
          ],
          'videoCards': [],
          'promotionEnabled': false,
        },
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async {
          await CreatorService(auth).updateVideo(
            'video/1',
            title: 'Edited title',
            description: 'Edited description',
            clearCategory: true,
            visibility: 'unlisted',
            tags: ['music', 'live'],
            chapters: [
              {'startSeconds': 30, 'title': 'Verse'},
            ],
            videoCards: const [],
            promotionEnabled: false,
          );
        },
      ),
      (
        name: 'delete managed video',
        method: 'DELETE',
        path: '/videos/video-1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => CreatorService(auth).deleteVideo('video-1'),
      ),
      (
        name: 'submit video for moderation',
        method: 'POST',
        path: '/videos/video-1/submit-moderation',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            CreatorService(auth).submitModeration('video-1'),
      ),
      (
        name: 'publish managed video',
        method: 'POST',
        path: '/videos/video-1/publish',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => CreatorService(auth).publish('video-1'),
      ),
      (
        name: 'cancel video upload',
        method: 'POST',
        path: '/videos/video-1/cancel-upload',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => CreatorService(auth).cancelUpload('video-1'),
      ),
      (
        name: 'retry failed video processing',
        method: 'POST',
        path: '/videos/video-1/retry-processing',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => CreatorService(auth).retryProcessing('video-1'),
      ),
      (
        name: 'managed comment filters and pagination',
        method: 'GET',
        path: '/channels/ch-1/comments/manage',
        query: {
          'status': 'hidden',
          'sort': 'newest',
          'page': '2',
          'pageSize': '50',
        },
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async {
          await CreatorService(
            auth,
          ).managedComments('ch-1', page: 2, status: 'hidden');
        },
      ),
      (
        name: 'set comment visibility and reason field',
        method: 'PATCH',
        path: '/comments/comment-1/visibility',
        query: const {},
        body: {'hidden': true, 'reason': null},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            CreatorService(auth).setCommentHidden('comment-1', true),
      ),
      (
        name: 'delete managed comment',
        method: 'DELETE',
        path: '/comments/comment-1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => CreatorService(auth).deleteComment('comment-1'),
      ),
    ];

    for (final scenario in scenarios) {
      for (final fail in [false, true]) {
        test(
          '${scenario.name}: ${fail ? 'surfaces server errors' : 'succeeds'}',
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
              if (fail) return jsonResponse({'code': 'CREATOR_FAILED'}, 503);
              return scenario.listResponse ? _list() : jsonResponse({});
            });
            final auth = await _auth(client);
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

    test(
      'metrics falls back to managed videos when metrics are unavailable',
      () async {
        ApiClient.invalidateReadCache();
        final requests = <String>[];
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          requests.add('${request.method} ${request.url.path}');
          if (request.url.path.endsWith('/metrics')) {
            return jsonResponse({'code': 'NOT_SUPPORTED'}, 404);
          }
          return jsonResponse({
            'items': [
              {'videoId': 'v-1', 'title': 'Fallback video'},
            ],
            'page': 1,
            'pageSize': 50,
            'total': 1,
          });
        });
        final auth = await _auth(client);
        final result = await CreatorService(auth).videoMetrics('ch-1');
        expect(result.items.single.title, 'Fallback video');
        expect(requests, [
          'GET /api/v1/studio/channels/ch-1/metrics',
          'GET /api/v1/videos/manage',
        ]);
        auth.dispose();
        client.close();
      },
    );

    test('thumbnail generation request is a multipart operation', () async {
      ApiClient.invalidateReadCache();
      var uploadPath = '';
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        uploadPath = '${request.method} ${request.url.path}';
        expect(request.headers['Authorization'], startsWith('Bearer '));
        expect(
          request.headers['content-type'],
          startsWith('multipart/form-data'),
        );
        return jsonResponse({'videoId': 'v-1'});
      });
      final auth = await _auth(client);
      await CreatorService(auth).updateThumbnail('v-1', generate: true);
      expect(uploadPath, 'POST /api/v1/videos/v-1/thumbnail');
      auth.dispose();
      client.close();
    });
  });

  group('Channel service request contracts', () {
    final scenarios = <_Scenario>[
      (
        name: 'my channel details',
        method: 'GET',
        path: '/channels/me',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).getMyChannel(),
      ),
      (
        name: 'accessible channels',
        method: 'GET',
        path: '/channels/accessible',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).getAccessibleChannels(),
      ),
      (
        name: 'lookup channel by handle',
        method: 'GET',
        path: '/channels/handle/My%20Channel',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: true,
        invoke: (auth) async => ChannelService(auth).getChannel('@My Channel'),
      ),
      (
        name: 'lookup channel by id',
        method: 'GET',
        path: '/channels/ch-1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: true,
        invoke: (auth) async => ChannelService(auth).getChannelById('ch-1'),
      ),
      (
        name: 'check handle normalizes punctuation and current channel',
        method: 'GET',
        path: '/channels/check-handle',
        query: {'handle': 'my.channel', 'currentChannelId': 'ch-1'},
        body: null,
        listResponse: false,
        publicRequest: true,
        invoke: (auth) async => ChannelService(
          auth,
        ).checkHandle(' @@My.Channel ', currentChannelId: 'ch-1'),
      ),
      (
        name: 'create channel trims user input',
        method: 'POST',
        path: '/channels',
        query: const {},
        body: {
          'name': 'My channel',
          'handle': 'my-channel',
          'description': 'About',
        },
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).createChannel(
          name: ' My channel ',
          handle: ' @My-Channel ',
          description: ' About ',
        ),
      ),
      (
        name: 'update channel metadata and settings',
        method: 'PATCH',
        path: '/channels/ch-1',
        query: const {},
        body: {
          'name': 'New name',
          'handle': 'new.handle',
          'description': 'New about',
          'settings': 'comments',
        },
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).updateChannel(
          'ch-1',
          name: ' New name ',
          handle: '@New.Handle',
          description: ' New about ',
          settings: 'comments',
        ),
      ),
      (
        name: 'delete channel',
        method: 'DELETE',
        path: '/channels/ch-1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).deleteChannel('ch-1'),
      ),
      (
        name: 'list channel roles',
        method: 'GET',
        path: '/channels/roles',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).getRoles(),
      ),
      (
        name: 'list channel members',
        method: 'GET',
        path: '/channels/ch-1/members',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).getMembers('ch-1'),
      ),
      (
        name: 'list pending channel invitations',
        method: 'GET',
        path: '/channels/ch-1/invitations',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: false,
        invoke: (auth) async =>
            ChannelService(auth).getPendingInvitations('ch-1'),
      ),
      (
        name: 'list my channel invitations',
        method: 'GET',
        path: '/channels/invitations/me',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).getMyInvitations(),
      ),
      (
        name: 'invite a channel member',
        method: 'POST',
        path: '/channels/ch-1/members/invite',
        query: const {},
        body: {'email': 'user@example.test', 'roleCode': 'editor'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ChannelService(
          auth,
        ).inviteMember('ch-1', ' user@example.test ', 'editor'),
      ),
      (
        name: 'accept a channel invitation',
        method: 'POST',
        path: '/channels/invitations/inv-1/accept',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).acceptInvitation('inv-1'),
      ),
      (
        name: 'decline a channel invitation',
        method: 'POST',
        path: '/channels/invitations/inv-1/decline',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).declineInvitation('inv-1'),
      ),
      (
        name: 'revoke a channel invitation',
        method: 'DELETE',
        path: '/channels/ch-1/invitations/inv-1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ChannelService(auth).revokeInvitation('ch-1', 'inv-1'),
      ),
      (
        name: 'change member role',
        method: 'PATCH',
        path: '/channels/ch-1/members/u-1/role',
        query: const {},
        body: {'roleCode': 'moderator'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ChannelService(auth).changeMemberRole('ch-1', 'u-1', 'moderator'),
      ),
      (
        name: 'remove channel member',
        method: 'DELETE',
        path: '/channels/ch-1/members/u-1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ChannelService(auth).removeMember('ch-1', 'u-1'),
      ),
      (
        name: 'subscribe to channel',
        method: 'POST',
        path: '/channels/ch-1/subscribe',
        query: const {},
        body: {},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).subscribe('ch-1'),
      ),
      (
        name: 'unsubscribe from channel',
        method: 'DELETE',
        path: '/channels/ch-1/subscribe',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).unsubscribe('ch-1'),
      ),
      (
        name: 'read subscription status',
        method: 'GET',
        path: '/channels/ch-1/subscribe-status',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ChannelService(auth).getSubscriptionStatus('ch-1'),
      ),
      (
        name: 'update subscription notifications',
        method: 'PATCH',
        path: '/channels/ch-1/subscribe-notifications',
        query: const {},
        body: {'enabled': false},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            ChannelService(auth).updateSubscriptionNotifications('ch-1', false),
      ),
      (
        name: 'list subscribed channels',
        method: 'GET',
        path: '/subscriptions',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: false,
        invoke: (auth) async => ChannelService(auth).getSubscribedChannels(),
      ),
    ];

    for (final scenario in scenarios) {
      final swallowErrors = scenario.name == 'list subscribed channels';
      for (final fail in [false, true]) {
        if (fail && swallowErrors) continue;
        test(
          '${scenario.name}: ${fail ? 'surfaces server errors' : 'succeeds'}',
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
              if (!scenario.publicRequest) {
                expect(request.headers['Authorization'], startsWith('Bearer '));
              } else {
                expect(request.headers, isNot(contains('Authorization')));
              }
              if (scenario.body != null) {
                expect(jsonDecode(request.body), scenario.body);
              }
              if (fail) return jsonResponse({'code': 'CHANNEL_FAILED'}, 503);
              return scenario.listResponse ? _list() : jsonResponse({});
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

    test('missing owned channel is a normal empty result', () async {
      ApiClient.invalidateReadCache();
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        return jsonResponse({'code': 'CHANNEL_MISSING'}, 404);
      });
      final auth = await _auth(client);
      expect(await ChannelService(auth).getMyChannel(), isNull);
      auth.dispose();
      client.close();
    });

    test('missing subscription status is represented as null', () async {
      ApiClient.invalidateReadCache();
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        return jsonResponse({'code': 'NOT_SUBSCRIBED'}, 404);
      });
      final auth = await _auth(client);
      expect(await ChannelService(auth).getSubscriptionStatus('ch-1'), isNull);
      auth.dispose();
      client.close();
    });

    test('subscription list failure degrades to an empty list', () async {
      ApiClient.invalidateReadCache();
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        return jsonResponse({'code': 'TEMPORARY'}, 503);
      });
      final auth = await _auth(client);
      expect(await ChannelService(auth).getSubscribedChannels(), isEmpty);
      auth.dispose();
      client.close();
    });
  });

  group('Playlist service request contracts', () {
    final scenarios = <_Scenario>[
      (
        name: 'load my playlists',
        method: 'GET',
        path: '/playlists',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: false,
        invoke: (auth) async => PlaylistService(auth).mine(),
      ),
      (
        name: 'load public channel playlists',
        method: 'GET',
        path: '/playlists/channel/ch%2F1',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: true,
        invoke: (auth) async => PlaylistService(auth).publicByChannel('ch/1'),
      ),
      (
        name: 'load channel-managed playlists',
        method: 'GET',
        path: '/playlists/channel/ch-1/mine',
        query: const {},
        body: null,
        listResponse: true,
        publicRequest: false,
        invoke: (auth) async => PlaylistService(auth).channelMine('ch-1'),
      ),
      (
        name: 'read playlist detail',
        method: 'GET',
        path: '/playlists/pl-1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => PlaylistService(auth).get('pl-1'),
      ),
      (
        name: 'create private playlist with trimmed name',
        method: 'POST',
        path: '/playlists',
        query: const {},
        body: {'name': 'Mix', 'description': 'Notes', 'visibility': 'private'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            PlaylistService(auth).create(name: ' Mix ', description: ' Notes '),
      ),
      (
        name: 'update playlist visibility and metadata',
        method: 'PATCH',
        path: '/playlists/pl-1',
        query: const {},
        body: {
          'name': 'Shared mix',
          'description': 'For everyone',
          'visibility': 'public',
        },
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => PlaylistService(auth).update(
          'pl-1',
          name: ' Shared mix ',
          description: ' For everyone ',
          visibility: 'public',
        ),
      ),
      (
        name: 'delete playlist',
        method: 'DELETE',
        path: '/playlists/pl-1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => PlaylistService(auth).delete('pl-1'),
      ),
      (
        name: 'save a video to a playlist',
        method: 'POST',
        path: '/playlists/save',
        query: const {},
        body: {'videoId': 'v-1'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => PlaylistService(auth).saveVideo('v-1'),
      ),
      (
        name: 'add a video to an existing playlist',
        method: 'POST',
        path: '/playlists/pl-1/videos',
        query: const {},
        body: {'videoId': 'v-1'},
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async => PlaylistService(auth).addVideo('pl-1', 'v-1'),
      ),
      (
        name: 'remove a video from a playlist',
        method: 'DELETE',
        path: '/playlists/pl-1/videos/v%2F1',
        query: const {},
        body: null,
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            PlaylistService(auth).removeVideo('pl-1', 'v/1'),
      ),
      (
        name: 'reorder playlist videos',
        method: 'PUT',
        path: '/playlists/pl-1/order',
        query: const {},
        body: {
          'videoIds': ['v-3', 'v-1', 'v-2'],
        },
        listResponse: false,
        publicRequest: false,
        invoke: (auth) async =>
            PlaylistService(auth).reorder('pl-1', ['v-3', 'v-1', 'v-2']),
      ),
    ];

    for (final scenario in scenarios) {
      for (final fail in [false, true]) {
        test(
          '${scenario.name}: ${fail ? 'surfaces server errors' : 'succeeds'}',
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
              if (!scenario.publicRequest) {
                expect(request.headers['Authorization'], startsWith('Bearer '));
              } else {
                expect(request.headers, isNot(contains('Authorization')));
              }
              if (scenario.body != null) {
                expect(jsonDecode(request.body), scenario.body);
              }
              if (fail) return jsonResponse({'code': 'PLAYLIST_FAILED'}, 503);
              return scenario.listResponse ? _list() : jsonResponse({});
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

    test('playlist cover uses upload endpoint and content type', () async {
      ApiClient.invalidateReadCache();
      var upload = false;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        upload = true;
        expect(request.method, 'POST');
        expect(request.url.path, '/api/v1/playlists/pl-1/cover');
        expect(request.headers['Authorization'], startsWith('Bearer '));
        expect(
          request.headers['content-type'],
          startsWith('multipart/form-data'),
        );
        expect(request.body, contains('cover.png'));
        expect(request.body, contains('content-type: image/png'));
        return jsonResponse({'playlistId': 'pl-1'});
      });
      final auth = await _auth(client);
      await PlaylistService(auth).uploadCover(
        'pl-1',
        const UploadPayload(
          bytes: [1, 2, 3],
          fileName: 'cover.png',
          contentType: 'image/png',
        ),
      );
      expect(upload, isTrue);
      auth.dispose();
      client.close();
    });
  });

  group('Feedback survey status parsing', () {
    const acceptedUrl =
        'https://docs.google.com/forms/d/e/1FAIpQLSef9jF_PK4qnoEZjPjS7kq1Lk4iQbEtoSXxS8XMkoWcVcoecg/viewform';

    test(
      'status request returns enabled state and an approved HTTPS form link',
      () async {
        ApiClient.invalidateReadCache();
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          expect(request.method, 'GET');
          expect(request.url.path, '/api/v1/feedback-survey');
          return jsonResponse({
            'enabled': true,
            'hasCompleted': false,
            'formUrl': acceptedUrl,
          });
        });
        final auth = await _auth(client);
        final status = await FeedbackSurveyService(auth).getStatus();
        expect(status.enabled, isTrue);
        expect(status.hasCompleted, isFalse);
        expect(status.formUri.toString(), acceptedUrl);
        auth.dispose();
        client.close();
      },
    );

    final urls = <String?>[
      null,
      '',
      acceptedUrl.replaceFirst('https://', 'http://'),
      acceptedUrl.replaceFirst('docs.google.com', 'evil.example'),
      acceptedUrl.replaceFirst('/viewform', '/edit'),
      acceptedUrl.replaceFirst('docs.google.com', 'user@docs.google.com'),
      acceptedUrl.replaceFirst('https://', 'https://docs.google.com:443@'),
    ];
    for (final url in urls) {
      test('rejects non-approved survey URL ${url ?? '(missing)'}', () {
        final status = FeedbackSurveyStatus.fromJson({
          'enabled': true,
          'hasCompleted': false,
          'formUrl': ?url,
        });
        expect(status.enabled, isTrue);
        expect(status.hasCompleted, isFalse);
        expect(status.formUri, isNull);
      });
    }

    test('preserves prefills on the approved survey URL', () {
      final status = FeedbackSurveyStatus.fromJson({
        'enabled': true,
        'formUrl': '$acceptedUrl?entry.1=value',
      });
      expect(status.formUri?.queryParameters, {'entry.1': 'value'});
    });

    test('ignores truthy values that are not JSON booleans', () {
      final status = FeedbackSurveyStatus.fromJson({
        'enabled': 'true',
        'hasCompleted': 1,
        'formUrl': acceptedUrl,
      });
      expect(status.enabled, isFalse);
      expect(status.hasCompleted, isFalse);
      expect(status.formUri, isNotNull);
    });
  });
}
