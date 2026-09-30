using Microsoft.Extensions.Options;
using TransactionalOutbox.Infrastructure.Outbox;

namespace TransactionalOutbox.UnitTests.Outbox;

public sealed class RetryScheduleTests
{
    private static RetrySchedule Create(TimeSpan? max = null) =>
        new(Options.Create(new OutboxOptions { MaxRetryDelay = max ?? TimeSpan.FromMinutes(5) }));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void DelayIsExponentialBaseWithNoJitterAtZeroSample(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), Create().GetDelay(attempt, 0d));

    [Theory]
    [InlineData(1, 2, 2.5)]
    [InlineData(2, 4, 5)]
    [InlineData(3, 8, 10)]
    public void DelayAddsUpTo25PercentJitter(int attempt, double min, double max)
    {
        RetrySchedule schedule = Create();
        Assert.InRange(schedule.GetDelay(attempt, 0.5), TimeSpan.FromSeconds(min), TimeSpan.FromSeconds(max));
        Assert.InRange(schedule.GetDelay(attempt, 0.999999), TimeSpan.FromSeconds(min), TimeSpan.FromSeconds(max));
    }

    [Fact]
    public void AttemptThreeWithHalfJitterIsNineSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(9), Create().GetDelay(attemptCount: 3, jitterSample: 0.5));

    [Theory]
    [InlineData(9, 0d)]
    [InlineData(20, 0.99)]
    [InlineData(1000, 0.99)]
    [InlineData(int.MaxValue, 0.99)]
    public void DelayNeverExceedsConfiguredCap(int attempt, double jitter) =>
        Assert.True(Create(TimeSpan.FromSeconds(30)).GetDelay(attempt, jitter) <= TimeSpan.FromSeconds(30));

    [Fact]
    public void NonPositiveAttemptCountsUseFirstDelay() =>
        Assert.Equal(TimeSpan.FromSeconds(2), Create().GetDelay(0, 0d));
}
