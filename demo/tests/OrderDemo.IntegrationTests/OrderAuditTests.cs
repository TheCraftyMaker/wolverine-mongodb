using FluentAssertions;
using MongoDB.Driver;
using OrderDemo.Contracts.Commands.Audit;
using OrderDemo.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Tracking;

namespace OrderDemo.IntegrationTests;

/// <summary>
/// Safety-net integration tests for the demo <see cref="Wolverine.MongoDB.MongoDbUnitOfWork"/>
/// showcase (T4.1): <c>RecordOrderAuditHandler</c> writes an <see cref="OrderAuditEntry"/>
/// directly through <c>uow.Collection&lt;T&gt;(name)</c> — no repository, no
/// <c>IClientSessionHandle</c> parameter — alongside the demo's other two write patterns
/// (repository + session, and Tier-1 [Entity]/IStorageAction&lt;T&gt;).
///
/// The audit collection is caller-named ("order_audit_entries"), not derived from the
/// entity-collection naming convention — matching the UoW's free-form Collection&lt;T&gt;(name)
/// contract (see D5 §2.3).
/// </summary>
[Collection("orders")]
public class OrderAuditTests(OrdersFixture fixture)
{
    private const string AuditCollection = "order_audit_entries";

    private static Task<OrderAuditEntry?> FindEntryAsync(IMongoDatabase mongo, Guid orderId)
        => mongo.GetCollection<OrderAuditEntry>(AuditCollection)
            .Find(Builders<OrderAuditEntry>.Filter.Eq(e => e.OrderId, orderId))
            .FirstOrDefaultAsync()!;

    // ── can_record_order_audit_entry ────────────────────────────────────────────

    [Fact]
    public async Task Can_Record_Order_Audit_Entry()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var orderId = Guid.NewGuid();
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new RecordOrderAuditCommand(orderId, "Shipped", "alice"));

        var entry = await FindEntryAsync(mongo, orderId);

        entry.Should().NotBeNull("MongoDbUnitOfWork.Collection<T>().InsertOneAsync must persist the audit entry");
        entry!.OrderId.Should().Be(orderId);
        entry.Action.Should().Be("Shipped");
        entry.PerformedBy.Should().Be("alice");
    }

    // ── audit_entry_commits_atomically_with_outbox ──────────────────────────────

    [Fact]
    public async Task Audit_Entry_Rolls_Back_With_Outbox_On_Forced_Failure()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var orderId = Guid.NewGuid();

        // The handler stages the audit write via the UoW, then throws (ForceFailure=true).
        // The throw must abort the transaction so the write never lands.
        var act = async () => await bus.InvokeAsync(
            new RecordOrderAuditCommand(orderId, "Shipped", "alice", ForceFailure: true));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*forced post-write failure*");

        // Give any (incorrectly) relayed cascade/outbox entry a chance to land, so a genuine
        // atomicity violation surfaces instead of being masked by timing.
        await Task.Delay(500);

        (await FindEntryAsync(mongo, orderId))
            .Should().BeNull("the audit write must roll back with the transaction on a forced failure");
    }
}
