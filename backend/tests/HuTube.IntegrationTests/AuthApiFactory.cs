using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using HuTube.Application.Auth;
using HuTube.Application.Storage;
using HuTube.Application.Videos;
using HuTube.Infrastructure.Persistence;
using HuTube.Infrastructure.Recommendations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace HuTube.IntegrationTests;

public sealed class TestEmails : IAuthEmailSender
{
    public ConcurrentDictionary<string, string> Bodies { get; } = new();
    public Task SendAsync(string email, string subject, string body, CancellationToken cancellationToken) { Bodies[email] = body; return Task.CompletedTask; }
    public string Token(string email) => Uri.UnescapeDataString(Bodies[email].Split("?token=")[1]);
    public string Code(string email) => Regex.Match(Bodies[email], @"(?m)^Mã OTP: (?<code>\d{8})\r?$") is { Success: true } match
        ? match.Groups["code"].Value
        : throw new InvalidOperationException("The email did not contain a password reset OTP.");
}
public sealed class TestGoogleTokenVerifier : IGoogleTokenVerifier
{
    public Task<GoogleIdentity> VerifyAsync(string credential, CancellationToken cancellationToken) => credential == "valid-google-token-123"
        ? Task.FromResult(new GoogleIdentity("google-subject-123", "google.user@example.com", "Google User", null))
        : throw new AuthException(401, "INVALID_GOOGLE_TOKEN", "Google token không hợp lệ.");
}
public sealed class TestObjectStorage : IObjectStorage
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
    public Func<string, string, Task>? BeforeSave { get; set; }
    public Task<string> SaveFileAsync(string folder, string fileName, Stream content, string contentType, CancellationToken ct = default) => SaveAsync(folder, fileName, content, ct);
    public Task<string> SaveVideoAsync(string folder, string fileName, Stream content, string contentType, CancellationToken ct = default) => SaveAsync(folder, fileName, content, ct);
    private async Task<string> SaveAsync(string folder, string fileName, Stream content, CancellationToken ct)
    {
        if (BeforeSave != null) await BeforeSave(folder, fileName);
        var path = $"test://{folder}/{Guid.NewGuid():N}{Path.GetExtension(fileName)}";
        using var buffer = new MemoryStream(); await content.CopyToAsync(buffer, ct); Objects[path] = buffer.ToArray(); return path;
    }
    public Task<string> GetReadUrlAsync(string storedPath, TimeSpan lifetime, CancellationToken ct = default) => Task.FromResult(storedPath.Replace("test://", "https://storage.test/"));
    public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new MemoryStream(Objects.TryGetValue(storedPath, out var bytes) ? bytes : throw new FileNotFoundException(), writable: false));
    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default) { Objects.TryRemove(relativePath, out _); return Task.CompletedTask; }
}

