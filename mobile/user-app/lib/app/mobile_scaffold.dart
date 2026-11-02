import '../core/media/picture_in_picture.dart';
import '../channel/models/channel_models.dart';
import '../channel/services/channel_service.dart';
import '../core/widgets/scrollable_sheet.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../auth.dart';
import '../core/localization/app_strings.dart';
import '../core/theme/app_icons.dart';
import '../core/theme/app_theme.dart';
import '../core/widgets/app_logo.dart';
import '../features/content/mini_player.dart';
import '../features/content/playback_session.dart';
import '../features/notifications/notification_center.dart';
import '../features/creator/upload_controller.dart';

class MobileScaffold extends StatelessWidget {
  const MobileScaffold({
    super.key,
    required this.auth,
    required this.playback,
    required this.notifications,
    required this.location,
    required this.child,
  });

  final AuthController auth;
  final PlaybackSession playback;
  final NotificationCenter notifications;
  final String location;
  final Widget child;

  int get _selectedIndex {
    if (location.startsWith('/explore')) return 1;
    if (location.startsWith('/subscriptions')) return 3;
    if (location.startsWith('/account')) return 4;
    return 0;
  }

  void _select(BuildContext context, int index) {
    switch (index) {
      case 0:
        context.go('/home');
      case 1:
        context.go('/explore');
      case 2:
        _showCreateSheet(context);
      case 3:
        context.go('/subscriptions');
      case 4:
        auth.authenticated ? context.go('/account') : context.go('/auth');
    }
  }

