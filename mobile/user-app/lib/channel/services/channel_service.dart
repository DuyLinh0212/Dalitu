import 'package:image_picker/image_picker.dart';
import '../../auth.dart';
import '../../core/localization/app_strings.dart';
import '../models/channel_models.dart';
import '../channel_handle.dart';

class ChannelService {
  ChannelService(this.auth);
  final AuthController auth;

  Future<ChannelDetail?> getMyChannel() async {
    try {
      final res = await auth.protected('GET', '/channels/me');
      return ChannelDetail.fromJson(res);
    } on ApiFailure catch (e) {
      if (e.status == 404) return null;
      rethrow;
    }
  }

  Future<List<ChannelDetail>> getAccessibleChannels() async {
    final items = await auth.protectedList('/channels/accessible');
    return items
        .whereType<Map<String, dynamic>>()
        .map(ChannelDetail.fromJson)
        .toList();
  }

  Future<ChannelDetail> getChannel(String handleOrId) async {
    final value = handleOrId.trim();
    // Channel cards and own-channel navigation can use the stable server ID.
    if (!value.startsWith('@') &&
        RegExp(
          r'^[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$',
        ).hasMatch(value)) {
      return getChannelById(value);
    }
    final clean = normalizeChannelHandle(value);
    final path = '/channels/handle/${Uri.encodeComponent(clean)}';
    final res = auth.authenticated
        ? await auth.protected('GET', path)
        : await auth.api.request('GET', path);
    return ChannelDetail.fromJson(res);
  }

  Future<ChannelDetail> getChannelById(
    String channelId, {
    bool refresh = false,
  }) async {
    final res = auth.authenticated
        ? await auth.protected(
            'GET',
            '/channels/${Uri.encodeComponent(channelId)}${refresh ? '?_refresh=1' : ''}',
          )
        : await auth.api.request(
            'GET',
            '/channels/${Uri.encodeComponent(channelId)}${refresh ? '?_refresh=1' : ''}',
          );
    return ChannelDetail.fromJson(res);
  }

  Future<bool> checkHandle(String handle, {String? currentChannelId}) async {
    final query = Uri(
      queryParameters: {
        'handle': normalizeChannelHandle(handle),
        'currentChannelId': ?currentChannelId,
      },
    ).query;
    final res = await auth.api.request('GET', '/channels/check-handle?$query');
    return res['isAvailable'] as bool? ?? false;
  }

  Future<ChannelDetail> createChannel({
    required String name,
    required String handle,
    String? description,
    String? contactEmail,
  }) async {
    final clean = normalizeChannelHandle(handle);
    final res = await auth.protected(
      'POST',
      '/channels',
      body: {
        'name': name.trim(),
        'handle': clean.trim(),
        'description': description?.trim(),
        if (contactEmail != null) 'contactEmail': contactEmail.trim(),
      },
    );
    return ChannelDetail.fromJson(res);
  }

  Future<ChannelDetail> updateChannel(
    String id, {
    required String name,
    String? handle,
    String? description,
    String? settings,
    String? contactEmail,
  }) async {
    final res = await auth.protected(
      'PATCH',
      '/channels/${Uri.encodeComponent(id)}',
      body: {
        'name': name.trim(),
        if (handle != null) 'handle': normalizeChannelHandle(handle),
        'description': description?.trim(),
        // ignore: use_null_aware_elements
        if (settings != null) 'settings': settings,
        if (contactEmail != null) 'contactEmail': contactEmail.trim(),
      },
    );
    return ChannelDetail.fromJson(res);
  }

  Future<ChannelDetail> uploadAvatar(String channelId, XFile file) =>
      _uploadImage(channelId, file, avatar: true);

  Future<ChannelDetail> uploadBanner(String channelId, XFile file) =>
      _uploadImage(channelId, file, avatar: false);

  Future<ChannelDetail> uploadWatermark(String channelId, XFile file) async {
    final contentType = file.mimeType ?? _contentTypeFromName(file.name);
    if (!const {
      'image/jpeg',
      'image/png',
      'image/webp',
    }.contains(contentType)) {
      throw ApiFailure(
        400,
        'INVALID_FILE_TYPE',
        AppStrings.t('channel.watermarkTypes'),
      );
    }
    final bytes = await file.readAsBytes();
    if (bytes.length > 2 * 1024 * 1024) {
      throw ApiFailure(
        400,
        'FILE_TOO_LARGE',
        AppStrings.t('channel.watermarkSize'),
      );
    }
    final response = await auth.protectedUpload(
      '/channels/${Uri.encodeComponent(channelId)}/watermark',
      UploadPayload(
        bytes: bytes,
        fileName: file.name.trim().isEmpty
            ? 'channel-watermark.png'
            : file.name,
        contentType: contentType,
      ),
    );
    return ChannelDetail.fromJson(response);
  }

