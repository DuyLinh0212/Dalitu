using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace HuTube.IntegrationTests;

public sealed class RemoteApiPerformanceFactAttribute : FactAttribute
{
    public RemoteApiPerformanceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HUTUBE_PERFORMANCE_API_BASE_URL")))
            Skip = "Set HUTUBE_PERFORMANCE_API_BASE_URL to an API base URL to run the opt-in read-only benchmark.";
    }
}

public sealed class ApiPerformanceIntegrationTests(ITestOutputHelper output)
{
    private const int RequestCount = 100;
    private const int MaxConcurrency = 5;
    private static readonly TimeSpan RateLimitSafeBatchInterval = TimeSpan.FromSeconds(5.5);

    [RemoteApiPerformanceFact]
    [Trait("Category", "Performance")]
    public async Task RepresentativeReadApis_RecordsLatencyAndVerifiesRedisCacheMissAndHit()
    {
        // Arrange
        var configuredBaseUrl = Environment.GetEnvironmentVariable("HUTUBE_PERFORMANCE_API_BASE_URL")!;
        var baseUri = new Uri(configuredBaseUrl.EndsWith('/') ? configuredBaseUrl : configuredBaseUrl + "/",
            UriKind.Absolute);
        Assert.Empty(baseUri.UserInfo);
        Assert.Empty(baseUri.Query);
        Assert.Empty(baseUri.Fragment);

        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        var runId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var bootstrapTimer = Stopwatch.StartNew();
        using var bootstrap = await client.GetAsync(
            $"feed/home?page=1&pageSize=20&benchRun={runId}-bootstrap");
        bootstrapTimer.Stop();
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        var videoId = await ReadFirstVideoIdAsync(bootstrap);

        var routes = new (string Name, Func<int, string> Url)[]
        {
            ("Danh sách video", i => $"feed/home?page=1&pageSize=20&benchRun={runId}-list-{i}"),
            ($"Chi tiết video {videoId}", i => $"videos/{videoId}?benchRun={runId}-detail-{i}"),
            ("Tìm kiếm video", i => $"videos/search?q=video&page=1&pageSize=20&benchRun={runId}-search-{i}"),
            ("Đề xuất video", i => $"feed/recommended?page=1&pageSize=20&benchRun={runId}-recommended-{i}")
        };
        var apiResults = new List<ApiEndpointMeasurement>();

        // Act: requests are paced in five-request waves. This keeps search below
        // its 60-request/minute limit while preserving a maximum concurrency of five.
        foreach (var route in routes)
        {
            apiResults.Add(await MeasureAsync(client, route.Name, route.Url, RequestCount,
                RateLimitSafeBatchInterval));
        }

        // The detail route has no search/feed limiter. Reuse these exact 100
        // cache keys so the second group measures Redis cache hits.
        var cacheUrls = Enumerable.Range(0, RequestCount)
            .Select(i => $"videos/{videoId}?benchRun={runId}-cache-pair-{i}")
            .ToArray();
        var cacheMiss = await MeasureAsync(client, "Chi tiết video — cache miss",
            i => cacheUrls[i], RequestCount, batchInterval: null);
        var cacheHit = await MeasureAsync(client, "Chi tiết video — Redis cache hit",
            i => cacheUrls[i], RequestCount, batchInterval: null);

        var finishedAt = DateTimeOffset.UtcNow;
        var report = new ApiPerformanceReport(
            RunId: runId,
            StartedAtUtc: startedAt,
            FinishedAtUtc: finishedAt,
            ApiBase: $"{baseUri.Scheme}://{baseUri.Authority}{baseUri.AbsolutePath.TrimEnd('/')}",
            RequestsPerEndpoint: RequestCount,
            MaxConcurrency: MaxConcurrency,
            BatchIntervalSeconds: RateLimitSafeBatchInterval.TotalSeconds,
            Bootstrap: new BootstrapMeasurement((int)bootstrap.StatusCode, bootstrapTimer.Elapsed.TotalMilliseconds),
            ApiEndpoints: apiResults,
            CacheComparison: new ApiCacheComparison(
                Endpoint: "GET videos/{id}",
                Miss: cacheMiss,
                Hit: cacheHit,
                MeanReductionPercent: ReductionPercent(cacheMiss.AverageMs, cacheHit.AverageMs),
                P95ReductionPercent: ReductionPercent(cacheMiss.P95Ms, cacheHit.P95Ms)));

        var reportPath = await SaveReportAsync(report);
        output.WriteLine($"API performance report: {reportPath}");
        output.WriteLine($"Environment: public API over the current network path; GET-only; total elapsed {(finishedAt - startedAt).TotalSeconds:F2}s.");
        output.WriteLine($"Requests/endpoint={RequestCount}, max concurrency={MaxConcurrency}, paced batch interval={RateLimitSafeBatchInterval.TotalSeconds:F1}s.");
        foreach (var result in apiResults.Append(cacheMiss).Append(cacheHit))
            output.WriteLine($"{result.Name}: requests={result.Requests}, avg={result.AverageMs:F2} ms, p95={result.P95Ms:F2} ms, errors={result.Errors}, elapsed={result.DurationSeconds:F2}s, cache hit/miss/none={result.CacheHeaders["hit"]}/{result.CacheHeaders["miss"]}/{result.CacheHeaders["none"]}.");
        output.WriteLine($"Cache reduction by measured groups: mean={report.CacheComparison.MeanReductionPercent:F2}%, p95={report.CacheComparison.P95ReductionPercent:F2}%.");

        // Assert: status and cache headers confirm the API actually took each branch.
        Assert.All(apiResults, result =>
        {
            Assert.Equal(RequestCount, result.Requests);
            Assert.Equal(0, result.Errors);
            Assert.Equal(RequestCount, result.CacheHeaders["miss"]);
        });
        Assert.Equal(RequestCount, cacheMiss.Requests);
        Assert.Equal(0, cacheMiss.Errors);
        Assert.Equal(RequestCount, cacheMiss.CacheHeaders["miss"]);
        Assert.Equal(RequestCount, cacheHit.Requests);
        Assert.Equal(0, cacheHit.Errors);
        Assert.Equal(RequestCount, cacheHit.CacheHeaders["hit"]);
    }

