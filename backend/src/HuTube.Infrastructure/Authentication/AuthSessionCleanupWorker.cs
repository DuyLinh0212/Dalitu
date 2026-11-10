using HuTube.Application.Auth;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HuTube.Infrastructure.Authentication;

/// <summary>
/// Revokes expired or inactive sessions for both the user and admin clients.
/// Rows are retained for account security history, but can no longer authenticate.
/// </summary>
public sealed class AuthSessionCleanupWorker(
    IServiceScopeFactory scopeFactory,
    AuthOptions options,
    TimeProvider clock,
    ILogger<AuthSessionCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CleanupSafelyAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(BackgroundWorkSchedule.UntilNextBoundary(Interval, clock.GetUtcNow()), clock, stoppingToken);
                await CleanupSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CleanupSafelyAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
            var now = clock.GetUtcNow();
            var inactiveBefore = now.AddDays(-options.SessionInactivityDays);
            var revoked = await db.Sessions
                .Where(session => session.RevokedAt == null
                    && (session.ExpiresAt <= now || session.LastActiveAt <= inactiveBefore))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(session => session.RevokedAt, now)
                    .SetProperty(session => session.RevokeReason, "expired-or-inactive"), ct);

            if (revoked > 0)
                logger.LogInformation("Revoked {SessionCount} expired or inactive login sessions.", revoked);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clean up expired or inactive login sessions.");
        }
    }
}
