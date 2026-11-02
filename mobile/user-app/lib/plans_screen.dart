import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:share_plus/share_plus.dart';
import 'auth.dart';
import 'core/localization/app_strings.dart';
import 'core/theme/app_theme.dart';
import 'core/widgets/hutube_widgets.dart';
import 'features/payments/payment_service.dart';
import 'features/plans/plan_service.dart';
import 'features/plans/family_storage_usage.dart';

String _localizedPlanStatus(Object? rawStatus) {
  final raw = '${rawStatus ?? ''}'.trim();
  if (raw.isEmpty) return '—';
  final normalized = raw
      .toLowerCase()
      .replaceAll('-', '_')
      .replaceAll(' ', '_');
  final statusKey = switch (normalized) {
    'pending' || 'pending_payment' || 'awaiting_payment' => 'pending',
    'paid' => 'paid',
    'completed' || 'complete' => 'completed',
    'success' || 'successful' => 'success',
    'failed' || 'failure' => 'failed',
    'cancelled' || 'canceled' => 'cancelled',
    'expired' => 'expired',
    'refunded' => 'refunded',
    'active' => 'active',
    'inactive' => 'inactive',
    'suspended' => 'suspended',
    _ => null,
  };
  return statusKey == null
      ? AppStrings.t('common.unknown')
      : AppStrings.t('plans.status.$statusKey');
}

class PlansScreen extends StatefulWidget {
  const PlansScreen({
    super.key,
    required this.auth,
    this.invitationId,
    this.invitationToken,
    this.initialPlanId,
  });

  final AuthController auth;
  final String? invitationId;
  final String? invitationToken;
  final String? initialPlanId;

  @override
  State<PlansScreen> createState() => _PlansScreenState();
}

