using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using HuTube.Application.Auth;
using HuTube.Application.Storage;
using HuTube.Domain.Channels;
using HuTube.Domain.Rbac;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Caching;
using HuTube.Infrastructure.Persistence;
using HuTube.Infrastructure.Recommendations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HuTube.IntegrationTests;

public sealed class LivePlannedFactAttribute : FactAttribute
{
    public LivePlannedFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LIVE_PLANNED") != "1")
            Skip = "Opt-in real R2/Redis/CF tests: scripts/testing/run_blocked_cases.py";
    }
}

// Instrument only SQL executed for an HTTP request, excluding worker polling.
public sealed class LiveSqlCounter(IHttpContextAccessor accessor) : DbCommandInterceptor
{
    public long Count;
    private void Tick(DbCommand command) {
        // HybridCache deliberately runs its factory outside request ExecutionContext.
        // Include notification SQL by table, while still excluding worker polling.
        if (accessor.HttpContext != null || command.CommandText.Contains("notifications", StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref Count);
    }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    { Tick(command); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    { Tick(command); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
    { Tick(command); return ValueTask.FromResult(result); }
}

public sealed class LiveTransport : IHttpClientFactory
{
    public string? FaultTarget;
    public long RedisCalls;
    public long CfCalls;
    public ConcurrentDictionary<string, byte> WrittenKeys { get; } = new();
    public HttpClient CreateClient(string name) => new(new Handler(this)) {
        Timeout = TimeSpan.FromSeconds(name == "redis-rest" ? 3 : 10)
    };
    private sealed class Handler(LiveTransport owner) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var redis = request.RequestUri!.Host == new Uri(LivePlannedFixture.Required("LIVE_REDIS_URL")).Host;
            if (!redis && request.RequestUri.AbsolutePath == "/internal/recommendations") Interlocked.Increment(ref owner.CfCalls);
            if (redis)
            {
                Interlocked.Increment(ref owner.RedisCalls);
                if (request.Content != null)
                {
                    var args = JsonSerializer.Deserialize<string[]>(await request.Content.ReadAsStringAsync(ct))!;
                    if (args[0] == "SET") owner.WrittenKeys.TryAdd(args[1], 0);
                    if (args[0] == "EVAL" && args.Length > 3) owner.WrittenKeys.TryAdd(args[3], 0);
                }
            }
            if ((redis && owner.FaultTarget == "redis") || (!redis && owner.FaultTarget == "cf"))
            {
                // Real loopback HTTP listener delays past the client's timeout.
                // Never forward production authorization to the fault listener.
                request.RequestUri = new Uri("http://127.0.0.1:55354/delay");
                request.Headers.Authorization = null;
                request.Headers.Remove("X-Service-Token");
                request.Headers.Remove("X-Model-Admin-Token");
            }
            return await base.SendAsync(request, ct);
        }
    }
}

// Reuse the production S3 snapshot implementation. Fixed logical keys are mapped
// into one owned run prefix; CSV cleanup must never enumerate outside that prefix.
public sealed class LiveSnapshots(R2Options options, string prefix) : IRecommendationSnapshotStore, IDisposable
{
    private readonly RecommendationSnapshotStore inner = new(options);
    public bool ForceUnavailable;
    private string Key(string key)
    {
        if (!key.StartsWith("collaborative_cf/") || key.Contains("..")) throw new ArgumentException("Unsafe test key");
        return prefix + key;
    }
    public Task<byte[]?> ReadOptionalAsync(string key, CancellationToken ct) => inner.ReadOptionalAsync(Key(key), ct);
    public async Task WriteAsync(string key, byte[] bytes, string contentType, CancellationToken ct)
    {
        if (ForceUnavailable)
        {
            using var unavailable = new RecommendationSnapshotStore(new R2Options {
                AccountId = "test", AccessKeyId = "local-only", SecretAccessKey = "local-only", BucketName = "test",
                EndpointUrl = "http://127.0.0.1:55355" });
            await unavailable.WriteAsync(Key(key), bytes, contentType, ct);
            return;
        }
        await inner.WriteAsync(Key(key), bytes, contentType, ct);
    }
    public async Task DeleteCsvExceptAsync(string keepKey, CancellationToken ct)
    {
        using var client = LivePlannedFixture.S3(options);
        var page = await client.ListObjectsV2Async(new ListObjectsV2Request {
            BucketName = options.BucketName, Prefix = prefix }, ct);
        foreach (var item in page.S3Objects.Where(x => x.Key.EndsWith(".csv") && x.Key != Key(keepKey)))
        {
            if (!item.Key.StartsWith(prefix)) throw new InvalidOperationException("Namespace escaped");
            await client.DeleteObjectAsync(options.BucketName, item.Key, ct);
        }
    }
    public void Dispose() => inner.Dispose();
}

public sealed class LiveApiFactory(LivePlannedFixture fixture, bool useCache) : WebApplicationFactory<Program>
{
    public LiveTransport Transport { get; } = new();
    public LiveSqlCounter Counter => Services.GetRequiredService<LiveSqlCounter>();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseContentRoot(fixture.Runtime);
        var settings = new Dictionary<string, string?> {
            ["ConnectionStrings:Database"] = fixture.Connection,
            ["Jwt:SigningKey"] = new string('x', 96), ["Email:Mode"] = "Pickup",
            ["Email:PickupDirectory"] = fixture.Mail, ["Redis:Enabled"] = "false",
            ["Auth:WebBaseUrl"] = "http://localhost:4200", ["Auth:AdminBaseUrl"] = "http://localhost:4201",
            ["Features:ModerationEnabled"] = "false", ["GeoIp:Enabled"] = "false",
            ["Recommendation:Enabled"] = "true", ["Recommendation:ServiceUrl"] = "http://127.0.0.1:55352",
            ["Recommendation:ServiceToken"] = Required("LIVE_CF_TOKEN"),
            ["Recommendation:AdminToken"] = Required("LIVE_CF_ADMIN"),
            ["Recommendation:FeedTimeoutSeconds"] = "2", ["Recommendation:TimeoutSeconds"] = "5",
            ["Logging:LogLevel:Default"] = "Error", ["VideoProcessing:Enabled"] = "false",
            ["Sepay:SecretKey"] = "live-test-no-money", ["Sepay:SignatureMode"] = "HmacSha256",
            ["Sepay:AccountNumber"] = "test-account", ["Sepay:BankCode"] = "test-bank"
        };
        foreach (var name in new[] { "Auth", "Feed", "Search", "Playback", "Upload", "Admin", "PaymentWebhook", "RecommendationJob" })
            settings[$"RateLimit:{name}PermitLimit"] = "100000";
        foreach (var pair in settings) builder.UseSetting(pair.Key, pair.Value);
        builder.ConfigureServices(services => {
            services.RemoveAll<DbContextOptions<HuTubeDbContext>>();
            services.AddSingleton<LiveSqlCounter>();
            services.AddDbContext<HuTubeDbContext>((provider, options) => options.UseNpgsql(fixture.Connection,
                npgsql => npgsql.CommandTimeout(3)).AddInterceptors(provider.GetRequiredService<LiveSqlCounter>()));
            services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(Transport);
            services.RemoveAll<IGoogleTokenVerifier>(); services.AddSingleton<IGoogleTokenVerifier, TestGoogleTokenVerifier>();
            services.RemoveAll<IRecommendationSnapshotStore>(); services.AddSingleton<IRecommendationSnapshotStore>(fixture.Snapshots);
            services.RemoveAll<UpstashRedisClient>();
            services.AddSingleton(provider => new UpstashRedisClient(Transport,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Redis:Enabled"] = useCache ? "true" : "false",
                    ["UPSTASH_REDIS_REST_URL"] = Required("LIVE_REDIS_URL"),
                    ["UPSTASH_REDIS_REST_TOKEN"] = Required("LIVE_REDIS_TOKEN")
                }).Build(), TimeProvider.System, provider.GetRequiredService<ILogger<UpstashRedisClient>>()));
            if (useCache) services.AddSingleton<IDistributedCache>(provider => new UpstashDistributedCache(
                provider.GetRequiredService<UpstashRedisClient>(), fixture.RedisPrefix, TimeProvider.System));
            else services.PostConfigure<HybridCacheOptions>(options => options.DefaultEntryOptions = new() {
                Flags = HybridCacheEntryFlags.DisableLocalCache | HybridCacheEntryFlags.DisableDistributedCache });
            services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(fixture.Runtime, "keys")));
        });
    }
    private static string Required(string name) => LivePlannedFixture.Required(name);
}

