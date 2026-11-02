import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/core/localization/app_strings.dart';
import 'package:user_app/features/moderation/moderation_screen.dart';
import 'package:user_app/features/moderation/report_dialog.dart';
import 'package:user_app/features/policies/policy_catalog.dart';
import 'package:user_app/features/policies/policies_screen.dart';
import 'package:user_app/plans_screen.dart';

import 'auth_test.dart' show MemoryStore, jsonResponse, session;

const _base = 'https://mobile.test/api/v1';

http.Response _array(List<dynamic> values, [int status = 200]) => http.Response(
  jsonEncode(values),
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

Map<String, dynamic> _freePlan({String id = 'free-plan'}) => {
  'planId': id,
  'code': id,
  'name': 'Free plan',
  'description': 'Starter plan',
  'price': 0,
  'currency': 'VND',
  'durationDays': 30,
  'storageLimit': 1073741824,
  'maxMembers': 1,
  'features': {'download': false},
};

Map<String, dynamic> _policy(String group, String name) => {
  'policyId': 'p-$group',
  'group': group,
  'name': name,
  'version': '1.0',
  'effectiveAt': '2026-01-01T00:00:00Z',
  'description': 'Text for $name',
};

void main() {
  setUp(ApiClient.invalidateReadCache);

  group('Plan purchase and family plan workflows', () {
    testWidgets('free subscription sends the selected plan and reloads state', (
      tester,
    ) async {
      Map<String, dynamic>? subscriptionBody;
      var catalogueReads = 0;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/plans') && request.method == 'GET') {
          catalogueReads++;
          return _array([_freePlan()]);
        }
        if (request.url.path.endsWith('/plans/my-plan')) {
          return jsonResponse({});
        }
        if (request.url.path.endsWith('/payments')) return _array([]);
        if (request.url.path.endsWith('/plans/free-plan/subscribe')) {
          subscriptionBody = jsonDecode(request.body) as Map<String, dynamic>;
          return jsonResponse({'status': 'active'});
        }
        return jsonResponse({});
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(body: PlansScreen(auth: auth)),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Free plan'), findsOneWidget);
      await tester.tap(find.text(AppStrings.t('plans.subscribe')));
      await tester.pumpAndSettle();
      expect(subscriptionBody, {'autoRenew': false});
      expect(catalogueReads, 2);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    testWidgets(
      'paid plan polls the payment and enables completion after success',
      (tester) async {
        final methods = <String>[];
        var paymentPolls = 0;
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          methods.add('${request.method} ${request.url.path}');
          if (request.url.path.endsWith('/plans')) {
            return _array([
              {
                ..._freePlan(id: 'paid-plan'),
                'name': 'Creator Plus',
                'price': 29000,
              },
            ]);
          }
          if (request.url.path.endsWith('/plans/my-plan')) {
            return jsonResponse({});
          }
          if (request.url.path.endsWith('/payments') &&
              request.method == 'GET') {
            return _array([]);
          }
          if (request.url.path.endsWith('/payments') &&
              request.method == 'POST') {
            return jsonResponse({
              'paymentId': 'pay-1',
              'planName': 'Creator Plus',
              'amount': 29000,
              'currency': 'VND',
              'transactionCode': 'PAY-001',
              'status': 'pending',
            });
          }
          if (request.url.path.endsWith('/payments/pay-1')) {
            paymentPolls++;
            return jsonResponse({
              'paymentId': 'pay-1',
              'planName': 'Creator Plus',
              'amount': 29000,
              'currency': 'VND',
              'transactionCode': 'PAY-001',
              'status': 'completed',
            });
          }
          return jsonResponse({});
        });
        final auth = await _auth(client);
        await tester.pumpWidget(
          MaterialApp(
            home: Scaffold(body: PlansScreen(auth: auth)),
          ),
        );
        await tester.pumpAndSettle();
        await tester.tap(find.text(AppStrings.t('plans.subscribe')));
        await tester.pumpAndSettle();
        expect(find.text('PAY-001'), findsOneWidget);
        await tester.tap(find.text(AppStrings.t('plans.checkPayment')));
        await tester.pumpAndSettle();
        expect(paymentPolls, 1);
        expect(find.text(AppStrings.t('plans.activated')), findsOneWidget);
        await tester.tap(find.text(AppStrings.t('plans.paymentDone')));
        await tester.pumpAndSettle();
        expect(methods, contains('POST /api/v1/payments'));
        expect(methods, contains('GET /api/v1/payments/pay-1'));
        await tester.pumpWidget(const SizedBox.shrink());
        auth.dispose();
        client.close();
      },
    );

    testWidgets('paid member can cancel a pending payment after confirming', (
      tester,
    ) async {
      var cancellationCount = 0;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/plans')) {
          return _array([
            {..._freePlan(id: 'paid-plan'), 'name': 'Pro', 'price': 10000},
          ]);
        }
        if (request.url.path.endsWith('/plans/my-plan')) {
          return jsonResponse({});
        }
        if (request.url.path.endsWith('/payments') && request.method == 'GET') {
          return _array([]);
        }
        if (request.url.path.endsWith('/payments') &&
            request.method == 'POST') {
          return jsonResponse({'paymentId': 'pay-2', 'status': 'pending'});
        }
        if (request.url.path.endsWith('/payments/pay-2/cancel')) {
          cancellationCount++;
          return jsonResponse({});
        }
        return jsonResponse({});
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(body: PlansScreen(auth: auth)),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text(AppStrings.t('plans.subscribe')));
      await tester.pumpAndSettle();
      await tester.tap(find.text(AppStrings.t('plans.paymentCancel')));
      await tester.pumpAndSettle();
      await tester.tap(find.text(AppStrings.t('common.cancel')).last);
      await tester.pumpAndSettle();
      expect(cancellationCount, 0);
      await tester.tap(find.text(AppStrings.t('plans.paymentCancel')));
      await tester.pumpAndSettle();
      await tester.tap(find.text(AppStrings.t('plans.paymentCancel')).last);
      await tester.pumpAndSettle();
      expect(cancellationCount, 1);
      expect(find.text(AppStrings.t('plans.paymentExpired')), findsOneWidget);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    testWidgets(
      'private plan and payment history errors do not hide the catalogue',
      (tester) async {
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          if (request.url.path.endsWith('/plans')) return _array([_freePlan()]);
          if (request.url.path.endsWith('/plans/my-plan') ||
              request.url.path.endsWith('/payments')) {
            return jsonResponse({'code': 'TEMPORARY'}, 503);
          }
          return jsonResponse({});
        });
        final auth = await _auth(client);
        await tester.pumpWidget(
          MaterialApp(
            home: Scaffold(body: PlansScreen(auth: auth)),
          ),
        );
        await tester.pumpAndSettle();
        expect(find.text('Free plan'), findsOneWidget);
        expect(find.byType(CircularProgressIndicator), findsNothing);
        await tester.pumpWidget(const SizedBox.shrink());
        auth.dispose();
        client.close();
      },
    );

    testWidgets(
      'catalogue failure exposes retry and successful retry renders plans',
      (tester) async {
        var failed = false;
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          if (request.url.path.endsWith('/plans') && !failed) {
            failed = true;
            return jsonResponse({'code': 'TEMPORARY'}, 503);
          }
          if (request.url.path.endsWith('/plans')) return _array([_freePlan()]);
          if (request.url.path.endsWith('/plans/my-plan')) {
            return jsonResponse({});
          }
          if (request.url.path.endsWith('/payments')) return _array([]);
          return jsonResponse({});
        });
        final auth = await _auth(client);
        await tester.pumpWidget(
          MaterialApp(
            home: Scaffold(body: PlansScreen(auth: auth)),
          ),
        );
        await tester.pumpAndSettle();
        expect(find.text('Free plan'), findsNothing);
        await tester.tap(find.text(AppStrings.t('common.retry')));
        await tester.pumpAndSettle();
        expect(find.text('Free plan'), findsOneWidget);
        await tester.pumpWidget(const SizedBox.shrink());
        auth.dispose();
        client.close();
      },
    );

    for (final invitation in [
      (id: 'member-a', token: 'token-a', paid: false),
      (id: 'member-b', token: 'token-b', paid: true),
    ]) {
      testWidgets(
        'invitation ${invitation.id} submits the correct consent flag',
        (tester) async {
          final accepts = <Map<String, dynamic>>[];
          final client = MockClient((request) async {
            if (request.url.path.endsWith('/auth/refresh')) {
              return jsonResponse(session());
            }
            if (request.url.path.endsWith('/plans')) {
              return _array([_freePlan()]);
            }
            if (request.url.path.endsWith('/plans/my-plan')) {
              return jsonResponse({});
            }
            if (request.url.path.endsWith('/payments')) return _array([]);
            if (request.url.path.endsWith('/members/${invitation.id}/accept')) {
              final body = jsonDecode(request.body) as Map<String, dynamic>;
              accepts.add(body);
              if (invitation.paid && accepts.length == 1) {
                return jsonResponse({
                  'code': 'PAID_PLAN_CONFIRMATION_REQUIRED',
                  'message': 'Joining replaces your paid plan.',
                }, 409);
              }
              return jsonResponse({});
            }
            return jsonResponse({});
          });
          final auth = await _auth(client);
          await tester.pumpWidget(
            MaterialApp(
              home: Scaffold(
                body: PlansScreen(
                  auth: auth,
                  invitationId: invitation.id,
                  invitationToken: invitation.token,
                ),
              ),
            ),
          );
          await tester.pumpAndSettle();
          if (invitation.paid) {
            await tester.tap(find.text(AppStrings.t('plans.acceptInvite')));
            await tester.pumpAndSettle();
          }
          expect(accepts, hasLength(invitation.paid ? 2 : 1));
          expect(accepts.first['token'], invitation.token);
          expect(accepts.first['confirmCancelPaidPlan'], isFalse);
          if (invitation.paid) {
            expect(accepts.last['confirmCancelPaidPlan'], isTrue);
          }
          await tester.pumpWidget(const SizedBox.shrink());
          auth.dispose();
          client.close();
        },
      );
    }

    testWidgets('invite quota over remaining storage disables submit', (
      tester,
    ) async {
      tester.view.physicalSize = const Size(800, 1200);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/plans')) return _array([_freePlan()]);
        if (request.url.path.endsWith('/plans/my-plan')) {
          return jsonResponse({
            'planId': 'family',
            'name': 'Family',
            'maxMembers': 4,
            'storageLimit': 4 * 1024 * 1024 * 1024,
            'ownerAllocatedStorage': 2 * 1024 * 1024 * 1024,
            'members': [],
          });
        }
        if (request.url.path.endsWith('/payments')) return _array([]);
        return jsonResponse({});
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(body: PlansScreen(auth: auth)),
        ),
      );
      await tester.pumpAndSettle();
      await tester.ensureVisible(find.text(AppStrings.t('plans.invite')));
      await tester.tap(find.text(AppStrings.t('plans.invite')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField).last, '3');
      await tester.pumpAndSettle();
      final send = tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, AppStrings.t('common.send')),
      );
      expect(send.onPressed, isNull);
      await tester.tap(find.text(AppStrings.t('common.cancel')));
      await tester.pumpAndSettle();
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    testWidgets(
      'valid family invitation trims email and converts GB to bytes',
      (tester) async {
        Map<String, dynamic>? invitationBody;
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          if (request.url.path.endsWith('/plans')) return _array([_freePlan()]);
          if (request.url.path.endsWith('/plans/my-plan')) {
            return jsonResponse({
              'planId': 'family',
              'name': 'Family',
              'maxMembers': 4,
              'storageLimit': 10 * 1024 * 1024 * 1024,
              'ownerAllocatedStorage': 2 * 1024 * 1024 * 1024,
              'members': [],
            });
          }
          if (request.url.path.endsWith('/payments')) return _array([]);
          if (request.url.path.endsWith('/plans/members/invite')) {
            invitationBody = jsonDecode(request.body) as Map<String, dynamic>;
            return jsonResponse({});
          }
          return jsonResponse({});
        });
        final auth = await _auth(client);
        await tester.pumpWidget(
          MaterialApp(
            home: Scaffold(body: PlansScreen(auth: auth)),
          ),
        );
        await tester.pumpAndSettle();
        await tester.drag(find.byType(ListView).last, const Offset(0, -500));
        await tester.pumpAndSettle();
        await tester.tap(find.text(AppStrings.t('plans.invite')));
        await tester.pumpAndSettle();
        await tester.enterText(
          find.byType(TextField).first,
          ' member@example.test ',
        );
        await tester.enterText(find.byType(TextField).last, '2');
        await tester.tap(find.text(AppStrings.t('common.send')));
        await tester.pumpAndSettle();
        expect(invitationBody, {
          'email': 'member@example.test',
          'allocatedStorage': 2 * 1024 * 1024 * 1024,
        });
        await tester.pumpWidget(const SizedBox.shrink());
        auth.dispose();
        client.close();
      },
    );
  });

  group('Content report dialog', () {
    testWidgets('guest cannot load report reasons or submit a report', (
      tester,
    ) async {
      var requests = 0;
      final client = MockClient((_) async {
        requests++;
        return _array([]);
      });
      final auth = AuthController(
        ApiClient(baseUrl: _base, client: client),
        MemoryStore(),
      );
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: Builder(
              builder: (context) => TextButton(
                onPressed: () => showContentReportDialog(
                  context,
                  auth: auth,
                  targetType: 'video',
                  targetId: 'v1',
                ),
                child: const Text('Report'),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('Report'));
      await tester.pumpAndSettle();
      expect(requests, 0);
      expect(find.text(AppStrings.t('report.loginRequired')), findsOneWidget);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    testWidgets('empty reason catalogue stops before showing the form', (
      tester,
    ) async {
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        return _array([]);
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: Builder(
              builder: (context) => TextButton(
                onPressed: () => showContentReportDialog(
                  context,
                  auth: auth,
                  targetType: 'video',
                  targetId: 'v1',
                ),
                child: const Text('Report'),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('Report'));
      await tester.pumpAndSettle();
      expect(find.byType(DropdownButtonFormField<String>), findsNothing);
      expect(find.text(AppStrings.t('report.noReasons')), findsOneWidget);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    for (final target in [
      (type: 'video', id: 'v-1', suffix: '/videos/v-1/report'),
      (type: 'channel', id: 'c-1', suffix: '/channels/c-1/report'),
      (type: 'comment', id: 'cm-1', suffix: '/comments/cm-1/report'),
      (type: 'playlist', id: 'pl-1', suffix: '/reports'),
    ]) {
      testWidgets('submits a trimmed ${target.type} report to its endpoint', (
        tester,
      ) async {
        http.Request? report;
        final client = MockClient((request) async {
          if (request.url.path.endsWith('/auth/refresh')) {
            return jsonResponse(session());
          }
          if (request.url.path.endsWith('/violation-types')) {
            return _array([
              {'violationTypeId': 'spam', 'name': 'Spam'},
            ]);
          }
          report = request;
          return jsonResponse({'reportId': 'r1'});
        });
        final auth = await _auth(client);
        await tester.pumpWidget(
          MaterialApp(
            home: Scaffold(
              body: Builder(
                builder: (context) => TextButton(
                  onPressed: () => showContentReportDialog(
                    context,
                    auth: auth,
                    targetType: target.type,
                    targetId: target.id,
                  ),
                  child: const Text('Report'),
                ),
              ),
            ),
          ),
        );
        await tester.tap(find.text('Report'));
        await tester.pumpAndSettle();
        expect(
          tester
              .widget<FilledButton>(
                find.widgetWithText(
                  FilledButton,
                  AppStrings.t('report.submit'),
                ),
              )
              .onPressed,
          isNull,
        );
        await tester.enterText(find.byType(TextField), '  details  ');
        await tester.pumpAndSettle();
        await tester.tap(find.text(AppStrings.t('report.submit')));
        await tester.pumpAndSettle();
        expect(report!.method, 'POST');
        expect(report!.url.path, '/api/v1${target.suffix}');
        final body = jsonDecode(report!.body) as Map<String, dynamic>;
        expect(body['violationTypeId'], 'spam');
        expect(body['description'], 'details');
        if (target.type == 'playlist') {
          expect(body, containsPair('targetType', 'playlist'));
          expect(body, containsPair('targetId', 'pl-1'));
        }
        await tester.pumpWidget(const SizedBox.shrink());
        auth.dispose();
        client.close();
      });
    }

    testWidgets('canceling a report does not create it', (tester) async {
      var writes = 0;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/violation-types')) {
          return _array([
            {'violationTypeId': 'spam', 'name': 'Spam'},
          ]);
        }
        writes++;
        return jsonResponse({});
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: Builder(
              builder: (context) => TextButton(
                onPressed: () => showContentReportDialog(
                  context,
                  auth: auth,
                  targetType: 'video',
                  targetId: 'v1',
                ),
                child: const Text('Report'),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('Report'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField), 'reason');
      await tester.tap(find.byTooltip(AppStrings.t('common.close')));
      await tester.pumpAndSettle();
      expect(writes, 0);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });
  });

  group('Moderation and appeal screen', () {
    testWidgets('renders appeal decision and channel strike information', (
      tester,
    ) async {
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/appeals/my')) {
          return _array([
            {
              'appealId': 'a1',
              'targetType': 'video',
              'targetTitle': 'My appeal',
              'reason': 'Context',
              'status': 'rejected',
              'reviewNote': 'Please add more evidence.',
              'createdAt': '2026-01-01T00:00:00Z',
            },
          ]);
        }
        if (request.url.path.endsWith('/channels/me')) {
          return jsonResponse({
            'channelId': 'c1',
            'name': 'Creator',
            'handle': 'creator',
          });
        }
        if (request.url.path.endsWith('/channels/c1/strikes')) {
          return jsonResponse({
            'isSuspended': true,
            'activeStrikesCount': 2,
            'strikes': [
              {
                'reason': 'Repeated spam',
                'severity': 'high',
                'createdAt': '2026-01-01T00:00:00Z',
              },
            ],
          });
        }
        return jsonResponse({});
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(body: ModerationScreen(auth: auth)),
        ),
      );
      await tester.pumpAndSettle();
      await tester.drag(find.byType(ListView), const Offset(0, -500));
      await tester.pumpAndSettle();
      expect(find.text('My appeal'), findsOneWidget);
      expect(find.textContaining('Please add more evidence.'), findsOneWidget);
      expect(find.textContaining('Repeated spam'), findsOneWidget);
      expect(find.text(AppStrings.t('channel.suspendedTitle')), findsOneWidget);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    testWidgets('appeal form requires both target and reason before posting', (
      tester,
    ) async {
      var writes = 0;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/appeals/my')) return _array([]);
        if (request.url.path.endsWith('/channels/me')) {
          return jsonResponse({}, 404);
        }
        writes++;
        return jsonResponse({});
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(body: ModerationScreen(auth: auth)),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text(AppStrings.t('moderation.sendAppeal')).first);
      await tester.pumpAndSettle();
      final send = find.text(AppStrings.t('moderation.sendAppeal')).last;
      await tester.tap(send);
      await tester.pumpAndSettle();
      expect(writes, 0);
      await tester.enterText(
        find.byKey(const ValueKey('appeal-target')),
        'video-1',
      );
      await tester.tap(send);
      await tester.pumpAndSettle();
      expect(writes, 0);
      await tester.tap(find.byTooltip(AppStrings.t('common.close')));
      await tester.pumpAndSettle();
      expect(writes, 0);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    testWidgets('submits a trimmed appeal and refreshes the list', (
      tester,
    ) async {
      Map<String, dynamic>? appealBody;
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/auth/refresh')) {
          return jsonResponse(session());
        }
        if (request.url.path.endsWith('/appeals/my')) return _array([]);
        if (request.url.path.endsWith('/channels/me')) {
          return jsonResponse({}, 404);
        }
        if (request.url.path.endsWith('/appeals')) {
          appealBody = jsonDecode(request.body) as Map<String, dynamic>;
          return jsonResponse({'appealId': 'a1'});
        }
        return jsonResponse({});
      });
      final auth = await _auth(client);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(body: ModerationScreen(auth: auth)),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text(AppStrings.t('moderation.sendAppeal')).first);
      await tester.pumpAndSettle();
      await tester.enterText(
        find.byKey(const ValueKey('appeal-target')),
        ' video-9 ',
      );
      await tester.enterText(
        find.byKey(const ValueKey('appeal-reason')),
        '  More context  ',
      );
      await tester.enterText(find.byType(TextFormField).last, ' screenshot ');
      await tester.tap(find.text(AppStrings.t('moderation.sendAppeal')).last);
      await tester.pumpAndSettle();
      expect(appealBody, {
        'targetType': 'video',
        'targetId': 'video-9',
        'reason': 'More context',
        'evidenceUrl': null,
        'evidenceNote': 'screenshot',
        'moderationCaseId': null,
        'strikeId': null,
      });
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });
  });

  group('Public policies screen', () {
    testWidgets('guidelines group includes legacy content policies only', (
      tester,
    ) async {
      final client = MockClient((request) async {
        if (request.url.path.endsWith('/policies')) {
          return _array([
            _policy('guidelines', 'Community guide'),
            _policy('content', 'Content guide'),
            _policy('privacy', 'Privacy notice'),
            _policy('terms', 'Terms of service'),
          ]);
        }
        return jsonResponse({});
      });
      final auth = AuthController(
        ApiClient(baseUrl: _base, client: client),
        MemoryStore(),
      );
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: PoliciesScreen(auth: auth, initialGroup: 'guidelines'),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.drag(find.byType(ListView), const Offset(0, -400));
      await tester.pumpAndSettle();
      expect(find.text('Community guide'), findsOneWidget);
      expect(find.text('Content guide'), findsOneWidget);
      expect(find.text('Privacy notice'), findsNothing);
      expect(find.text('Terms of service'), findsNothing);
      await tester.drag(find.byType(ListView), const Offset(0, 500));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('policy-group-guidelines')));
      await tester.pumpAndSettle();
      await tester.tap(
        find.textContaining(policyLabel('policies.privacy')).last,
      );
      await tester.pumpAndSettle();
      await tester.drag(find.byType(ListView), const Offset(0, -400));
      await tester.pumpAndSettle();
      expect(find.text('Privacy notice'), findsOneWidget);
      expect(find.text('Community guide'), findsNothing);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    testWidgets('empty policy response provides a useful fallback', (
      tester,
    ) async {
      final client = MockClient((_) async => _array([]));
      final auth = AuthController(
        ApiClient(baseUrl: _base, client: client),
        MemoryStore(),
      );
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: PoliciesScreen(auth: auth, initialGroup: 'guidelines'),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.drag(find.byType(ListView), const Offset(0, -400));
      await tester.pumpAndSettle();
      expect(find.byType(CircularProgressIndicator), findsNothing);
      expect(find.text('CHILD_SAFETY'), findsOneWidget);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    testWidgets('policy fetch failure falls back to readable policy guidance', (
      tester,
    ) async {
      final client = MockClient(
        (_) async => jsonResponse({'code': 'UNAVAILABLE'}, 503),
      );
      final auth = AuthController(
        ApiClient(baseUrl: _base, client: client),
        MemoryStore(),
      );
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(body: PoliciesScreen(auth: auth)),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.byType(CircularProgressIndicator), findsNothing);
      expect(find.text(policyLabel('policies.loadError')), findsOneWidget);
      await tester.drag(find.byType(ListView), const Offset(0, -400));
      await tester.pumpAndSettle();
      expect(find.text('CHILD_SAFETY'), findsOneWidget);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });

    testWidgets('unsupported device export reports an actionable message', (
      tester,
    ) async {
      final client = MockClient(
        (_) async => _array([_policy('guidelines', 'Rules')]),
      );
      final auth = AuthController(
        ApiClient(baseUrl: _base, client: client),
        MemoryStore(),
      );
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(body: PoliciesScreen(auth: auth)),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.byType(PopupMenuButton<bool>));
      await tester.pumpAndSettle();
      await tester.tap(find.text(AppStrings.t('parity.print')));
      await tester.pumpAndSettle();
      expect(find.text(AppStrings.t('parity.exportError')), findsOneWidget);
      await tester.pumpWidget(const SizedBox.shrink());
      auth.dispose();
      client.close();
    });
  });
}