class _PlansScreenState extends State<PlansScreen> {
  late final PlanService _planService = PlanService(widget.auth);
  late final PaymentService _paymentService = PaymentService(widget.auth);
  late final PageController _planController = PageController(
    viewportFraction: .82,
  );
  List<Map<String, dynamic>> _plans = [];
  List<Map<String, dynamic>> _payments = [];
  Map<String, dynamic>? _myPlan;
  int _selectedPlanIndex = 0;
  bool _loading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    if (widget.invitationId != null && widget.invitationToken != null) {
      WidgetsBinding.instance.addPostFrameCallback((_) => _acceptInvitation());
    }
    _load();
  }

  @override
  void didUpdateWidget(covariant PlansScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.initialPlanId != widget.initialPlanId && !_loading) {
      final index = _plans.indexWhere(
        (plan) => plan['planId'] == widget.initialPlanId,
      );
      if (index >= 0) {
        setState(() => _selectedPlanIndex = index);
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted && _planController.hasClients) {
            _planController.jumpToPage(index);
          }
        });
      }
    }
  }

  @override
  void dispose() {
    _planController.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    if (!mounted) return;
    if (!_loading || _error != null) {
      setState(() {
        _loading = true;
        _error = null;
      });
    }
    try {
      final plans = await _planService.catalogue();
      Map<String, dynamic>? myPlan;
      var payments = <Map<String, dynamic>>[];
      if (widget.auth.authenticated) {
        try {
          myPlan = await _planService.myPlan();
        } on ApiFailure {
          // The public plan catalogue remains usable if the private quota
          // snapshot is temporarily unavailable.
        }
        try {
          payments = await _paymentService.mine();
        } on ApiFailure {
          // Payment history is optional for the public plan catalogue.
        }
      }
      if (!mounted) {
        return;
      }
      setState(() {
        _plans = plans;
        final requested = plans.indexWhere(
          (plan) => plan['planId'] == widget.initialPlanId,
        );
        _selectedPlanIndex = requested < 0 ? 0 : requested;
        _myPlan = myPlan;
        _payments = payments;
        _loading = false;
        _error = null;
      });
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted && _planController.hasClients) {
          _planController.jumpToPage(_selectedPlanIndex);
        }
      });
    } on ApiFailure catch (error) {
      if (mounted) {
        setState(() {
          _error = AppStrings.apiError(error, fallback: 'plans.loadError');
          _loading = false;
        });
      }
    } catch (_) {
      if (mounted) {
        setState(() {
          _error = AppStrings.t('plans.loadError');
          _loading = false;
        });
      }
    }
  }

  Future<void> _subscribe(String planId) async {
    if (_myPlan?['isSharedMember'] == true) return;
    try {
      Map<String, dynamic>? plan;
      for (final item in _plans) {
        if ('${item['planId']}' == planId) {
          plan = item;
          break;
        }
      }
      final price = _number(plan?['price']);
      if (price > 0) {
        final payment = await _paymentService.initiate(planId);
        if (!mounted) return;
        final paid = await showDialog<bool>(
          context: context,
          barrierDismissible: false,
          builder: (_) =>
              _PaymentDialog(payment: payment, service: _paymentService),
        );
        if (paid != true) {
          if (mounted) await _load();
          return;
        }
        if (paid == true) {
          if (mounted) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(AppStrings.t('plans.paymentConfirmed'))),
            );
          }
        }
      } else {
        await _planService.subscribe(planId);
      }
      if (mounted) {
        if (price == 0) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(AppStrings.t('plans.subscribeSuccess'))),
          );
        }
        await _load();
      }
    } on ApiFailure catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              AppStrings.apiError(error, fallback: 'plans.subscribeError'),
            ),
          ),
        );
      }
    }
  }

  Future<void> _sharePlan(Map<String, dynamic> plan) async {
    try {
      final share = await _planService.share('${plan['planId']}');
      final url = share['shareUrl'] as String?;
      if (url == null || url.isEmpty) {
        return;
      }
      try {
        await Share.share(
          '${plan['name'] ?? AppStrings.t('common.appName')}\n$url',
          subject: AppStrings.format('plans.shareSubject', {
            'name': plan['name'] ?? AppStrings.t('common.appName'),
          }),
        );
      } catch (_) {
        await Clipboard.setData(ClipboardData(text: url));
        if (mounted) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(AppStrings.t('plans.shareCopied'))),
          );
        }
      }
    } on ApiFailure catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              AppStrings.apiError(error, fallback: 'plans.shareError'),
            ),
          ),
        );
      }
    }
  }

  Future<void> _inviteMember() async {
    final availableBytes = _unallocatedStorage(_myPlan ?? const {});
    final invitation = await showDialog<_PlanInviteDraft>(
      context: context,
      builder: (_) => _PlanInviteDialog(
        availableBytes: availableBytes,
        availableBytesLabel: _formatBytes(availableBytes),
      ),
    );
    if (invitation == null) return;
    final trimmed = invitation.email.trim();
    if (trimmed.isEmpty) return;
    if (!trimmed.contains('@')) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(AppStrings.t('auth.emailInvalid'))),
        );
      }
      return;
    }
    try {
      await _planService.invite(
        trimmed,
        allocatedStorage: invitation.allocatedStorage,
      );
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(AppStrings.t('plans.inviteSent'))),
        );
        await _load();
      }
    } on ApiFailure catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              AppStrings.apiError(error, fallback: 'plans.inviteError'),
            ),
          ),
        );
      }
    }
  }

  Future<void> _acceptInvitation({bool confirmCancelPaidPlan = false}) async {
    final id = widget.invitationId;
    final token = widget.invitationToken;
    if (id == null || token == null || !widget.auth.authenticated) return;
    try {
      await _planService.acceptInvitation(
        id,
        token,
        confirmCancelPaidPlan: confirmCancelPaidPlan,
      );
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(AppStrings.t('plans.invitationAccepted'))),
        );
        await _load();
      }
    } on ApiFailure catch (error) {
      if (error.code == 'PAID_PLAN_CONFIRMATION_REQUIRED' &&
          !confirmCancelPaidPlan) {
        if (!mounted) return;
        final confirmed = await showDialog<bool>(
          context: context,
          builder: (dialogContext) => AlertDialog(
            title: Text(AppStrings.t('plans.paidJoinTitle')),
            content: Text(error.message),
            actions: [
              TextButton(
                onPressed: () => Navigator.of(dialogContext).pop(false),
                child: Text(AppStrings.t('common.cancel')),
              ),
              FilledButton(
                onPressed: () => Navigator.of(dialogContext).pop(true),
                child: Text(AppStrings.t('plans.acceptInvite')),
              ),
            ],
          ),
        );
        if (confirmed == true && mounted) {
          await _acceptInvitation(confirmCancelPaidPlan: true);
        }
        return;
      }
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(AppStrings.apiError(error))));
      }
    }
  }

  Future<void> _removeMember(Map<String, dynamic> member) async {
    final id = '${member['planMemberId'] ?? ''}';
    if (id.isEmpty) return;
    try {
      await _planService.removeMember(id);
      await _load();
    } on ApiFailure catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(AppStrings.apiError(error))));
      }
    }
  }

  Future<void> _leaveSharedPlan() async {
    final owner =
        '${_myPlan?['sharedOwnerDisplayName'] ?? AppStrings.t('plans.sharedPlanOwnerFallback')}';
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(AppStrings.t('plans.leaveSharedPlan')),
        content: Text(
          AppStrings.format('plans.leaveSharedConfirm', {'owner': owner}),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: Text(AppStrings.t('common.cancel')),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: Text(AppStrings.t('plans.leaveSharedPlan')),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await _planService.leaveSharedPlan();
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(AppStrings.t('plans.leftSharedPlan'))),
        );
        await _load();
      }
    } on ApiFailure catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              AppStrings.apiError(error, fallback: 'plans.leaveSharedError'),
            ),
          ),
        );
      }
    }
  }

  Future<void> _showPaymentDetails(String paymentId) async {
    try {
      final payment = await _paymentService.get(paymentId);
      if (!mounted) return;
      await showDialog<void>(
        context: context,
        builder: (context) => AlertDialog(
          title: Text(
            '${payment['planName'] ?? AppStrings.t('plans.transaction')}',
          ),
          content: Text(
            AppStrings.format('plans.transactionInfo', {
              'code': payment['transactionCode'] ?? '—',
              'status': _localizedPlanStatus(payment['status']),
              'amount': payment['amount'] ?? '—',
              'currency': payment['currency'] ?? '',
            }),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context),
              child: Text(AppStrings.t('plans.close')),
            ),
          ],
        ),
      );
    } on ApiFailure catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(AppStrings.apiError(error))));
      }
    }
  }

  Future<void> _showPlanDetails(String planId) async {
    try {
      final plan = await _planService.get(planId);
      if (!mounted) return;
      await showDialog<void>(
        context: context,
        builder: (context) => AlertDialog(
          title: Text('${plan['name'] ?? AppStrings.t('plans.planFallback')}'),
          content: SingleChildScrollView(
            child: Text(
              '${plan['description'] ?? ''}\n\n${AppStrings.format('plans.planDetailInfo', {'price': plan['price'] ?? 0, 'days': plan['durationDays'] ?? 0, 'storage': _formatBytes(plan['storageLimit']), 'upload': _formatBytes(plan['maxUploadSize']), 'duration': plan['maxVideoDuration'] ?? 0, 'quality': plan['maxVideoQuality'] ?? '—', 'members': plan['maxMembers'] ?? 1, 'promotion': AppStrings.t(_features(plan['features'])['video_promotion'] == true ? 'common.yes' : 'common.no')})}',
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context),
              child: Text(AppStrings.t('plans.close')),
            ),
          ],
        ),
      );
    } on ApiFailure catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(AppStrings.apiError(error))));
      }
    }
  }

  String _formatBytes(dynamic raw) {
    final bytes = raw is num ? raw.toDouble() : 0;
    if (bytes >= 1024 * 1024 * 1024) {
      return '${(bytes / (1024 * 1024 * 1024)).toStringAsFixed(bytes >= 100 * 1024 * 1024 * 1024 ? 0 : 2)} GB';
    }
    return '${(bytes / (1024 * 1024)).toStringAsFixed(0)} MB';
  }

  double _number(dynamic raw) =>
      raw is num ? raw.toDouble() : double.tryParse(raw?.toString() ?? '') ?? 0;

  int _unallocatedStorage(Map<String, dynamic> plan) {
    final limit = _number(plan['storageLimit']).round();
    var assigned = _number(plan['ownerAllocatedStorage']).round();
    for (final value in (plan['members'] as List? ?? const [])) {
      if (value is Map) assigned += _number(value['allocatedStorage']).round();
    }
    final remaining = limit - assigned;
    return remaining < 0 ? 0 : remaining;
  }

  String _formatDate(dynamic raw) {
    final date = DateTime.tryParse(raw?.toString() ?? '')?.toLocal();
    if (date == null) {
      return '';
    }
    return AppStrings.date(date);
  }

  Map<String, dynamic> _features(dynamic raw) => raw is Map
      ? raw.map((key, value) => MapEntry(key.toString(), value))
      : <String, dynamic>{};

  String _featureText(Map<String, dynamic> plan) {
    final features = _features(plan['features']);
    final labels = <String>[];
    if (features['download'] == true) {
      labels.add(AppStrings.t('common.download'));
    }
    if (features['background_play'] == true) {
      labels.add(AppStrings.t('watch.background'));
    }
    if (features['pip'] == true) labels.add(AppStrings.t('watch.pipAction'));
    if (features['video_promotion'] == true) {
      labels.add(AppStrings.t('plans.videoPromotion'));
    }
    return labels.isEmpty
        ? AppStrings.t('plans.featuresNone')
        : labels.join(' · ');
  }

  String _qualityText(Map<String, dynamic> plan) {
    final upload = plan['maxVideoQuality'] as String? ?? '720p';
    final download = plan['maxDownloadQuality'] as String? ?? upload;
    return AppStrings.format('plans.quality', {
      'upload': upload,
      'download': download,
    });
  }

  String _priceText(Map<String, dynamic> plan) {
    final price = _number(plan['price']);
    if (price <= 0) return AppStrings.t('plans.free');
    final currency = '${plan['currency'] ?? 'VND'}';
    final amount = price == price.roundToDouble()
        ? price.toStringAsFixed(0)
        : price.toStringAsFixed(2);
    return '$amount $currency';
  }

  Widget _planCarousel(BuildContext context) {
    if (_plans.isEmpty) return const SizedBox.shrink();
    final selected = _plans[_selectedPlanIndex.clamp(0, _plans.length - 1)];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SizedBox(
          height: 405,
          child: PageView.builder(
            controller: _planController,
            itemCount: _plans.length,
            onPageChanged: (index) =>
                setState(() => _selectedPlanIndex = index),
            itemBuilder: (context, index) {
              final distance = (index - _selectedPlanIndex).abs();
              return AnimatedPadding(
                duration: const Duration(milliseconds: 220),
                curve: Curves.easeOutCubic,
                padding: EdgeInsets.fromLTRB(
                  6,
                  distance == 0 ? 0 : 18,
                  6,
                  distance == 0 ? 0 : 18,
                ),
                child: AnimatedOpacity(
                  duration: const Duration(milliseconds: 220),
                  opacity: distance == 0 ? 1 : .7,
                  child: _PlanCard(
                    plan: _plans[index],
                    index: index,
                    isFeatured: distance == 0,
                    onShare: () => _sharePlan(_plans[index]),
                    onDetails: () =>
                        _showPlanDetails('${_plans[index]['planId']}'),
                    onSubscribe:
                        widget.auth.authenticated &&
                            _myPlan?['isSharedMember'] != true
                        ? () => _subscribe(_plans[index]['planId'] as String)
                        : null,
                    formatBytes: _formatBytes,
                    qualityText: _qualityText,
                    featureText: _featureText,
                    priceText: _priceText,
                  ),
                ),
              );
            },
          ),
        ),
        const SizedBox(height: 12),
        Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: List.generate(_plans.length, (index) {
            final active = index == _selectedPlanIndex;
            return AnimatedContainer(
              duration: const Duration(milliseconds: 180),
              margin: const EdgeInsets.symmetric(horizontal: 3),
              width: active ? 22 : 7,
              height: 7,
              decoration: BoxDecoration(
                color: active
                    ? AppColors.primary
                    : AppColors.borderFor(context),
                borderRadius: BorderRadius.circular(99),
              ),
            );
          }),
        ),
        const SizedBox(height: 8),
        Center(
          child: Text(
            '${selected['name'] ?? AppStrings.t('plans.planFallback')} · ${AppStrings.t('plans.swipeHint')}',
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ),
      ],
    );
  }

  Widget _currentPlanCard(BuildContext context) {
    final plan = _myPlan;
    if (plan == null) {
      return const SizedBox.shrink();
    }

    final total = _number(plan['storageLimit']);
    final used = _number(plan['usedStorage']);
    final remaining = _number(plan['remainingStorage']);
    final ownerAllocated = _number(plan['ownerAllocatedStorage']);
    final familyStorage = showFamilyStorageUsage(plan);
    final progress = total <= 0
        ? 0.0
        : (used / total).clamp(0.0, 1.0).toDouble();
    final subscription = plan['subscription'] is Map
        ? Map<String, dynamic>.from(plan['subscription'] as Map)
        : null;
    final endedAt = _formatDate(subscription?['endedAt']);
    final members = (plan['members'] as List? ?? const [])
        .whereType<Map>()
        .map((item) => Map<String, dynamic>.from(item))
        .toList();

    return Container(
      margin: const EdgeInsets.only(bottom: 20),
      padding: const EdgeInsets.all(18),
      decoration: BoxDecoration(
        gradient: const LinearGradient(
          colors: [AppColors.ink, Color(0xFF36263C)],
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
        ),
        borderRadius: BorderRadius.circular(18),
      ),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              AppStrings.format('plans.currentTitle', {
                'name':
                    plan['name'] as String? ?? AppStrings.t('common.appName'),
              }),
              style: Theme.of(context).textTheme.titleMedium?.copyWith(
                color: Colors.white,
                fontWeight: FontWeight.w900,
              ),
            ),
            const SizedBox(height: 10),
            Wrap(
              alignment: WrapAlignment.spaceBetween,
              spacing: 12,
              runSpacing: 8,
              children: [
                Text(
                  AppStrings.t(
                    familyStorage
                        ? 'plans.familyStorageTitle'
                        : 'plans.storageUsed',
                  ),
                  style: TextStyle(color: Colors.white.withValues(alpha: .75)),
                ),
                Text(
                  '${_formatBytes(used)} / ${_formatBytes(total)}',
                  style: const TextStyle(
                    color: Colors.white,
                    fontWeight: FontWeight.w800,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 7),
            if (familyStorage)
              FamilyStorageUsage(
                usage: (plan['storageUsageBreakdown'] as List)
                    .whereType<Map>()
                    .toList(),
                totalBytes: total,
                usedBytes: used,
                onDark: true,
              )
            else
              LinearProgressIndicator(
                value: progress,
                color: AppColors.primary,
                backgroundColor: Colors.white.withValues(alpha: .14),
              ),
            const SizedBox(height: 7),
            Text(
              AppStrings.format('plans.remaining', {
                'size': _formatBytes(remaining),
              }),
              style: TextStyle(color: Colors.white.withValues(alpha: .7)),
            ),
            if (endedAt.isNotEmpty) ...[
              const SizedBox(height: 5),
              Text(
                AppStrings.format('plans.expires', {'date': endedAt}),
                style: TextStyle(color: Colors.white.withValues(alpha: .7)),
              ),
            ],
            const SizedBox(height: 8),
            Text(
              _qualityText(plan),
              style: TextStyle(color: Colors.white.withValues(alpha: .7)),
            ),
            const SizedBox(height: 4),
            Text(
              _featureText(plan),
              style: TextStyle(color: Colors.white.withValues(alpha: .7)),
            ),
            if (plan['isSharedMember'] == true) ...[
              const SizedBox(height: 12),
              Row(
                children: [
                  const Icon(
                    Icons.groups_rounded,
                    color: Colors.white70,
                    size: 18,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      AppStrings.format('plans.sharedWithOwner', {
                        'owner':
                            plan['sharedOwnerDisplayName'] ??
                            AppStrings.t('plans.sharedPlanOwnerFallback'),
                      }),
                      style: const TextStyle(color: Colors.white70),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              OutlinedButton.icon(
                onPressed: _leaveSharedPlan,
                icon: const Icon(Icons.logout_rounded),
                label: Text(AppStrings.t('plans.leaveSharedPlan')),
              ),
            ] else if ((plan['maxMembers'] as num? ?? 1) > 1) ...[
              if (showPlanStorageAllocation(plan)) ...[
                const SizedBox(height: 12),
                Row(
                  children: [
                    Icon(
                      Icons.storage_rounded,
                      size: 18,
                      color: Colors.white.withValues(alpha: .75),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        plan['ownerAllocatedStorage'] is num
                            ? AppStrings.format('plans.ownerQuotaValue', {
                                'size': _formatBytes(ownerAllocated),
                              })
                            : AppStrings.t('plans.sharedQuota'),
                        style: TextStyle(
                          color: Colors.white.withValues(alpha: .82),
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                    ),
                  ],
                ),
              ],
              const SizedBox(height: 8),
              OutlinedButton.icon(
                onPressed: _inviteMember,
                icon: const Icon(Icons.person_add_alt_1_outlined),
                label: Text(AppStrings.t('plans.invite')),
              ),
            ],
            if (members.isNotEmpty) ...[
              const SizedBox(height: 12),
              Text(
                AppStrings.t('plans.membersTitle'),
                style: Theme.of(context).textTheme.titleSmall?.copyWith(
                  color: Colors.white,
                  fontWeight: FontWeight.w900,
                ),
              ),
              for (final member in members)
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  dense: true,
                  title: Text(
                    '${member['memberEmail'] ?? AppStrings.t('plans.memberFallback')}',
                    style: const TextStyle(
                      color: Colors.white,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  subtitle: Text(
                    '${_localizedPlanStatus(member['status'])} · ${_formatBytes(member['allocatedStorage'] ?? 0)}',
                    style: TextStyle(
                      color: Colors.white.withValues(alpha: .65),
                    ),
                  ),
                  trailing: plan['isSharedMember'] == true
                      ? null
                      : PopupMenuButton<String>(
                          iconColor: Colors.white,
                          onSelected: (choice) {
                            if (choice == 'remove') _removeMember(member);
                          },
                          itemBuilder: (_) => [
                            PopupMenuItem(
                              value: 'remove',
                              child: Text(AppStrings.t('plans.removeMember')),
                            ),
                          ],
                        ),
                ),
            ],
          ],
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    if (_loading) {
      return ListView(
        padding: const EdgeInsets.fromLTRB(20, 22, 20, 32),
        children: const [
          _PlanSkeleton(height: 150),
          SizedBox(height: 18),
          _PlanSkeleton(height: 180),
          SizedBox(height: 12),
          _PlanSkeleton(height: 180),
        ],
      );
    }
    if (_error != null) {
      return HuTubeStateView(
        icon: Icons.workspace_premium_outlined,
        title: _error!,
        message: AppStrings.t('common.networkError'),
        actionLabel: AppStrings.t('common.retry'),
        onAction: _load,
        accent: AppColors.violet,
      );
    }
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(20, 20, 20, 32),
        children: [
          _planCarousel(context),
          const SizedBox(height: 22),
          _currentPlanCard(context),
          if (_payments.isNotEmpty) ...[
            Text(
              AppStrings.t('plans.paymentHistory'),
              style: Theme.of(
                context,
              ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w900),
            ),
            const SizedBox(height: 4),
            Text(
              AppStrings.t('plans.paymentHistoryDescription'),
              style: Theme.of(context).textTheme.bodySmall,
            ),
            const SizedBox(height: 8),
            for (final payment in _payments.take(5))
              Card(
                child: ListTile(
                  leading: const Icon(
                    Icons.receipt_long_outlined,
                    color: AppColors.violet,
                  ),
                  title: Text(
                    '${payment['planName'] ?? AppStrings.t('plans.planFallback')}',
                  ),
                  subtitle: Text(
                    '${payment['transactionCode'] ?? ''} · ${_localizedPlanStatus(payment['status'])}',
                  ),
                  trailing: Text(
                    '${payment['amount'] ?? ''} ${payment['currency'] ?? ''}',
                    style: const TextStyle(fontWeight: FontWeight.w800),
                  ),
                  onTap: () =>
                      _showPaymentDetails('${payment['paymentId'] ?? ''}'),
                ),
              ),
            const SizedBox(height: 12),
          ],
          if (_plans.isEmpty)
            HuTubeStateView(
              icon: Icons.auto_awesome_outlined,
              title: AppStrings.t('plans.noPlans'),
              message: AppStrings.t('plans.serverNoPlans'),
              compact: true,
              accent: AppColors.violet,
            ),
        ],
      ),
    );
  }
}

class _PlanInviteDraft {
  const _PlanInviteDraft({required this.email, required this.allocatedStorage});

  final String email;
  final int? allocatedStorage;
}

class _PlanInviteDialog extends StatefulWidget {
  const _PlanInviteDialog({
    required this.availableBytes,
    required this.availableBytesLabel,
  });

  final int availableBytes;
  final String availableBytesLabel;

  @override
  State<_PlanInviteDialog> createState() => _PlanInviteDialogState();
}

class _PlanInviteDialogState extends State<_PlanInviteDialog> {
  final _emailController = TextEditingController();
  final _quotaController = TextEditingController();

  double get _maxGb => widget.availableBytes / (1024 * 1024 * 1024);
  double? get _quotaGb => double.tryParse(_quotaController.text.trim());
  String? get _quotaError {
    final text = _quotaController.text.trim();
    final value = _quotaGb;
    if (text.isEmpty || (value != null && value >= 0 && value <= _maxGb)) {
      return null;
    }
    return AppStrings.format('plans.quotaExceedsAvailable', {
      'available': widget.availableBytesLabel,
    });
  }

  @override
  void dispose() {
    _emailController.dispose();
    _quotaController.dispose();
    super.dispose();
  }

  void _submit() {
    final quota = _quotaGb;
    Navigator.of(context).pop(
      _PlanInviteDraft(
        email: _emailController.text,
        allocatedStorage: quota == null
            ? null
            : (quota * 1024 * 1024 * 1024).round(),
      ),
    );
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text(AppStrings.t('plans.inviteDialogTitle')),
    content: Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        TextField(
          controller: _emailController,
          keyboardType: TextInputType.emailAddress,
          autofocus: true,
          decoration: InputDecoration(
            labelText: AppStrings.t('plans.inviteEmail'),
            hintText: AppStrings.t('plans.inviteEmailHint'),
          ),
        ),
        const SizedBox(height: 12),
        TextField(
          controller: _quotaController,
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: [
            FilteringTextInputFormatter.allow(RegExp(r'[0-9.]')),
          ],
          decoration: InputDecoration(
            labelText: AppStrings.t('plans.memberStorageTitle'),
            hintText: AppStrings.t('plans.storageGBPlaceholder'),
            helperText: AppStrings.format('plans.inviteQuotaMax', {
              'size': widget.availableBytesLabel,
            }),
            errorText: _quotaError,
          ),
          onChanged: (_) => setState(() {}),
        ),
      ],
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.of(context).pop(),
        child: Text(AppStrings.t('common.cancel')),
      ),
      FilledButton(
        onPressed: _quotaError == null ? _submit : null,
        child: Text(AppStrings.t('common.send')),
      ),
    ],
  );
}

class _PlanCard extends StatelessWidget {
  const _PlanCard({
    required this.plan,
    required this.index,
    required this.isFeatured,
    required this.onShare,
    required this.onDetails,
    required this.onSubscribe,
    required this.formatBytes,
    required this.qualityText,
    required this.featureText,
    required this.priceText,
  });
  final Map<String, dynamic> plan;
  final int index;
  final bool isFeatured;
  final VoidCallback onShare;
  final VoidCallback onDetails;
  final VoidCallback? onSubscribe;
  final String Function(dynamic) formatBytes;
  final String Function(Map<String, dynamic>) qualityText;
  final String Function(Map<String, dynamic>) featureText;
  final String Function(Map<String, dynamic>) priceText;

  @override
  Widget build(BuildContext context) {
    final accents = [AppColors.primary, AppColors.violet, AppColors.success];
    final accent = accents[index % accents.length];
    final price = priceText(plan);
    return AnimatedContainer(
      duration: const Duration(milliseconds: 220),
      margin: const EdgeInsets.only(bottom: 4),
      padding: const EdgeInsets.all(18),
      decoration: BoxDecoration(
        color: isFeatured
            ? accent.withValues(alpha: .09)
            : AppColors.surfaceFor(context),
        borderRadius: BorderRadius.circular(24),
        border: Border.all(
          color: isFeatured
              ? accent.withValues(alpha: .6)
              : AppColors.borderFor(context),
          width: isFeatured ? 1.5 : 1,
        ),
        boxShadow: isFeatured
            ? [
                BoxShadow(
                  color: accent.withValues(alpha: .18),
                  blurRadius: 24,
                  offset: const Offset(0, 12),
                ),
              ]
            : const [],
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    if (isFeatured)
                      Text(
                        AppStrings.t('plans.explorePlans'),
                        style: TextStyle(
                          color: accent,
                          fontSize: 11,
                          fontWeight: FontWeight.w900,
                          letterSpacing: 1.1,
                        ),
                      ),
                    const SizedBox(height: 3),
                    Text(
                      plan['name'] as String? ?? AppStrings.t('common.appName'),
                      style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w900,
                        letterSpacing: -.4,
                      ),
                    ),
                  ],
                ),
              ),
              InkWell(
                borderRadius: BorderRadius.circular(99),
                onTap: onDetails,
                child: HuTubePill(
                  label: AppStrings.t('plans.details'),
                  color: accent.withValues(alpha: .13),
                  textColor: accent,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Text(
            price,
            style: Theme.of(context).textTheme.headlineSmall?.copyWith(
              color: accent,
              fontWeight: FontWeight.w900,
              letterSpacing: -.8,
            ),
          ),
          const SizedBox(height: 8),
          Text(
            plan['description'] as String? ??
                AppStrings.t('plans.defaultDescription'),
            style: Theme.of(
              context,
            ).textTheme.bodyMedium?.copyWith(height: 1.4),
          ),
          const SizedBox(height: 14),
          Text(
            '${formatBytes(plan['storageLimit'])} · ${AppStrings.format('plans.maxMembers', {'count': AppStrings.number(plan['maxMembers'] ?? 1)})}',
            style: const TextStyle(fontWeight: FontWeight.w800),
          ),
          const SizedBox(height: 7),
          Text(qualityText(plan), style: Theme.of(context).textTheme.bodySmall),
          const SizedBox(height: 5),
          Text(featureText(plan), style: Theme.of(context).textTheme.bodySmall),
          const SizedBox(height: 16),
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: onShare,
                  icon: const Icon(Icons.share_outlined, size: 18),
                  label: Text(AppStrings.t('plans.share')),
                ),
              ),
              if (onSubscribe != null) ...[
                const SizedBox(width: 10),
                Expanded(
                  child: FilledButton(
                    onPressed: onSubscribe,
                    child: Text(AppStrings.t('plans.subscribe')),
                  ),
                ),
              ],
            ],
          ),
        ],
      ),
    );
  }
}

