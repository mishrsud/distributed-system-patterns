using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Options;

namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed class EventBridgePublisher(IAmazonEventBridge client, IOptions<EventBridgeOptions> options)
    : IEventPublisher
{
    private static readonly HashSet<string> PermanentErrorCodes = new(StringComparer.Ordinal)
    {
        "AccessDeniedException",
        "InvalidArgument",
        "MalformedDetail",
    };

    private readonly EventBridgeOptions _options = options.Value;

    public async Task<PublishResult> PublishAsync(ClaimedOutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var request = new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = _options.EventBusName,
                    Source = _options.Source,
                    DetailType = _options.DetailType,
                    Detail = message.Payload,
                },
            ],
        };

        PutEventsResponse? response;
        try
        {
            response = await client.PutEventsAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonServiceException ex)
        {
            string code = string.IsNullOrEmpty(ex.ErrorCode) ? ex.GetType().Name : ex.ErrorCode;
            return PublishResult.Retryable(code, $"{code}: {ex.Message}");
        }
#pragma warning disable CA1031 // Any non-cancellation transport failure is retried durably.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            string code = ex.GetType().Name;
            return PublishResult.Retryable(code, $"{code}: {ex.Message}");
        }

        return Classify(response);
    }

    private static PublishResult Classify(PutEventsResponse? response)
    {
        PutEventsResultEntry? entry = response?.Entries is { Count: 1 } entries ? entries[0] : null;
        if (response is null || entry is null)
        {
            return PublishResult.Retryable(null, "PutEvents returned no single result entry.");
        }

        if (!string.IsNullOrEmpty(entry.ErrorCode))
        {
            string summary = $"{entry.ErrorCode}: {entry.ErrorMessage}";
            return PermanentErrorCodes.Contains(entry.ErrorCode)
                ? PublishResult.Permanent(entry.ErrorCode, summary)
                : PublishResult.Retryable(entry.ErrorCode, summary);
        }

        if (response.FailedEntryCount != 0)
        {
            return PublishResult.Retryable(
                null,
                response.FailedEntryCount is null
                    ? "PutEvents response has no FailedEntryCount."
                    : $"PutEvents reported {response.FailedEntryCount} failed entries.");
        }

        if (string.IsNullOrEmpty(entry.EventId))
        {
            return PublishResult.Retryable(null, "PutEvents result entry has no event ID.");
        }

        return PublishResult.Succeeded(entry.EventId);
    }
}
