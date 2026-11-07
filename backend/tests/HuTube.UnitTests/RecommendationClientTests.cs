using System.Net;
using System.Net.Http.Json;
using HuTube.Infrastructure.Recommendations;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HuTube.UnitTests;

public sealed class RecommendationClientTests
{
    [Fact]
    public async Task UnavailableServiceUsesOneHealthProbeAndSharesCooldownAcrossClients()
    {
        var calls = new List<string>();
        using var services = CacheProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        using var http = new HttpClient(new Handler((request, _) => {
            calls.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        }));
        var first = Client(http, cache);
        var second = Client(http, cache);
        Assert.Null(await first.GetRecommendationsAsync(Guid.NewGuid(), 50));
        Assert.Null(await second.GetRecommendationsAsync(Guid.NewGuid(), 50));
        Assert.Equal(new[] { "/internal/recommendations", "/health" }, calls);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(0)]
    public async Task FeedDeadlineFallsBackWithoutWaitingForTheModelAdminTimeout(int timeoutSeconds)
    {
        using var services = CacheProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        using var http = new HttpClient(new Handler(async (_, ct) => {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var client = new RecommendationClient(http, new RecommendationOptions {
            ServiceUrl = "https://recommender.test", TimeoutSeconds = timeoutSeconds, FeedTimeoutSeconds = 1
        }, NullLogger<RecommendationClient>.Instance, cache);
        Assert.Null(await client.GetRecommendationsAsync(Guid.NewGuid(), 50).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutOpeningTheCooldown()
    {
        var calls = 0;
        using var services = CacheProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        using var http = new HttpClient(new Handler((_, ct) => {
            calls++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = JsonContent.Create(new { items = Array.Empty<object>() })
            });
        }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var client = Client(http, cache);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client
            .GetRecommendationsAsync(Guid.NewGuid(), 50, ct: cancelled.Token));
        Assert.Empty((await client.GetRecommendationsAsync(Guid.NewGuid(), 50))!);
        Assert.InRange(calls, 1, 2);
    }

    private static RecommendationClient Client(HttpClient http, IDistributedCache cache) =>
        new(http, new RecommendationOptions { ServiceUrl = "https://recommender.test" },
            NullLogger<RecommendationClient>.Instance, cache);

    private static ServiceProvider CacheProvider()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDistributedMemoryCache();
        return services.BuildServiceProvider();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request, ct);
    }
}
