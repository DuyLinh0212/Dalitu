import '../channel/screens/create_channel_screen.dart';
import '../features/creator/upload_controller.dart';
import '../core/deep_links/content_link.dart';
import 'dart:async';

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../auth.dart';
import '../core/localization/app_strings.dart';
import '../core/network/network_status.dart';
import '../core/theme/app_theme.dart';
import '../core/theme/theme_notifier.dart';
import '../core/widgets/app_shell.dart';
import '../core/widgets/network_fallback_overlay.dart';
import '../features/account/account_hub_screen.dart';
import '../features/account/account_settings_screen.dart';
import '../features/content/home_screen.dart';
import '../features/content/explore_screen.dart';
import '../features/content/downloads_screen.dart';
import '../features/content/library_screen.dart';
import '../features/content/playback_session.dart';
import '../features/content/download_coordinator.dart';
import '../features/content/watch_screen.dart';
import '../features/moderation/moderation_screen.dart';
import '../channel/screens/channel_invitations_screen.dart';
import '../channel/screens/channel_screen.dart';
import '../features/creator/creator_hub_screen.dart';
import '../features/feedback/feedback_survey_host.dart';
import '../features/notifications/notifications_screen.dart';
import '../features/notifications/notification_center.dart';
import '../features/playlists/playlists_screen.dart';
import '../features/policies/policies_screen.dart';
import '../features/plans/mobile_plans_screen.dart';
import '../features/search/search_screen.dart';
import '../features/subscriptions/subscriptions_screen.dart';
import 'mobile_scaffold.dart';

class HuTubeApp extends StatefulWidget {
  const HuTubeApp({super.key, required this.auth, this.links});

  final AuthController auth;
  final Stream<Uri>? links;

  @override
  State<HuTubeApp> createState() => _HuTubeAppState();
}

class _HuTubeAppState extends State<HuTubeApp> {
  late final GoRouter _router;
  late final UploadController _uploads;
  final _navigatorKey = GlobalKey<NavigatorState>();
  final PlaybackSession _playback = PlaybackSession();
  late final DownloadCoordinator _downloads;
  late final NotificationCenter _notifications;
  StreamSubscription<Uri>? _linkSubscription;
  Timer? _networkRetryTimer;
  String? _pendingNotificationPath;

  @override
  void initState() {
    super.initState();
    _uploads = UploadController(widget.auth);
    _downloads = DownloadCoordinator(widget.auth);
    unawaited(_downloads.initialize());
    _notifications = NotificationCenter(widget.auth);
    _router = _buildRouter(widget.auth, _playback, _notifications);
    unawaited(_notifications.initialize(onOpenNotification: _openNotification));
    _linkSubscription = widget.links?.listen(_openDeepLink);
    _networkRetryTimer = Timer.periodic(const Duration(seconds: 20), (_) {
      if (NetworkStatus.instance.unavailable) {
        unawaited(_checkNetworkAndRestoreSession());
      } else if (widget.auth.hasStoredSession &&
          !widget.auth.authenticated &&
          !widget.auth.restoring) {
        // A transient startup failure must not turn into a permanent signed
        // out state while the app remains open.
        unawaited(widget.auth.restore());
      }
    });
    unawaited(widget.auth.restore());
  }

  Future<void> _checkNetworkAndRestoreSession() async {
    final available = await widget.auth.api.checkConnection();
    if (available &&
        widget.auth.hasStoredSession &&
        !widget.auth.authenticated &&
        !widget.auth.restoring) {
      unawaited(widget.auth.restore());
    }
  }

