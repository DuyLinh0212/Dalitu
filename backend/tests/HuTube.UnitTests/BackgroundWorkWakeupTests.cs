using HuTube.Infrastructure.Persistence;
using Npgsql;

namespace HuTube.UnitTests;

public sealed class BackgroundWorkWakeupTests
{
    [Fact]
    public void IdleReconciliationAlignsAndEarlierDeadlinesWin()
    {
        var now = new DateTimeOffset(2026, 11, 1, 0, 10, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMinutes(20), BackgroundWorkWakeup<object>.DelayUntil(null, now));
        Assert.Equal(TimeSpan.FromMinutes(3), BackgroundWorkWakeup<object>.DelayUntil(now.AddMinutes(3), now));
        Assert.Equal(TimeSpan.FromSeconds(1), BackgroundWorkWakeup<object>.DelayUntil(now.AddMinutes(-1), now));
        Assert.Equal(TimeSpan.FromMinutes(50), BackgroundWorkSchedule.UntilNextBoundary(TimeSpan.FromHours(1), now));
    }

    [Fact]
    public async Task SignalWakesAllDeliveryWorkersAndCoalescesDuplicateSignals()
    {
        using var wakeup = new BackgroundWorkWakeup<object>(4);
        var waiters = Enumerable.Range(0, 4)
            .Select(_ => wakeup.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)).ToArray();
        wakeup.Signal();
        Assert.All(await Task.WhenAll(waiters), Assert.True);
        Assert.False(await wakeup.WaitAsync(TimeSpan.Zero, CancellationToken.None));

        wakeup.Signal();
        wakeup.Signal();
        for (var i = 0; i < 4; i++)
            Assert.True(await wakeup.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await wakeup.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Theory]
    [InlineData("ep-example-pooler.us-east-1.aws.neon.tech", "ep-example.us-east-1.aws.neon.tech")]
    [InlineData("postgres-pooler.internal", "postgres-pooler.internal")]
    public void AdvisoryLockConnectionUsesDedicatedSession(string inputHost, string expectedHost)
    {
        var input = new NpgsqlConnectionStringBuilder { Host = inputHost, Database = "test", Username = "test", Password = "test", Pooling = true };
        var output = new NpgsqlConnectionStringBuilder(DatabaseConnectionString.ForSessionLocks(input.ConnectionString));
        Assert.Equal(expectedHost, output.Host);
        Assert.False(output.Pooling);
        Assert.Equal(input.Username, output.Username);
        Assert.Equal(input.Password, output.Password);
    }
}
