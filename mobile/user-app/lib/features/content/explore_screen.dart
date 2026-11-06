import 'dart:async';

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../auth.dart';
import '../../channel/services/channel_service.dart';
import '../../core/localization/app_strings.dart';
import '../../core/theme/app_theme.dart';
import '../../core/widgets/hutube_widgets.dart';
import 'content_models.dart';
import 'content_service.dart';

/// The mobile version of the Web User explore showcase.
///
/// Explore is intentionally a showcase instead of a generic feed: the API
/// already returns the same ranked videos, featured creators and trending
/// videos that the Web User page uses.
class ExploreScreen extends StatefulWidget {
  const ExploreScreen({super.key, required this.auth});

  final AuthController auth;

  @override
  State<ExploreScreen> createState() => _ExploreScreenState();
}

class _ExploreScreenState extends State<ExploreScreen> {
  late final ContentService _content;
  late final ChannelService _channels;

  ExploreHub? _hub;
  String? _error;
  String? _selectedCategoryId;
  String _selectedCategoryName = '';
  List<VideoCard> _categoryVideos = const [];
  bool _categoryLoading = false;
  bool _loading = true;
  int _loadGeneration = 0;
  int _categoryGeneration = 0;
  final Map<String, bool> _subscriptions = {};
  final Set<String> _subscriptionLoading = {};
  final Map<String, int> _subscriptionVersions = {};
  late int _sessionGeneration;
  late bool _authenticated;
  String? _ownChannelId;
  bool _ownershipReady = false;

  @override
  void initState() {
    super.initState();
    _content = ContentService(widget.auth);
    _channels = ChannelService(widget.auth);
    _sessionGeneration = widget.auth.sessionGeneration;
    _authenticated = widget.auth.authenticated;
    widget.auth.addListener(_identityChanged);
    unawaited(_load());
  }

  void _identityChanged() {
    if (_sessionGeneration == widget.auth.sessionGeneration &&
        _authenticated == widget.auth.authenticated) {
      return;
    }
    _sessionGeneration = widget.auth.sessionGeneration;
    _authenticated = widget.auth.authenticated;
    setState(() {
      _subscriptions.clear();
      _subscriptionLoading.clear();
      _subscriptionVersions.clear();
      _ownChannelId = null;
      _ownershipReady = false;
    });
    unawaited(_loadOwnership());
    if (_authenticated && _hub != null) {
      unawaited(_loadSubscriptionStates(_hub!.creators));
    }
  }

  Future<void> _loadOwnership() async {
    final session = widget.auth.sessionGeneration;
    final authenticated = widget.auth.authenticated;
    try {
      final channel = authenticated ? await _channels.getMyChannel() : null;
      if (!mounted ||
          session != widget.auth.sessionGeneration ||
          authenticated != widget.auth.authenticated) {
        return;
      }
      setState(() {
        _ownChannelId = channel?.id;
        _ownershipReady = true;
      });
    } catch (_) {
      // Leave subscription controls disabled until ownership can be verified.
    }
  }

  @override
  void dispose() {
    widget.auth.removeListener(_identityChanged);
    super.dispose();
  }

  Future<void> _load() async {
    if (!mounted) return;
    if (!_ownershipReady) unawaited(_loadOwnership());
    final generation = ++_loadGeneration;
    if (mounted) {
      setState(() {
        _loading = true;
        _error = null;
      });
    }
    try {
      final hub = await _content.exploreHub();
      if (!mounted || generation != _loadGeneration) return;
      setState(() {
        _hub = hub;
        _loading = false;
      });
      if (widget.auth.authenticated) {
        unawaited(_loadSubscriptionStates(hub.creators));
      }
    } on ApiFailure catch (error) {
      if (!mounted || generation != _loadGeneration) return;
      setState(() {
        _loading = false;
        _error = AppStrings.apiError(error, fallback: 'explore.loadError');
      });
    } catch (_) {
      if (!mounted || generation != _loadGeneration) return;
      setState(() {
        _loading = false;
        _error = AppStrings.t('explore.loadError');
      });
    }
  }

