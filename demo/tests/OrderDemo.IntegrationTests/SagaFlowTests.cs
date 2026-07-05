using FluentAssertions;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using OrderDemo.Application.Sagas;
using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Events;
using OrderDemo.Domain.Aggregates;
using OrderDemo.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;

namespace OrderDemo.IntegrationTests;

/// <summary>
/// Safety-net integration tests for the demo <see cref="OrderFulfillmentSaga"/> (S13).
///
/// These lock every saga flow behind automated tests so a future library change can't silently
/// break them: <b>start</b>, <b>continue</b>, <b>complete</b> (document deleted), <b>missing
/// state</b>, <b>duplicate/repeated message</b>, <b>across host restart</b>, and <b>inbox/outbox
/// interaction</b> (the saga and the <c>OrderSummaryProjector</c> stay consistent).
///
/// <para>These run on the shared <see cref="OrdersFixture.CreateHostAsync"/> — the same host the
/// rest of the demo tests use, which now mirrors <c>Program.cs</c>: the saga is active and
/// <see cref="MultipleHandlerBehavior.Separated"/> lets the saga and the projector both handle
/// <c>OrderPlaced</c>/<c>OrderShipped</c> independently. (Previously the fixture excluded the saga
/// to keep the projector working; that hid the fact that a saga chain clears co-registered
/// handlers, so adding the saga had silently dropped the read-model projection.)</para>
///
/// The saga document is read straight from MongoDB (bypassing Wolverine) to independently verify
/// persistence — the collection name is the library's convention: <c>wolverine_saga_</c> +
/// lower-cased type name = <c>wolverine_saga_orderfulfillmentsaga</c>.
/// </summary>
[Collection("orders")]
public class SagaFlowTests(OrdersFixture fixture)
{
    // MongoConstants.SagaCollectionName(typeof(OrderFulfillmentSaga)) — internal to the library, so
    // the exact value is reproduced here. Every flow that reads it would fail if the convention drifted.
    private const string SagaCollection = "wolverine_saga_orderfulfillmentsaga";

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Reads the saga document straight from MongoDB by its _id (= Id = OrderId).</summary>
    private static Task<OrderFulfillmentSaga?> LoadSagaAsync(IMongoDatabase mongo, Guid orderId)
        => mongo.GetCollection<OrderFulfillmentSaga>(SagaCollection)
            .Find(Builders<OrderFulfillmentSaga>.Filter.Eq(s => s.Id, orderId))
            .FirstOrDefaultAsync()!;

    private static Task<long> CountSagaAsync(IMongoDatabase mongo, Guid orderId)
        => mongo.GetCollection<OrderFulfillmentSaga>(SagaCollection)
            .CountDocumentsAsync(Builders<OrderFulfillmentSaga>.Filter.Eq(s => s.Id, orderId));

    private static Task<OrderSummary?> FindSummaryAsync(IMongoDatabase mongo, Guid orderId)
        => mongo.GetCollection<OrderSummary>("order_summaries")
            .Find(Builders<OrderSummary>.Filter.Eq(s => s.OrderId, orderId))
            .FirstOrDefaultAsync()!;

    private static Task<FulfillmentDeliveryStatus?> FindDeliveryStatusAsync(IMongoDatabase mongo, Guid orderId)
        => mongo.GetCollection<FulfillmentDeliveryStatus>("fulfillment_delivery_statuses")
            .Find(Builders<FulfillmentDeliveryStatus>.Filter.Eq(s => s.OrderId, orderId))
            .FirstOrDefaultAsync()!;

    private static async Task<Guid> FindOrderIdAsync(IMongoDatabase mongo, Guid customerId)
    {
        var order = await mongo.GetCollection<Order>("orders")
            .Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();
        order.Should().NotBeNull("PlaceOrderHandler must persist the order");
        return order.Id;
    }

    /// <summary>
    /// Seeds a product and places an order, waiting for the full pipeline (incl. the saga Start and
    /// the projector). Returns the generated order id.
    /// </summary>
    private static async Task<Guid> PlaceOrderAsync(IHost host, IMongoDatabase mongo, Guid customerId)
    {
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Widget");

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new PlaceOrderCommand(customerId, [new(productId, "Widget", 1, 25m)]));

