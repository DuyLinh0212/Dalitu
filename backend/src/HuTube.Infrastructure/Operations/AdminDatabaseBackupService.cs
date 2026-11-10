using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using HuTube.Application.Operations;
using HuTube.Application.Rbac;
using HuTube.Application.Storage;
using HuTube.Domain.Backups;
using HuTube.Domain.Rbac;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HuTube.Infrastructure.Operations;

public sealed class DatabaseBackupAlreadyRunningException()
    : Exception("A database backup is already queued or running.");

public sealed class DatabaseBackupNotAvailableException(string message)
    : Exception(message);

public sealed class DatabaseBackupStorageUnavailableException(string message)
    : Exception(message);

public sealed class AdminDatabaseBackupService(
    HuTubeDbContext db,
    RbacService rbac,
    IConfiguration configuration,
    R2Options r2Options,
    TimeProvider clock,
    ILogger<AdminDatabaseBackupService> logger,
    BackgroundWorkWakeup<DatabaseBackupWorker>? backupWakeup = null) : IDisposable
{
    private const string DefaultTimeZoneId = "Asia/Ho_Chi_Minh";
    private const string BackupPrefix = "database-backups";
    private const long MultipartThreshold = 100L * 1024 * 1024;
    private const long MultipartPartSize = 64L * 1024 * 1024;
    private static readonly TimeZoneInfo BackupTimeZone = ResolveTimeZone(DefaultTimeZoneId);
    private AmazonS3Client? s3Client;
    private TransferUtility? transfer;

    public async Task<AdminDatabaseBackupOverviewResponse> GetOverviewAsync(
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken ct = default)
    {
        if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value)
            throw new ArgumentException("Ngày bắt đầu phải nằm trước hoặc bằng ngày kết thúc.");

        var schedule = await db.DatabaseBackupSchedules.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, ct)
            ?? new DatabaseBackupSchedule();
        var activeRun = await db.DatabaseBackupRuns.AsNoTracking()
            .Where(item => item.Status == "queued" || item.Status == "running")
            .OrderBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var query = db.DatabaseBackupRuns.AsNoTracking();
        if (fromDate.HasValue)
            query = query.Where(item => item.CreatedAt >= ToUtcStartOfDay(fromDate.Value));
        if (toDate.HasValue)
            query = query.Where(item => item.CreatedAt < ToUtcStartOfDay(toDate.Value.AddDays(1)));

        var runs = await query.OrderByDescending(item => item.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        return new AdminDatabaseBackupOverviewResponse(
            MapSchedule(schedule),
            activeRun == null ? null : MapRun(activeRun),
            runs.Select(MapRun).ToList(),
            IsR2Configured());
    }

    public async Task<DatabaseBackupScheduleResponse> UpdateScheduleAsync(
        UpdateDatabaseBackupScheduleRequest request,
        Guid actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        var frequency = request.Frequency?.Trim().ToLowerInvariant();
        if (frequency is not ("daily" or "weekly"))
            throw new ArgumentException("Hãy chọn lịch hàng ngày hoặc hàng tuần.");
        if (!TimeOnly.TryParseExact(request.Time?.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            throw new ArgumentException("Giờ chạy không hợp lệ.");
        if (frequency == "weekly" && (!request.DayOfWeek.HasValue || request.DayOfWeek is < 0 or > 6))
            throw new ArgumentException("Hãy chọn ngày chạy trong tuần.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT id FROM public.database_backup_schedules WHERE id = 1 FOR UPDATE", ct);
        var schedule = await db.DatabaseBackupSchedules.SingleOrDefaultAsync(item => item.Id == 1, ct);
        if (schedule == null)
        {
            schedule = new DatabaseBackupSchedule { Id = 1 };
            db.DatabaseBackupSchedules.Add(schedule);
        }

        var oldValues = JsonSerializer.Serialize(new
        {
            schedule.Enabled,
            schedule.Frequency,
            schedule.DayOfWeek,
            schedule.Hour,
            schedule.Minute,
            schedule.TimeZoneId
        });
        schedule.Enabled = request.Enabled;
        schedule.Frequency = frequency;
        schedule.DayOfWeek = frequency == "weekly" ? request.DayOfWeek : null;
        schedule.Hour = time.Hour;
        schedule.Minute = time.Minute;
        schedule.TimeZoneId = DefaultTimeZoneId;
        schedule.UpdatedAt = clock.GetUtcNow();
        schedule.UpdatedByUserId = actorUserId;
        schedule.NextRunAt = request.Enabled ? GetNextRunAt(schedule, clock.GetUtcNow()) : null;

        var newValues = JsonSerializer.Serialize(new
        {
            schedule.Enabled,
            schedule.Frequency,
            schedule.DayOfWeek,
            schedule.Hour,
            schedule.Minute,
            schedule.TimeZoneId,
            schedule.NextRunAt
        });
        await db.SaveChangesAsync(ct);
        await rbac.LogAuditAsync(new AuditLogEntry(
            actorUserId,
            "admin.database_backup.schedule_updated",
            "database_backup_schedule",
            null,
            "Cập nhật lịch sao lưu dữ liệu.",
            oldValues,
            newValues,
            ipAddress,
            userAgent), ct);
        await transaction.CommitAsync(ct);
        backupWakeup?.Signal();
        return MapSchedule(schedule);
    }

    public async Task<DatabaseBackupRunResponse> QueueManualBackupAsync(
        Guid actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        if (!IsR2Configured())
            throw new DatabaseBackupStorageUnavailableException("Cloudflare R2 backup storage is not configured.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockBackupQueueAsync(ct);
        if (await HasActiveRunAsync(ct))
            throw new DatabaseBackupAlreadyRunningException();

        var now = clock.GetUtcNow();
        var run = new DatabaseBackupRun
        {
            RunId = Guid.NewGuid(),
            Trigger = "manual",
            Status = "queued",
            CreatedAt = now,
            InitiatedByUserId = actorUserId
        };
        db.DatabaseBackupRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await rbac.LogAuditAsync(new AuditLogEntry(
            actorUserId,
            "admin.database_backup.manual_requested",
            "database_backup_run",
            run.RunId,
            "Yêu cầu sao lưu dữ liệu ngay.",
            NewValues: JsonSerializer.Serialize(new { run.Trigger, run.Status }),
            IpAddress: ipAddress,
            UserAgent: userAgent), ct);
        await transaction.CommitAsync(ct);
        backupWakeup?.Signal();
        return MapRun(run);
    }

    public async Task<DatabaseBackupDownloadResponse> GetDownloadUrlAsync(
        Guid runId,
        Guid actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        var run = await db.DatabaseBackupRuns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RunId == runId, ct);
        if (run == null || run.Status != "succeeded" || string.IsNullOrWhiteSpace(run.ObjectKey)
            || string.IsNullOrWhiteSpace(run.FileName))
            throw new DatabaseBackupNotAvailableException("Bản sao lưu này chưa sẵn sàng để tải xuống.");

        var url = GetS3Client().GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = r2Options.BucketName,
            Key = run.ObjectKey,
            Verb = HttpVerb.GET,
            Expires = clock.GetUtcNow().AddMinutes(5).UtcDateTime,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentDisposition = $"attachment; filename=\"{run.FileName}\"",
                ContentType = "application/octet-stream"
            }
        });

        await rbac.LogAuditAsync(new AuditLogEntry(
            actorUserId,
            "admin.database_backup.download_url_issued",
            "database_backup_run",
            run.RunId,
            "Cấp liên kết tải bản sao lưu có thời hạn 5 phút.",
            IpAddress: ipAddress,
            UserAgent: userAgent), ct);
        return new DatabaseBackupDownloadResponse(url);
    }

    public async Task RecoverInterruptedRunsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var interrupted = await db.DatabaseBackupRuns
            .Where(item => item.Status == "running")
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Status, "failed")
                .SetProperty(item => item.CompletedAt, now)
                .SetProperty(item => item.ErrorMessage, "Tiến trình sao lưu bị gián đoạn. Hãy chạy lại."), ct);
        if (interrupted > 0)
            logger.LogWarning("Marked {RunCount} interrupted database backups as failed.", interrupted);
    }

    public Task<DateTimeOffset?> NextScheduleDueAsync(CancellationToken ct) => !IsR2Configured()
        ? Task.FromResult<DateTimeOffset?>(null)
        : db.DatabaseBackupSchedules.AsNoTracking().Where(schedule => schedule.Enabled)
            .Select(schedule => schedule.NextRunAt).FirstOrDefaultAsync(ct);

    public async Task QueueDueScheduleAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockBackupQueueAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT id FROM public.database_backup_schedules WHERE id = 1 FOR UPDATE", ct);
        var schedule = await db.DatabaseBackupSchedules.SingleOrDefaultAsync(item => item.Id == 1, ct);
        var now = clock.GetUtcNow();
        if (!IsR2Configured() || schedule == null || !schedule.Enabled || schedule.NextRunAt is null || schedule.NextRunAt > now
            || await HasActiveRunAsync(ct))
        {
            await transaction.CommitAsync(ct);
            return;
        }

        db.DatabaseBackupRuns.Add(new DatabaseBackupRun
        {
            RunId = Guid.NewGuid(),
            Trigger = "schedule",
            Status = "queued",
            CreatedAt = now
        });
        schedule.NextRunAt = GetNextRunAt(schedule, now);
        schedule.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Queued scheduled database backup; next run at {NextRunAt}.", schedule.NextRunAt);
    }

    public async Task<bool> ProcessNextQueuedRunAsync(CancellationToken ct)
    {
        var run = await db.DatabaseBackupRuns
            .Where(item => item.Status == "queued")
            .OrderBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (run == null) return false;

        var startedAt = clock.GetUtcNow();
        var claimed = await db.DatabaseBackupRuns
            .Where(item => item.RunId == run.RunId && item.Status == "queued")
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Status, "running")
                .SetProperty(item => item.StartedAt, startedAt), ct);
        if (claimed == 0) return false;
        await db.Entry(run).ReloadAsync(ct);

        try
        {
            var result = await DumpAndUploadAsync(run.RunId, ct);
            var completedAt = clock.GetUtcNow();
            run.Status = "succeeded";
            run.CompletedAt = completedAt;
            run.ObjectKey = result.ObjectKey;
            run.FileName = result.FileName;
            run.SizeBytes = result.SizeBytes;
            run.ErrorMessage = null;
            await SaveCompletedRunAsync(run, completedAt, ct);
            logger.LogInformation("Database backup {RunId} uploaded to Cloudflare R2 ({SizeBytes} bytes).",
                run.RunId, result.SizeBytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var completedAt = clock.GetUtcNow();
            run.Status = "failed";
            run.CompletedAt = completedAt;
            run.ErrorMessage = "Không thể tạo hoặc tải bản sao lưu lên Cloudflare R2. Kiểm tra log của API.";
            await SaveCompletedRunAsync(run, completedAt, ct);
            logger.LogError(ex, "Database backup {RunId} failed.", run.RunId);
        }

        return true;
    }

    private async Task SaveCompletedRunAsync(DatabaseBackupRun run, DateTimeOffset completedAt, CancellationToken ct)
    {
        var schedule = await db.DatabaseBackupSchedules.SingleOrDefaultAsync(item => item.Id == 1, ct);
        if (schedule != null) schedule.LastRunAt = completedAt;
        await db.SaveChangesAsync(ct);
    }

    private async Task<(string FileName, string ObjectKey, long SizeBytes)> DumpAndUploadAsync(
        Guid runId,
        CancellationToken ct)
    {
        var rawConnection = configuration.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(rawConnection))
            throw new InvalidOperationException("ConnectionStrings__Database is not configured.");

        var connection = new NpgsqlConnectionStringBuilder(DatabaseConnectionString.Normalize(rawConnection));
        var now = clock.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(now, BackupTimeZone);
        var fileName = $"dalitu-postgresql-{localNow:yyyyMMdd-HHmmss}-{runId.ToString("N")[..8]}.dump";
        var objectKey = $"{BackupPrefix}/{localNow:yyyy/MM}/{fileName}";
        var configuredTempDirectory = configuration["DatabaseBackups:TempDirectory"];
        var tempDirectory = string.IsNullOrWhiteSpace(configuredTempDirectory)
            ? Path.Combine(Path.GetTempPath(), "dalitu-database-backups")
            : configuredTempDirectory;
        Directory.CreateDirectory(tempDirectory);
        var dumpPath = Path.Combine(tempDirectory, $"{runId:N}.dump");

        try
        {
            await RunPgDumpAsync(connection, dumpPath, ct);
            var sizeBytes = new FileInfo(dumpPath).Length;
            if (sizeBytes <= 0) throw new InvalidOperationException("pg_dump produced an empty database backup.");

            var request = new TransferUtilityUploadRequest
            {
                BucketName = r2Options.BucketName,
                Key = objectKey,
                FilePath = dumpPath,
                ContentType = "application/octet-stream",
                PartSize = MultipartPartSize,
                DisableDefaultChecksumValidation = true,
                DisablePayloadSigning = true
            };
            await GetTransferUtility().UploadAsync(request, ct);
            return (fileName, objectKey, sizeBytes);
        }
        finally
        {
            try { if (File.Exists(dumpPath)) File.Delete(dumpPath); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove temporary database backup {Path}.", dumpPath); }
        }
    }

    private async Task RunPgDumpAsync(NpgsqlConnectionStringBuilder connection, string dumpPath, CancellationToken ct)
    {
        var executable = configuration["DatabaseBackups:PgDumpPath"];
        var startInfo = new ProcessStartInfo(string.IsNullOrWhiteSpace(executable) ? "pg_dump" : executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--format=custom");
        startInfo.ArgumentList.Add("--compress=6");
        startInfo.ArgumentList.Add("--no-owner");
        startInfo.ArgumentList.Add("--no-privileges");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(dumpPath);
        startInfo.Environment["PGHOST"] = connection.Host;
        startInfo.Environment["PGPORT"] = connection.Port.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["PGDATABASE"] = connection.Database;
        startInfo.Environment["PGUSER"] = connection.Username;
        startInfo.Environment["PGPASSWORD"] = connection.Password;
        startInfo.Environment["PGSSLMODE"] = connection.SslMode switch
        {
            SslMode.Disable => "disable",
            SslMode.Allow => "allow",
            SslMode.Require => "require",
            SslMode.VerifyCA => "verify-ca",
            SslMode.VerifyFull => "verify-full",
            _ => "prefer"
        };
        startInfo.Environment["PGCONNECT_TIMEOUT"] = Math.Max(connection.Timeout, 30)
            .ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["PGCLIENTENCODING"] = "UTF8";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Could not start pg_dump.");
        var errorOutput = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var diagnostic = await errorOutput;
        if (process.ExitCode != 0)
        {
            var detail = diagnostic.Length > 1200 ? diagnostic[..1200] : diagnostic;
            throw new InvalidOperationException($"pg_dump exited with code {process.ExitCode}: {detail}");
        }
    }

    private AmazonS3Client GetS3Client()
    {
        if (s3Client != null) return s3Client;
        if (!IsR2Configured())
            throw new DatabaseBackupStorageUnavailableException("Cloudflare R2 backup storage is not configured.");

        s3Client = new AmazonS3Client(
            new BasicAWSCredentials(r2Options.AccessKeyId, r2Options.SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = string.IsNullOrWhiteSpace(r2Options.EndpointUrl)
                    ? $"https://{r2Options.AccountId}.r2.cloudflarestorage.com"
                    : r2Options.EndpointUrl.TrimEnd('/'),
                AuthenticationRegion = "auto",
                ForcePathStyle = true,
                RetryMode = RequestRetryMode.Adaptive,
                MaxErrorRetry = 5,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED
            });
        return s3Client;
    }

    private TransferUtility GetTransferUtility()
    {
        if (transfer != null) return transfer;
        transfer = new TransferUtility(GetS3Client(), new TransferUtilityConfig
        {
            MinSizeBeforePartUpload = MultipartThreshold,
            ConcurrentServiceRequests = 2
        });
        return transfer;
    }

    private bool IsR2Configured() =>
        !string.IsNullOrWhiteSpace(r2Options.AccountId)
        && !string.IsNullOrWhiteSpace(r2Options.AccessKeyId)
        && !string.IsNullOrWhiteSpace(r2Options.SecretAccessKey)
        && !string.IsNullOrWhiteSpace(r2Options.BucketName);

    private async Task LockBackupQueueAsync(CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('hutube:database-backup-queue', 0))", ct);

    private Task<bool> HasActiveRunAsync(CancellationToken ct) =>
        db.DatabaseBackupRuns.AnyAsync(item => item.Status == "queued" || item.Status == "running", ct);

    private static DatabaseBackupScheduleResponse MapSchedule(DatabaseBackupSchedule schedule) => new(
        schedule.Enabled,
        schedule.Frequency,
        schedule.DayOfWeek,
        $"{schedule.Hour:00}:{schedule.Minute:00}",
        schedule.TimeZoneId,
        schedule.NextRunAt,
        schedule.LastRunAt,
        schedule.UpdatedAt);

    private static DatabaseBackupRunResponse MapRun(DatabaseBackupRun run) => new(
        run.RunId,
        run.Trigger,
        run.Status,
        run.CreatedAt,
        run.StartedAt,
        run.CompletedAt,
        run.FileName,
        run.SizeBytes,
        run.ErrorMessage,
        run.Status == "succeeded" && !string.IsNullOrWhiteSpace(run.ObjectKey));

    private static DateTimeOffset? GetNextRunAt(DatabaseBackupSchedule schedule, DateTimeOffset now)
    {
        if (!schedule.Enabled) return null;
        var timeZone = ResolveTimeZone(schedule.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone).DateTime;
        var today = DateOnly.FromDateTime(localNow);
        var time = new TimeOnly(schedule.Hour, schedule.Minute);
        for (var offset = 0; offset <= 7; offset++)
        {
            var candidateDate = today.AddDays(offset);
            if (schedule.Frequency == "weekly"
                && (int)candidateDate.DayOfWeek != schedule.DayOfWeek)
                continue;
            var localCandidate = candidateDate.ToDateTime(time, DateTimeKind.Unspecified);
            if (localCandidate <= localNow) continue;
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localCandidate, timeZone));
        }
        return null;
    }

    private static DateTimeOffset ToUtcStartOfDay(DateOnly date)
    {
        var localStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, BackupTimeZone));
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException) when (timeZoneId == DefaultTimeZoneId)
        {
            return TimeZoneInfo.CreateCustomTimeZone(DefaultTimeZoneId, TimeSpan.FromHours(7), "UTC+07:00", "UTC+07:00");
        }
    }

    public void Dispose()
    {
        transfer?.Dispose();
        s3Client?.Dispose();
    }
}