  Future<void> _loadSubscriptionStates(List<FeaturedCreator> creators) async {
    final session = widget.auth.sessionGeneration;
    final generation = _loadGeneration;
    final versions = Map<String, int>.from(_subscriptionVersions);
    try {
      final subscriptions = await _channels.getSubscribedChannels();
      if (!mounted ||
          session != widget.auth.sessionGeneration ||
          generation != _loadGeneration) {
        return;
      }
      final ids = subscriptions.map((item) => item.channelId).toSet();
      setState(() {
        for (final creator in creators) {
          if (_subscriptionVersions[creator.id] == versions[creator.id]) {
            _subscriptions[creator.id] = ids.contains(creator.id);
          }
        }
      });
    } catch (_) {
      // Keep the current state when the batch lookup fails.
    }
  }

  void _selectCategory(String? categoryId, String categoryName) {
    if (categoryId == _selectedCategoryId) return;
    final generation = ++_categoryGeneration;
    setState(() {
      _selectedCategoryId = categoryId;
      _selectedCategoryName = categoryName;
      _categoryVideos = categoryId == null
          ? const []
          : (_hub?.rankings
                    .firstWhere(
                      (group) => group.id == categoryId,
                      orElse: () => const CategoryRankingGroup(
                        id: '',
                        name: '',
                        slug: '',
                        videos: [],
                      ),
                    )
                    .videos ??
                const []);
      _categoryLoading = categoryId != null;
    });
    if (categoryId != null) unawaited(_loadCategory(categoryId, generation));
  }

  Future<void> _loadCategory(String categoryId, int generation) async {
    try {
      final result = await _content.searchVideos(
        query: '',
        categoryId: categoryId,
        sort: 'views',
        page: 1,
        pageSize: 12,
      );
      if (!mounted || generation != _categoryGeneration) return;
      setState(() {
        _categoryVideos = result.items;
        _categoryLoading = false;
      });
    } catch (_) {
      if (!mounted || generation != _categoryGeneration) return;
      setState(() => _categoryLoading = false);
    }
  }

  Future<void> _toggleSubscription(FeaturedCreator creator) async {
    if (!_ownershipReady || creator.id == _ownChannelId) return;
    if (!widget.auth.authenticated) {
      if (mounted) context.push('/auth');
      return;
    }
    if (_subscriptionLoading.contains(creator.id)) return;
    final session = widget.auth.sessionGeneration;
    final subscribed = _subscriptions[creator.id] ?? false;
    setState(() {
      _subscriptionVersions[creator.id] =
          (_subscriptionVersions[creator.id] ?? 0) + 1;
      _subscriptionLoading.add(creator.id);
      _subscriptions[creator.id] = !subscribed;
    });
    try {
      if (subscribed) {
        await _channels.unsubscribe(creator.id);
      } else {
        await _channels.subscribe(creator.id);
      }
    } catch (_) {
      if (mounted && session == widget.auth.sessionGeneration) {
        setState(() => _subscriptions[creator.id] = subscribed);
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(AppStrings.t('explore.loadError'))),
        );
      }
    } finally {
      if (mounted && session == widget.auth.sessionGeneration) {
        setState(() => _subscriptionLoading.remove(creator.id));
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_loading && _hub == null) return const _ExploreLoading();
    if (_error != null && _hub == null) {
      return _ExploreFailure(message: _error!, onRetry: _load);
    }

    final hub = _hub!;
    final rankingVideos = _selectedCategoryId == null
        ? hub.topVideos
        : _categoryVideos;
    final rankingTitle = _selectedCategoryId == null
        ? AppStrings.t('explore.overallTop12')
        : AppStrings.format('explore.top12Category', {
            'category':
                hub.rankings
                    .where((group) => group.id == _selectedCategoryId)
                    .firstOrNull
                    ?.displayName ??
                _selectedCategoryName,
          });

    return RefreshIndicator(
      color: AppColors.primaryPink,
      onRefresh: _load,
      child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(16, 14, 16, 28),
        children: [
          if (_error != null) ...[
            HuTubeNotice(
              message: _error!,
              actionLabel: AppStrings.t('common.retry'),
              onAction: _loading ? null : () => unawaited(_load()),
            ),
            const SizedBox(height: 16),
          ],
          const _ExploreHero(),
          const SizedBox(height: 14),
          _CategoryTabs(
            groups: hub.rankings,
            selectedId: _selectedCategoryId,
            onSelected: _selectCategory,
          ),
          const SizedBox(height: 24),
          _SectionTitle(title: rankingTitle),
          const SizedBox(height: 10),
          if (_categoryLoading)
            const LinearProgressIndicator(
              minHeight: 2,
              color: AppColors.primaryPink,
              backgroundColor: AppColors.primaryIndicator,
            ),
          if (rankingVideos.isEmpty)
            _EmptyExplore(message: AppStrings.t('explore.noRankedVideos'))
          else
            for (var index = 0; index < rankingVideos.length; index++)
              _RankedVideoTile(video: rankingVideos[index], rank: index + 1),
          if (hub.creators.isNotEmpty) ...[
            const SizedBox(height: 28),
            _SectionTitle(
              title: AppStrings.t('explore.featuredCreators'),
              subtitle: AppStrings.t('explore.featuredCreatorsSubtitle'),
              icon: Icons.star_rounded,
              iconColor: AppColors.warning,
            ),
            const SizedBox(height: 12),
            _CreatorsRow(
              creators: hub.creators,
              subscriptions: _subscriptions,
              loading: _subscriptionLoading,
              ownChannelId: _ownChannelId,
              ownershipReady: _ownershipReady,
              onSubscribe: _toggleSubscription,
            ),
          ],
          if (hub.trending.isNotEmpty) ...[
            const SizedBox(height: 28),
            _SectionTitle(
              title: AppStrings.t('explore.trendingVideos'),
              subtitle: AppStrings.t('explore.trendingSubtitle'),
              icon: Icons.local_fire_department_rounded,
              iconColor: const Color(0xFFEF4444),
            ),
            const SizedBox(height: 12),
            _TrendingRow(videos: hub.trending),
          ],
        ],
      ),
    );
  }
}

