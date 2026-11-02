using System.Data.Common;
using System.Net.Http.Json;
using HuTube.Application.Auth;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HuTube.IntegrationTests;

public sealed class SessionValidationQueryTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private sealed class Commands : DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Reads++; return ValueTask.FromResult(result); }
    }

    [Fact]
    public async Task ValidationSnapshot_OneUntrackedQuery_ObservesAccountAndRevocationChanges()
    {
        // Arrange.
        using var client = factory.CreateClient();
        var email = $"validation_{Guid.NewGuid():N}@example.invalid";
        const string password = "ValidationTest42!";
        (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(
            "v_" + Guid.NewGuid().ToString("N")[..16], email, "Validation", password))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new TokenRequest(factory.Emails.Token(email)))).EnsureSuccessStatusCode();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, "mobile"));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        await using var writer = factory.CreateDb();
        var id = await writer.Sessions.Where(s => s.UserId == login.User.UserId).Select(s => s.SessionId).SingleAsync();
        var counter = new Commands();
        await using var reader = new HuTubeDbContext(new DbContextOptionsBuilder<HuTubeDbContext>()
            .UseNpgsql(factory.Connection).AddInterceptors(counter).Options);
        var store = new AuthStore(reader);

        // Act / Assert: each validation reads fresh state in one SQL command.
        var valid = await store.ReadSessionValidationAsync(id, default);
        Assert.NotNull(valid); Assert.True(valid.UserAllowed); Assert.True(valid.AdminAllowed);
        Assert.Equal(1, counter.Reads); Assert.Empty(reader.ChangeTracker.Entries());
        await writer.Users.Where(u => u.UserId == login.User.UserId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "banned"));
        Assert.False((await store.ReadSessionValidationAsync(id, default))!.UserAllowed);
        await writer.Sessions.Where(s => s.SessionId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow));
        Assert.NotNull((await store.ReadSessionValidationAsync(id, default))!.RevokedAt);
        Assert.Equal(3, counter.Reads); Assert.Empty(reader.ChangeTracker.Entries());
    }
}
