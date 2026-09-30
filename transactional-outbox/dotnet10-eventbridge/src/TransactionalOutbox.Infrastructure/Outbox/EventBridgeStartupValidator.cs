using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed class EventBridgeStartupValidator(IAmazonEventBridge client, IOptions<EventBridgeOptions> options)
    : IHostedService
{
    private readonly string _busName = options.Value.EventBusName;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.DescribeEventBusAsync(new DescribeEventBusRequest { Name = _busName }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Any failure means the bus cannot be used; fail startup with context.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new InvalidOperationException(
                $"EventBridge event bus '{_busName}' could not be validated: {ex.Message}", ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
