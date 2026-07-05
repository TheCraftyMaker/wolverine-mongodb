namespace OrderDemo.Contracts.Commands.Audit;

/// <summary>
/// Records an audit entry for an action taken against an order. Handled by
/// <c>RecordOrderAuditHandler</c>, which writes directly through
/// <see cref="Wolverine.MongoDB.MongoDbUnitOfWork"/> rather than a repository.
/// </summary>
/// <param name="ForceFailure">
/// Test-only hook: when <c>true</c>, the handler throws after the audit write is staged so
/// tests can prove the write rolls back atomically with the outbox. Never set in production.
/// </param>
public sealed record RecordOrderAuditCommand(
    Guid OrderId,
    string Action,
    string PerformedBy,
    bool ForceFailure = false);