        return await FindOrderIdAsync(mongo, customerId);
    }

    // ── F1: start ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlaceOrder_StartsSaga_AndProjectsSummary()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var orderId = await PlaceOrderAsync(host, mongo, customerId);

        var saga = await LoadSagaAsync(mongo, orderId);
        saga.Should().NotBeNull("OrderPlacedApplicationEvent must start the saga");
        saga!.Id.Should().Be(orderId);
        saga.CustomerId.Should().Be(customerId);
        saga.OrderPlaced.Should().BeTrue();
        saga.OrderShipped.Should().BeFalse();
        saga.DeliveryConfirmed.Should().BeFalse();

        // The projector handled the same event independently (Separated mode): the read model exists.
        var summary = await FindSummaryAsync(mongo, orderId);
        summary.Should().NotBeNull("the OrderSummaryProjector must run alongside the saga");
        summary!.Status.Should().Be("Pending");
    }

    // ── F2: continue ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShipOrder_AdvancesSaga()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var orderId = await PlaceOrderAsync(host, mongo, Guid.NewGuid());

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new ShipOrderCommand(orderId));

        var saga = await LoadSagaAsync(mongo, orderId);
        saga.Should().NotBeNull("the saga must still exist after a continue message");
        saga!.OrderPlaced.Should().BeTrue();
        saga.OrderShipped.Should().BeTrue("OrderShippedApplicationEvent must advance the saga");
        saga.ShippedAt.Should().NotBeNull();
        saga.DeliveryConfirmed.Should().BeFalse();
    }

    // ── F3: complete (document deleted) ─────────────────────────────────────────────

    [Fact]
    public async Task ConfirmDelivery_CompletesAndDeletesSaga()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var orderId = await PlaceOrderAsync(host, mongo, Guid.NewGuid());

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new ShipOrderCommand(orderId));
        (await LoadSagaAsync(mongo, orderId)).Should().NotBeNull("the saga must exist before completion");

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new ConfirmDeliveryCommand(orderId, DateTimeOffset.UtcNow));

        // Direct Mongo query: MarkCompleted() must have deleted the saga document.
        (await LoadSagaAsync(mongo, orderId))
            .Should().BeNull("MarkCompleted() must delete the saga document from MongoDB");

        // Completing the saga only removes the saga document — the read model is untouched.
        var summary = await FindSummaryAsync(mongo, orderId);
        summary.Should().NotBeNull();
        summary!.Status.Should().Be("Shipped", "completing the saga must not disturb the read model");
    }

    // ── F4: missing state ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmDelivery_UnknownOrder_ThrowsUnknownSagaException()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();

        // No order was ever placed → no saga document for this id. The continue handler loads the
        // saga, finds nothing, and Wolverine throws UnknownSagaException (propagated inline because
        // the command is invoked directly rather than delivered through a durable queue).
        var act = async () => await bus.InvokeAsync(new ConfirmDeliveryCommand(Guid.NewGuid(), DateTimeOffset.UtcNow));

        await act.Should().ThrowAsync<UnknownSagaException>();
    }

    // ── F5: duplicate / repeated message (deterministic inbox dedup) ───────────────

    [Fact]
    public async Task RedeliveredStartEnvelope_DoesNotStartSagaTwice()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Widget");

        // Place the order and capture the OrderPlacedApplicationEvent envelope(s) that were handled.
        var tracked = await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new PlaceOrderCommand(customerId, [new(productId, "Widget", 1, 25m)]));
        var orderId = await FindOrderIdAsync(mongo, customerId);

        (await CountSagaAsync(mongo, orderId)).Should().Be(1, "the start must create exactly one saga document");

        // Redeliver the SAME envelope through the durable inbox. A redelivery's first action in the
        // durable receiver is StoreIncomingAsync, keyed on the envelope id; the already-present
        // handled marker makes it throw DuplicateIncomingEnvelopeException and the receiver discards
        // it — the saga Start is never re-run. Asserting that synchronously here proves the dedup
        // deterministically, with none of the async-receiver timing that would let a redelivery race
        // the marker window. (Under Separated mode the event fans out to the saga's durable queue and
        // the projector's queue; the saga's copy went through the durable inbox, so re-storing it is
        // rejected.)
        var startEnvelopes = tracked.Executed.Envelopes()
            .Where(e => e.Message is OrderPlacedApplicationEvent)
            .ToList();
        startEnvelopes.Should().NotBeEmpty();

        var store = host.Services.GetRequiredService<IMessageStore>();
        var rejected = 0;
        foreach (var envelope in startEnvelopes)
        {
            try
            {
                await store.Inbox.StoreIncomingAsync(envelope);
            }
            catch (DuplicateIncomingEnvelopeException)
            {
                rejected++;
            }
        }

        rejected.Should().BeGreaterThan(0,
            "a redelivered start envelope must be rejected by the durable inbox so the saga cannot start twice");
        (await CountSagaAsync(mongo, orderId)).Should().Be(1, "a redelivered envelope must not insert a second saga");
        (await LoadSagaAsync(mongo, orderId))!.OrderPlaced.Should().BeTrue();
    }

    // ── F6: across host restart ─────────────────────────────────────────────────────

    [Fact]
    public async Task SagaState_SurvivesHostRestart()
    {
        var db = OrdersFixture.CreateDatabaseName();

        // Host #1: start the saga, then tear the host down completely.
        Guid orderId;
        var host1 = await fixture.CreateHostAsync(db);
        try
        {
            var mongo1 = host1.Services.GetRequiredService<IMongoDatabase>();
            orderId = await PlaceOrderAsync(host1, mongo1, Guid.NewGuid());
            (await LoadSagaAsync(mongo1, orderId)).Should().NotBeNull();
        }
        finally
        {
            await host1.StopAsync();
            host1.Dispose();
        }

        // Host #2: a brand-new host on the SAME database continues the saga. State must survive.
        using var host2 = await fixture.CreateHostAsync(db);
        var mongo2 = host2.Services.GetRequiredService<IMongoDatabase>();

        await host2.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new ShipOrderCommand(orderId));

        var saga = await LoadSagaAsync(mongo2, orderId);
        saga.Should().NotBeNull("the saga document persisted in MongoDB must survive a host restart");
        saga!.OrderPlaced.Should().BeTrue("state written by the first host must still be present");
        saga.OrderShipped.Should().BeTrue("the second host must be able to advance the persisted saga");
        saga.ShippedAt.Should().NotBeNull();
    }

    // ── F7: inbox/outbox interaction (saga + projector stay consistent) ─────────────

    [Fact]
    public async Task SagaAndProjector_StayConsistent_ThroughInboxOutbox()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var orderId = await PlaceOrderAsync(host, mongo, Guid.NewGuid());

        // A single OrderShippedApplicationEvent flows through the durable inbox/outbox and is handled
        // independently by BOTH the saga (continue) and the OrderSummaryProjector (read model). After
        // the pipeline settles, the two must agree — proving they coexist consistently rather than one
        // silently shadowing the other.
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new ShipOrderCommand(orderId));

        var saga = await LoadSagaAsync(mongo, orderId);
        saga.Should().NotBeNull();
        saga!.OrderShipped.Should().BeTrue("the saga must have processed the shipped event");

        var summary = await FindSummaryAsync(mongo, orderId);
        summary.Should().NotBeNull("the projector must have processed the same shipped event");
        summary!.Status.Should().Be("Shipped");
        summary.ShippedAt.Should().NotBeNull();
        saga.ShippedAt.Should().NotBeNull();

        // Consistency: both reflect the same shipment. Compared with a tolerance because the saga
        // stores ShippedAt as a BSON Date (millisecond precision, per the library's DateTimeOffset
        // convention) while the read model keeps full .NET precision.
        summary.ShippedAt!.Value.Should().BeCloseTo(saga.ShippedAt!.Value, TimeSpan.FromSeconds(1));
    }

    // ── F8: saga cascade events are projected (T4.2) ─────────────────────────────────

    /// <summary>
    /// Proves the full saga → outbox → consumer path for the saga's cascaded events:
    /// <c>OrderFulfillmentSaga</c> returns <c>FulfillmentShippedEvent</c>/<c>FulfillmentCompletedEvent</c>,
    /// which <see cref="Infrastructure.Projectors.FulfillmentStatusProjector"/> consumes via a durable
    /// local queue to maintain the <c>fulfillment_delivery_statuses</c> read model — data
    /// <see cref="OrderSummary"/> does not track.
    /// </summary>
    [Fact]
    public async Task ShipAndConfirmOrder_ProjectsFulfillmentDeliveryStatus()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var orderId = await PlaceOrderAsync(host, mongo, Guid.NewGuid());

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new ShipOrderCommand(orderId));

        var afterShip = await FindDeliveryStatusAsync(mongo, orderId);
        afterShip.Should().NotBeNull("FulfillmentShippedEvent must be projected by FulfillmentStatusProjector");
        afterShip!.OrderId.Should().Be(orderId);
        afterShip.Status.Should().Be("Shipped");
        afterShip.DeliveredAt.Should().BeNull();

        var deliveredAt = DateTimeOffset.UtcNow;
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new ConfirmDeliveryCommand(orderId, deliveredAt));

        var afterDelivery = await FindDeliveryStatusAsync(mongo, orderId);
        afterDelivery.Should().NotBeNull();
        afterDelivery!.Status.Should().Be("Delivered", "FulfillmentCompletedEvent must update the delivery status");
        afterDelivery.DeliveredAt.Should().NotBeNull();
        afterDelivery.ShippedAt.Should().Be(afterShip.ShippedAt, "the shipped timestamp must not be disturbed by delivery");

        // The saga document is deleted on completion (F3), but the projection survives it —
        // orthogonal read model, not saga state.
        (await LoadSagaAsync(mongo, orderId)).Should().BeNull("MarkCompleted() must still delete the saga document");
    }
}