public sealed class LivePlannedFixture : IAsyncLifetime
{
    public static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing {name}; run via run_blocked_cases.py");
    public readonly string Database = "hutube_live_" + Guid.NewGuid().ToString("N");
    private static string AdminConnection => Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION")
        ?? throw new InvalidOperationException("TEST_DATABASE_CONNECTION is required for live planned tests.");
    public string Connection
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(AdminConnection) { Database = Database };
            return builder.ConnectionString;
        }
    }
    public string Runtime = "", Mail = "", RedisPrefix = "", Prefix = "";
    public R2Options Storage = null!;
    public LiveSnapshots Snapshots = null!;
    public LiveApiFactory A = null!, B = null!, Off = null!;
    public HttpClient Admin = null!;
    public Guid[] Users = [], Videos = [];
    public string[] Tokens = [];
    public const string Password = "LivePlannedLocal42!";
    public ConcurrentDictionary<string, object> Evidence { get; } = new();
    public HuTubeDbContext Db() => new(new DbContextOptionsBuilder<HuTubeDbContext>().UseNpgsql(Connection).Options);
    public static AmazonS3Client S3(R2Options o) => new(new BasicAWSCredentials(o.AccessKeyId, o.SecretAccessKey),
        new AmazonS3Config { ServiceURL = $"https://{o.AccountId}.r2.cloudflarestorage.com", AuthenticationRegion = "auto",
            ForcePathStyle = true, RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED, MaxErrorRetry = 1 });
    public static HttpClient Authorized(LiveApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }
    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("LIVE_PLANNED") != "1") return;
        Runtime = Path.Combine(Required("LIVE_WORK"), Database);
        Mail = Path.Combine(Runtime, "mail"); Directory.CreateDirectory(Mail);
        Prefix = Required("LIVE_R2_PREFIX"); RedisPrefix = Required("LIVE_REDIS_PREFIX");
        Storage = new() { AccountId = Required("LIVE_R2_ACCOUNT"), AccessKeyId = Required("LIVE_R2_ACCESS"),
            SecretAccessKey = Required("LIVE_R2_SECRET"), BucketName = Required("LIVE_R2_BUCKET") };
        Snapshots = new(Storage, Prefix);
        await using (var connection = new NpgsqlConnection(AdminConnection))
        { await connection.OpenAsync(); await new NpgsqlCommand($"CREATE DATABASE {Database}", connection).ExecuteNonQueryAsync(); }
        await using (var db = Db()) {
            await db.Database.MigrateAsync(); await db.Database.OpenConnectionAsync();
            await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync();
            await db.Database.CloseConnectionAsync();
        }
        A = new(this, true); B = new(this, true); Off = new(this, false);
        A.UseKestrel(0); B.UseKestrel(0); Off.UseKestrel(0);
        Users = new Guid[3]; Tokens = new string[3];
        for (var i = 0; i < 3; i++)
        {
            using var client = A.CreateClient();
            var address = $"live_{Guid.NewGuid():N}@bot.hutube.invalid";
            (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(
                "live_" + Guid.NewGuid().ToString("N")[..15], address, "Live fixture", Password))).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new TokenRequest(ReadPickupToken(address)))).EnsureSuccessStatusCode();
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(address, Password, "mobile", "Live", Guid.NewGuid().ToString()));
            response.EnsureSuccessStatusCode(); var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
            Users[i] = login.User.UserId; Tokens[i] = login.AccessToken;
        }
        await using (var db = Db())
        {
            await db.Users.Where(x => x.UserId == Users[0]).ExecuteUpdateAsync(u => u.SetProperty(x => x.RoleId, SystemRoles.SuperAdmin));
            var channel = new Channel { OwnerUserId = Users[0], Name = "Live test", Handle = "live-" + Guid.NewGuid().ToString("N")[..10] };
            db.Channels.Add(channel); await db.SaveChangesAsync();
            var videos = Enumerable.Range(0, 3).Select(i => new Video { ChannelId = channel.ChannelId,
                UploadedByUserId = Users[0], Title = "Live known item " + i, VideoUrl = "/uploads/test.mp4", Duration = 120,
                FileSize = 1024, Status = "published", Visibility = "public", ModerationStatus = "approved", PublishedAt = DateTimeOffset.UtcNow }).ToArray();
            db.Videos.AddRange(videos); await db.SaveChangesAsync(); Videos = videos.Select(x => x.VideoId).ToArray();
            foreach (var (u, v) in new[] { (0,0), (0,1), (1,0), (1,2), (2,1), (2,2) })
                db.ViewingHistories.Add(new ViewingHistory { UserId = Users[u], VideoId = Videos[v], WatchDuration = 60, Progress = 50 });
            for (var i = 0; i < 3; i++) db.Notifications.Add(new() { UserId = Users[1], Type = "test", Title = "Live notice " + i, Content = "Fixture" });
            await db.SaveChangesAsync();
        }
        Admin = Authorized(A, Tokens[0]);
        // Force the other independent hosts to start before measurements.
        using var b = B.CreateClient(); using var off = Off.CreateClient();
        (await b.GetAsync("/health")).EnsureSuccessStatusCode(); (await off.GetAsync("/health")).EnsureSuccessStatusCode();
    }
    public string ReadPickupToken(string address)
    {
        foreach (var path in Directory.EnumerateFiles(Mail, "*.eml"))
        {
            var message = MimeKit.MimeMessage.Load(path);
            if (!message.To.Mailboxes.Any(x => x.Address == address)) continue;
            var match = System.Text.RegularExpressions.Regex.Match(message.TextBody ?? "", @"[?&]token=([^\s<>]+)");
            if (match.Success) return Uri.UnescapeDataString(match.Groups[1].Value);
        }
        throw new InvalidOperationException("Real Pickup link not found");
    }
    public async Task<RecommendationJob> AwaitJob(Guid id, List<object>? samples = null)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            var job = await Admin.GetFromJsonAsync<RecommendationJob>($"/api/v1/admin/recommendations/jobs/{id}");
            samples?.Add(new { job!.Status, job.Step, job.Completed, job.Total, memory = Environment.WorkingSet, utc = DateTimeOffset.UtcNow });
            if (job!.Status is "completed" or "failed" or "cancelled") return job;
            await Task.Delay(500);
        }
        throw new TimeoutException("Job exceeded isolated test deadline");
    }
    public async Task<JsonElement> TrainKnownModel()
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/admin/recommendations/model-jobs", new { modelAlgorithm = "item_based" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();
        var job = await AwaitJob(id); Assert.True(job.Status == "completed", job.Error);
        return JsonSerializer.Deserialize<JsonElement>((await Snapshots.ReadOptionalAsync("collaborative_cf/active.json", default))!);
    }
    public void Record(string id, object value)
    {
        Evidence[id] = new { endedAt = DateTimeOffset.UtcNow, details = value };
        File.WriteAllText(Path.Combine(Required("LIVE_OUT"), "live-measurements.json"), JsonSerializer.Serialize(Evidence,
            new JsonSerializerOptions { WriteIndented = true }));
    }
    public async Task DisposeAsync()
    {
        if (A == null) return;
        Admin?.Dispose(); await Off.DisposeAsync(); await B.DisposeAsync(); await A.DisposeAsync();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Required("LIVE_REDIS_TOKEN"));
        var keys = A.Transport.WrittenKeys.Keys.Concat(B.Transport.WrittenKeys.Keys).Distinct().ToArray();
        foreach (var key in keys)
        {
            if (!key.StartsWith(RedisPrefix) && !key.StartsWith("dalitu:auth:session:")) throw new InvalidOperationException("Unexpected Redis key");
            (await http.PostAsJsonAsync(Required("LIVE_REDIS_URL"), new[] { "DEL", key })).EnsureSuccessStatusCode();
        }
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync(); await new NpgsqlCommand($"DROP DATABASE IF EXISTS {Database} WITH (FORCE)", connection).ExecuteNonQueryAsync();
        Record("cleanup", new { redisKeysDeleted = keys.Length, databaseDropped = Database, hostsStopped = 3 });
    }
}