  GoRouter _buildRouter(
    AuthController auth,
    PlaybackSession playback,
    NotificationCenter notifications,
  ) => GoRouter(
    navigatorKey: _navigatorKey,
    initialLocation: '/splash',
    refreshListenable: auth,
    redirect: (context, state) {
      final path = state.uri.path;
      if (auth.restoring) return path == '/splash' ? null : '/splash';
      // Start signed-out users on the public feed. Signing in remains an
      // explicit action from the account button or a protected destination.
      if (path == '/splash') {
        if (auth.authenticated && _pendingNotificationPath != null) {
          final target = _pendingNotificationPath!;
          _pendingNotificationPath = null;
          return target;
        }
        return '/home';
      }
      // Keep legacy account deep links on the auth shell long enough for its
      // session-management view to complete. Ordinary successful sign-in
      // still enters the new mobile home route.
      if (auth.authenticated && path == '/auth') {
        if (_pendingNotificationPath != null) {
          final target = _pendingNotificationPath!;
          _pendingNotificationPath = null;
          return target;
        }
        return state.uri.queryParameters['step'] == '/account' ? null : '/home';
      }
      const protected = <String>{
        '/library',
        '/history',
        '/liked',
        '/account',
        '/notifications',
        '/creator',
        '/create-channel',
        '/downloads',
        '/subscriptions',
        '/moderation',
        '/channel-invitations',
      };
      final needsAuth =
          path == '/playlists' ||
          protected.any((item) => path == item || path.startsWith('$item/'));
      return !auth.authenticated && needsAuth ? '/auth' : null;
    },
    routes: [
      GoRoute(path: '/splash', builder: (_, _) => const _SplashScreen()),
      GoRoute(
        path: '/auth',
        builder: (_, state) => AppShell(
          auth: auth,
          notifications: notifications,
          initialPage: state.uri.queryParameters['step'],
          initialToken: state.uri.queryParameters['token'],
        ),
      ),
      ShellRoute(
        builder: (context, state, child) => MobileScaffold(
          auth: auth,
          playback: playback,
          notifications: notifications,
          location: state.uri.path,
          child: child,
        ),
        routes: [
          GoRoute(
            path: '/home',
            builder: (_, _) => HomeScreen(auth: auth),
          ),
          GoRoute(
            path: '/explore',
            builder: (_, _) => ExploreScreen(auth: auth),
          ),
          GoRoute(
            path: '/subscriptions',
            builder: (_, _) => SubscriptionsScreen(auth: auth),
          ),
          GoRoute(
            path: '/search',
            builder: (_, state) => SearchScreen(
              auth: auth,
              initialQuery: state.uri.queryParameters['q'] ?? '',
              initialTag: state.uri.queryParameters['tag'],
              initialCategoryId: state.uri.queryParameters['categoryId'],
            ),
          ),
          GoRoute(
            path: '/playlists',
            builder: (_, state) => PlaylistsScreen(
              auth: auth,
              openCreate: state.uri.queryParameters['create'] == 'true',
            ),
          ),
          GoRoute(
            path: '/playlists/:playlistId',
            builder: (_, state) => PlaylistsScreen(
              auth: auth,
              playlistId: state.pathParameters['playlistId'],
            ),
          ),
          GoRoute(
            path: '/channels/:handle/playlists/:playlistId',
            builder: (_, state) => PlaylistsScreen(
              auth: auth,
              playlistId: state.pathParameters['playlistId'],
            ),
          ),
          GoRoute(
            path: '/channels/:handle',
            builder: (_, state) => ChannelScreen(
              auth: auth,
              channelOrHandle: state.pathParameters['handle']!,
            ),
          ),
          GoRoute(
            path: '/moderation',
            builder: (_, _) => ModerationScreen(auth: auth),
          ),
          GoRoute(
            path: '/channel-invitations',
            builder: (_, _) => ChannelInvitationsScreen(auth: auth),
          ),
          GoRoute(
            path: '/watch/:videoId',
            builder: (_, state) => WatchScreen(
              auth: auth,
              playback: playback,
              videoId: state.pathParameters['videoId']!,
              playlistId: state.uri.queryParameters['playlist'],
              playlistIndex: int.tryParse(
                state.uri.queryParameters['index'] ?? '',
              ),
              playlistShuffle: state.uri.queryParameters['shuffle'] == 'true',
              restartFromBeginning:
                  state.uri.queryParameters['autoplay'] == 'true',
            ),
          ),
          GoRoute(
            path: '/library',
            builder: (_, _) => LibraryScreen(auth: auth),
          ),
          GoRoute(
            path: '/history',
            builder: (_, _) => LibraryScreen(auth: auth, initialTab: 0),
          ),
          GoRoute(
            path: '/liked',
            builder: (_, _) => LibraryScreen(auth: auth, initialTab: 1),
          ),
          GoRoute(
            path: '/downloads',
            builder: (_, _) =>
                DownloadsScreen(auth: auth, coordinator: _downloads),
          ),
          GoRoute(
            path: '/account',
            builder: (_, _) =>
                AccountHubScreen(auth: auth, notifications: notifications),
          ),
          GoRoute(
            path: '/settings',
            builder: (_, _) =>
                AccountSettingsScreen(auth: auth, notifications: notifications),
          ),
          GoRoute(
            path: '/create-channel',
            builder: (_, _) => CreateChannelScreen(auth: auth),
          ),
          GoRoute(
            path: '/creator',
            builder: (_, state) => CreatorHubScreen(
              auth: auth,
              openUpload: state.uri.queryParameters['upload'] == 'true',
            ),
          ),
          GoRoute(
            path: '/notifications',
            builder: (_, _) =>
                NotificationsScreen(auth: auth, notifications: notifications),
          ),
          GoRoute(
            path: '/plans',
            builder: (_, state) => MobilePlansScreen(
              auth: auth,
              invitationId: state.uri.queryParameters['invitationId'],
              invitationToken:
                  state.uri.queryParameters['invitationToken'] ??
                  state.uri.queryParameters['token'],
              initialPlanId: state.uri.queryParameters['planId'],
            ),
          ),
          GoRoute(
            path: '/plans/accept-invite',
            builder: (_, state) => MobilePlansScreen(
              auth: auth,
              invitationId: state.uri.queryParameters['invitationId'],
              invitationToken:
                  state.uri.queryParameters['invitationToken'] ??
                  state.uri.queryParameters['token'],
            ),
          ),
          GoRoute(
            path: '/policies',
            builder: (_, state) => PoliciesScreen(
              auth: auth,
              initialGroup: state.uri.queryParameters['group'],
            ),
          ),
        ],
      ),
    ],
  );