class _ExploreHero extends StatelessWidget {
  const _ExploreHero();

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.fromLTRB(18, 18, 18, 20),
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(22),
      gradient: const LinearGradient(
        colors: [Color(0xFF291522), Color(0xFF171927)],
        begin: Alignment.topLeft,
        end: Alignment.bottomRight,
      ),
      boxShadow: const [
        BoxShadow(
          color: Color(0x2417111F),
          blurRadius: 18,
          offset: Offset(0, 8),
        ),
      ],
    ),
    child: Row(
      children: [
        Container(
          width: 58,
          height: 58,
          decoration: BoxDecoration(
            color: const Color(0x26FF2B66),
            borderRadius: BorderRadius.circular(17),
            border: Border.all(color: const Color(0x55FF6D95)),
          ),
          child: const Icon(
            Icons.bar_chart_rounded,
            color: Color(0xFFFFB11B),
            size: 31,
          ),
        ),
        const SizedBox(width: 14),
        Expanded(
          child: Text(
            AppStrings.t('explore.rankingTitle'),
            style: const TextStyle(
              color: Colors.white,
              fontSize: 20,
              height: 1.18,
              fontWeight: FontWeight.w900,
              letterSpacing: -.35,
            ),
          ),
        ),
      ],
    ),
  );
}

class _CategoryTabs extends StatelessWidget {
  const _CategoryTabs({
    required this.groups,
    required this.selectedId,
    required this.onSelected,
  });

  final List<CategoryRankingGroup> groups;
  final String? selectedId;
  final void Function(String?, String) onSelected;

  @override
  Widget build(BuildContext context) => SingleChildScrollView(
    scrollDirection: Axis.horizontal,
    child: Row(
      children: [
        _CategoryChip(
          label: AppStrings.t('explore.category.overall'),
          selected: selectedId == null,
          onTap: () => onSelected(null, ''),
        ),
        for (final group in groups)
          _CategoryChip(
            label: group.displayName,
            selected: selectedId == group.id,
            onTap: () => onSelected(group.id, group.displayName),
          ),
      ],
    ),
  );
}

class _CategoryChip extends StatelessWidget {
  const _CategoryChip({
    required this.label,
    required this.selected,
    required this.onTap,
  });

