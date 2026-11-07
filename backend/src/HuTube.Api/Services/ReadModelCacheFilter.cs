using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HuTube.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HuTube.Api.Services;

// Internal data cache only. Authentication, rate limits and permission action
// filters run first. No credentials/headers or mutation responses are cached.
public sealed class ReadModelCacheFilter(HuTubeDbContext db, HybridCache cache,
    IOptions<JsonOptions> json, IConfiguration configuration, ILogger<ReadModelCacheFilter> logger) : IAsyncActionFilter, IOrderedFilter
{
    public int Order => 10_000;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var path = request.Path.Value?.ToLowerInvariant() ?? "";
        var policy = ReadModelCachePolicy.For(path);
        var resolveRead = HttpMethods.IsPost(request.Method) && path == "/api/v1/feed/resolve";
        if (!configuration.GetValue("ReadCache:Enabled", true) || (!HttpMethods.IsGet(request.Method) && !resolveRead) || policy is null || request.Query.ContainsKey("_refresh")
            || request.Headers.CacheControl.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase)
            || !db.Database.IsNpgsql() || db.Database.CurrentTransaction is not null
            || request.QueryString.Value?.Length > 1024)
        {
            await next();
            return;
        }

        var subject = context.HttpContext.User.FindFirst("sub")?.Value;
        var actor = Guid.TryParse(subject, out var userId) ? userId.ToString() : "guest";
        var privateScopes = actor == "guest" || policy.Shared ? Array.Empty<string>()
            : policy.ViewerState ? new[] { "account:" + actor, "library:" + actor } : new[] { "account:" + actor };
        var scopes = policy.Scopes.Prepend("shared").Concat(privateScopes)
            .Distinct().Order().ToArray();
        // One small indexed query replaces repeated lists/counts/joins. Never
        // cache this revision: a committed revoke/visibility change must make
        // the old distributed result unreachable on the next request.
        string revision;
        try { revision = await db.Database.SqlQueryRaw<string>("""
            SELECT COALESCE(string_agg(scope || ':' || revision::text, ',' ORDER BY scope), '') AS "Value"
            FROM public.read_cache_revisions WHERE scope = ANY (@scopes)
            """, new NpgsqlParameter("scopes", scopes)).SingleAsync(request.HttpContext.RequestAborted); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Development/rolling upgrades remain functional until --migrate.
            // Do not manufacture a generation or cache data without revisions.
            await next();
            return;
        }
        var query = string.Join("&", request.Query.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(JsonSerializer.Serialize(item.Value.ToArray()))));
        var readBody = resolveRead ? JsonSerializer.Serialize(context.ActionArguments.Where(x => x.Value is not CancellationToken)
            .ToDictionary(x => x.Key, x => x.Value), json.Value.JsonSerializerOptions) : "";
        var dimension = JsonSerializer.Serialize(new { request.Method, path, query,
            language = request.Headers.AcceptLanguage.ToArray(), client = request.Headers["X-HuTube-Client"].ToArray(),
            app = request.Headers["X-HuTube-App"].ToArray(), readBody });
        var key = $"read:v1:{(policy.Shared ? "public" : actor)}:{Hash(revision)}:{Hash(dimension)}";
        var lifetime = new HybridCacheEntryOptions {
            Expiration = TimeSpan.FromSeconds(policy.Seconds + Random.Shared.Next(0, Math.Max(1, policy.Seconds / 10))),
            Flags = HybridCacheEntryFlags.DisableLocalCache
        };
        string? payload = null;
        try
        {
            // Do not run MVC continuations inside a shared cache factory: they
            // own a request-scoped DbContext and cancellation lifetime. On miss,
            // execute this request normally and publish only a completed DTO.
            payload = await cache.GetOrCreateAsync<string?>(key, _ => ValueTask.FromResult<string?>(null),
                new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableLocalCache
                    | HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite },
                cancellationToken: request.HttpContext.RequestAborted);
        }
        catch (Exception exception) when (!request.HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Read cache failed; using the data source");
        }
        if (payload is not null)
        {
            context.HttpContext.Response.Headers["X-Data-Cache"] = "hit";
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            context.Result = new ContentResult { Content = payload, ContentType = "application/json; charset=utf-8", StatusCode = 200 };
            return;
        }
        var executed = await next();
        if (executed.Exception is not null || executed.Result is not ObjectResult { Value: not null } result
            || (result.StatusCode ?? 200) != 200 || context.HttpContext.Response.StatusCode != 200
            || context.HttpContext.Response.Headers.ContainsKey("Set-Cookie")) return;
        var serialized = JsonSerializer.Serialize(result.Value, result.Value.GetType(), json.Value.JsonSerializerOptions);
        if (Encoding.UTF8.GetByteCount(serialized) > 256 * 1024) return;
        try { await cache.SetAsync(key, serialized, lifetime, cancellationToken: request.HttpContext.RequestAborted); }
        catch (Exception exception) when (!request.HttpContext.RequestAborted.IsCancellationRequested)
        { logger.LogWarning(exception, "Could not save read cache; retaining the original action result"); }
        context.HttpContext.Response.Headers["X-Data-Cache"] = "miss";
    }
}

