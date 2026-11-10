using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HuTube.Infrastructure.Recommendations;

public sealed class RecommendationJobWorker(IServiceScopeFactory scopes,
    BackgroundWorkWakeup<RecommendationJobWorker> wakeup, TimeProvider clock,
    ILogger<RecommendationJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var missedClaims = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try
            {
                var acquired = await RunWithLeadershipAsync(stoppingToken);
                missedClaims = acquired ? 0 : missedClaims + 1;
                delay = !acquired && missedClaims <= 3 ? TimeSpan.FromSeconds(5)
                    : BackgroundWorkWakeup<RecommendationJobWorker>.DelayUntil(null, clock.GetUtcNow());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Recommendation worker lost its database connection; reconnecting."); }
            if (!stoppingToken.IsCancellationRequested) await wakeup.WaitAsync(delay, stoppingToken);
        }
    }

    private async Task<bool> RunWithLeadershipAsync(CancellationToken stoppingToken)
    {
        // A session lock elects one worker across API instances. It is released by
        // PostgreSQL on process/connection loss, so only its new owner recovers jobs.
        await using var lockScope = scopes.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
        await using var lockConnection = new NpgsqlConnection(DatabaseConnectionString.ForSessionLocks(
            lockDb.Database.GetConnectionString() ?? throw new InvalidOperationException("Missing database connection.")));
        await lockConnection.OpenAsync(stoppingToken);
        await using var claim = lockConnection.CreateCommand();
        claim.CommandText = "SELECT pg_try_advisory_lock(hashtextextended('hutube:recommendation-worker', 0))";
        if (!(bool)(await claim.ExecuteScalarAsync(stoppingToken))!) return false;
        var hostStoppingToken = stoppingToken;
        using var lease = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = MonitorLockAsync(lockConnection, lease);
        stoppingToken = lease.Token;
        try
        {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
            var interrupted = await db.RecommendationJobs.Where(x => x.Status == "running").ToListAsync(stoppingToken);
            foreach (var job in interrupted)
            {
                if (job.Kind == "model_update" && job.ModelCsvKey != null && job.ModelCsvSha256 != null)
                { job.Status = "queued"; job.Step = "recovering"; job.Error = null; continue; }
                job.Status = "failed"; job.Step = "interrupted";
                job.Error = "Tiến trình bị dừng trước khi job hoàn thành. Có thể chạy lại.";
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
                var cancelled = await db.RecommendationJobs.Where(x => x.Status == "cancelling").ToListAsync(stoppingToken);
                foreach (var item in cancelled) { item.Status = "cancelled"; item.Step = "cancelled"; }
                await db.SaveChangesAsync(stoppingToken);
                var job = await db.RecommendationJobs.Where(x => x.Status == "queued")
                    .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(stoppingToken);
                if (job != null)
                {
                    var changed = await db.RecommendationJobs.Where(x => x.JobId == job.JobId && x.Status == "queued")
                        .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "running")
                            .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), stoppingToken);
                    if (changed == 0) continue;
                    await db.Entry(job).ReloadAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<RecommendationAdminService>()
                        .ProcessAsync(job, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Recommendation job polling failed."); throw; }
            break;
        }
        if (lease.IsCancellationRequested && !hostStoppingToken.IsCancellationRequested)
            throw new InvalidOperationException("Recommendation worker lost its leadership connection.");
        }
        finally
        {
            await lease.CancelAsync();
            await monitor;
            try
            {
                await using var release = lockConnection.CreateCommand();
                release.CommandText = "SELECT pg_advisory_unlock(hashtextextended('hutube:recommendation-worker', 0))";
                await release.ExecuteScalarAsync(CancellationToken.None);
            }
            finally { await lockConnection.CloseAsync(); }
        }
        return true;
    }

    private static async Task MonitorLockAsync(NpgsqlConnection connection, CancellationTokenSource lease)
    {
        try
        {
            while (!lease.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), lease.Token);
                await using var ping = connection.CreateCommand();
                ping.CommandText = "SELECT 1";
                await ping.ExecuteScalarAsync(lease.Token);
            }
        }
        catch (OperationCanceledException) when (lease.IsCancellationRequested) { }
        catch { await lease.CancelAsync(); }
    }
}
