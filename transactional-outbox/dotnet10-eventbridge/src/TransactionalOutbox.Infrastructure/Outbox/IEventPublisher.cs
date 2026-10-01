namespace TransactionalOutbox.Infrastructure.Outbox;

public interface IEventPublisher
{
    Task<PublishResult> PublishAsync(ClaimedOutboxMessage message, CancellationToken cancellationToken);
}