internal sealed record ReadModelCachePolicy(int Seconds, string[] Scopes, bool Shared = false, bool ViewerState = false)
{
    private static readonly string[] Content = ["catalog", "content", "channels", "accounts", "comments", "playlists", "plans", "moderation"];
    private static readonly string[] Admin = [.. Content, "jobs"];
    public static ReadModelCachePolicy? For(string path)
    {
        // Explicit exclusions: live/security/side-effect/file endpoints.
        if (path.Contains("/auth/") || path.EndsWith("/me") && path.StartsWith("/api/v1/admin")
            || path.Contains("playback") || path.Contains("download-options") || path.Contains("/file")
            || path.Contains("/export") || path.EndsWith(".csv") || path.Contains("/evidence")
            || path.StartsWith("/api/v1/session") || path.StartsWith("/api/v1/payments/")
            || path == "/api/v1/admin/operations/system" || path.Contains("audit-logs")
            || path.StartsWith("/api/v1/admin/database-backups")
            || path.Contains("recommendation-context") || path.Contains("/check-handle")
            || path.Contains("upload")
            || path.StartsWith("/api/v1/feedback-survey") || path.Contains("my-plan")) return null;
        if (path.Contains("/recommendations/jobs/")) return null; // Live PK lookup: do not add a revision query to every poll.
        if (path == "/api/v1/categories" || path == "/api/v1/violation-types" || path == "/api/v1/channels/roles")
            return new(1800, ["catalog"], Shared: true);
        if (path == "/api/v1/policies") return new(30, ["catalog"], Shared: true);
        if (path == "/api/v1/plans" || path.StartsWith("/api/v1/plans/") && !path.Contains("invite"))
            return new(60, ["plans", "catalog"]);
        if (path.StartsWith("/api/v1/admin/"))
        {
            if (path.Contains("/moderation/") || path.Contains("/reports/") || path.Contains("/appeals") || path.Contains("/strikes")) return new(5, Admin);
            if (path.Contains("/recommendations/status") || path.Contains("matrix-preview")) return new(30, Admin);
            return new(15, path.Contains("/recommendations/") ? Admin : Content);
        }
        if (path.StartsWith("/api/v1/account/")) return new(60, []);
        if (path.StartsWith("/api/v1/notifications")) return null; // Dedicated user cache already handles invalidation.
        if (path.StartsWith("/api/v1/library/") || path == "/api/v1/downloads") return new(15, Content, ViewerState: true);
        if (path.StartsWith("/api/v1/channels") || path.StartsWith("/api/v1/subscriptions"))
            return new(30, ["channels", "accounts", "content", "moderation", "comments", "catalog"]);
        if (path.StartsWith("/api/v1/playlists")) return new(30, ["playlists", "content", "channels", "plans", "accounts"]);
        if (path.StartsWith("/api/v1/feed/") || path.StartsWith("/api/v1/videos")
            || path.StartsWith("/api/v1/comments") || path.StartsWith("/api/v1/studio/")) return new(30, Content, ViewerState: true);
        if (path == "/api/v1/payments") return new(5, ["plans"]);
        return null;
    }
}
