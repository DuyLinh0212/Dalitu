import 'dart:async';
import 'dart:io';
import 'dart:math';

import '../../auth.dart';
import '../../core/network/upload_control.dart';

/// Uses the same resumable session and asynchronous completion API as user-web.
class VideoUploadTransport {
  VideoUploadTransport(
    this.auth, {
    this.pollInterval = const Duration(seconds: 5),
  });
  final AuthController auth;
  final Duration pollInterval;

  Future<Map<String, dynamic>> upload({
    required String uploadId,
    required String channelId,
    required int duration,
    required String quality,
    required Map<String, String> fields,
    required MultipartFilePayload video,
    required UploadControl control,
    MultipartFilePayload? thumbnail,
    void Function(int, int)? onProgress,
  }) async {
    final generation = auth.sessionGeneration;
    void check() {
      control.check();
      if (generation != auth.sessionGeneration || !auth.authenticated) {
        throw const UploadCancelled();
      }
    }

    Future<void> pause(Duration delay) async {
      await Future.any([Future<void>.delayed(delay), control.aborted]);
      check();
    }

    Future<Map<String, dynamic>> attempt(
      Future<Map<String, dynamic>> Function() send,
    ) async {
      for (var retry = 0; ; retry++) {
        check();
        try {
          final result = await send();
          check();
          return result;
        } on ApiFailure catch (error) {
          check();
          final transient =
              error.status == 0 || [429, 502, 503, 504].contains(error.status);
          if (!transient || retry >= 2) rethrow;
          await pause(
            Duration(seconds: error.status == 429 ? 60 : (retry + 1) * 2),
          );
        }
      }
    }

    final path = '/videos/upload-sessions/${Uri.encodeComponent(uploadId)}';
    check();
    final source = File(video.path);
    final length = await source.length();
    var started = false;
    try {
      final session = await attempt(
        () => auth.protected(
          'POST',
          '/videos/upload-sessions',
          body: {
            'uploadId': uploadId,
            'channelId': channelId,
            'fileName': video.fileName,
            'contentType': video.contentType,
            'fileSize': length,
            'duration': duration,
            'sourceQuality': quality,
          },
        ),
      );
      started = true;
      final chunkSize = session['chunkSize'];
      final next = session['nextChunk'];
      final protocol = session['chunkProtocol'] ?? 1;
      if (session['uploadId'] != uploadId ||
          chunkSize is! int ||
          chunkSize <= 0 ||
          chunkSize > 8 * 1024 * 1024 ||
          protocol is! int ||
          ![1, 2].contains(protocol) ||
          next is! int ||
          next < 0 ||
          next > (length / chunkSize).ceil()) {
        throw const ApiFailure(
          502,
          'INVALID_UPLOAD_SESSION',
          'Invalid upload session',
        );
      }
      onProgress?.call(min(next * chunkSize, length), length);
      final file = await source.open();
      try {
        for (var index = next; index * chunkSize < length; index++) {
          check();
          final offset = index * chunkSize;
          await file.setPosition(offset);
          final bytes = await file.read(min(chunkSize, length - offset));
          final chunkPath = protocol == 2
              ? '$path/chunks'
              : '$path/chunks/$index';
          final result = await attempt(
            () => auth.protectedMultipart(
              chunkPath,
              fields: const {},
              files: [
                MultipartFilePayload(
                  field: 'chunk',
                  path: '',
                  fileName: 'chunk',
                  contentType: 'application/octet-stream',
                  bytes: bytes,
                ),
              ],
              headers: protocol == 2
                  ? {'X-Upload-Chunk-Index': '$index'}
                  : null,
              uploadControl: control,
              onProgress: (sent, total) => onProgress?.call(
                offset +
                    min(
                      bytes.length - 1,
                      total > 0 ? sent * bytes.length ~/ total : 0,
                    ),
                length,
              ),
            ),
          );
          if (result['nextChunk'] != index + 1) {
            throw const ApiFailure(
              502,
              'INVALID_UPLOAD_SESSION',
              'Invalid chunk acknowledgement',
            );
          }
          onProgress?.call(min(offset + bytes.length, length), length);
        }
      } finally {
        await file.close();
      }
      var result = await attempt(
        () => auth.protectedMultipart(
          '$path/complete',
          fields: fields,
          files: [?thumbnail],
          headers: const {'Prefer': 'respond-async'},
          uploadControl: control,
        ),
      );
      // A completed session returns VideoResponse, whose own status may still
      // be "processing" (renditions/moderation). Only poll session receipts;
      // waiting on the video's status can otherwise keep this upload busy forever.
      while (!_hasVideo(result) &&
          (result['status'] == 'processing' ||
              result['status'] == 'uploading')) {
        await pause(pollInterval);
        result = await attempt(() => auth.protected('GET', path));
      }
      if (!_hasVideo(result)) {
        throw const ApiFailure(
          502,
          'INVALID_UPLOAD_RESPONSE',
          'Invalid upload response',
        );
      }
      return result;
    } on UploadCancelled {
      // Stop server-side processing too when the user explicitly cancels.
      if (started &&
          generation == auth.sessionGeneration &&
          auth.authenticated) {
        unawaited(
          auth
              .protected('DELETE', path)
              .then<void>((_) {}, onError: (Object _) {}),
        );
      }
      rethrow;
    }
  }

  static bool _hasVideo(Map<String, dynamic> result) =>
      result['videoId'] is String &&
      (result['videoId'] as String).trim().isNotEmpty;
}