  Future<void> _showCreateSheet(BuildContext context) async {
    if (!auth.authenticated) {
      context.go('/auth');
      return;
    }
    final session = auth.sessionGeneration;
    try {
      await showModalBottomSheet<void>(
        context: context,
        isScrollControlled: true,
        builder: (sheetContext) => ScrollableSheet(
          child: SafeArea(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(20, 4, 20, 22),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    AppStrings.t('app.create.title'),
                    style: Theme.of(context).textTheme.titleLarge?.copyWith(
                      fontWeight: FontWeight.w900,
                    ),
                  ),
                  const SizedBox(height: 5),
                  Text(
                    AppStrings.t('app.create.description'),
                    style: Theme.of(context).textTheme.bodyMedium,
                  ),
                  const SizedBox(height: 16),
                  _CreateAction(
                    customIcon: AppIcons.asset(
                      AppIcons.upload,
                      size: 22,
                      color: AppColors.primary,
                    ),
                    color: AppColors.primary,
                    title: AppStrings.t('app.create.videoTitle'),
                    description: AppStrings.t('app.create.videoDescription'),
                    onTap: () {
                      Navigator.pop(sheetContext);
                      context.push('/creator?upload=true');
                    },
                  ),
                  _CreateAction(
                    customIcon: AppIcons.asset(
                      AppIcons.playlist,
                      size: 22,
                      color: AppColors.violet,
                    ),
                    color: AppColors.violet,
                    title: AppStrings.t('app.create.playlistTitle'),
                    description: AppStrings.t('app.create.playlistDescription'),
                    onTap: () {
                      Navigator.pop(sheetContext);
                      context.push('/playlists?create=true');
                    },
                  ),
                  _ChannelCreateAction(
                    auth: auth,
                    onOpen: (id) {
                      Navigator.pop(sheetContext);
                      if (!context.mounted ||
                          session != auth.sessionGeneration) {
                        return;
                      }
                      context.push(
                        id == null ? '/create-channel' : '/channels/$id',
                      );
                    },
                  ),
                ],
              ),
            ),
          ),
        ),
      );
    } catch (error) {
      if (context.mounted && session == auth.sessionGeneration) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              error is ApiFailure
                  ? AppStrings.apiError(error)
                  : AppStrings.t('common.networkError'),
            ),
          ),
        );
      }
    }
  }

  void _restoreMinimizedVideo(BuildContext context) {
    final videoId = playback.videoId;
    if (!playback.minimized || videoId == null || !playback.hasVideo) return;

    // A bottom-nav change uses context.go(), which intentionally replaces the
    // shell route and removes the old watch page from the navigator stack.
    // Keep the playback session alive, but recreate its watch route when the
    // user presses Back so the video is never lost from the navigation flow.
    playback.restore();
    context.push('/watch/$videoId');
  }

  @override
  Widget build(BuildContext context) => ValueListenableBuilder<bool>(
    valueListenable: AppPictureInPicture.active,
    builder: (context, systemPip, _) {
      final wide = MediaQuery.sizeOf(context).width >= 720;
      final isDetail = location.startsWith('/watch/');
      final hideAppBar =
          systemPip ||
          isDetail ||
          location == '/account' ||
          location == '/settings';
      final scaffold = Scaffold(
        appBar: hideAppBar
            ? null
            : AppBar(
                leadingWidth: wide ? 56 : 52,
                leading: Builder(
                  builder: (drawerContext) => IconButton(
                    tooltip: AppStrings.t('common.menu'),
                    icon: const Icon(Icons.menu_rounded),
                    onPressed: () => Scaffold.of(drawerContext).openDrawer(),
                  ),
                ),
                title: HuTubeLogo(
                  size: 25,
                  showWordmark: MediaQuery.sizeOf(context).width >= 360,
                ),
                actions: [
                  if (!auth.authenticated && wide)
                    IconButton(
                      tooltip: AppStrings.t('common.signIn'),
                      onPressed: () => context.go('/auth'),
                      icon: const Icon(Icons.login_rounded),
                    ),
                  if (!isDetail)
                    IconButton(
                      tooltip: AppStrings.t('common.search'),
                      onPressed: () => context.push('/search'),
                      icon: AppIcons.asset(
                        AppIcons.search,
                        size: 24,
                        color: Theme.of(context).colorScheme.onSurface,
                      ),
                    ),
                  if (auth.authenticated)
                    AnimatedBuilder(
                      animation: notifications,
                      builder: (context, _) => IconButton(
                        tooltip: AppStrings.t('common.notifications'),
                        onPressed: () => context.push('/notifications'),
                        icon: Semantics(
                          label: notifications.unreadCount == 0
                              ? AppStrings.t('common.notifications')
                              : '${AppStrings.t('common.notifications')}, ${AppStrings.format('notifications.unreadCount', {'count': AppStrings.number(notifications.unreadCount)})}',
                          child: Stack(
                            clipBehavior: Clip.none,
                            children: [
                              AppIcons.asset(
                                AppIcons.notification,
                                size: 22,
                                color: Theme.of(context).colorScheme.onSurface,
                              ),
                              if (notifications.unreadCount > 0)
                                Positioned(
                                  top: -7,
                                  right: -9,
                                  child: Container(
                                    constraints: const BoxConstraints(
                                      minWidth: 17,
                                    ),
                                    height: 17,
                                    padding: const EdgeInsets.symmetric(
                                      horizontal: 4,
                                    ),
                                    alignment: Alignment.center,
                                    decoration: BoxDecoration(
                                      color: AppColors.primaryPink,
                                      borderRadius: BorderRadius.circular(9),
                                      border: Border.all(
                                        color: Theme.of(
                                          context,
                                        ).colorScheme.surface,
                                        width: 1.5,
                                      ),
                                    ),
                                    child: Text(
                                      notifications.unreadCount > 99
                                          ? '99+'
                                          : '${notifications.unreadCount}',
                                      style: const TextStyle(
                                        color: Colors.white,
                                        fontSize: 9,
                                        fontWeight: FontWeight.w900,
                                      ),
                                    ),
                                  ),
                                ),
                            ],
                          ),
                        ),
                      ),
                    ),
                ],
              ),
        drawer: isDetail ? null : _Drawer(auth: auth),
        floatingActionButton: auth.authenticated
            ? UploadTray(controller: UploadScope.of(context))
            : null,
        floatingActionButtonLocation: FloatingActionButtonLocation.endFloat,
        body: SafeArea(
          top: false,
          left: !isDetail,
          right: !isDetail,
          bottom: !isDetail && !systemPip,
          child: Stack(
            fit: StackFit.expand,
            children: [
              child,
              if (!isDetail) MiniPlayer(session: playback),
            ],
          ),
        ),
        bottomNavigationBar: (systemPip || wide || isDetail)
            ? null
            : NavigationBar(
                selectedIndex: _selectedIndex,
                onDestinationSelected: (value) => _select(context, value),
                destinations: [
                  NavigationDestination(
                    icon: AppIcons.asset(
                      AppIcons.home,
                      size: 22,
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                    ),
                    selectedIcon: AppIcons.asset(
                      AppIcons.home,
                      size: 22,
                      color: AppColors.primaryPink,
                    ),
                    label: AppStrings.t('nav.home'),
                  ),
                  NavigationDestination(
                    icon: AppIcons.asset(
                      AppIcons.compass,
                      size: 22,
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                    ),
                    selectedIcon: AppIcons.asset(
                      AppIcons.compass,
                      size: 22,
                      color: AppColors.primaryPink,
                    ),
                    label: AppStrings.t('nav.explore'),
                  ),
                  NavigationDestination(
                    icon: _CreateNavIcon(),
                    selectedIcon: _CreateNavIcon(selected: true),
                    label: AppStrings.t('app.createButton'),
                  ),
                  NavigationDestination(
                    icon: AppIcons.asset(
                      AppIcons.myChannel,
                      size: 22,
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                    ),
                    selectedIcon: AppIcons.asset(
                      AppIcons.myChannel,
                      size: 22,
                      color: AppColors.primaryPink,
                    ),
                    label: AppStrings.t('nav.subscriptions'),
                  ),
                  NavigationDestination(
                    icon: _YouNavIcon(auth: auth, selected: false),
                    selectedIcon: _YouNavIcon(auth: auth, selected: true),
                    label: AppStrings.t('app.navYou'),
                  ),
                ],
              ),
      );
      return AnimatedBuilder(
        animation: playback,
        builder: (context, _) {
          final canRestoreMinimizedVideo =
              !isDetail && playback.minimized && playback.hasVideo;
          return PopScope(
            canPop: !canRestoreMinimizedVideo,
            onPopInvokedWithResult: (didPop, _) {
              if (!didPop && canRestoreMinimizedVideo) {
                _restoreMinimizedVideo(context);
              }
            },
            child: scaffold,
          );
        },
      );
    },
  );
}

