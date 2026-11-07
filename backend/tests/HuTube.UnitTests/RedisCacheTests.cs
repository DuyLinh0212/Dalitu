using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using HuTube.Infrastructure.Caching;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HuTube.UnitTests;

public sealed class RedisCacheTests
{
    [Fact]
    public async Task RedisFailureBecomesCacheMissAndPausesRequestsForThirtySeconds()
    {
        var clock = new TestClock();
        var handler = new RedisHandler { Fail = true };
        var cache = new UpstashDistributedCache(Client(handler, clock), "test:cache:", clock);
        Assert.Null(await cache.GetAsync("a"));
        Assert.Null(await cache.GetAsync("b"));
        await cache.SetAsync("a", [1], new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) });
        Assert.Single(handler.Commands);
        clock.Advance(TimeSpan.FromSeconds(31));
        handler.Fail = false;
        Assert.Null(await cache.GetAsync("a"));
        Assert.Equal(2, handler.Commands.Count);
    }

    [Fact]
    public async Task HybridCacheUsesRedisForEveryLookupAcrossInstances()
    {
        var handler = new RedisHandler();
        var clock = new TestClock();
        using var first = Provider(handler, clock);
        using var second = Provider(handler, clock);
        var cache1 = first.GetRequiredService<HybridCache>();
        var cache2 = second.GetRequiredService<HybridCache>();
        var reads = 0;
        ValueTask<string> Source(CancellationToken _) { reads++; return ValueTask.FromResult("from-db"); }
        Assert.Equal("from-db", await cache1.GetOrCreateAsync("user:a", Source));
        Assert.Equal("from-db", await cache1.GetOrCreateAsync("user:a", Source));
        Assert.Equal("from-db", await cache2.GetOrCreateAsync("user:a", Source));
        Assert.Equal(1, reads);
        Assert.Equal(3, handler.Commands.Count(c => c[0] == "GET" && c[1] == "test:cache:user:a"));
        var write = Assert.Single(handler.Commands, c => c[0] == "SET");
        Assert.Equal("test:cache:user:a", write[1]);
        Assert.Equal("PX", write[3]);
        Assert.InRange(long.Parse(write[4]), 29_000, 30_000);
        await cache1.RemoveAsync("user:a");
        Assert.Equal("from-db", await cache1.GetOrCreateAsync("user:a", Source));
        Assert.Equal(2, reads);
        Assert.Equal(4, handler.Commands.Count(c => c[0] == "GET" && c[1] == "test:cache:user:a"));
        Assert.Contains(handler.Commands, c => c[0] == "DEL" && c[1] == "test:cache:user:a");
    }

    [Fact]
    public void DataProtectionKeysSurviveProviderRestartAndAreEncryptedInRedis()
    {
        var handler = new RedisHandler();
        var clock = new TestClock();
        using var first = ProtectionProvider(handler, clock, new string('a', 64));
        var protectedValue = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("private-token");
        using var second = ProtectionProvider(handler, clock, new string('a', 64));
        Assert.Equal("private-token", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedValue));
        Assert.NotEmpty(handler.Hash.Values);
        Assert.All(handler.Hash.Values, xml => {
            Assert.Contains("dalituEncryptedKey", xml);
            Assert.DoesNotContain("<masterKey", xml);
        });
        Assert.DoesNotContain(handler.Commands, c => c[0] is "EXPIRE" or "PEXPIRE" || c.Contains("PX"));
        using var wrongKey = ProtectionProvider(handler, clock, new string('b', 64));
        Assert.ThrowsAny<CryptographicException>(() => wrongKey.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("test").Unprotect(protectedValue));
    }

    [Fact]
    public void KeyEncryptionRejectsTampering()
    {
        var configuration = Config(new string('a', 64));
        var encryptor = new DataProtectionKeyEncryptor(configuration);
        var encrypted = encryptor.Encrypt(XElement.Parse("<secret>key-material</secret>")).EncryptedElement;
        encrypted.Element("tag")!.Value = Convert.ToBase64String(new byte[16]);
        Assert.ThrowsAny<CryptographicException>(() => encryptor.Decrypt(encrypted));
    }

    private static ServiceProvider Provider(RedisHandler handler, TimeProvider clock)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IDistributedCache>(new UpstashDistributedCache(Client(handler, clock), "test:cache:", clock));
        services.AddHybridCache(options => options.DefaultEntryOptions = new() {
            Expiration = TimeSpan.FromSeconds(30),
            Flags = HybridCacheEntryFlags.DisableLocalCache
        });
        return services.BuildServiceProvider();
    }

    private static ServiceProvider ProtectionProvider(RedisHandler handler, TimeProvider clock, string secret)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(Config(secret));
        services.AddSingleton(new RedisDataProtectionRepository(Client(handler, clock), "test:data-protection:keys"));
        services.AddSingleton<DataProtectionKeyEncryptor>();
        services.AddDataProtection().SetApplicationName("redis-test");
        services.AddOptions<KeyManagementOptions>().Configure<RedisDataProtectionRepository, DataProtectionKeyEncryptor>((options, repository, encryptor) => {
            options.XmlRepository = repository; options.XmlEncryptor = encryptor;
        });
        return services.BuildServiceProvider();
    }

    private static IConfiguration Config(string secret = "") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["UPSTASH_REDIS_REST_URL"] = "", ["UPSTASH_REDIS_REST_TOKEN"] = "",
        ["Redis:Endpoint"] = "https://redis.test", ["Redis:Password"] = "test-token",
        ["Jwt:SigningKey"] = secret
    }).Build();
    private static UpstashRedisClient Client(RedisHandler handler, TimeProvider clock) => new(
        new Factory(new HttpClient(handler)), Config(), clock, NullLogger<UpstashRedisClient>.Instance);
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string _) => client; }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
    private sealed class RedisHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public List<string[]> Commands { get; } = [];
        public Dictionary<string, string> Hash { get; } = [];
        private readonly Dictionary<string, string> values = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var args = JsonSerializer.Deserialize<string[]>(await request.Content!.ReadAsStringAsync(ct))!;
            Commands.Add(args);
            if (Fail) throw new HttpRequestException("test outage");
            object? result = args[0] switch {
                "GET" => values.GetValueOrDefault(args[1]),
                "SET" => Store(values, args[1], args[2]),
                "DEL" => values.Remove(args[1]) ? 1 : 0,
                "HSET" => Store(Hash, args[2], args[3]),
                "HVALS" => Hash.Values.ToArray(),
                _ => throw new InvalidOperationException(args[0])
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { result })) };
        }
        private static int Store(Dictionary<string, string> target, string key, string value) { target[key] = value; return 1; }
    }
}