  final String label;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(right: 8),
    child: ChoiceChip(
      label: Text(label),
      selected: selected,
      showCheckmark: false,
      onSelected: (_) => onTap(),
      labelStyle: TextStyle(
        color: selected
            ? Theme.of(context).colorScheme.onPrimary
            : AppColors.textSecondaryFor(context),
        fontWeight: FontWeight.w800,
        fontSize: 12,
      ),
      selectedColor: Theme.of(context).colorScheme.primary,
      backgroundColor: AppColors.surfaceFor(context),
      side: BorderSide(
        color: selected
            ? Theme.of(context).colorScheme.primary
            : AppColors.borderFor(context),
      ),
      shape: const StadiumBorder(),
      visualDensity: VisualDensity.compact,
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
    ),
  );
}

class _SectionTitle extends StatelessWidget {
  const _SectionTitle({
    required this.title,
    this.subtitle,
    this.icon,
    this.iconColor,
  });

  final String title;
  final String? subtitle;
  final IconData? icon;
  final Color? iconColor;

  @override
  Widget build(BuildContext context) => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      if (icon != null) ...[
        Icon(icon, size: 21, color: iconColor),
        const SizedBox(width: 7),
      ],
      Expanded(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              title,
              style: Theme.of(context).textTheme.titleMedium?.copyWith(
                fontWeight: FontWeight.w900,
                letterSpacing: -.2,
              ),
            ),
            if (subtitle != null) ...[
              const SizedBox(height: 2),
              Text(
                subtitle!,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: AppColors.textMutedFor(context),
                  fontSize: 11,
                ),
              ),
            ],
          ],
        ),
      ),
    ],
  );
}

class _RankedVideoTile extends StatelessWidget {
  const _RankedVideoTile({required this.video, required this.rank});

