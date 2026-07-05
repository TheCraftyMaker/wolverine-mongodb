using OrderDemo.Contracts.Commands.Audit;
using OrderDemo.Infrastructure.Persistence;
using Wolverine.MongoDB;

namespace OrderDemo.Application.Audit;

/// <summary>
/// Demonstrates <see cref="MongoDbUnitOfWork"/> as the recommended write surface for
/// handlers that write directly to a collection without a repository layer.
///
/// MongoDbUnitOfWork is injected by Wolverine's generated frame (constructed from the
/// open IClientSessionHandle). Every write through Collection&lt;T&gt;(name) automatically
/// participates in the TransactionalFrame transaction — it is impossible to forget the session.
/// </summary>
public static class RecordOrderAuditHandler
{
    public static async Task Handle(
        RecordOrderAuditCommand cmd,
        MongoDbUnitOfWork uow,
        CancellationToken ct)
    {
        var entry = new OrderAuditEntry
        {
            Id = Guid.NewGuid(),
            OrderId = cmd.OrderId,
            Action = cmd.Action,
            PerformedBy = cmd.PerformedBy,
            OccurredAt = DateTimeOffset.UtcNow
        };

        // Collection<T>(name) returns a session-bound write surface — the session is
        // threaded automatically so the write commits with the outbox in one transaction.
        await uow.Collection<OrderAuditEntry>("order_audit_entries").InsertOneAsync(entry, ct);

        // Test-only hook: prove the write above rolls back with the surrounding transaction
        // when the handler fails after staging it (mirrors saga_atomicity.cs's Boom flag).
        if (cmd.ForceFailure)
            throw new InvalidOperationException("forced post-write failure (order audit atomicity test)");
    }
}
