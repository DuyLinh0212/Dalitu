import 'dart:convert';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/features/payments/payment_service.dart';
import 'package:user_app/features/plans/plan_service.dart';
import 'package:user_app/features/moderation/moderation_service.dart';
import 'package:user_app/features/playlists/playlist_service.dart';
import 'auth_test.dart' show MemoryStore, jsonResponse, session;

void main() {
  final scenarios =
      <
        (
          String,
          String,
          String,
          Map<String, dynamic>?,
          Future<void> Function(AuthController),
        )
      >[
        (
          'payment initiation preserves renewal choice',
          'POST',
          '/payments',
          {'planId': 'plan-1', 'autoRenew': true},
          (a) async {
            await PaymentService(a).initiate('plan-1', autoRenew: true);
          },
        ),
        (
          'payment cancellation',
          'POST',
          '/payments/payment-1/cancel',
          {},
          (a) async {
            await PaymentService(a).cancelPayment('payment-1');
          },
        ),
        (
          'payment history decodes list responses',
          'GET',
          '/payments',
          null,
          (a) async {
            await PaymentService(a).mine();
          },
        ),
        (
          'payment lookup encodes the payment identifier',
          'GET',
          '/payments/payment%2Fone',
          null,
          (a) async {
            await PaymentService(a).get('payment/one');
          },
        ),
        (
          'plan subscription',
          'POST',
          '/plans/plan-1/subscribe',
          {'autoRenew': false},
          (a) async {
            await PlanService(a).subscribe('plan-1');
          },
        ),
        (
          'shared plan invite trims email',
          'POST',
          '/plans/members/invite',
          {'email': 'user@example.com', 'allocatedStorage': 1024},
          (a) async {
            await PlanService(
              a,
            ).invite(' user@example.com ', allocatedStorage: 1024);
          },
        ),
        (
          'shared plan acceptance preserves paid cancellation confirmation',
          'POST',
          '/plans/members/member-1/accept',
          {'token': 'invite', 'confirmCancelPaidPlan': true},
          (a) async {
            await PlanService(a).acceptInvitation(
              'member-1',
              'invite',
              confirmCancelPaidPlan: true,
            );
          },
        ),
        (
          'leave shared plan',
          'POST',
          '/plans/members/leave',
          {},
          (a) async {
            await PlanService(a).leaveSharedPlan();
          },
        ),
        (
          'member storage can be reset',
          'PATCH',
          '/plans/members/member-1/storage',
          {'allocatedStorage': null},
          (a) async {
            await PlanService(a).updateMemberStorage('member-1', null);
          },
        ),
        (
          'owner storage allocation can be updated',
          'PATCH',
          '/plans/my-subscription/owner-storage',
          {'allocatedStorage': 2048},
          (a) async {
            await PlanService(a).updateOwnerStorage(2048);
          },
        ),
        (
          'shared plan member can be removed',
          'DELETE',
          '/plans/members/member-1',
          null,
          (a) async {
            await PlanService(a).removeMember('member-1');
          },
        ),
        (
          'violation types load from the public catalogue',
          'GET',
          '/violation-types',
          null,
          (a) async {
            await ModerationService(a).violationTypes();
          },
        ),
        (
          'video report trims description',
          'POST',
          '/videos/video-1/report',
          {'violationTypeId': 'spam', 'description': 'report'},
          (a) async {
            await ModerationService(a).reportVideo(
              videoId: 'video-1',
              violationTypeId: 'spam',
              description: ' report ',
            );
          },
        ),
        (
          'comment report',
          'POST',
          '/comments/comment-1/report',
          {'violationTypeId': 'spam', 'description': 'report'},
          (a) async {
            await ModerationService(a).reportComment(
              commentId: 'comment-1',
              violationTypeId: 'spam',
              description: 'report',
            );
          },
        ),
        (
          'channel report trims description and encodes channel id',
          'POST',
          '/channels/channel%2F1/report',
          {'violationTypeId': 'spam', 'description': 'details'},
          (a) async {
            await ModerationService(a).reportChannel(
              channelId: 'channel/1',
              violationTypeId: 'spam',
              description: ' details ',
            );
          },
        ),
        (
          'generic report uses the moderation report endpoint',
          'POST',
          '/reports',
          {
            'targetType': 'playlist',
            'targetId': 'list-1',
            'violationTypeId': 'spam',
            'description': 'description',
          },
          (a) async {
            await ModerationService(a).createReport(
              targetType: 'playlist',
              targetId: 'list-1',
              violationTypeId: 'spam',
              description: ' description ',
            );
          },
        ),
        (
          'appeal history decodes list responses',
          'GET',
          '/appeals/my',
          null,
          (a) async {
            await ModerationService(a).myAppeals();
          },
        ),
        (
          'appeal submission trims reason and evidence',
          'POST',
          '/appeals',
          {
            'targetType': 'video',
            'targetId': 'video-1',
            'reason': 'Reason',
            'evidenceUrl': 'https://example.test/proof',
            'evidenceNote': 'Evidence',
            'moderationCaseId': 'case-1',
            'strikeId': 'strike-1',
          },
          (a) async {
            await ModerationService(a).createAppeal(
              targetType: 'video',
              targetId: 'video-1',
              reason: ' Reason ',
              evidenceUrl: ' https://example.test/proof ',
              evidenceNote: ' Evidence ',
              moderationCaseId: 'case-1',
              strikeId: 'strike-1',
            );
          },
        ),
        (
          'channel strikes use an encoded channel identifier',
          'GET',
          '/channels/channel%2F1/strikes',
          null,
          (a) async {
            await ModerationService(a).channelStrikes('channel/1');
          },
        ),
        (
          'playlist creation trims metadata',
          'POST',
          '/playlists',
          {
            'name': 'My playlist',
            'description': 'Notes',
            'visibility': 'private',
          },
          (a) async {
            await PlaylistService(
              a,
            ).create(name: ' My playlist ', description: ' Notes ');
          },
        ),
        (
          'playlist delete',
          'DELETE',
          '/playlists/list-1',
          null,
          (a) async {
            await PlaylistService(a).delete('list-1');
          },
        ),
      ];
  for (final scenario in scenarios) {
    for (final fail in [false, true]) {
      test(
        '${scenario.$1}: ${fail ? 'server failure surfaces' : 'authenticated contract'}',
        () async {
          var calls = 0;
          final client = MockClient((request) async {
            if (request.url.path.endsWith('/auth/refresh')) {
              return jsonResponse(session());
            }
            calls++;
            expect(request.method, scenario.$2);
            expect(request.url.path, '/api/v1${scenario.$3}');
            final publicRequest =
                scenario.$1 == 'violation types load from the public catalogue'
                ? true
                : false;
            if (!publicRequest) {
              expect(request.headers['Authorization'], startsWith('Bearer '));
            }
            if (scenario.$4 != null) {
              expect(jsonDecode(request.body), scenario.$4);
            }
            if (fail) return jsonResponse({'code': 'FAILED'}, 503);
            if (request.url.path.endsWith('/payments') ||
                request.url.path.endsWith('/appeals/my') ||
                request.url.path.endsWith('/violation-types')) {
              return http.Response(
                '[]',
                200,
                headers: {'content-type': 'application/json'},
              );
            }
            return jsonResponse({});
          });
          final auth = AuthController(
            ApiClient(baseUrl: 'https://mobile.test/api/v1', client: client),
            MemoryStore('refresh'),
          );
          await auth.restore();
          if (fail) {
            await expectLater(
              scenario.$5(auth),
              throwsA(isA<ApiFailure>().having((e) => e.status, 'status', 503)),
            );
          } else {
            await scenario.$5(auth);
          }
          expect(calls, 1, reason: 'Writes must not be retried automatically');
          auth.dispose();
          client.close();
        },
      );
    }
  }
}