class _YouNavIcon extends StatelessWidget {
  const _YouNavIcon({required this.auth, required this.selected});
  final AuthController auth;
  final bool selected;

  @override
  Widget build(BuildContext context) {
    final user = auth.user;
    final avatarUrl = user?['avatarUrl'] as String?;
    final name = (user?['displayName'] as String?) ?? 'U';
    final initial = name.isNotEmpty ? name[0].toUpperCase() : 'U';
    final isDark = Theme.of(context).brightness == Brightness.dark;

    return Tooltip(
      message: AppStrings.t(
        auth.authenticated ? 'common.account' : 'common.signIn',
      ),
      child: Container(
        width: 26,
        height: 26,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          border: selected
              ? Border.all(
                  color: isDark ? Colors.white : AppColors.primaryPink,
                  width: 2,
                )
              : Border.all(color: Colors.transparent, width: 2),
        ),
        child: ClipOval(
          child: avatarUrl != null && avatarUrl.isNotEmpty
              ? Image.network(
                  avatarUrl,
                  fit: BoxFit.cover,
                  errorBuilder: (_, _, _) => Container(
                    color: AppColors.primaryPink.withValues(alpha: 0.2),
                    alignment: Alignment.center,
                    child: Text(
                      initial,
                      style: TextStyle(
                        fontSize: 11,
                        fontWeight: FontWeight.bold,
                        color: isDark ? Colors.white : AppColors.primaryPink,
                      ),
                    ),
                  ),
                )
              : Container(
                  color: AppColors.primaryPink.withValues(alpha: 0.2),
                  alignment: Alignment.center,
                  child: Text(
                    initial,
                    style: TextStyle(
                      fontSize: 11,
                      fontWeight: FontWeight.bold,
                      color: isDark ? Colors.white : AppColors.primaryPink,
                    ),
                  ),
                ),
        ),
      ),
    );
  }
}

