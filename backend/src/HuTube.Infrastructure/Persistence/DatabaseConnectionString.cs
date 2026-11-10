using Npgsql;

namespace HuTube.Infrastructure.Persistence;

public static class DatabaseConnectionString
{
    // Session advisory locks require a dedicated PostgreSQL session. Neon's
    // transaction pooler may hand each command to a different backend.
    public static string ForSessionLocks(string value)
    {
        var builder = new NpgsqlConnectionStringBuilder(Normalize(value));
        var host = builder.Host;
        if (host != null && host.EndsWith(".neon.tech", StringComparison.OrdinalIgnoreCase)
            && host.Contains("-pooler.", StringComparison.OrdinalIgnoreCase))
            builder.Host = host.Replace("-pooler.", ".", StringComparison.OrdinalIgnoreCase);
        builder.Pooling = false;
        return builder.ConnectionString;
    }

    public static string Normalize(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("postgres" or "postgresql")) return value;
        var builder = new NpgsqlConnectionStringBuilder { Host = uri.Host, Port = uri.IsDefaultPort ? 5432 : uri.Port, Database = uri.AbsolutePath.Trim('/') };
        var credentials = uri.UserInfo.Split(':', 2);
        if (credentials.Length > 0) builder.Username = Uri.UnescapeDataString(credentials[0]);
        if (credentials.Length == 2) builder.Password = Uri.UnescapeDataString(credentials[1]);
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2)
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.OrdinalIgnoreCase);
        if (query.TryGetValue("sslmode", out var ssl) && Enum.TryParse<SslMode>(ssl, true, out var sslMode)) builder.SslMode = sslMode;
        if (query.TryGetValue("channel_binding", out var binding) && Enum.TryParse<ChannelBinding>(binding, true, out var channelBinding)) builder.ChannelBinding = channelBinding;
        return builder.ConnectionString;
    }
}
