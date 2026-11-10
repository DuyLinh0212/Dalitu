using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HuTube.Infrastructure.Operations;

public sealed class DatabaseBackupWorker(
    IServiceScopeFactory scopes,
    BackgroundWorkWakeup<DatabaseBackupWorker> wakeup,
    TimeProvider clock,
    ILogger<DatabaseBackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var missedClaims = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try
            {
                var (acquired, nextDue) = await RunWithLeadershipAsync(stoppingToken);
                missedClaims = acquired ? 0 : missedClaims + 1;
                delay = !acquired && missedClaims <= 3 ? TimeSpan.FromSeconds(5)
                    : BackgroundWorkWakeup<DatabaseBackupWorker>.DelayUntil(nextDue, clock.GetUtcNow());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Database backup worker lost its database connection; reconnecting."); }
            if (!stoppingToken.IsCancellationRequested)
                await wakeup.WaitAsync(delay, stoppingToken);
        }
    }

    private async Task<(bool Acquired, DateTimeOffset? NextDue)> RunWithLeadershipAsync(CancellationToken stoppingToken)
    {
        // A PostgreSQL session lock keeps only one API instance responsible for schedules and jobs.
        await using var lockScope = scopes.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
        await using var lockConnection = new NpgsqlConnection(DatabaseConnectionString.ForSessionLocks(
            lockDb.Database.GetConnectionString() ?? throw new InvalidOperationException("Missing database connection.")));
        await lockConnection.OpenAsync(stoppingToken);
        await using var claim = lockConnection.CreateCommand();
        claim.CommandText = "SELECT pg_try_advisory_lock(hashtextextended('hutube:database-backup-worker', 0))";
        if (!(bool)(await claim.ExecuteScalarAsync(stoppingToken))!) return (false, null);

        DateTimeOffset? nextDue = null;
        using var lease = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = MonitorLockAsync(lockConnection, lease);
        var workerToken = lease.Token;
        try
        {
            await using (var scope = scopes.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AdminDatabaseBackupService>()
                    .RecoverInterruptedRunsAsync(workerToken);

            while (!workerToken.IsCancellationRequested)
            {
                var processed = false;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var backups = scope.ServiceProvider.GetRequiredService<AdminDatabaseBackupService>();
                    await backups.QueueDueScheduleAsync(workerToken);
                    processed = await backups.ProcessNextQueuedRunAsync(workerToken);
                    if (!processed) nextDue = await backups.NextScheduleDueAsync(workerToken);
                }
                catch (OperationCanceledException) when (workerToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Database backup job polling failed."); throw; }

                if (!processed) break;
            }
            if (lease.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                throw new InvalidOperationException("Database backup worker lost its leadership connection.");
        }
        finally
        {
            await lease.CancelAsync();
            await monitor;
            try
            {
                await using var release = lockConnection.CreateCommand();
                release.CommandText = "SELECT pg_advisory_unlock(hashtextextended('hutube:database-backup-worker', 0))";
                await release.ExecuteScalarAsync(CancellationToken.None);
            }
            finally { await lockConnection.CloseAsync(); }
        }
        return (true, nextDue);
    }

    private static async Task MonitorLockAsync(NpgsqlConnection connection, CancellationTokenSource lease)
    {
        try
        {
            while (!lease.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), lease.Token);
                await using var ping = connection.CreateCommand();
                ping.CommandText = "SELECT 1";
                await ping.ExecuteScalarAsync(lease.Token);
            }
        }
        catch (OperationCanceledException) when (lease.IsCancellationRequested) { }
        catch { await lease.CancelAsync(); }
    }
}
