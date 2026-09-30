using Microsoft.Extensions.Options;

namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed class RetrySchedule(IOptions<OutboxOptions> options)
{
    private const int MaxExponent = 30;
    private const double MaxJitterFraction = 0.25;

    private readonly TimeSpan _maxDelay = options.Value.MaxRetryDelay;

    /// <summary>
    /// Returns min(2^attemptCount seconds, MaxRetryDelay) plus up to 25% positive jitter,
    /// never exceeding MaxRetryDelay. <paramref name="jitterSample"/> is in [0, 1).
    /// </summary>
    public TimeSpan GetDelay(int attemptCount, double jitterSample)
    {
        int exponent = Math.Clamp(attemptCount, 1, MaxExponent);
        double baseSeconds = Math.Min(Math.Pow(2, exponent), _maxDelay.TotalSeconds);
        double sample = Math.Clamp(jitterSample, 0d, 1d);
        double seconds = Math.Min(baseSeconds * (1 + (MaxJitterFraction * sample)), _maxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