  final VideoCard video;
  final int rank;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 9),
    child: InkWell(
      borderRadius: BorderRadius.circular(16),
      onTap: () => context.push('/watch/${video.id}'),
      child: Ink(
        padding: const EdgeInsets.all(8),
        decoration: BoxDecoration(
          color: AppColors.surfaceFor(context),
          borderRadius: BorderRadius.circular(16),
          border: Border.all(color: AppColors.borderFor(context)),
        ),
        child: Row(
          children: [
            _RankBadge(rank: rank),
            const SizedBox(width: 9),
            _ExploreThumbnail(video: video, width: 126, height: 72),
            const SizedBox(width: 10),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    video.title,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      fontWeight: FontWeight.w800,
                      fontSize: 13,
                      height: 1.23,
                    ),
                  ),
                  const SizedBox(height: 5),
                  Text(
                    video.channelName,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      color: AppColors.textMutedFor(context),
                      fontSize: 11,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    AppStrings.format('feed.views', {
                      'count': AppStrings.number(video.views),
                    }),
                    style: TextStyle(
                      color: AppColors.textMutedFor(context),
                      fontSize: 10.5,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    ),
  );
}

class _RankBadge extends StatelessWidget {
  const _RankBadge({required this.rank});

  final int rank;

  @override
  Widget build(BuildContext context) {
    final color = switch (rank) {
      1 => const Color(0xFFF59E0B),
      2 => const Color(0xFF94A3B8),
      3 => const Color(0xFFCD7C4B),
      _ => AppColors.textMutedFor(context),
    };
    return SizedBox(
      width: 24,
      child: Text(
        '$rank',
        textAlign: TextAlign.center,
        style: TextStyle(
          color: color,
          fontSize: rank <= 3 ? 17 : 13,
          fontWeight: FontWeight.w900,
        ),
      ),
    );
  }
}

class _CreatorsRow extends StatelessWidget {
  const _CreatorsRow({
    required this.creators,
    required this.subscriptions,
    required this.loading,
    required this.onSubscribe,
    required this.ownChannelId,
    required this.ownershipReady,
  });

  final List<FeaturedCreator> creators;
  final Map<String, bool> subscriptions;
  final Set<String> loading;
  final ValueChanged<FeaturedCreator> onSubscribe;
  final String? ownChannelId;
  final bool ownershipReady;

  @override
  Widget build(BuildContext context) => SizedBox(
    height: 194 + (MediaQuery.textScalerOf(context).scale(24) - 24),
    child: ListView.separated(
      scrollDirection: Axis.horizontal,
      itemCount: creators.length,
      separatorBuilder: (_, _) => const SizedBox(width: 10),
      itemBuilder: (context, index) {
        final creator = creators[index];
        final subscribed = subscriptions[creator.id] ?? false;
        final isOwner = creator.id == ownChannelId;
        return SizedBox(
          width: 162,
          child: HuTubeSurface(
            padding: const EdgeInsets.fromLTRB(10, 12, 10, 10),
            borderRadius: 18,
            child: Column(
              children: [
                InkWell(
                  borderRadius: BorderRadius.circular(30),
                  onTap: () => context.push('/channels/${creator.handle}'),
                  child: HuTubeAvatar(
                    url: creator.avatarUrl,
                    label: creator.name,
                    radius: 27,
                  ),
                ),
                const SizedBox(height: 8),
                Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    Flexible(
                      child: Text(
                        creator.name,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        textAlign: TextAlign.center,
                        style: const TextStyle(
                          fontWeight: FontWeight.w900,
                          fontSize: 12,
                        ),
                      ),
                    ),
                    if (creator.verified) ...[
                      const SizedBox(width: 3),
                      const Icon(
                        Icons.verified_rounded,
                        color: AppColors.info,
                        size: 14,
                      ),
                    ],
                  ],
                ),
                const SizedBox(height: 3),
                Text(
                  '${AppStrings.number(creator.subscriberCount)} ${AppStrings.t('explore.subscribers')}',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    color: AppColors.textMutedFor(context),
                    fontSize: 10,
                  ),
                ),
                const Spacer(),
                SizedBox(
                  width: double.infinity,
                  height: 48,
                  child: OutlinedButton(
                    onPressed: !ownershipReady || loading.contains(creator.id)
                        ? null
                        : isOwner
                        ? () => context.push('/channels/${creator.handle}')
                        : () => onSubscribe(creator),
                    style: OutlinedButton.styleFrom(
                      padding: EdgeInsets.zero,
                      side: BorderSide(
                        color: subscribed
                            ? AppColors.success
                            : AppColors.primaryPink,
                      ),
                      foregroundColor: subscribed
                          ? AppColors.success
                          : AppColors.primaryPink,
                      shape: const StadiumBorder(),
                    ),
                    child: loading.contains(creator.id)
                        ? const SizedBox(
                            width: 14,
                            height: 14,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : Text(
                            AppStrings.t(
                              isOwner
                                  ? 'channel.yourChannel'
                                  : subscribed
                                  ? 'explore.subscribed'
                                  : 'explore.subscribe',
                            ),
                            style: const TextStyle(
                              fontSize: 10.5,
                              fontWeight: FontWeight.w800,
                            ),
                          ),
                  ),
                ),
              ],
            ),
          ),
        );
      },
    ),
  );
}

class _TrendingRow extends StatelessWidget {
  const _TrendingRow({required this.videos});

  final List<VideoCard> videos;

