using System.Net;
using System.Text.Json;
using HuTube.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HuTube.UnitTests;

// B-CACHE.09/.10/.11/.13. These exercise the REST adapter, not a live Redis server.
public sealed class PlannedCacheContractTests
{
    [Theory]
    [InlineData("{\"result\":\"not base64!\"}")]
    [InlineData("{\"result\":null}")]
    [InlineData("{\"result\":123}")]
    [InlineData("{\"result\":{\"user\":\"other\"}}")]
    [InlineData("invalid JSON")]
    [InlineData("{\"error\":\"unavailable\"}")]
    public async Task B_CACHE_09_CorruptOrUnexpectedRedisValue_IsACacheMiss(string body)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        var cache = Cache(handler);

        var result = await cache.GetAsync("user:a");

        Assert.Null(result);
        Assert.Single(handler.Commands);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task B_CACHE_10_ReadWriteDeleteFailure_UsesOneBoundedAttemptAndCooldown(int status)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var cache = Cache(handler);

        Assert.Null(await cache.GetAsync("a"));
        await cache.SetAsync("a", [1], new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(20) });
        await cache.RemoveAsync("a");

        Assert.Single(handler.Commands);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("remove")]
    public async Task B_CACHE_11_CancellationDuringRedisCall_IsPropagated(string operation)
    {
        using var canceled = new CancellationTokenSource();
        var handler = new Handler((_, ct) => { canceled.Cancel(); return Task.FromCanceled<HttpResponseMessage>(ct); });
        var cache = Cache(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => {
            if (operation == "get") await cache.GetAsync("a", canceled.Token);
            else if (operation == "set") await cache.SetAsync("a", [1], new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(20) }, canceled.Token);
            else await cache.RemoveAsync("a", canceled.Token);
        });

        Assert.Single(handler.Commands);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task B_CACHE_13_AbsoluteExpiryBeforeAtAfterNow_NeverCreatesPermanentEntry(int seconds)
    {
        var clock = new Clock();
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":1}") }));
        var cache = Cache(handler, clock);

        await cache.SetAsync("private", [1, 2], new() { AbsoluteExpiration = clock.GetUtcNow().AddSeconds(seconds) });

        var command = Assert.Single(handler.Commands);
        Assert.Equal("planned:cache:private", command[1]);
        if (seconds <= 0) Assert.Equal("DEL", command[0]);
        else { Assert.Equal("SET", command[0]); Assert.Equal("PX", command[3]); Assert.Equal("1000", command[4]); }
    }

    [Fact]
    public async Task B_CACHE_13_MissingExpiryAndSlidingExpiry_AreRejected()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("No remote command expected"));
        var cache = Cache(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => cache.SetAsync("private", [1], new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => cache.SetAsync("private", [1], new() { SlidingExpiration = TimeSpan.FromSeconds(10) }));

        Assert.Empty(handler.Commands);
    }

    [Fact]
    public async Task B_CACHE_13_ShortestDeadlineWins_AndRefreshDoesNotExtendExpiry()
    {
        var clock = new Clock();
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":1}") }));
        var cache = Cache(handler, clock);

        await cache.SetAsync("private", [1], new() {
            AbsoluteExpiration = clock.GetUtcNow().AddSeconds(4), AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(9)
        });
        await cache.RefreshAsync("private");

        Assert.Equal("4000", Assert.Single(handler.Commands)[4]);
    }

    private static UpstashDistributedCache Cache(Handler handler, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["UPSTASH_REDIS_REST_URL"] = "https://planned-redis.invalid", ["UPSTASH_REDIS_REST_TOKEN"] = "local-test"
        }).Build();
        return new(new UpstashRedisClient(new Factory(new HttpClient(handler)), config, clock,
            NullLogger<UpstashRedisClient>.Instance), "planned:cache:", clock);
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<string[]> Commands { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Commands.Add(JsonSerializer.Deserialize<string[]>(await request.Content!.ReadAsStringAsync(ct))!);
            return await send(request, ct);
        }
    }
}
