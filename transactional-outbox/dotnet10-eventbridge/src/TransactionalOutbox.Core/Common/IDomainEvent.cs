namespace TransactionalOutbox.Core.Common;

public interface IDomainEvent
{
    Guid Id { get; }

    DateTimeOffset OccurredOnUtc { get; }
}
