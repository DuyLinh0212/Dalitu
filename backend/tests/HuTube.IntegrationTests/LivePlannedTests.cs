using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HuTube.Application.Auth;
using HuTube.Application.Notifications;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Recommendations;
using HuTube.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HuTube.IntegrationTests;

public sealed class LivePlannedTests(LivePlannedFixture f) : IClassFixture<LivePlannedFixture>
{
    [LivePlannedFact]
    public async Task B_MEDIA_03_ProductionR2Adapter_RoundTripsSignedUrlExpiryAndDelete()
    {
        // Arrange: production adapter, real R2, unique owned prefix.
        using var storage = new R2ObjectStorageService(f.Storage);
        var bytes = RandomNumberGenerator.GetBytes(4096);
        string? stored = null;
        try
        {
            // Act.
            stored = await storage.SaveFileAsync(f.Prefix + "media", "fixture.bin", new MemoryStream(bytes), "application/octet-stream");
            await using var source = await storage.OpenReadAsync(stored);
            using var buffer = new MemoryStream(); await source.CopyToAsync(buffer);
            using var client = new HttpClient();
            var signed = await storage.GetReadUrlAsync(stored, TimeSpan.FromSeconds(2));
            var immediate = await client.GetAsync(signed);
            await Task.Delay(4000);
            var expired = await client.GetAsync(signed);
            await storage.DeleteFileAsync(stored);
            var deleted = await client.GetAsync(await storage.GetReadUrlAsync(stored, TimeSpan.FromMinutes(1)));
            f.Record("B-MEDIA.03", new { bytes = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                immediate = (int)immediate.StatusCode, expired = (int)expired.StatusCode, deleted = (int)deleted.StatusCode });
            // Assert actual bytes and actual R2 authorization/absence responses.
            Assert.Equal(bytes, buffer.ToArray()); Assert.Equal(bytes, await immediate.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.OK, immediate.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode); Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }
        finally { if (stored != null) await storage.DeleteFileAsync(stored); }
    }

