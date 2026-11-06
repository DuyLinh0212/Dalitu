import 'dart:async';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/testing.dart';
import 'package:user_app/auth.dart';
import 'package:user_app/core/network/upload_control.dart';
import 'package:user_app/features/creator/video_upload_transport.dart';

import 'auth_test.dart' show MemoryStore, jsonResponse;

void main() {
  setUp(ApiClient.invalidateReadCache);
  for (final terminalStatus in ['processing', 'ready', 'published', 'failed']) {
    for (final cancel in [false, true]) {
      test(
        'resume chunks and ${cancel ? 'cancel' : 'finish'} async upload with video status=$terminalStatus',
        () async {
          final directory = await Directory.systemTemp.createTemp(
            'hutube-chunks-',
          );
          addTearDown(() async {
            expect(
              directory.absolute.path.startsWith(
                '${Directory.systemTemp.absolute.path}${Platform.pathSeparator}',
              ),
              isTrue,
            );
            await directory.delete(recursive: true);
          });
          final file = await File(
            '${directory.path}/video.mp4',
          ).writeAsBytes([1, 2, 3, 4, 5, 6]);
          const id = 'f6703ff8-51ae-41bb-9d89-fb9df3402f13';
          final control = UploadControl();
          final chunks = <String>[];
          final progress = <int>[];
          var polls = 0;
          final deleted = Completer<void>();
          final client = MockClient((r) async {
            if (r.method == 'DELETE') {
              deleted.complete();
              return jsonResponse({});
            }
            if (r.url.path.endsWith('/upload-sessions')) {
              return jsonResponse({
                'uploadId': id,
                'chunkSize': 2,
                'nextChunk': 1,
              });
            }
            if (r.url.path.contains('/chunks/')) {
              chunks.add(r.url.path.split('/').last);
              return jsonResponse({'nextChunk': int.parse(chunks.last) + 1});
            }
            if (r.url.path.endsWith('/complete')) {
              expect(r.headers['prefer'], 'respond-async');
              expect(r.body, isNot(contains('name="Video"')));
              if (cancel) control.cancel();
              return jsonResponse({'status': 'processing'}, 202);
            }
            polls++;
            expect(
              polls,
              lessThanOrEqualTo(2),
              reason:
                  'A videoId completes the upload even when the video is processing',
            );
            return polls == 1
                ? jsonResponse({'status': 'processing'}, 202)
                : jsonResponse({'videoId': 'saved', 'status': terminalStatus});
          });
          final auth = AuthController(ApiClient(client: client), MemoryStore())
            ..user = {'userId': 'one'};
          addTearDown(() {
            auth.dispose();
            client.close();
          });
          final future = VideoUploadTransport(auth, pollInterval: Duration.zero)
              .upload(
                uploadId: id,
                channelId: 'channel',
                duration: 5,
                quality: '720p',
                fields: {'ChannelId': 'channel', 'Title': 'Video'},
                video: MultipartFilePayload(
                  field: 'Video',
                  path: file.path,
                  fileName: 'video.mp4',
                  contentType: 'video/mp4',
                ),
                control: control,
                onProgress: (sent, total) {
                  expect(total, 6);
                  progress.add(sent);
                },
              );
          if (cancel) {
            await expectLater(future, throwsA(isA<UploadCancelled>()));
            await deleted.future;
            expect(polls, 0);
          } else {
            expect((await future)['videoId'], 'saved');
            expect(polls, 2);
            expect(progress.first, 2);
            expect(progress.last, 6);
          }
          expect(chunks, ['1', '2']);
        },
      );
    }
  }
}