class _CreateNavIcon extends StatelessWidget {
  const _CreateNavIcon({this.selected = false});
  final bool selected;

  @override
  Widget build(BuildContext context) => Container(
    width: 42,
    height: 32,
    decoration: BoxDecoration(
      color: selected ? AppColors.primaryDark : AppColors.primary,
      borderRadius: BorderRadius.circular(10),
    ),
    child: const Icon(Icons.add_rounded, color: Colors.white, size: 23),
  );
}

class _CreateAction extends StatelessWidget {
  const _CreateAction({
    required this.customIcon,
    required this.color,
    required this.title,
    required this.description,
    required this.onTap,
  });
  final Widget customIcon;
  final Color color;
  final String title;
  final String description;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: const EdgeInsets.symmetric(vertical: 3),
    leading: Container(
      width: 44,
      height: 44,
      decoration: BoxDecoration(
        color: color.withValues(alpha: .1),
        borderRadius: BorderRadius.circular(12),
      ),
      alignment: Alignment.center,
      child: customIcon,
    ),
    title: Text(title, style: const TextStyle(fontWeight: FontWeight.w800)),
    subtitle: Text(description),
    trailing: const Icon(Icons.chevron_right_rounded),
    onTap: onTap,
  );
}

class _Drawer extends StatelessWidget {
  const _Drawer({required this.auth});

  final AuthController auth;

  @override
  Widget build(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final iconColor = isDark ? Colors.white : AppColors.textPrimary;
    final textColor = isDark ? Colors.white : AppColors.textPrimary;

    return Drawer(
      width: 260,
      backgroundColor: isDark ? const Color(0xFF181818) : AppColors.surface,
      child: SafeArea(
        child: ListView(
          padding: EdgeInsets.zero,
          children: [
            const Padding(
              padding: EdgeInsets.fromLTRB(20, 18, 16, 12),
              child: HuTubeLogo(size: 28),
            ),
            _item(
              context,
              null,
              AppStrings.t('nav.home'),
              '/home',
              customIcon: AppIcons.asset(
                AppIcons.home,
                size: 20,
                color: iconColor,
              ),
              textColor: textColor,
            ),
            _item(
              context,
              null,
              AppStrings.t('nav.explore'),
              '/explore',
              customIcon: AppIcons.asset(
                AppIcons.compass,
                size: 20,
                color: iconColor,
              ),
              textColor: textColor,
            ),
            _item(
              context,
              null,
              AppStrings.t('nav.subscriptions'),
              '/subscriptions',
              customIcon: AppIcons.asset(
                AppIcons.channel,
                size: 20,
                color: iconColor,
              ),
              textColor: textColor,
            ),
            _item(
              context,
              null,
              AppStrings.t('nav.playlists'),
              '/playlists',
              customIcon: AppIcons.asset(
                AppIcons.playlist,
                size: 20,
                color: iconColor,
              ),
              protected: true,
              textColor: textColor,
            ),
            _SectionLabel(AppStrings.t('app.librarySection')),
            _item(
              context,
              null,
              '${AppStrings.t('library.history')} & ${AppStrings.t('library.liked')}',
              '/library',
              customIcon: AppIcons.asset(
                AppIcons.history,
                size: 20,
                color: iconColor,
              ),
              protected: true,
              textColor: textColor,
            ),
            _item(
              context,
              null,
              AppStrings.t('downloads.title'),
              '/downloads',
              customIcon: AppIcons.asset(
                AppIcons.download,
                size: 20,
                color: iconColor,
              ),
              protected: true,
              textColor: textColor,
            ),
            _SectionLabel(AppStrings.t('app.otherSection')),
            _item(
              context,
              null,
              AppStrings.t('plans.title'),
              '/plans',
              customIcon: AppIcons.asset(
                AppIcons.plan,
                size: 20,
                color: iconColor,
              ),
              textColor: textColor,
            ),
            _item(
              context,
              null,
              AppStrings.t('policies.title'),
              '/policies',
              customIcon: AppIcons.asset(
                AppIcons.appeals,
                size: 20,
                color: iconColor,
              ),
              textColor: textColor,
            ),
            _item(
              context,
              null,
              AppStrings.t('moderation.navLabel'),
              '/moderation',
              customIcon: AppIcons.asset(
                AppIcons.warningStrike,
                size: 20,
                color: iconColor,
              ),
              protected: true,
              textColor: textColor,
            ),
            if (auth.authenticated) ...[
              _item(
                context,
                null,
                AppStrings.t('creator.title'),
                '/creator',
                customIcon: AppIcons.asset(
                  AppIcons.dashboard,
                  size: 20,
                  color: iconColor,
                ),
                protected: true,
                textColor: textColor,
              ),
            ],
          ],
        ),
      ),
    );
  }

