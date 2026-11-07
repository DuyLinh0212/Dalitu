using System.Net;
using System.Text.Json;
using HuTube.Application.Auth;
using HuTube.Domain.Users;
using HuTube.Infrastructure.Authentication;
using HuTube.Infrastructure.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HuTube.UnitTests;

public sealed class RedisSessionPlayerPreferencesStoreTests
{
    [Fact]
    public async Task RedisReadsPreferencesOnEveryLookupWithoutRamCopy()
    {
        var handler = new PreferencesRedisHandler();
        using var http = new HttpClient(handler);
        var store = CreateStore(http, handler.Configuration);
        var session = new UserSession { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var expected = new SessionPlayerPreferences(0.35m, true);

        await store.SetAsync(session, expected, CancellationToken.None);
        Assert.Equal(expected, await store.GetAsync(session, CancellationToken.None));
        Assert.Equal(expected, await store.GetAsync(session, CancellationToken.None));

        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(2, handler.Commands.Count(command => command[0] == "HMGET"));
    }

    [Fact]
    public async Task RedisFailureUsesDefaultsInsteadOfAProcessLocalCopy()
    {
        var handler = new PreferencesRedisHandler { FailReads = true };
        using var http = new HttpClient(handler);
        var store = CreateStore(http, handler.Configuration);
        var session = new UserSession { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };

        await store.SetAsync(session, new SessionPlayerPreferences(0.35m, true), CancellationToken.None);

        Assert.Null(await store.GetAsync(session, CancellationToken.None));
        Assert.Null(await store.GetAsync(session, CancellationToken.None));
        Assert.Equal(2, handler.RequestCount); // Redis cooldown prevents another failing request.
    }

    [Fact]
    public async Task MissingRedisConfigurationDoesNotKeepPreferencesInProcessMemory()
    {
        var handler = new PreferencesRedisHandler();
        using var http = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var store = CreateStore(http, configuration);
        var session = new UserSession { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };

        await store.SetAsync(session, new SessionPlayerPreferences(0.35m, true), CancellationToken.None);

        Assert.Null(await store.GetAsync(session, CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    private static RedisSessionPlayerPreferencesStore CreateStore(HttpClient http, IConfiguration configuration) =>
        new(new UpstashRedisClient(new FixedHttpClientFactory(http), configuration,
                TimeProvider.System, NullLogger<UpstashRedisClient>.Instance),
            NullLogger<RedisSessionPlayerPreferencesStore>.Instance);

    private sealed class PreferencesRedisHandler : HttpMessageHandler
    {
        public IConfiguration Configuration { get; } = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UPSTASH_REDIS_REST_URL"] = "https://redis.example.test",
                ["UPSTASH_REDIS_REST_TOKEN"] = "test-token"
            }).Build();
        public bool FailReads { get; init; }
        public int RequestCount { get; private set; }
        public List<string[]> Commands { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var command = JsonSerializer.Deserialize<string[]>(await request.Content!.ReadAsStringAsync(ct))!;
            Commands.Add(command);
            if (FailReads && command[0] == "HMGET")
                throw new HttpRequestException("Redis disconnected during preference read.");

            object? result = command[0] switch
            {
                "EVAL" => 1,
                "HMGET" => new string?[] { "0.35", "true" },
                _ => throw new InvalidOperationException(command[0])
            };
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { result }))
            };
        }
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
