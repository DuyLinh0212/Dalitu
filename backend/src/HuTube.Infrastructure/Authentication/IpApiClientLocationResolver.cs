using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HuTube.Application.Auth;
using HuTube.Infrastructure.Caching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HuTube.Infrastructure.Authentication;

public sealed class IpApiClientLocationResolver(
    HttpClient http,
    GeoIpOptions options,
    UpstashRedisClient redis,
    IHostEnvironment environment,
    ILogger<IpApiClientLocationResolver> logger) : IClientLocationResolver
{
    private const string CacheLifetimeSeconds = "86400";
    private readonly string cachePrefix = $"dalitu:{environment.EnvironmentName.ToLowerInvariant()}:cache:auth:geoip:v1:";

    public async Task<ClientLocation?> ResolveAsync(string? ipAddress, CancellationToken ct = default)
    {
        if (!options.Enabled || !IPAddress.TryParse(ipAddress, out var address) || IsPrivate(address)) return null;
        var cacheKey = CacheKey(ipAddress!);
        if (redis.Configured && await ReadCachedAsync(cacheKey, ct) is { } cached) return cached;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(options.TimeoutMilliseconds, 100, 5000)));
        try
        {
            var endpoint = options.Endpoint.TrimEnd('/') + "/" + Uri.EscapeDataString(ipAddress!) + "/json/";
            using var response = await http.GetAsync(endpoint, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var payload = await response.Content.ReadFromJsonAsync<IpApiResponse>(cancellationToken: timeout.Token);
            if (payload is null || payload.Error == true) return null;

            var location = new ClientLocation(
                Clean(payload.CountryCode, 8), Clean(payload.Region, 120), Clean(payload.City, 120),
                payload.Latitude, payload.Longitude);
            if (redis.Configured) await WriteCachedAsync(cacheKey, location, ct);
            return location;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("GeoIP lookup timed out.");
            return null;
        }
        catch (HttpRequestException)
        {
            logger.LogDebug("GeoIP lookup failed.");
            return null;
        }
        catch (JsonException)
        {
            logger.LogDebug("GeoIP lookup returned an invalid response.");
            return null;
        }
        catch (UriFormatException)
        {
            logger.LogWarning("GeoIP endpoint configuration is invalid.");
            return null;
        }
    }

    private string CacheKey(string ip) => cachePrefix
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ip.Trim())));

    private async Task<ClientLocation?> ReadCachedAsync(string key, CancellationToken ct)
    {
        try
        {
            var result = await redis.CommandAsync(["GET", key], ct);
            return result.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<ClientLocation>(result.GetString()!)
                : null;
        }
        catch (RedisUnavailableException)
        {
            logger.LogDebug("GeoIP Redis cache is unavailable.");
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteCachedAsync(string key, ClientLocation location, CancellationToken ct)
    {
        try
        {
            await redis.CommandAsync(["SET", key, JsonSerializer.Serialize(location), "EX", CacheLifetimeSeconds], ct);
        }
        catch (RedisUnavailableException)
        {
            logger.LogDebug("GeoIP Redis cache write failed.");
        }
    }
    private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
    }

    private sealed record IpApiResponse(
        [property: JsonPropertyName("country_code")]
        string? CountryCode,
        [property: JsonPropertyName("region")]
        string? Region,
        [property: JsonPropertyName("city")]
        string? City,
        [property: JsonPropertyName("latitude")]
        double? Latitude,
        [property: JsonPropertyName("longitude")]
        double? Longitude,
        [property: JsonPropertyName("error")]
        bool? Error);
}
