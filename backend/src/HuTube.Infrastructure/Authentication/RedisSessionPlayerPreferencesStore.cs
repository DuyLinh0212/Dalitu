using System.Globalization;
using System.Text.Json;
using HuTube.Application.Auth;
using HuTube.Domain.Users;
using Microsoft.Extensions.Logging;
using HuTube.Infrastructure.Caching;

namespace HuTube.Infrastructure.Authentication;

public sealed class RedisSessionPlayerPreferencesStore(
    UpstashRedisClient redis,
    ILogger<RedisSessionPlayerPreferencesStore> logger) : ISessionPlayerPreferencesStore
{
    // HSET changes only audio fields; existing authentication fields remain intact.
    private const string SaveScript = """
        redis.call('HSET', KEYS[1], 'volume', ARGV[1], 'muted', ARGV[2])
        redis.call('PEXPIREAT', KEYS[1], ARGV[3])
        return 1
        """;
    private const string RemoveScript = """
        redis.call('HDEL', KEYS[1], 'volume', 'muted')
        if redis.call('HLEN', KEYS[1]) == 0 then redis.call('DEL', KEYS[1]) end
        return 1
        """;

    private static string Key(Guid id) => $"dalitu:auth:session:{id:N}";

    public async Task<SessionPlayerPreferences?> GetAsync(UserSession session, CancellationToken ct)
    {
        if (!redis.Configured) return null;
        try
        {
            var result = await CommandAsync(["HMGET", Key(session.SessionId), "volume", "muted"], ct);
            if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() == 2
                && decimal.TryParse(result[0].GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var volume)
                && volume is >= 0m and <= 1m && bool.TryParse(result[1].GetString(), out var muted))
                return new SessionPlayerPreferences(volume, muted);
            return null;
        }
        catch (Exception ex) when (Unavailable(ex, ct))
        {
            logger.LogWarning(ex, "Could not read session player preferences from Redis; using the default player preferences.");
            return null;
        }
    }

    public async Task SetAsync(UserSession session, SessionPlayerPreferences preferences, CancellationToken ct)
    {
        if (!redis.Configured) return;
        try
        {
            await CommandAsync(["EVAL", SaveScript, "1", Key(session.SessionId),
                preferences.Volume.ToString(CultureInfo.InvariantCulture), preferences.Muted ? "true" : "false",
                session.ExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)], ct);
        }
        catch (Exception ex) when (Unavailable(ex, ct))
        {
            logger.LogWarning(ex, "Could not save session player preferences in Redis.");
        }
    }

    public async Task RemoveAsync(Guid sessionId, CancellationToken ct)
    {
        if (!redis.Configured) return;
        try { await CommandAsync(["EVAL", RemoveScript, "1", Key(sessionId)], ct); }
        catch (Exception ex) when (Unavailable(ex, ct))
        {
            logger.LogWarning(ex, "Could not clear old player preferences; the Redis key will expire with the session.");
        }
    }

    private Task<JsonElement> CommandAsync(string[] command, CancellationToken ct) => redis.CommandAsync(command, ct);

    private static bool Unavailable(Exception ex, CancellationToken ct) => !ct.IsCancellationRequested
        && ex is RedisUnavailableException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException;
}