  @override
  Widget build(BuildContext context) => SizedBox(
    height: 226,
    child: ListView.separated(
      scrollDirection: Axis.horizontal,
      itemCount: videos.length,
      separatorBuilder: (_, _) => const SizedBox(width: 12),
      itemBuilder: (context, index) {
        final video = videos[index];
        return SizedBox(
          width: 238,
          child: InkWell(
            borderRadius: BorderRadius.circular(17),
            onTap: () => context.push('/watch/${video.id}'),
            child: Ink(
              decoration: BoxDecoration(
                color: AppColors.surfaceFor(context),
                borderRadius: BorderRadius.circular(17),
                border: Border.all(color: AppColors.borderFor(context)),
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  _ExploreThumbnail(
                    video: video,
                    width: 238,
                    height: 132,
                    radius: 16,
                  ),
                  Padding(
                    padding: const EdgeInsets.fromLTRB(11, 9, 11, 10),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          video.title,
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                          style: const TextStyle(
                            fontWeight: FontWeight.w800,
                            fontSize: 12.5,
                            height: 1.2,
                          ),
                        ),
                        const SizedBox(height: 5),
                        Text(
                          '${video.channelName} · ${AppStrings.format('feed.views', {'count': AppStrings.number(video.views)})}',
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: TextStyle(
                            color: AppColors.textMutedFor(context),
                            fontSize: 10.5,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        );
      },
    ),
  );
}

class _ExploreThumbnail extends StatelessWidget {
  const _ExploreThumbnail({
    required this.video,
    required this.width,
    required this.height,
    this.radius = 12,
  });

  final VideoCard video;
  final double width;
  final double height;
  final double radius;

  @override
  Widget build(BuildContext context) => ClipRRect(
    borderRadius: BorderRadius.circular(radius),
    child: SizedBox(
      width: width,
      height: height,
      child: Stack(
        fit: StackFit.expand,
        children: [
          if (video.thumbnailUrl?.startsWith('http') == true)
            Image.network(
              video.thumbnailUrl!,
              fit: BoxFit.cover,
              cacheWidth: (width * MediaQuery.devicePixelRatioOf(context))
                  .ceil(),
              errorBuilder: (_, _, _) => const _ExploreThumbnailFallback(),
            )
          else
            const _ExploreThumbnailFallback(),
          Positioned(
            right: 6,
            bottom: 6,
            child: HuTubePill(
              label: _duration(video.duration),
              icon: Icons.schedule_rounded,
              color: Colors.black.withValues(alpha: .78),
              textColor: Colors.white,
            ),
          ),
        ],
      ),
    ),
  );

  static String _duration(int seconds) =>
      '${seconds ~/ 60}:${(seconds % 60).toString().padLeft(2, '0')}';
}

class _ExploreThumbnailFallback extends StatelessWidget {
  const _ExploreThumbnailFallback();

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: const BoxDecoration(
      gradient: LinearGradient(
        colors: [Color(0xFF371426), Color(0xFF171927)],
        begin: Alignment.topLeft,
        end: Alignment.bottomRight,
      ),
    ),
    child: const Center(
      child: Icon(
        Icons.play_circle_fill_rounded,
        color: Colors.white70,
        size: 38,
      ),
    ),
  );
}

class _EmptyExplore extends StatelessWidget {
  const _EmptyExplore({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 24),
    child: Text(
      message,
      textAlign: TextAlign.center,
      style: TextStyle(color: AppColors.textMutedFor(context)),
    ),
  );
}

class _ExploreLoading extends StatelessWidget {
  const _ExploreLoading();

  @override
  Widget build(BuildContext context) => ListView(
    physics: const NeverScrollableScrollPhysics(),
    padding: const EdgeInsets.fromLTRB(16, 14, 16, 24),
    children: [
      Container(
        height: 94,
        decoration: BoxDecoration(
          color: AppColors.skeletonFor(context),
          borderRadius: BorderRadius.circular(22),
        ),
      ),
      const SizedBox(height: 20),
      Row(
        children: [
          for (var index = 0; index < 3; index++) ...[
            Container(
              width: 80,
              height: 32,
              decoration: BoxDecoration(
                color: AppColors.skeletonFor(context),
                borderRadius: BorderRadius.circular(20),
              ),
            ),
            if (index != 2) const SizedBox(width: 8),
          ],
        ],
      ),
      const SizedBox(height: 28),
      for (var index = 0; index < 4; index++) ...[
        const _LoadingRankedTile(),
        if (index != 3) const SizedBox(height: 9),
      ],
    ],
  );
}

class _LoadingRankedTile extends StatelessWidget {
  const _LoadingRankedTile();

  @override
  Widget build(BuildContext context) => Container(
    height: 90,
    decoration: BoxDecoration(
      color: AppColors.skeletonFor(context),
      borderRadius: BorderRadius.circular(16),
    ),
  );
}

class _ExploreFailure extends StatelessWidget {
  const _ExploreFailure({required this.message, required this.onRetry});

  final String message;
  final Future<void> Function() onRetry;

  @override
  Widget build(BuildContext context) => Center(
    child: HuTubeStateView(
      icon: Icons.cloud_off_rounded,
      title: message,
      message: AppStrings.t('common.checkConnection'),
      actionLabel: AppStrings.t('common.retry'),
      onAction: () => unawaited(onRetry()),
    ),
  );
}
