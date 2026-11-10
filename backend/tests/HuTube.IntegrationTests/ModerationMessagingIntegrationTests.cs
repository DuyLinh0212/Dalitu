using System.Text.Json;
using HuTube.Application.Auth;
using HuTube.Application.Notifications;
using HuTube.Application.Rbac;
using HuTube.Application.Videos;
using HuTube.Domain.Channels;
using HuTube.Domain.Rbac;
using HuTube.Domain.Users;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Notifications;
using HuTube.Infrastructure.Persistence;
using HuTube.Infrastructure.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace HuTube.IntegrationTests;

public sealed class ModerationMessagingIntegrationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private sealed record Fixture(User Owner, User Reviewer, Channel Channel, Video Video, ModerationCase Case, List<User> Viewers);

    private static string? ReadLocalMessagingValue(string key)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var environmentFile = Path.Combine(directory.FullName, ".env.messaging.local");
            if (!File.Exists(environmentFile)) continue;

            foreach (var line in File.ReadLines(environmentFile))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                var separator = trimmed.IndexOf('=');
                if (separator < 0 || !string.Equals(trimmed[..separator].Trim(), key, StringComparison.Ordinal)) continue;

                var value = trimmed[(separator + 1)..].Trim();
                if (value.Length >= 2 &&
                    ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\''))))
                    value = value[1..^1];
                return value;
            }
        }

        return null;
    }

    private static string GetRabbitMqPassword()
    {
        var password = Environment.GetEnvironmentVariable("TEST_RABBITMQ_PASSWORD");
        if (!string.IsNullOrWhiteSpace(password)) return password;

        password = ReadLocalMessagingValue("RabbitMq__Password");
        return !string.IsNullOrWhiteSpace(password)
            ? password
            : throw new InvalidOperationException("Set TEST_RABBITMQ_PASSWORD or configure RabbitMq__Password in .env.messaging.local for real broker integration tests.");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Publisher : IModerationEventPublisher
    {
        public bool Fail { get; set; }
        public List<Guid> Published { get; } = [];
        public Task PublishAsync(ModerationOutboxEvent message, CancellationToken ct)
        {
            if (Fail) throw new IOException("Simulated broker outage");
            Published.Add(message.EventId); return Task.CompletedTask;
        }
    }
    private sealed class FailDecisionAudit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuditLog>().Any(entry => entry.State == EntityState.Added && entry.Entity.Action == "moderation.approve"))
                throw new IOException("Simulated transaction failure");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ConsumerLog : ILogger<ModerationNotificationWorker>
    {
        private int completed;
        public int Completed => Volatile.Read(ref completed);
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Information && formatter(state, error).Contains("notification fanout committed")) Interlocked.Increment(ref completed); }
    }
    private ModerationService Service(HuTubeDbContext db) => new(db,
        new RbacService(new RbacStore(db), new AuthStore(db)), factory.Storage);

    private async Task<Fixture> SeedAsync(int subscriberCount = 4)
    {
        await using var db = factory.CreateDb();
        User Person() => new() { Username = "mq_" + Guid.NewGuid().ToString("N")[..15], Email = $"{Guid.NewGuid():N}@example.test",
            DisplayName = "Messaging fixture", PasswordHash = "fixture", RoleId = UserRoles.User, Status = "active" };
        var owner = Person(); var reviewer = Person();
        var viewers = Enumerable.Range(0, subscriberCount).Select(_ => Person()).ToList();
        db.Users.AddRange(viewers.Prepend(owner).Prepend(reviewer)); await db.SaveChangesAsync();
        var channel = new Channel { OwnerUserId = owner.UserId, Name = "Messaging channel", Handle = "mq_" + Guid.NewGuid().ToString("N")[..12] };
        db.Channels.Add(channel); await db.SaveChangesAsync();
        var video = new Video { ChannelId = channel.ChannelId, UploadedByUserId = owner.UserId, Title = "Moderation messaging",
            VideoUrl = "test://videos/queued.mp4", Status = "processing", ModerationStatus = "pending", Visibility = "public", Duration = 20, FileSize = 100 };
        db.Videos.Add(video); await db.SaveChangesAsync();
        var item = new ModerationCase { VideoId = video.VideoId, CaseType = "upload_review", SubmittedAt = DateTimeOffset.UtcNow };
        db.ModerationCases.Add(item);
        db.Subscriptions.AddRange(viewers.Select(viewer => new Subscription { ChannelId = channel.ChannelId, UserId = viewer.UserId, SubscribedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        await db.SaveChangesAsync();
        return new(owner, reviewer, channel, video, item, viewers);
    }
    private async Task<VideoModerationEvent> ApproveAsync(Fixture data)
    {
        await using var db = factory.CreateDb();
        var service = Service(db);
        var claim = await service.ClaimCaseAsync(data.Reviewer.UserId, data.Case.ModerationCaseId);
        await service.ResolveCaseAsync(data.Reviewer.UserId, data.Case.ModerationCaseId, new("approve", null, null, null, claim.ClaimId));
        var events = await db.ModerationOutboxEvents.ToListAsync();
        return events.Select(row => JsonSerializer.Deserialize<VideoModerationEvent>(row.Payload)!).Single(message => message.VideoId == data.Video.VideoId);
    }
    private async Task<bool> BatchAsync(VideoModerationEvent message, int size = 2)
    {
        await using var db = factory.CreateDb();
        return await new NotificationFanout(db, TimeProvider.System).ProcessBatchAsync(message, size);
    }

    [Fact]
    public async Task CommittedDecisionAndFanoutSignalTheirIdleWorkers()
    {
        var data = await SeedAsync(0);
        using var outboxWakeup = new BackgroundWorkWakeup<ModerationOutboxWorker>();
        using var deliveryWakeup = new BackgroundWorkWakeup<NotificationDeliveryWorker>(4);
        VideoModerationEvent message;
        await using (var db = factory.CreateDb())
        {
            var rbac = new RbacService(new RbacStore(db), new AuthStore(db));
            var service = new ModerationService(db, rbac, factory.Storage,
                new StrikePolicySettingsService(db, rbac), outboxWakeup: outboxWakeup);
            var claim = await service.ClaimCaseAsync(data.Reviewer.UserId, data.Case.ModerationCaseId);
            await service.ResolveCaseAsync(data.Reviewer.UserId, data.Case.ModerationCaseId,
                new("approve", null, null, null, claim.ClaimId));
            message = (await db.ModerationOutboxEvents.ToListAsync())
                .Select(row => JsonSerializer.Deserialize<VideoModerationEvent>(row.Payload)!)
                .Single(item => item.VideoId == data.Video.VideoId);
        }
        Assert.True(await outboxWakeup.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        await using (var db = factory.CreateDb())
            await new NotificationFanout(db, TimeProvider.System, deliveryWakeup).ProcessBatchAsync(message);
        Assert.True(await deliveryWakeup.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task DecisionCommitsOneSmallOutboxEvent_WithoutWaitingForAnyNotificationDelivery()
    {
        var data = await SeedAsync(12);
        var message = await ApproveAsync(data);
        await using var db = factory.CreateDb();
        var video = await db.Videos.SingleAsync(row => row.VideoId == data.Video.VideoId);
        Assert.Equal("published", video.Status); Assert.Equal("approved", video.ModerationStatus);
        Assert.True(message.NotifySubscribers); Assert.Single(message.OwnerNotifications);
        Assert.Empty(await db.Notifications.Where(row => row.ResourceId == video.VideoId).ToListAsync());
        Assert.Empty(await db.NotificationDeliveries.Where(row => row.EventId == message.EventId).ToListAsync());
        Assert.Null((await db.ModerationOutboxEvents.FindAsync(message.EventId))!.PublishedAt);
        var service = Service(db);
        var error = await Assert.ThrowsAsync<AuthException>(() => service.ResolveCaseAsync(data.Reviewer.UserId, data.Case.ModerationCaseId, new("approve", null, null, null)));
        Assert.Equal(409, error.Status);
        Assert.Single(await db.ModerationOutboxEvents.Where(row => row.EventId == message.EventId).ToListAsync());
    }

    [Fact]
    public async Task TransactionFailureRollsBackVideoDecisionAndOutboxTogether()
    {
        var data = await SeedAsync(0);
        Guid? claimId;
        await using (var db = factory.CreateDb()) claimId = (await Service(db).ClaimCaseAsync(data.Reviewer.UserId, data.Case.ModerationCaseId)).ClaimId;
        var options = new DbContextOptionsBuilder<HuTubeDbContext>().UseNpgsql(factory.Connection).AddInterceptors(new FailDecisionAudit()).Options;
        await using (var db = new HuTubeDbContext(options))
            await Assert.ThrowsAsync<IOException>(() => Service(db).ResolveCaseAsync(data.Reviewer.UserId, data.Case.ModerationCaseId, new("approve", null, null, null, claimId)));
        await using var check = factory.CreateDb();
        Assert.Equal("pending", (await check.Videos.FindAsync(data.Video.VideoId))!.ModerationStatus);
        Assert.Null((await check.ModerationCases.FindAsync(data.Case.ModerationCaseId))!.ResolvedAt);
        Assert.DoesNotContain(await check.ModerationOutboxEvents.ToListAsync(), row => JsonSerializer.Deserialize<VideoModerationEvent>(row.Payload)!.VideoId == data.Video.VideoId);
    }

    [Fact]
    public async Task BrokerOutageKeepsOutboxPending_AndRetryMarksItPublishedOnlyAfterConfirmation()
    {
        var message = await ApproveAsync(await SeedAsync(0));
        var clock = new Clock { Now = DateTimeOffset.UtcNow.AddDays(1) };
        var publisher = new Publisher { Fail = true };
        // Limit selection to this event; other decisions in this fixture can remain pending.
        await using (var db = factory.CreateDb())
            await db.ModerationOutboxEvents.Where(row => row.EventId != message.EventId).ExecuteUpdateAsync(set => set.SetProperty(row => row.AvailableAt, clock.Now.AddYears(1)));
        await using (var db = factory.CreateDb()) Assert.True(await new ModerationOutboxPublisher(db, publisher, clock).PublishNextAsync());
        await using (var db = factory.CreateDb())
        {
            var pending = (await db.ModerationOutboxEvents.FindAsync(message.EventId))!;
            Assert.Null(pending.PublishedAt); Assert.Equal(1, pending.Attempts); Assert.True(pending.AvailableAt > clock.Now);
        }
        publisher.Fail = false; clock.Now = clock.Now.AddMinutes(10);
        await using (var db = factory.CreateDb()) Assert.True(await new ModerationOutboxPublisher(db, publisher, clock).PublishNextAsync());
        Assert.Equal(message.EventId, Assert.Single(publisher.Published));
        await using var final = factory.CreateDb();
        Assert.NotNull((await final.ModerationOutboxEvents.FindAsync(message.EventId))!.PublishedAt);
    }

    [Fact]
    public async Task FanoutResumesInBatches_HonorsPreferences_AndDuplicateConsumersDoNotDuplicateNotifications()
    {
        var data = await SeedAsync(6);
        await using (var db = factory.CreateDb())
        {
            await db.Subscriptions.Where(row => row.UserId == data.Viewers[0].UserId).ExecuteUpdateAsync(set => set.SetProperty(row => row.NotificationsEnabled, false));
            db.NotificationSettings.Add(new NotificationSetting { UserId = data.Viewers[1].UserId, NewVideoEnabled = false });
            db.NotificationSettings.Add(new NotificationSetting { UserId = data.Viewers[2].UserId, InAppEnabled = false, EmailEnabled = false });
            await db.Subscriptions.Where(row => row.UserId == data.Viewers[3].UserId).ExecuteUpdateAsync(set => set.SetProperty(row => row.SubscribedAt, DateTimeOffset.UtcNow.AddDays(1)));
            await db.SaveChangesAsync();
        }
        var message = await ApproveAsync(data);
        Assert.False(await BatchAsync(message, 1));
        // Independent DbContexts simulate another worker resuming after restart.
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => BatchAsync(message, 1)));
        while (!await BatchAsync(message, 1)) { }
        Assert.True(await BatchAsync(message, 1));
        await using var check = factory.CreateDb();
        var rows = await check.Notifications.Where(row => row.ResourceId == data.Video.VideoId).ToListAsync();
        Assert.Equal(3, rows.Count); // owner + two enabled subscribers
        Assert.Equal(3, rows.Select(row => row.UserId).Distinct().Count());
        Assert.Single(rows, row => row.UserId == data.Owner.UserId && row.Type == "video_approved");
        Assert.DoesNotContain(rows, row => data.Viewers.Take(4).Any(user => user.UserId == row.UserId));
        Assert.NotNull((await check.NotificationInboxEvents.FindAsync(message.EventId))!.CompletedAt);
    }

    [Theory]
    [InlineData("private", "published")]
    [InlineData("public", "blocked")]
    public async Task WorkerDoesNotAnnounceVideoThatBecameUnavailable(string visibility, string status)
    {
        var data = await SeedAsync(); var message = await ApproveAsync(data);
        await using (var db = factory.CreateDb())
            await db.Videos.Where(row => row.VideoId == data.Video.VideoId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Visibility, visibility).SetProperty(row => row.Status, status));
        Assert.True(await BatchAsync(message));
        await using var check = factory.CreateDb();
        Assert.Empty(await check.Notifications.Where(row => row.ResourceId == data.Video.VideoId && row.Type == "new_video").ToListAsync());
    }

    [Fact]
    public async Task RabbitMqConfirmedPublish_RealConsumerAndRedeliveryProduceOneInboxPerRecipient()
    {
        var password = GetRabbitMqPassword();
        var suffix = Guid.NewGuid().ToString("N");
        var host = Environment.GetEnvironmentVariable("TEST_RABBITMQ_HOST");
        if (string.IsNullOrWhiteSpace(host)) host = ReadLocalMessagingValue("RabbitMq__HostName");
        var options = new RabbitMqOptions { Enabled = true, Password = password,
            HostName = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host,
            Exchange = "dalitu.test." + suffix, Queue = "dalitu.test." + suffix, FanoutBatchSize = 2 };
        await using var transport = new RabbitMqTransport(options);
        await using var connection = await transport.CreateFactory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var data = await SeedAsync(3); var message = await ApproveAsync(data);
        var log = new ConsumerLog();
        var worker = new ModerationNotificationWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), transport, options, log);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await using (var db = factory.CreateDb()) await transport.PublishAsync((await db.ModerationOutboxEvents.FindAsync(message.EventId))!, CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                await using var db = factory.CreateDb();
                if (await db.NotificationInboxEvents.AnyAsync(row => row.EventId == message.EventId && row.CompletedAt != null)) break;
                Assert.True(DateTime.UtcNow < deadline, "Real RabbitMQ consumer did not complete.");
                await Task.Delay(100);
            }
            await using (var db = factory.CreateDb()) await transport.PublishAsync((await db.ModerationOutboxEvents.FindAsync(message.EventId))!, CancellationToken.None);
            while (log.Completed < 2)
            {
                Assert.True(DateTime.UtcNow < deadline, "The duplicate event was not acknowledged.");
                await Task.Delay(100);
            }
            await using var check = factory.CreateDb();
            Assert.Equal(4, await check.Notifications.CountAsync(row => row.ResourceId == data.Video.VideoId));
            Assert.Equal(4, await check.NotificationDeliveries.CountAsync(row => row.EventId == message.EventId));
            // Poison payloads must leave the main queue without an infinite retry loop.
            await channel.BasicPublishAsync(options.Exchange, "video.moderated.v1", false,
                new BasicProperties { Persistent = true, MessageId = Guid.NewGuid().ToString() }, "invalid-json"u8.ToArray());
            BasicGetResult? poison;
            do
            {
                poison = await channel.BasicGetAsync(options.Queue + ".dead", autoAck: true);
                if (poison == null) await Task.Delay(100);
                Assert.True(DateTime.UtcNow < deadline, "Poison message did not reach the dead-letter queue.");
            } while (poison == null);
            Assert.Equal("invalid-json", System.Text.Encoding.UTF8.GetString(poison.Body.Span));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None); worker.Dispose();
            foreach (var queue in new[] { options.Queue, options.Queue + ".retry", options.Queue + ".dead" })
                await channel.QueueDeleteAsync(queue, false, false);
            await channel.ExchangeDeleteAsync(options.Exchange, false);
            await channel.ExchangeDeleteAsync(options.Exchange + ".dead", false);
        }
    }
}
