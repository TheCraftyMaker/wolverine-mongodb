using FluentAssertions;
using MongoDB.Driver;
using OrderDemo.Contracts.Commands;
using OrderDemo.Domain.Aggregates;
using OrderDemo.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Tracking;

namespace OrderDemo.IntegrationTests;

/// <summary>
/// Tests that verify the transactional outbox guarantee:
/// a failed command must leave both the domain state and the projected read model
/// completely unchanged — as if the command was never issued.
///
/// Strategy: run a successful command first via TrackActivity (waits for the full
/// pipeline to complete), then run a failing command, then assert that:
///   1. The expected exception is thrown.
///   2. The Order aggregate is unmodified (status, total, Version all unchanged).
///   3. The read-model projection is unmodified (no event leaked through the outbox).
///
/// Checking the read model rather than querying wolverine_outgoing_envelopes directly
/// is more robust: the outbox relay can clear entries before we read them, but a
/// projection change would be permanent and detectable.
/// </summary>
[Collection("orders")]
public class OutboxAtomicityTests(OrdersFixture fixture)
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<Order> FindOrderByCustomerAsync(IMongoDatabase mongo, Guid customerId)
    {
        var orders = mongo.GetCollection<Order>("orders");
        return await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();
    }

    private async Task<OrderSummary?> FindSummaryAsync(IMongoDatabase mongo, Guid orderId)
    {
        var summaries = mongo.GetCollection<OrderSummary>("order_summaries");
        return await summaries.Find(Builders<OrderSummary>.Filter.Eq(s => s.OrderId, orderId))
            .FirstOrDefaultAsync();
    }

    // ── ship a cancelled order ────────────────────────────────────────────────

    /// <summary>
    /// Cancel an order successfully, then attempt to ship it.
    /// The ship command must be rejected and neither the aggregate status
    /// nor the projected summary must flip to "Shipped".
    /// </summary>
    [Fact]
    public async Task ShipCancelledOrder_ExceptionThrown_StatusRemainsCancel_NoShippedEventDispatched()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Widget");

        // Arrange: place then cancel — wait for the full pipeline so the summary is projected
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new PlaceOrderCommand(customerId, [new(productId, "Widget", 1, 50m)]));

        var order = await FindOrderByCustomerAsync(mongo, customerId);
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new CancelOrderCommand(order.Id, "Changed mind"));

        // Snapshot state after successful cancel
        var orderBeforeAttempt = await FindOrderByCustomerAsync(mongo, customerId);
        var summaryBeforeAttempt = await FindSummaryAsync(mongo, order.Id);

        orderBeforeAttempt.Status.Should().Be(OrderStatus.Cancelled);
        summaryBeforeAttempt!.Status.Should().Be("Cancelled");

        var versionBeforeAttempt = orderBeforeAttempt.Version;

        // Act: try to ship the cancelled order
        var act = async () => await bus.InvokeAsync(new ShipOrderCommand(order.Id));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot ship*");

        // Allow any (non-existent) outbox relay a moment to run
        await Task.Delay(500);

        // Assert: domain state unchanged
        var orderAfter = await FindOrderByCustomerAsync(mongo, customerId);
        orderAfter.Status.Should().Be(OrderStatus.Cancelled, "failed ship must not alter order status");
        orderAfter.Version.Should().Be(versionBeforeAttempt, "Version must not increment on a failed command");

        // Assert: read model unchanged — no OrderShippedApplicationEvent leaked to the projector
        var summaryAfter = await FindSummaryAsync(mongo, order.Id);
        summaryAfter!.Status.Should().Be("Cancelled", "projection must not flip to Shipped on a failed command");
        summaryAfter.ShippedAt.Should().BeNull("ShippedAt must not be set when ship was rejected");
    }

    // ── apply discount to a shipped order ─────────────────────────────────────

    /// <summary>
    /// Ship an order successfully, then attempt to apply a discount.
    /// The discount command must be rejected; order total and projected summary
    /// must remain exactly as they were after the ship.
    /// </summary>
    [Fact]
    public async Task ApplyDiscountShippedOrder_ExceptionThrown_TotalUnchanged_NoDiscountEventDispatched()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Laptop");

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new PlaceOrderCommand(customerId, [new(productId, "Laptop", 1, 200m)]));

        var order = await FindOrderByCustomerAsync(mongo, customerId);
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new ShipOrderCommand(order.Id));

        var orderBeforeAttempt = await FindOrderByCustomerAsync(mongo, customerId);
        var summaryBeforeAttempt = await FindSummaryAsync(mongo, order.Id);
        var versionBeforeAttempt = orderBeforeAttempt.Version;

        // Act: attempt to discount a shipped order
        var act = async () => await bus.InvokeAsync(new ApplyDiscountCommand(order.Id, 10m));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot apply a discount*");

        await Task.Delay(500);

        // Assert: domain state unchanged
        var orderAfter = await FindOrderByCustomerAsync(mongo, customerId);
        orderAfter.TotalAmount.Should().Be(orderBeforeAttempt.TotalAmount,
            "total must not change when discount is rejected");
        orderAfter.DiscountPercent.Should().Be(0m);
        orderAfter.Version.Should().Be(versionBeforeAttempt, "Version must not increment on a failed command");

        // Assert: projection unchanged — no DiscountAppliedApplicationEvent leaked
        var summaryAfter = await FindSummaryAsync(mongo, order.Id);
        summaryAfter!.TotalAmount.Should().Be(summaryBeforeAttempt!.TotalAmount,
            "projected total must not change when discount command was rejected");
        summaryAfter.DiscountPercent.Should().Be(0m);
    }

    // ── command against non-existent order ────────────────────────────────────

    /// <summary>
    /// Issuing a command for an order that does not exist must throw and produce
    /// no side-effects whatsoever — no documents written, no events dispatched.
    /// </summary>
    [Fact]
    public async Task ShipNonExistentOrder_ExceptionThrown_NoDocumentsCreated_NoEventDispatched()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var phantomOrderId = Guid.NewGuid();

        var act = async () => await bus.InvokeAsync(new ShipOrderCommand(phantomOrderId));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{phantomOrderId}*not found*");

        await Task.Delay(500);

        // Assert: no order document created (it never existed)
        var orders = mongo.GetCollection<Order>("orders");
        var count = await orders.CountDocumentsAsync(Builders<Order>.Filter.Eq(o => o.Id, phantomOrderId));
        count.Should().Be(0);

        // Assert: no summary created (projector must not have received OrderShippedApplicationEvent)
        var summary = await FindSummaryAsync(mongo, phantomOrderId);
        summary.Should().BeNull("no projection must run when the command was rejected before any write");
    }

    // ── domain invariant violation (invalid argument) ─────────────────────────

    /// <summary>
    /// An out-of-range discount (0%) is rejected by the aggregate before any write.
    /// Order total and projected summary must be unchanged.
    /// </summary>
    [Fact]
    public async Task ApplyZeroDiscount_ExceptionThrown_TotalUnchanged_NoEventDispatched()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Camera");

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new PlaceOrderCommand(customerId, [new(productId, "Camera", 1, 100m)]));

        var order = await FindOrderByCustomerAsync(mongo, customerId);
        var versionAfterPlace = order.Version;

        var act = async () => await bus.InvokeAsync(new ApplyDiscountCommand(order.Id, 0m));
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();

        await Task.Delay(500);

        var orderAfter = await FindOrderByCustomerAsync(mongo, customerId);
        orderAfter.TotalAmount.Should().Be(100m);
        orderAfter.DiscountPercent.Should().Be(0m);
        orderAfter.Version.Should().Be(versionAfterPlace,
            "Version must not increment: aggregate rejected the command before any mutation");

        // Wait for any phantom event dispatch and confirm summary was never updated with a discount
        var summary = await FindSummaryAsync(mongo, order.Id);
        summary!.TotalAmount.Should().Be(100m,
            "projected total must be unchanged: no DiscountAppliedApplicationEvent must have been dispatched");
        summary.DiscountPercent.Should().Be(0m);
    }

    // ── insufficient stock rolls back the entire transaction ───────────────────

    /// <summary>
    /// Placing an order with more items than available stock must fail and
    /// produce NO side effects: no Order document, no summary, no outbox entry,
    /// and inventory stock must remain unchanged.
    ///
    /// This proves that domain event side effects (inventory reservation) run
    /// within the same MongoDB transaction as the order write.
    /// </summary>
    [Fact]
    public async Task PlaceOrder_InsufficientStock_RollsBackEntireTransaction()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        // Seed a product with only 2 units available
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Scarce Widget", stock: 2);

        var customerId = Guid.NewGuid();

        // Try to order 5 units — reservation must fail
        var act = async () => await bus.InvokeAsync(
            new PlaceOrderCommand(customerId, [new(productId, "Scarce Widget", 5, 10m)]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Insufficient stock*");

        await Task.Delay(500);

        // Assert: no Order document created (transaction rolled back)
        var orders = mongo.GetCollection<Order>("orders");
        var orderCount = await orders.CountDocumentsAsync(
            Builders<Order>.Filter.Eq(o => o.CustomerId, customerId));
        orderCount.Should().Be(0, "order write must be rolled back when inventory reservation fails");

        // Assert: no summary projected (no OrderPlacedApplicationEvent in outbox)
        var summaries = mongo.GetCollection<OrderSummary>("order_summaries");
        var summaryCount = await summaries.CountDocumentsAsync(
            Builders<OrderSummary>.Filter.Eq(s => s.CustomerId, customerId));
        summaryCount.Should().Be(0, "no projection must run when the order was rolled back");

        // Assert: inventory unchanged — ReservedStock still 0
        var products = mongo.GetCollection<Product>("products");
        var product = await products.Find(Builders<Product>.Filter.Eq(p => p.Id, productId))
            .FirstOrDefaultAsync();
        product.Should().NotBeNull();
        product.ReservedStock.Should().Be(0, "reservation must be rolled back with the transaction");
        product.AvailableStock.Should().Be(2, "available stock must remain at the seeded value");
    }
}
