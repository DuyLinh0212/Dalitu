using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HuTube.Application.Auth;
using HuTube.Infrastructure.Authentication;
using HuTube.Infrastructure.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace HuTube.UnitTests;

public sealed class IpApiClientLocationResolverTests
{
    [Fact]
    public async Task SuccessfulLookupIsSharedThroughRedisWithHashedIpKeyAndOneDayTtl()
    {
        const string ip = "203.0.113.8";
        var redisHandler = new RedisHandler();
        var redis = new UpstashRedisClient(new Factory(new HttpClient(redisHandler)), Configuration(),
            TimeProvider.System, NullLogger<UpstashRedisClient>.Instance);
        var providerCalls = 0;
        using var locationHttp = new HttpClient(new LocationHandler(_ =>
        {
            providerCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"country_code":"VN","region":"HCMC","city":"Ho Chi Minh City","latitude":10.8,"longitude":106.7}""")
            };
        }));
        var first = Resolver(locationHttp, redis);
        var second = Resolver(locationHttp, redis);

        var result1 = await first.ResolveAsync(ip);
        var result2 = await second.ResolveAsync(ip);

        Assert.Equal(new ClientLocation("VN", "HCMC", "Ho Chi Minh City", 10.8, 106.7), result1);
        Assert.Equal(result1, result2);
        Assert.Equal(1, providerCalls);
        var set = Assert.Single(redisHandler.Commands, command => command[0] == "SET");
        var expectedKey = "dalitu:production:cache:auth:geoip:v1:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(ip)));
        Assert.Equal(expectedKey, set[1]);
        Assert.DoesNotContain(ip, set[1]);
        Assert.Equal(new[] { "EX", "86400" }, set[^2..]);
    }

    private static IpApiClientLocationResolver Resolver(HttpClient http, UpstashRedisClient redis) => new(
        http, new GeoIpOptions { Endpoint = "https://geo.test", TimeoutMilliseconds = 1000 }, redis,
        new TestEnvironment(), NullLogger<IpApiClientLocationResolver>.Instance);

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> {
            ["Redis:Endpoint"] = "https://redis.test",
            ["Redis:Password"] = "test-token"
        }).Build();

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "HuTube.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RedisHandler : HttpMessageHandler
    {
        public List<string[]> Commands { get; } = [];
        private readonly Dictionary<string, string> values = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var command = JsonSerializer.Deserialize<string[]>(await request.Content!.ReadAsStringAsync(ct))!;
            Commands.Add(command);
            object? result = command[0] switch
            {
                "GET" => values.GetValueOrDefault(command[1]),
                "SET" => Store(command[1], command[2]),
                _ => throw new InvalidOperationException(command[0])
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { result })) };
        }

        private string Store(string key, string value)
        {
            values[key] = value;
            return "OK";
        }
    }

    private sealed class LocationHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