  void _openNotification(Map<String, dynamic> data) {
    final target =
        _safeNotificationTarget(data['actionUrl'] as String?) ??
        _fallbackNotificationTarget(data);
    const protected = [
      '/account',
      '/creator',
      '/downloads',
      '/library',
      '/history',
      '/liked',
      '/moderation',
      '/notifications',
      '/playlists',
      '/subscriptions',
      '/channel-invitations',
      '/settings',
    ];
    if (!widget.auth.authenticated &&
        protected.any(
          (path) => target == path || target.startsWith('$path/'),
        )) {
      _pendingNotificationPath = target;
      _router.go('/auth');
      return;
    }
    _router.go(target);
  }

  String? _safeNotificationTarget(String? actionUrl) {
    if (actionUrl == null ||
        !actionUrl.startsWith('/') ||
        actionUrl.startsWith('//')) {
      return null;
    }
    final uri = Uri.tryParse(actionUrl);
    if (uri == null || uri.hasAuthority || uri.pathSegments.contains('..')) {
      return null;
    }
    const supported = {
      '/home',
      '/explore',
      '/subscriptions',
      '/search',
      '/playlists',
      '/moderation',
      '/channel-invitations',
      '/account',
      '/creator',
      '/downloads',
      '/library',
      '/history',
      '/liked',
      '/notifications',
      '/plans',
      '/policies',
    };
    final pathSegments = uri.pathSegments;
    final isSupportedDynamicRoute =
        pathSegments.length == 2 &&
        const {'watch', 'channels', 'playlists'}.contains(pathSegments.first);
    if (!supported.contains(uri.path) && !isSupportedDynamicRoute) {
      return null;
    }
    return uri.toString();
  }

  String _fallbackNotificationTarget(Map<String, dynamic> data) {
    final resourceType = '${data['resourceType'] ?? ''}';
    final resourceId = '${data['resourceId'] ?? ''}';
    if (resourceType == 'video' && resourceId.isNotEmpty) {
      return '/watch/${Uri.encodeComponent(resourceId)}';
    }
    return '/notifications';
  }

  void _openDeepLink(Uri uri) {
    final authLink = AuthLink.parse(uri);
    if (authLink != null) {
      _router.go(
        '/auth?step=${Uri.encodeQueryComponent(authLink.path)}'
        '${authLink.token == null ? '' : '&token=${Uri.encodeQueryComponent(authLink.token!)}'}',
      );
      return;
    }
    final target = contentLinkTarget(uri);
    if (target != null) _openNotification({'actionUrl': target});
  }

  @override
  void dispose() {
    _linkSubscription?.cancel();
    _networkRetryTimer?.cancel();
    _uploads.dispose();
    _downloads.dispose();
    _router.dispose();
    _playback.dispose();
    _notifications.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => ValueListenableBuilder<ThemeMode>(
    valueListenable: ThemeNotifier.themeMode,
    builder: (context, themeMode, _) => ValueListenableBuilder<String>(
      valueListenable: AppStrings.currentLang,
      builder: (context, _, _) => MaterialApp.router(
        title: 'Dalitu',
        debugShowCheckedModeBanner: false,
        theme: AppTheme.lightTheme,
        darkTheme: AppTheme.darkTheme,
        themeMode: themeMode,
        routerConfig: _router,
        // The banner is outside the router's Navigator.
        builder: (context, child) => Overlay.wrap(
          child: FeedbackSurveyHost(
            auth: widget.auth,
            router: _router,
            navigatorKey: _navigatorKey,
            child: NetworkFallbackOverlay(
              api: widget.auth.api,
              child: UploadScope(
                controller: _uploads,
                child: child ?? const SizedBox.shrink(),
              ),
            ),
          ),
        ),
      ),
    ),
  );
}

class _SplashScreen extends StatelessWidget {
  const _SplashScreen();

  @override
  Widget build(BuildContext context) => Scaffold(
    body: Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(
            Icons.play_circle_fill_rounded,
            size: 56,
            color: AppColors.primaryPink,
          ),
          SizedBox(height: 16),
          Text(AppStrings.t('app.opening')),
          const SizedBox(height: 16),
          const CircularProgressIndicator(color: AppColors.primaryPink),
        ],
      ),
    ),
  );
}