class _PaymentDialog extends StatefulWidget {
  const _PaymentDialog({required this.payment, required this.service});
  final Map<String, dynamic> payment;
  final PaymentService service;

  @override
  State<_PaymentDialog> createState() => _PaymentDialogState();
}

class _PaymentDialogState extends State<_PaymentDialog> {
  late Map<String, dynamic> _payment = widget.payment;
  Timer? _pollTimer;
  bool _checking = false;
  bool _cancelling = false;

  String get _status => '${_payment['status'] ?? 'pending'}'.toLowerCase();
  bool get _paid =>
      _status == 'paid' || _status == 'completed' || _status == 'success';
  bool get _expired =>
      _status == 'expired' || _status == 'cancelled' || _status == 'failed';

  @override
  void initState() {
    super.initState();
    if (!_paid && !_expired) {
      _pollTimer = Timer.periodic(const Duration(seconds: 5), (_) => _poll());
    }
  }

  @override
  void dispose() {
    _pollTimer?.cancel();
    super.dispose();
  }

  Future<void> _poll() async {
    if (_checking || !mounted) return;
    _checking = true;
    try {
      final latest = await widget.service.get('${widget.payment['paymentId']}');
      if (!mounted) return;
      setState(() => _payment = latest);
      if (_paid || _expired) _pollTimer?.cancel();
    } on ApiFailure {
      // Keep the pending payment visible; the user can retry by reopening it.
    } finally {
      _checking = false;
    }
  }