public sealed class TestRecommendationSnapshots : IRecommendationSnapshotStore
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
    public Task<byte[]?> ReadOptionalAsync(string key, CancellationToken ct) =>
        Task.FromResult(Objects.TryGetValue(key, out var value) ? value : null);
    public Task WriteAsync(string key, byte[] bytes, string contentType, CancellationToken ct)
    { Objects[key] = bytes; return Task.CompletedTask; }
    public Task DeleteCsvExceptAsync(string keepKey, CancellationToken ct)
    {
        foreach (var key in Objects.Keys.Where(key => key.StartsWith("collaborative_cf/", StringComparison.Ordinal)
                     && key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(key, keepKey, StringComparison.Ordinal)))
            Objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public sealed class TestVideoTranscoder : IVideoTranscoder
{
    public async Task<ProbedVideo> ProbeAsync(string sourceFilePath, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllTextAsync(sourceFilePath, ct);
        var duration = bytes.StartsWith("fake-video-duration:") && int.TryParse(bytes[20..], out var parsed) ? parsed : 120;
        var fileQuality = Path.GetFileNameWithoutExtension(sourceFilePath);
        var height = fileQuality.EndsWith('p') && int.TryParse(fileQuality[..^1], out var parsedHeight) ? parsedHeight : 720;
        return new ProbedVideo(duration, height * 16 / 9, height);
    }
    public async Task<IReadOnlyList<TranscodedVideo>> CreateLowerRenditionsAsync(string sourceFilePath, string sourceQuality, string workingDirectory, CancellationToken ct = default)
    {
        var result = new List<TranscodedVideo>();
        foreach (var quality in HuTube.Domain.Videos.VideoRules.LowerQualities(sourceQuality))
        {
            var height = HuTube.Domain.Videos.VideoRules.QualityHeight(quality);
            var output = Path.Combine(workingDirectory, quality + ".mp4");
            await using var source = File.OpenRead(sourceFilePath);
            await using var target = File.Create(output);
            await source.CopyToAsync(target, ct);
            result.Add(new(quality, height * 16 / 9, height, 1000, output, source.Length));
        }
        return result;
    }
}

public class AuthApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = "hutube_test_" + Guid.NewGuid().ToString("N");
    private readonly string _adminConnection = Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION")
        ?? throw new InvalidOperationException("TEST_DATABASE_CONNECTION is required. Integration tests always run against a real, isolated PostgreSQL database.");
    public string Connection { get; private set; } = "";
    public TestEmails Emails { get; } = new();
    public TestObjectStorage Storage { get; } = new();
    public TestRecommendationSnapshots Snapshots { get; } = new();
    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnection); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", connection); await command.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _databaseName };
        Connection = builder.ConnectionString;
        await using var db = CreateDb(); await db.Database.MigrateAsync();
        // Migration creates citext after Npgsql first loads types. Production migrates in a
        // separate process; this fixture hosts both steps and must refresh its type cache.
        await db.Database.OpenConnectionAsync();
        await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync();
        await db.Database.CloseConnectionAsync();
    }
    public HuTubeDbContext CreateDb() => new(new DbContextOptionsBuilder<HuTubeDbContext>().UseNpgsql(Connection).Options);
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Database", Connection);
        builder.UseSetting("Jwt:SigningKey", new string('t', 80));
        builder.UseSetting("Email:Mode", "Pickup");
        builder.UseSetting("RateLimit:AuthPermitLimit", "10000");
        builder.UseSetting("RateLimit:FeedPermitLimit", "10000");
        builder.UseSetting("RateLimit:SearchPermitLimit", "10000");
        builder.UseSetting("RateLimit:PlaybackPermitLimit", "10000");
        builder.UseSetting("RateLimit:UploadPermitLimit", "10000");
        builder.UseSetting("RateLimit:UploadPreflightPermitLimit", "10000");
        builder.UseSetting("RateLimit:UploadChunkPermitLimit", "10000");
        builder.UseSetting("RateLimit:PaymentInitiatePermitLimit", "10000");
        builder.UseSetting("RateLimit:PaymentWebhookPermitLimit", "10000");
        builder.UseSetting("RateLimit:AdminPermitLimit", "10000");
        builder.UseSetting("RateLimit:ModerationPermitLimit", "10000");
        builder.UseSetting("RateLimit:CommentPermitLimit", "10000");
        builder.UseSetting("RateLimit:CfSeederPermitLimit", "10000");
        builder.UseSetting("RateLimit:CfSeederUploadPermitLimit", "10000");
        builder.UseSetting("RateLimit:RecommendationJobPermitLimit", "10000");
        builder.UseSetting("RateLimit:RecommendationJobConcurrencyLimit", "10000");
        builder.UseSetting("RateLimit:RecommendationJobQueueLimit", "10000");
        builder.UseSetting("Auth:WebBaseUrl", "http://localhost:4200");
        builder.UseSetting("Auth:AdminBaseUrl", "http://localhost:4201");
        builder.UseSetting("Auth:AllowedOrigins:0", "");
        builder.UseSetting("Auth:AllowedOrigins:1", "");
        builder.UseSetting("Features:ModerationEnabled", "true");
        builder.UseSetting("Features:PlanEnforcementEnabled", "true");
        builder.UseSetting("RabbitMq:Enabled", "false");
        builder.UseSetting("Fcm:Enabled", "false");
        builder.ConfigureServices(services => { services.RemoveAll<IAuthEmailSender>(); services.RemoveAll<IGoogleTokenVerifier>(); services.RemoveAll<IObjectStorage>(); services.RemoveAll<IVideoTranscoder>(); services.RemoveAll<IRecommendationSnapshotStore>(); services.AddSingleton<IAuthEmailSender>(Emails); services.AddSingleton<IGoogleTokenVerifier, TestGoogleTokenVerifier>(); services.AddSingleton<IObjectStorage>(Storage); services.AddSingleton<IVideoTranscoder, TestVideoTranscoder>(); services.AddSingleton<IRecommendationSnapshotStore>(Snapshots); });
    }
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnection); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", connection); await command.ExecuteNonQueryAsync();
    }
}