  Widget _item(
    BuildContext context,
    IconData? icon,
    String label,
    String route, {
    Widget? customIcon,
    bool protected = false,
    Color? textColor,
  }) => ListTile(
    dense: true,
    visualDensity: const VisualDensity(horizontal: -2, vertical: -2),
    contentPadding: const EdgeInsets.symmetric(horizontal: 20, vertical: 0),
    leading: customIcon ?? (icon != null ? Icon(icon, color: textColor) : null),
    title: Text(
      label,
      style: TextStyle(
        fontSize: 14,
        fontWeight: FontWeight.w500,
        color: textColor,
      ),
    ),
    minLeadingWidth: 24,
    onTap: () {
      Navigator.of(context).pop();
      context.go(protected && !auth.authenticated ? '/auth' : route);
    },
  );
}

class _SectionLabel extends StatelessWidget {
  const _SectionLabel(this.text);
  final String text;

  @override
  Widget build(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 14, 16, 4),
      child: Text(
        text,
        style: Theme.of(context).textTheme.labelSmall?.copyWith(
          fontWeight: FontWeight.w800,
          letterSpacing: 0.8,
          fontSize: 11,
          color: isDark ? const Color(0xFFAAAAAA) : AppColors.textMuted,
        ),
      ),
    );
  }
}

class _ChannelCreateAction extends StatefulWidget {
  const _ChannelCreateAction({required this.auth, required this.onOpen});
  final AuthController auth;
  final ValueChanged<String?> onOpen;
  @override
  State<_ChannelCreateAction> createState() => _ChannelCreateActionState();
}

class _ChannelCreateActionState extends State<_ChannelCreateAction> {
  late Future<ChannelDetail?> _channel = ChannelService(
    widget.auth,
  ).getMyChannel();
  @override
  Widget build(BuildContext context) => FutureBuilder<ChannelDetail?>(
    future: _channel,
    builder: (context, snapshot) {
      if (snapshot.connectionState != ConnectionState.done) {
        return ListTile(
          leading: const SizedBox.square(
            dimension: 20,
            child: CircularProgressIndicator(strokeWidth: 2),
          ),
          title: Text(AppStrings.t('account.checkingChannel')),
        );
      }
      if (snapshot.hasError) {
        return ListTile(
          title: Text(AppStrings.t('channel.loadError')),
          trailing: IconButton(
            tooltip: AppStrings.t('common.retry'),
            icon: const Icon(Icons.refresh_rounded),
            onPressed: () => setState(
              () => _channel = ChannelService(widget.auth).getMyChannel(),
            ),
          ),
        );
      }
      final channel = snapshot.data;
      return _CreateAction(
        customIcon: AppIcons.asset(
          AppIcons.channel,
          size: 22,
          color: AppColors.success,
        ),
        color: AppColors.success,
        title: AppStrings.t(
          channel == null
              ? 'app.create.channelTitle'
              : 'app.create.existingChannel',
        ),
        description: AppStrings.t(
          channel == null
              ? 'app.create.channelDescription'
              : 'app.create.existingChannelHint',
        ),
        onTap: () => widget.onOpen(channel?.id),
      );
    },
  );
}