  Future<void> _cancel() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(AppStrings.t('plans.paymentCancel')),
        content: Text(AppStrings.t('plans.paymentCancelConfirm')),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx, false),
            child: Text(AppStrings.t('common.cancel')),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(ctx, true),
            child: Text(AppStrings.t('plans.paymentCancel')),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    setState(() => _cancelling = true);
    try {
      await widget.service.cancelPayment('${_payment['paymentId']}');
      _pollTimer?.cancel();
      if (!mounted) return;
      setState(() => _payment = {..._payment, 'status': 'cancelled'});
    } catch (_) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(AppStrings.t('plans.paymentCancelError'))),
      );
    } finally {
      if (mounted) setState(() => _cancelling = false);
    }
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text(
      AppStrings.t(_paid ? 'plans.paymentReceived' : 'plans.paymentHeader'),
    ),
    content: ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 360, maxHeight: 540),
      child: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (_paid)
              const Padding(
                padding: EdgeInsets.all(16),
                child: Icon(
                  Icons.check_circle_rounded,
                  color: AppColors.success,
                  size: 64,
                ),
              )
            else if (_expired)
              const Padding(
                padding: EdgeInsets.all(16),
                child: Icon(
                  Icons.error_outline_rounded,
                  color: AppColors.danger,
                  size: 56,
                ),
              )
            else if ('${_payment['qrCodeUrl'] ?? ''}'.startsWith('http'))
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Image.network(
                  '${_payment['qrCodeUrl']}',
                  width: 210,
                  height: 210,
                  fit: BoxFit.contain,
                  errorBuilder: (_, _, _) => const SizedBox(
                    width: 210,
                    height: 210,
                    child: Icon(Icons.qr_code_2_rounded, size: 100),
                  ),
                ),
              ),
            Text(
              '${_payment['planName'] ?? AppStrings.t('plans.planFallback')}',
              style: const TextStyle(fontWeight: FontWeight.w900),
            ),
            const SizedBox(height: 5),
            Text(
              '${_payment['amount'] ?? ''} ${_payment['currency'] ?? ''}',
              style: Theme.of(context).textTheme.titleLarge?.copyWith(
                fontWeight: FontWeight.w900,
                color: AppColors.primary,
              ),
            ),
            const SizedBox(height: 12),
            SelectableText(
              '${_payment['transactionCode'] ?? ''}',
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontWeight: FontWeight.w900,
                letterSpacing: 1.2,
              ),
            ),
            TextButton.icon(
              onPressed: () async {
                await Clipboard.setData(
                  ClipboardData(text: '${_payment['transactionCode'] ?? ''}'),
                );
                if (context.mounted) {
                  ScaffoldMessenger.of(context).showSnackBar(
                    SnackBar(
                      content: Text(AppStrings.t('plans.paymentCopied')),
                    ),
                  );
                }
              },
              icon: const Icon(Icons.copy_rounded, size: 17),
              label: Text(AppStrings.t('plans.copyPayment')),
            ),
            const SizedBox(height: 8),
            Text(
              _paid
                  ? AppStrings.t('plans.activated')
                  : _expired
                  ? AppStrings.t('plans.paymentExpired')
                  : AppStrings.t('plans.paymentInstructions'),
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodySmall,
            ),
            if (_checking && !_paid && !_expired) ...[
              const SizedBox(height: 12),
              const LinearProgressIndicator(minHeight: 2),
            ],
          ],
        ),
      ),
    ),
    actions: [
      if (!_paid && !_expired)
        TextButton(
          onPressed: _poll,
          child: Text(AppStrings.t('plans.checkPayment')),
        ),
      if (!_paid && !_expired)
        TextButton(
          onPressed: _cancelling ? null : _cancel,
          child: Text(AppStrings.t('plans.paymentCancel')),
        ),
      TextButton(
        onPressed: () => Navigator.pop(context, _paid),
        child: Text(AppStrings.t(_paid ? 'plans.paymentDone' : 'plans.close')),
      ),
    ],
  );
}

class _PlanSkeleton extends StatelessWidget {
  const _PlanSkeleton({required this.height});
  final double height;

  @override
  Widget build(BuildContext context) => Container(
    height: height,
    decoration: BoxDecoration(
      color: AppColors.borderSubtle,
      borderRadius: BorderRadius.circular(16),
    ),
  );
}
