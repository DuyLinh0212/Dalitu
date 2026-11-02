using HuTube.Api.Services;
using Microsoft.AspNetCore.Http;

namespace HuTube.IntegrationTests;

public sealed class PlannedChunkIoTests
{
    [Fact]
    public async Task B_MEDIA_02_WriteFailureDoesNotAdvanceCheckpointAndRetryCommitsOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "hutube-planned-io-" + Guid.NewGuid().ToString("N"));
        var actor = Guid.NewGuid();
        var id = Guid.NewGuid();
        var request = new VideoUploadSessionRequest(id, Guid.NewGuid(), "source.mp4", "video/mp4", 3, 1, "720p");
        try
        {
            using var store = new VideoChunkUploadStore(root);
            store.Start(actor, request);
            var path = Path.Combine(root, id.ToString("N") + ".upload");
            await File.WriteAllBytesAsync(path, []);
            // A real sharing violation exercises the filesystem write-failure path
            // without filling a user's disk or mocking persistence.
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using var bytes = new MemoryStream(new byte[] { 1, 2, 3 });
                await Assert.ThrowsAnyAsync<IOException>(() => store.AppendAsync(actor, id, 0,
                    new FormFile(bytes, 0, 3, "chunk", "part"), default));
                Assert.Equal(0, store.Require(actor, id).NextChunk);
                Assert.Equal(0, new FileInfo(path).Length);
            }
            using var retry = new MemoryStream(new byte[] { 1, 2, 3 });
            Assert.Equal(1, await store.AppendAsync(actor, id, 0,
                new FormFile(retry, 0, 3, "chunk", "part"), default));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            await store.CancelAsync(actor, id, default);
            Assert.False(File.Exists(path));
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
