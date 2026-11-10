using System.Text.Json;
using HuTube.Application.Notifications;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace HuTube.Infrastructure.Notifications;

public sealed class ModerationOutboxPublisher(HuTubeDbContext db, IModerationEventPublisher publisher, TimeProvider clock)
{
    public Task<DateTimeOffset?> NextDueAsync(CancellationToken ct) => db.ModerationOutboxEvents.AsNoTracking()
        .Where(message => message.PublishedAt == null)
        .Select(message => (DateTimeOffset?)message.AvailableAt).MinAsync(ct);

    public async Task<bool> PublishNextAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var message = await db.ModerationOutboxEvents.FromSqlInterpolated($"""
            SELECT * FROM moderation_outbox_events WHERE published_at IS NULL AND available_at <= {now}
            ORDER BY available_at, event_id LIMIT 1 FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(ct);
        if (message == null) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await publisher.PublishAsync(message, timeout.Token);
            message.PublishedAt = clock.GetUtcNow(); message.LastError = null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            message.Attempts++;
            message.LastError = ex.GetType().Name; // Never persist credentials or broker URI.
            message.AvailableAt = clock.GetUtcNow().AddSeconds(RetrySeconds(message.Attempts));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    internal static double RetrySeconds(int attempts) => Math.Min(300, Math.Pow(2, Math.Min(attempts, 8)));
}

public sealed class ModerationOutboxWorker(IServiceScopeFactory scopes, RabbitMqOptions options,
    BackgroundWorkWakeup<ModerationOutboxWorker> wakeup, TimeProvider clock,
    ILogger<ModerationOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var publisher = scope.ServiceProvider.GetRequiredService<ModerationOutboxPublisher>();
                if (await publisher.PublishNextAsync(stoppingToken)) continue;
                delay = BackgroundWorkWakeup<ModerationOutboxWorker>.DelayUntil(
                    await publisher.NextDueAsync(stoppingToken), clock.GetUtcNow());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Moderation outbox worker will retry; decisions remain committed"); }
            await wakeup.WaitAsync(delay, stoppingToken);
        }
    }
}

public sealed class ModerationNotificationWorker(IServiceScopeFactory scopes, RabbitMqTransport transport,
    RabbitMqOptions options, ILogger<ModerationNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var factory = transport.CreateFactory();
                factory.AutomaticRecoveryEnabled = false;
                await using var connection = await factory.CreateConnectionAsync("dalitu-notifications", stoppingToken);
                using var consumerStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var consumerToken = consumerStop.Token;
                await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
                await transport.DeclareAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 1, false, stoppingToken);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) => { consumerStop.Cancel(); disconnected.TrySetResult(); return Task.CompletedTask; };
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    // Deserialize before returning: RabbitMQ owns the delivery buffer.
                    try
                    {
                        var message = JsonSerializer.Deserialize<VideoModerationEvent>(delivery.Body.Span)
                            ?? throw new JsonException("Missing moderation event.");
                        if (delivery.BasicProperties.MessageId != message.EventId.ToString()) throw new JsonException("Event id mismatch.");
                        var complete = false;
                        while (!complete)
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            complete = await scope.ServiceProvider.GetRequiredService<NotificationFanout>()
                                .ProcessBatchAsync(message, options.FanoutBatchSize, consumerToken);
                        }
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, consumerToken);
                        logger.LogInformation("Moderation event {EventId} notification fanout committed", message.EventId);
                    }
                    catch (OperationCanceledException) when (consumerToken.IsCancellationRequested) { /* Unacked delivery returns on close. */ }
                    catch (JsonException ex)
                    {
                        logger.LogError(ex, "Invalid moderation message {MessageId}; moved to dead-letter queue", delivery.BasicProperties.MessageId);
                        await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, consumerToken);
                    }
                    catch (Exception ex)
                    {
                        var retries = delivery.BasicProperties.Headers?.TryGetValue("x-notification-retries", out var value) == true
                            ? Convert.ToInt32(value) : 0;
                        logger.LogWarning(ex, "Moderation fanout {MessageId} failed, attempt {Attempt}", delivery.BasicProperties.MessageId, retries + 1);
                        if (retries >= options.MaxConsumerRetries)
                            await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, consumerToken);
                        else
                        {
                            // Confirm the durable retry copy before acknowledging the original.
                            var properties = new BasicProperties {
                                Persistent = true, ContentType = "application/json", Type = delivery.BasicProperties.Type,
                                MessageId = delivery.BasicProperties.MessageId,
                                Headers = new Dictionary<string, object?> { ["x-notification-retries"] = retries + 1 }
                            };
                            try
                            {
                                await channel.BasicPublishAsync("", options.Queue + ".retry", mandatory: true, properties, delivery.Body, consumerToken);
                                await channel.BasicAckAsync(delivery.DeliveryTag, false, consumerToken);
                            }
                            catch (Exception retryError)
                            {
                                logger.LogWarning(retryError, "Retry copy not confirmed; original message remains unacknowledged");
                                disconnected.TrySetResult();
                            }
                        }
                    }
                };
                await channel.BasicConsumeAsync(options.Queue, autoAck: false, consumer, stoppingToken);
                await disconnected.Task.WaitAsync(stoppingToken);
                // This loop owns reconnects; do not race automatic consumer recovery.
                await connection.CloseAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Notification consumer connection unavailable; will reconnect"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

public sealed class NotificationDeliveryWorker(IServiceScopeFactory scopes, RabbitMqOptions options,
    BackgroundWorkWakeup<NotificationDeliveryWorker> wakeup, TimeProvider clock,
    ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => options.Enabled
        ? Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunAsync(stoppingToken))) : Task.CompletedTask;

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<NotificationDeliveryProcessor>();
                if (await processor.DeliverNextAsync(stoppingToken)) continue;
                delay = BackgroundWorkWakeup<NotificationDeliveryWorker>.DelayUntil(
                    await processor.NextDueAsync(stoppingToken), clock.GetUtcNow());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Notification delivery worker will retry"); }
            await wakeup.WaitAsync(delay, stoppingToken);
        }
    }
}