    [LivePlannedFact]
    public async Task B_MEDIA_04_PickupAndVerifiedGoogleIdentity_LinkRetrySessionAndOrigin()
    {
        // Scope agreed by user: Google UI/provider verification belongs to web E2E.
        // Only the verifier boundary is stubbed; HTTP/auth/database/Pickup are real.
        using var client = f.A.CreateClient();
        const string address = "google.user@example.com";
        (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(
            "google_" + Guid.NewGuid().ToString("N")[..12], address, "Google link fixture", LivePlannedFixture.Password))).EnsureSuccessStatusCode();
        var pickupToken = f.ReadPickupToken(address);
        (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new TokenRequest(pickupToken))).EnsureSuccessStatusCode();
        var payload = new GoogleLoginRequest("valid-google-token-123", "mobile", "Google test", "google-device");
        var first = await client.PostAsJsonAsync("/api/v1/auth/google", payload);
        var login = (await first.Content.ReadFromJsonAsync<LoginResponse>())!;
        var retry = await client.PostAsJsonAsync("/api/v1/auth/google", payload);
        var again = (await retry.Content.ReadFromJsonAsync<LoginResponse>())!;
        using var session = LivePlannedFixture.Authorized(f.B, again.AccessToken);
        var me = await session.GetFromJsonAsync<UserResponse>("/api/v1/auth/me");
        var invalid = await client.PostAsJsonAsync("/api/v1/auth/google", payload with { Credential = "invalid-google-token-123" });
        using var browser = f.B.CreateClient(); browser.DefaultRequestHeaders.Add("X-HuTube-Client", "web");
        browser.DefaultRequestHeaders.Add("Origin", "https://invalid-origin.example");
        var origin = await browser.PostAsJsonAsync("/api/v1/auth/google", payload with { Platform = "web" });
        await using var db = f.Db();
        var users = await db.Users.Where(x => x.Email == address).ToListAsync();
        var sessions = await db.Sessions.CountAsync(x => x.UserId == login.User.UserId && x.RevokedAt == null);
        f.Record("B-MEDIA.04", new { scope = "User agreed: backend after verified identity; live Google UI deferred to web E2E",
            realPickupLink = true, first = (int)first.StatusCode, retry = (int)retry.StatusCode,
            invalid = (int)invalid.StatusCode, origin = (int)origin.StatusCode, userCount = users.Count, sessions });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(login.User.UserId, again.User.UserId); Assert.Equal(login.User.UserId, me!.UserId);
        Assert.True(login.User.EmailVerified); Assert.Single(users); Assert.Equal("google-subject-123", users[0].GoogleSubject);
        Assert.Equal(1, sessions); Assert.NotEmpty(again.RefreshToken!);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode); Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
    }

    [LivePlannedFact]
    public async Task B_MEDIA_05_RealCfTrainR2SnapshotAndFeed_WithSignedWebhookBoundary()
    {
        // Arrange/Act: backend worker exports known DB rows, real CF trains/publishes on R2.
        var manifest = await f.TrainKnownModel();
        using var cf = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:55352") };
        cf.DefaultRequestHeaders.Add("X-Service-Token", LivePlannedFixture.Required("LIVE_CF_TOKEN"));
        var recommendation = await cf.PostAsJsonAsync("/internal/recommendations", new {
            userId = f.Users[0].ToString(), limit = 10, excludeItemIds = new[] { f.Videos[0].ToString(), f.Videos[1].ToString() } });
        var ranked = (await recommendation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        var feed = await f.Admin.GetFromJsonAsync<JsonElement>("/api/v1/feed/recommended?page=1&pageSize=10");
        using var anonymous = f.A.CreateClient();
        var body = JsonSerializer.Serialize(new { id = 88990011, gateway = "test-bank", transactionDate = "2026-10-23",
            accountNumber = "test-account", content = "NO-PAYMENT-FIXTURE", transferType = "in", transferAmount = 1, accumulated = 1 });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("live-test-no-money"),
            Encoding.UTF8.GetBytes(timestamp + "." + body)));
        async Task<HttpResponseMessage> Webhook(string value)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/webhook/sepay") {
                Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-SePay-Signature", value); request.Headers.Add("X-SePay-Timestamp", timestamp);
            return await anonymous.SendAsync(request);
        }
        var valid = await Webhook(signature); var forged = await Webhook("sha256=" + new string('0',64));
        var unsignedCf = await anonymous.PostAsJsonAsync("http://127.0.0.1:55352/internal/recommendations", new { userId = "unknown" });
        f.Record("B-MEDIA.05", new { version = manifest.GetProperty("modelVersion").GetString(), realCf = true,
            realR2 = true, ranked, feed, signedWebhook = (int)valid.StatusCode, forgedWebhook = (int)forged.StatusCode,
            unsignedCf = (int)unsignedCf.StatusCode, payment = "Synthetic signed inbound payload, no money transferred" });
        Assert.Equal(HttpStatusCode.OK, recommendation.StatusCode);
        Assert.Contains(ranked.EnumerateArray(), x => x.GetProperty("itemId").GetString() == f.Videos[2].ToString());
        Assert.Contains(feed.GetProperty("items").EnumerateArray(), x => x.GetProperty("videoId").GetGuid() == f.Videos[2]);
        Assert.DoesNotContain(feed.GetProperty("items").EnumerateArray(), x => x.GetProperty("videoId").GetGuid() == f.Videos[0]);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode); Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unsignedCf.StatusCode);
    }

    [LivePlannedFact]
    public async Task B_CACHE_14_TwoRealKestrelHosts_ShareRedisAndRejectRevokedSession()
    {
        // Independent Kestrel hosts share only Redis; their DI providers are separate.
        using var a = LivePlannedFixture.Authorized(f.A, f.Tokens[1]);
        using var b = LivePlannedFixture.Authorized(f.B, f.Tokens[1]);
        await f.A.Services.GetRequiredService<HybridCache>().RemoveAsync($"notifications:{f.Users[1]:N}:recent");
        await f.B.Services.GetRequiredService<HybridCache>().RemoveAsync($"notifications:{f.Users[1]:N}:recent");
        var first = (await a.GetFromJsonAsync<NotificationPage>("/api/v1/notifications"))!;
        var before = f.B.Transport.RedisCalls;
        var other = (await b.GetFromJsonAsync<NotificationPage>("/api/v1/notifications"))!;
        var sharedRead = f.B.Transport.RedisCalls - before;
        var unread = first.Items.First(x => !x.IsRead);
        (await a.PatchAsync($"/api/v1/notifications/{unread.NotificationId}/read", null)).EnsureSuccessStatusCode();
        var immediate = (await b.GetFromJsonAsync<NotificationPage>("/api/v1/notifications"))!;
        var repeated = (await b.GetFromJsonAsync<NotificationPage>("/api/v1/notifications"))!;
        using var otherUser = LivePlannedFixture.Authorized(f.B, f.Tokens[2]);
        var isolated = (await otherUser.GetFromJsonAsync<NotificationPage>("/api/v1/notifications"))!;
        // A dedicated new session is revoked without breaking the other test fixtures.
        await using var db = f.Db();
        var email = await db.Users.Where(x => x.UserId == f.Users[2]).Select(x => x.Email).SingleAsync();
        var loginResponse = await a.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, LivePlannedFixture.Password, "mobile", "Revoke", "cache-revoke"));
        var login = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!;
        using var revoked = LivePlannedFixture.Authorized(f.B, login.AccessToken);
        var allowed = await revoked.GetAsync("/api/v1/auth/me");
        (await a.PostAsJsonAsync("/api/v1/auth/logout", new RefreshRequest(login.RefreshToken))).EnsureSuccessStatusCode();
        var denied = await revoked.GetAsync("/api/v1/auth/me");
        var freshNow = immediate.Items.Single(x => x.NotificationId == unread.NotificationId).IsRead;
        f.Record("B-CACHE.14", new { hosts = new[] { a.BaseAddress!.ToString(), b.BaseAddress!.ToString() }, sharedRead,
            sameTotal = first.Total == other.Total, immediateInvalidation = freshNow,
            repeatedRead = repeated.Items.Single(x => x.NotificationId == unread.NotificationId).IsRead,
            isolatedTotal = isolated.Total, beforeLogout = (int)allowed.StatusCode, afterLogout = (int)denied.StatusCode });
        Assert.True(sharedRead > 0); Assert.Equal(first.Total, other.Total); Assert.Equal(0, isolated.Total);
        Assert.True(repeated.Items.Single(x => x.NotificationId == unread.NotificationId).IsRead);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.True(freshNow, "Instance B served a stale notification after instance A invalidated the shared Redis entry.");
    }

    [LivePlannedFact]
    public async Task B_PYCF_11_RealR2Training_RejectsMissingAndPartialDatasetWithoutPublishing()
    {
        var active = await f.TrainKnownModel();
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:55352") };
        client.DefaultRequestHeaders.Add("X-Model-Admin-Token", LivePlannedFixture.Required("LIVE_CF_ADMIN"));
        var failures = new List<object>();
        foreach (var partial in new[] { false, true })
        {
            var key = "collaborative_cf/fixture/" + Guid.NewGuid().ToString("N") + ".csv";
            var complete = Encoding.UTF8.GetBytes("user_id,video_id,score\nu1,v1,1\nu2,v2,1\nu1,v2,0.5\n");
            if (partial) await f.Snapshots.WriteAsync(key, complete[..35], "text/csv", default);
            var id = Guid.NewGuid().ToString();
            var queued = await client.PostAsJsonAsync("/internal/model/train", new { jobId = id, csvKey = key,
                csvSha256 = Convert.ToHexStringLower(SHA256.HashData(complete)) });
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            JsonElement progress = default;
            for (var i = 0; i < 100; i++)
            {
                progress = await client.GetFromJsonAsync<JsonElement>("/internal/model/jobs/" + id);
                if (progress.GetProperty("status").GetString() == "failed") break;
                await Task.Delay(300);
            }
            var after = JsonSerializer.Deserialize<JsonElement>((await f.Snapshots.ReadOptionalAsync("collaborative_cf/active.json", default))!);
            failures.Add(new { partial, status = progress.GetProperty("status").GetString(), activeUnchanged = after.GetProperty("modelVersion").GetString() == active.GetProperty("modelVersion").GetString() });
            Assert.Equal("failed", progress.GetProperty("status").GetString());
            Assert.Equal(active.GetProperty("modelVersion").GetString(), after.GetProperty("modelVersion").GetString());
        }
        f.Record("B-PYCF.11", new { realNetwork = true, failures, activeVersion = active.GetProperty("modelVersion").GetString() });
    }

    [LivePlannedFact]
    public async Task B_PERF_01_ColdWarmCacheOnOff_RecordsLatencyBytesAndRealSql()
    {
        using var on = LivePlannedFixture.Authorized(f.A, f.Tokens[1]);
        using var off = LivePlannedFixture.Authorized(f.Off, f.Tokens[1]);
        await f.A.Services.GetRequiredService<HybridCache>().RemoveAsync($"notifications:{f.Users[1]:N}:recent");
        var stages = new List<object>();
        string? invariant = null;
        async Task<long> Measure(HttpClient client, LiveApiFactory factory, string label, int count)
        {
            var latencies = new List<double>(); long bytes = 0; var sqlBefore = factory.Counter.Count;
            for (var i = 0; i < count; i++)
            {
                var watch = Stopwatch.StartNew(); var response = await client.GetAsync("/api/v1/notifications");
                var body = await response.Content.ReadAsByteArrayAsync(); latencies.Add(watch.Elapsed.TotalMilliseconds);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); bytes += body.Length;
                invariant ??= Convert.ToHexStringLower(SHA256.HashData(body));
                Assert.Equal(invariant, Convert.ToHexStringLower(SHA256.HashData(body)));
            }
            latencies.Sort(); var queries = factory.Counter.Count - sqlBefore;
            stages.Add(new { label, requests = count, vus = 1, sqlQueries = queries, responseBytes = bytes,
                p50ms = latencies[(int)((count-1)*.5)], p95ms = latencies[(int)((count-1)*.95)], p99ms = latencies[(int)((count-1)*.99)] });
            return queries;
        }
        await Measure(on, f.A, "on-cold", 1); var warm = await Measure(on, f.A, "on-warm", 30);
        await Measure(off, f.Off, "off-cold", 1); var disabled = await Measure(off, f.Off, "off-warm", 30);
        f.Record("B-PERF.01", new { endpoint = "/api/v1/notifications", dataset = "same DB/user/notifications", stages,
            onFlags = f.A.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HybridCacheOptions>>().Value.DefaultEntryOptions?.Flags?.ToString(),
            offFlags = f.Off.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HybridCacheOptions>>().Value.DefaultEntryOptions?.Flags?.ToString(),
            bodySha256 = invariant, note = "Current feed/profile have no response cache. Baseline uses the actual notifications cache." });
        Assert.True(warm < disabled, $"Warm cache {warm} SQL queries; cache disabled {disabled}.");
    }

    [LivePlannedFact]
    public async Task B_PERF_09_RealModelAndSimulationJobs_AcceptanceCompletionCancelAndProgress()
    {
        var preview = await f.Admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/recommendations/matrix-preview");
        var csv = await f.Admin.GetByteArrayAsync("/api/v1/admin/recommendations/matrix.csv");
        Assert.True(preview.GetProperty("total").GetInt32() >= 6); Assert.Contains("user_id", Encoding.UTF8.GetString(csv));
        var samples = new List<object>(); var watch = Stopwatch.StartNew();
        var queued = await f.Admin.PostAsJsonAsync("/api/v1/admin/recommendations/model-jobs", new { modelAlgorithm = "item_based" });
        var acceptance = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var id = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();
        var completed = await f.AwaitJob(id, samples);
        Assert.True(completed.Status == "completed", completed.Error);
        var completion = watch.Elapsed.TotalMilliseconds;
        var request = new SimulatorRequest(f.Users, f.Videos, "target", 3, 1000, null,
            100, 0, 0, 0, 0, 0, [], false);
        var simulation = await f.Admin.PostAsJsonAsync("/api/v1/admin/recommendations/simulation-jobs", request);
        Assert.Equal(HttpStatusCode.Accepted, simulation.StatusCode);
        var stopId = (await simulation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();
        (await f.Admin.PostAsJsonAsync($"/api/v1/admin/recommendations/jobs/{stopId}/stop", new { })).EnsureSuccessStatusCode();
        var cancelled = await f.AwaitJob(stopId);
        await using var db = f.Db(); var sameJob = await db.RecommendationJobs.CountAsync(x => x.JobId == id);
        f.Record("B-PERF.09", new { acceptanceMs = acceptance, completionMs = completion, samples,
            cancelStatus = cancelled.Status, sameJob, previewRows = preview.GetProperty("total").GetInt32(), csvBytes = csv.Length });
        Assert.Equal("cancelled", cancelled.Status); Assert.Equal(1, sameJob);
        Assert.True(completion > acceptance); Assert.NotEmpty(samples);
    }

    [LivePlannedFact]
    public async Task B_PERF_10_ActualNetworkAndDatabaseFaults_AreBoundedAndRecover()
    {
        var measurements = new List<object>();
        using var client = LivePlannedFixture.Authorized(f.A, f.Tokens[2]);
        var cache = f.A.Services.GetRequiredService<HybridCache>();
        await cache.RemoveAsync($"notifications:{f.Users[2]:N}:recent");
        try
        {
            f.A.Transport.FaultTarget = "redis";
            var calls = f.A.Transport.RedisCalls; var watch = Stopwatch.StartNew();
            var requests = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.GetAsync("/api/v1/notifications")));
            measurements.Add(new { dependency = "redis-timeout", ms = watch.Elapsed.TotalMilliseconds,
                calls = f.A.Transport.RedisCalls - calls, statuses = requests.Select(x => (int)x.StatusCode).ToArray() });
            Assert.All(requests, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
            Assert.True(f.A.Transport.RedisCalls - calls <= 4);
        }
        finally { f.A.Transport.FaultTarget = null; }
        try
        {
            f.A.Transport.FaultTarget = "cf";
            var watch = Stopwatch.StartNew();
            var requests = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.GetAsync("/api/v1/feed/recommended")));
            measurements.Add(new { dependency = "cf-timeout", ms = watch.Elapsed.TotalMilliseconds,
                statuses = requests.Select(x => (int)x.StatusCode).ToArray() });
            Assert.All(requests, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally { f.A.Transport.FaultTarget = null; }
        try
        {
            f.Snapshots.ForceUnavailable = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var watch = Stopwatch.StartNew(); Exception? failure = null;
            try { await f.Snapshots.WriteAsync("collaborative_cf/outage.csv", [1,2,3], "text/csv", timeout.Token); }
            catch (Exception ex) { failure = ex; }
            measurements.Add(new { dependency = "storage-connection-refused", ms = watch.Elapsed.TotalMilliseconds, errorType = failure?.GetType().Name });
            Assert.NotNull(failure); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6));
        }
        finally { f.Snapshots.ForceUnavailable = false; }
        await using (var connection = new NpgsqlConnection(f.Connection))
        {
            await connection.OpenAsync(); await using var transaction = await connection.BeginTransactionAsync();
            await new NpgsqlCommand("LOCK TABLE users IN ACCESS EXCLUSIVE MODE", connection, transaction).ExecuteNonQueryAsync();
            var watch = Stopwatch.StartNew();
            var response = await client.GetAsync("/api/v1/auth/me");
            measurements.Add(new { dependency = "database-lock-timeout", ms = watch.Elapsed.TotalMilliseconds, status = (int)response.StatusCode });
            Assert.False(response.IsSuccessStatusCode); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12));
            await transaction.RollbackAsync();
        }
        var recovery = await client.GetAsync("/api/v1/auth/me");
        // Wait out the production 30-second circuit cooldown, then require real
        // outbound traffic again (HTTP 200 from fallback alone is insufficient).
        await Task.Delay(31000);
        var redisBefore = f.A.Transport.RedisCalls; var cfBefore = f.A.Transport.CfCalls;
        await cache.RemoveAsync($"notifications:{f.Users[2]:N}:recent");
        var notificationRecovery = await client.GetAsync("/api/v1/notifications");
        var feedRecovery = await client.GetAsync("/api/v1/feed/recommended");
        var redisRecoveredCalls = f.A.Transport.RedisCalls - redisBefore;
        var cfRecoveredCalls = f.A.Transport.CfCalls - cfBefore;
        f.Record("B-PERF.10", new { measurements, recovery = (int)recovery.StatusCode, feedRecovery = (int)feedRecovery.StatusCode,
            notificationRecovery = (int)notificationRecovery.StatusCode, redisRecoveredCalls, cfRecoveredCalls,
            scope = "3 concurrent readers, real socket timeouts/refusal and PostgreSQL lock; no synthetic payment writes" });
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode); Assert.Equal(HttpStatusCode.OK, feedRecovery.StatusCode);
        Assert.Equal(HttpStatusCode.OK, notificationRecovery.StatusCode); Assert.True(redisRecoveredCalls > 0); Assert.True(cfRecoveredCalls > 0);
    }
}
