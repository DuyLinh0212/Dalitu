import '../../core/widgets/scrollable_sheet.dart';
import '../../core/widgets/modal_header.dart';
import 'package:flutter/material.dart';

import '../../auth.dart';
import '../../core/localization/app_strings.dart';
import 'moderation_service.dart';

Future<void> showContentReportDialog(
  BuildContext context, {
  required AuthController auth,
  required String targetType,
  required String targetId,
}) async {
  if (!auth.authenticated) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(AppStrings.t('report.loginRequired'))),
    );
    return;
  }
  final service = ModerationService(auth);
  try {
    final types = await service.violationTypes();
    if (!context.mounted) return;
    if (types.isEmpty) {
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(AppStrings.t('report.noReasons'))));
      return;
    }
    final draft = await showModalBottomSheet<_ReportDraft>(
      context: context,
      isScrollControlled: true,
      builder: (_) => _ContentReportSheet(targetType: targetType, types: types),
    );
    if (draft == null || !context.mounted) return;
    try {
      switch (targetType) {
        case 'video':
          await service.reportVideo(
            videoId: targetId,
            violationTypeId: draft.violationTypeId,
            description: draft.description,
          );
        case 'channel':
          await service.reportChannel(
            channelId: targetId,
            violationTypeId: draft.violationTypeId,
            description: draft.description,
          );
        case 'comment':
          await service.reportComment(
            commentId: targetId,
            violationTypeId: draft.violationTypeId,
            description: draft.description,
          );
        default:
          await service.createReport(
            targetType: targetType,
            targetId: targetId,
            violationTypeId: draft.violationTypeId,
            description: draft.description,
          );
      }
      if (context.mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(AppStrings.t('report.success'))));
      }
    } on ApiFailure catch (error) {
      if (context.mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(AppStrings.apiError(error))));
      }
    }
  } on ApiFailure catch (error) {
    if (context.mounted) {
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(AppStrings.apiError(error))));
    }
  }
}

class _ReportDraft {
  const _ReportDraft({
    required this.violationTypeId,
    required this.description,
  });

  final String violationTypeId;
  final String description;
}

class _ContentReportSheet extends StatefulWidget {
  const _ContentReportSheet({required this.targetType, required this.types});

  final String targetType;
  final List<Map<String, dynamic>> types;

  @override
  State<_ContentReportSheet> createState() => _ContentReportSheetState();
}

class _ContentReportSheetState extends State<_ContentReportSheet> {
  late final TextEditingController _description = TextEditingController();
  late String? _selected = '${widget.types.first['violationTypeId'] ?? ''}';

  @override
  void dispose() {
    _description.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => StatefulBuilder(
    builder: (context, refresh) => ScrollableSheet(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(20, 12, 20, 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            ModalHeader(
              padding: EdgeInsets.zero,
              title: AppStrings.format('report.title', {
                'target': _targetLabel(widget.targetType),
              }),
              subtitle: AppStrings.t('report.instruction'),
            ),
            const SizedBox(height: 14),
            DropdownButtonFormField<String>(
              isExpanded: true,
              initialValue: _selected,
              decoration: InputDecoration(
                labelText: AppStrings.t('report.reason'),
              ),
              items: [
                for (final type in widget.types)
                  DropdownMenuItem(
                    value: '${type['violationTypeId'] ?? ''}',
                    child: Text(
                      '${type['name'] ?? type['code'] ?? AppStrings.t('report.other')}',
                    ),
                  ),
              ],
              onChanged: (value) => setState(() => _selected = value),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _description,
              minLines: 3,
              maxLines: 5,
              maxLength: 1000,
              onChanged: (_) => refresh(() {}),
              decoration: InputDecoration(
                labelText: AppStrings.t('report.description'),
                hintText: AppStrings.t('report.descriptionHint'),
              ),
            ),
            const SizedBox(height: 10),
            FilledButton(
              onPressed: _selected == null || _description.text.trim().isEmpty
                  ? null
                  : () => Navigator.pop(
                      context,
                      _ReportDraft(
                        violationTypeId: _selected!,
                        description: _description.text.trim(),
                      ),
                    ),
              child: Text(AppStrings.t('report.submit')),
            ),
            TextButton(
              onPressed: () => Navigator.pop(context),
              child: Text(AppStrings.t('common.cancel')),
            ),
          ],
        ),
      ),
    ),
  );
}

String _targetLabel(String targetType) => switch (targetType) {
  'video' => AppStrings.t('report.target.video'),
  'channel' => AppStrings.t('report.target.channel'),
  'comment' => AppStrings.t('report.target.comment'),
  _ => AppStrings.t('report.target.content'),
};