    private static async Task<Guid> ReadFirstVideoIdAsync(HttpResponseMessage response)
    {
        await using var content = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(content);
        var items = document.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0, "The public home feed must contain a video to measure the detail API.");
        return items[0].GetProperty("videoId").GetGuid();
    }

    private static async Task<ApiEndpointMeasurement> MeasureAsync(HttpClient client, string name,
        Func<int, string> urlFactory, int requestedCount, TimeSpan? batchInterval)
    {
        var timings = new ConcurrentBag<double>();
        var statuses = new ConcurrentBag<int>();
        var cacheStates = new ConcurrentBag<string>();
        var wallTimer = Stopwatch.StartNew();
        var completed = 0;

        for (var firstIndex = 0; firstIndex < requestedCount; firstIndex += MaxConcurrency)
        {
            var batchTimer = Stopwatch.StartNew();
            var indexes = Enumerable.Range(firstIndex, Math.Min(MaxConcurrency, requestedCount - firstIndex));
            await Task.WhenAll(indexes.Select(async index =>
            {
                var requestTimer = Stopwatch.StartNew();
                using var response = await client.GetAsync(urlFactory(index));
                requestTimer.Stop();
                timings.Add(requestTimer.Elapsed.TotalMilliseconds);
                statuses.Add((int)response.StatusCode);
                cacheStates.Add(response.Headers.TryGetValues("X-Data-Cache", out var values)
                    ? values.FirstOrDefault() ?? "none"
                    : "none");
                Interlocked.Increment(ref completed);
            }));

            if (batchInterval.HasValue && firstIndex + MaxConcurrency < requestedCount)
            {
                var remaining = batchInterval.Value - batchTimer.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining);
            }
            if (statuses.Contains((int)HttpStatusCode.TooManyRequests))
                break;
        }

        wallTimer.Stop();
        var ordered = timings.Order().ToArray();
        var cacheCounts = cacheStates.GroupBy(state => state, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key.ToLowerInvariant(), group => group.Count());
        cacheCounts.TryAdd("hit", 0);
        cacheCounts.TryAdd("miss", 0);
        cacheCounts.TryAdd("none", 0);
        var errors = statuses.Count(status => status != (int)HttpStatusCode.OK);

        return new ApiEndpointMeasurement(
            Name: name,
            Requested: requestedCount,
            Requests: completed,
            Successful: statuses.Count(status => status == (int)HttpStatusCode.OK),
            Errors: errors,
            AverageMs: ordered.Length == 0 ? 0 : ordered.Average(),
            P95Ms: ordered.Length == 0 ? 0 : ordered[Math.Max(0, (int)Math.Ceiling(ordered.Length * 0.95) - 1)],
            DurationSeconds: wallTimer.Elapsed.TotalSeconds,
            CacheHeaders: cacheCounts,
            StatusCodes: statuses.GroupBy(status => status.ToString())
                .ToDictionary(group => group.Key, group => group.Count()));
    }

    private static double? ReductionPercent(double missMs, double hitMs) => missMs <= 0
        ? null
        : Math.Round((missMs - hitMs) / missMs * 100, 2);

    private static async Task<string> SaveReportAsync(ApiPerformanceReport report)
    {
        var root = FindRepositoryRoot();
        var reportDirectory = Path.Combine(root, "backend", "tests", "HuTube.IntegrationTests",
            "TestResults", "performance");
        Directory.CreateDirectory(reportDirectory);
        var filePath = Path.Combine(reportDirectory,
            $"api-performance-xunit-{report.StartedAtUtc:yyyyMMddTHHmmssZ}.json");
        await File.WriteAllTextAsync(filePath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            }));
        return filePath;
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "backend", "HuTube.sln")))
                    return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate backend/HuTube.sln to save the performance report.");
    }

    private sealed record BootstrapMeasurement(int Status, double DurationMs);

    private sealed record ApiEndpointMeasurement(
        string Name,
        int Requested,
        int Requests,
        int Successful,
        int Errors,
        double AverageMs,
        double P95Ms,
        double DurationSeconds,
        Dictionary<string, int> CacheHeaders,
        Dictionary<string, int> StatusCodes);

    private sealed record ApiCacheComparison(
        string Endpoint,
        ApiEndpointMeasurement Miss,
        ApiEndpointMeasurement Hit,
        double? MeanReductionPercent,
        double? P95ReductionPercent);

    private sealed record ApiPerformanceReport(
        string RunId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        string ApiBase,
        int RequestsPerEndpoint,
        int MaxConcurrency,
        double BatchIntervalSeconds,
        BootstrapMeasurement Bootstrap,
        IReadOnlyList<ApiEndpointMeasurement> ApiEndpoints,
        ApiCacheComparison CacheComparison);
}