  Future<ChannelDetail> _uploadImage(
    String channelId,
    XFile file, {
    required bool avatar,
  }) async {
    final contentType = file.mimeType ?? _contentTypeFromName(file.name);
    if (!_allowedImageTypes.contains(contentType) ||
        (!avatar && contentType == 'image/gif')) {
      throw ApiFailure(
        400,
        'INVALID_FILE_TYPE',
        AppStrings.t(
          avatar ? 'common.imageTypesAvatar' : 'common.imageTypesBanner',
        ),
      );
    }
    final bytes = await file.readAsBytes();
    final maximumBytes = avatar ? 5 * 1024 * 1024 : 10 * 1024 * 1024;
    if (bytes.length > maximumBytes) {
      throw ApiFailure(
        400,
        'FILE_TOO_LARGE',
        AppStrings.format('common.channelImageMaxSize', {
          'size': maximumBytes ~/ 1024 ~/ 1024,
        }),
      );
    }
    final response = await auth.protectedUpload(
      '/channels/${Uri.encodeComponent(channelId)}/${avatar ? 'avatar' : 'banner'}',
      UploadPayload(
        bytes: bytes,
        fileName: file.name.trim().isEmpty
            ? (avatar ? 'channel-avatar.jpg' : 'channel-banner.jpg')
            : file.name,
        contentType: contentType,
      ),
    );
    return ChannelDetail.fromJson(response);
  }

  static const _allowedImageTypes = {
    'image/jpeg',
    'image/png',
    'image/webp',
    'image/gif',
  };

  static String _contentTypeFromName(String name) {
    final lower = name.toLowerCase();
    if (lower.endsWith('.jpg') || lower.endsWith('.jpeg')) return 'image/jpeg';
    if (lower.endsWith('.png')) return 'image/png';
    if (lower.endsWith('.webp')) return 'image/webp';
    if (lower.endsWith('.gif')) return 'image/gif';
    return 'application/octet-stream';
  }

  Future<void> deleteChannel(String id) async {
    await auth.protected('DELETE', '/channels/${Uri.encodeComponent(id)}');
  }

  Future<List<ChannelRole>> getRoles() async {
    final items = await auth.protectedList('/channels/roles');
    return items
        .whereType<Map<String, dynamic>>()
        .map(ChannelRole.fromJson)
        .toList();
  }

  Future<List<ChannelMember>> getMembers(String channelId) async {
    final items = await auth.protectedList('/channels/$channelId/members');
    return items
        .whereType<Map<String, dynamic>>()
        .map(ChannelMember.fromJson)
        .toList();
  }

  Future<List<ChannelInvitation>> getPendingInvitations(
    String channelId,
  ) async {
    final items = await auth.protectedList('/channels/$channelId/invitations');
    return items
        .whereType<Map<String, dynamic>>()
        .map(ChannelInvitation.fromJson)
        .toList();
  }

  Future<List<ChannelInvitation>> getMyInvitations() async {
    final items = await auth.protectedList('/channels/invitations/me');
    return items
        .whereType<Map<String, dynamic>>()
        .map(ChannelInvitation.fromJson)
        .toList();
  }

  Future<void> inviteMember(
    String channelId,
    String email,
    String roleCode,
  ) async {
    await auth.protected(
      'POST',
      '/channels/$channelId/members/invite',
      body: {'email': email.trim(), 'roleCode': roleCode},
    );
  }

  Future<void> acceptInvitation(String invitationId) async {
    await auth.protected('POST', '/channels/invitations/$invitationId/accept');
  }

  Future<void> declineInvitation(String invitationId) async {
    await auth.protected('POST', '/channels/invitations/$invitationId/decline');
  }

  Future<void> revokeInvitation(String channelId, String invitationId) async {
    await auth.protected(
      'DELETE',
      '/channels/$channelId/invitations/$invitationId',
    );
  }

  Future<void> changeMemberRole(
    String channelId,
    String userId,
    String roleCode,
  ) async {
    await auth.protected(
      'PATCH',
      '/channels/$channelId/members/$userId/role',
      body: {'roleCode': roleCode},
    );
  }

  Future<void> removeMember(String channelId, String userId) async {
    await auth.protected('DELETE', '/channels/$channelId/members/$userId');
  }

  Future<Map<String, dynamic>> subscribe(String channelId) async {
    final response = await auth.protected(
      'POST',
      '/channels/${Uri.encodeComponent(channelId)}/subscribe',
      body: const {},
    );
    return Map<String, dynamic>.from(response);
  }

  Future<void> unsubscribe(String channelId) async {
    await auth.protected(
      'DELETE',
      '/channels/${Uri.encodeComponent(channelId)}/subscribe',
    );
  }

  Future<Map<String, dynamic>?> getSubscriptionStatus(String channelId) async {
    try {
      final response = await auth.protected(
        'GET',
        '/channels/${Uri.encodeComponent(channelId)}/subscribe-status',
      );
      return Map<String, dynamic>.from(response);
    } on ApiFailure catch (error) {
      if (error.status == 404) return null;
      rethrow;
    }
  }

  Future<Map<String, dynamic>> updateSubscriptionNotifications(
    String channelId,
    bool enabled,
  ) async {
    final response = await auth.protected(
      'PATCH',
      '/channels/${Uri.encodeComponent(channelId)}/subscribe-notifications',
      body: {'enabled': enabled},
    );
    return Map<String, dynamic>.from(response);
  }

  Future<List<SubscribedChannelResponse>> getSubscribedChannels() async {
    try {
      final items = await auth.protectedList('/subscriptions');
      return items
          .whereType<Map<String, dynamic>>()
          .map(SubscribedChannelResponse.fromJson)
          .toList();
    } catch (_) {
      return const [];
    }
  }
}
