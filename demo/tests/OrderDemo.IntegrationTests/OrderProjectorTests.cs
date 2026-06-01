using FluentAssertions;
using MongoDB.Driver;
using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Events;
using OrderDemo.Domain.Aggregates;
using OrderDemo.Infrastructure.Persistence;
using Wolverine.Tracking;

namespace OrderDemo.IntegrationTests;

/// <summary>
/// Tests that verify the full event-driven projection pipeline:
/// command → handler → app event cascaded to local durable queue → projector → read model.
///
/// Uses Wolverine's <see cref="TrackingExtensions.TrackActivity"/> API to wait until all
/// cascaded messages (app events + projector execution) have completed before asserting
/// on the read model. This avoids any timing/polling in the tests.
/// </summary>
[Collection("orders")]
public class OrderProjectorTests(OrdersFixture fixture)
{
    [Fact]
    public async Task PlaceOrder_ProjectsOrderSummaryWithPendingStatus()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var cmd = new PlaceOrderCommand(customerId, [new(Guid.NewGuid(), "Widget", 3, 10m)]);

        // TrackActivity waits for all cascaded messages (incl. the projector) to complete
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30)).InvokeMessageAndWaitAsync(cmd);

        var summaries = mongo.GetCollection<OrderSummary>("order_summaries");
        var summary = await summaries
            .Find(Builders<OrderSummary>.Filter.Eq(s => s.CustomerId, customerId))
            .FirstOrDefaultAsync();

        summary.Should().NotBeNull();
        summary.Status.Should().Be("Pending");
        summary.TotalAmount.Should().BeApproximately(30m, 0.001m);
        summary.PlacedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ShipOrder_UpdatesProjectedSummaryStatusToShipped()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();

        // Place
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new PlaceOrderCommand(customerId, [new(Guid.NewGuid(), "Widget", 1, 20m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        // Ship
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new ShipOrderCommand(order.Id));

        var summaries = mongo.GetCollection<OrderSummary>("order_summaries");
        var summary = await summaries
            .Find(Builders<OrderSummary>.Filter.Eq(s => s.OrderId, order.Id))
            .FirstOrDefaultAsync();

        summary.Should().NotBeNull();
        summary.Status.Should().Be("Shipped");
        summary.ShippedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelOrder_UpdatesProjectedSummaryStatusToCancelled()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new PlaceOrderCommand(customerId, [new(Guid.NewGuid(), "Widget", 1, 15m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new CancelOrderCommand(order.Id, "No longer needed"));

        var summaries = mongo.GetCollection<OrderSummary>("order_summaries");
        var summary = await summaries
            .Find(Builders<OrderSummary>.Filter.Eq(s => s.OrderId, order.Id))
            .FirstOrDefaultAsync();

        summary.Should().NotBeNull();
        summary.Status.Should().Be("Cancelled");
        summary.CancelReason.Should().Be("No longer needed");
        summary.CancelledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ApplyDiscount_UpdatesProjectedSummaryTotal()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new PlaceOrderCommand(customerId, [new(Guid.NewGuid(), "Phone", 1, 500m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        // 20% off → £400
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new ApplyDiscountCommand(order.Id, 20m));

        var summaries = mongo.GetCollection<OrderSummary>("order_summaries");
        var summary = await summaries
            .Find(Builders<OrderSummary>.Filter.Eq(s => s.OrderId, order.Id))
            .FirstOrDefaultAsync();

        summary.Should().NotBeNull();
        summary.TotalAmount.Should().BeApproximately(400m, 0.001m);
        summary.DiscountPercent.Should().BeApproximately(20m, 0.001m);
    }

    /// <summary>
    /// Directly test the projector handler as a unit (no Wolverine bus needed)
    /// to verify idempotency: applying the same event twice must produce the same result.
    /// </summary>
    [Fact]
    public async Task Projector_OrderPlaced_IsIdempotent()
    {
        var db = OrdersFixture.CreateDatabaseName();
        var mongo = fixture.MongoClient.GetDatabase(db);
        var repo = new OrderSummaryRepository(mongo);

        var evt = new OrderPlacedApplicationEvent(Guid.NewGuid(), Guid.NewGuid(), 99.99m, DateTimeOffset.UtcNow);

        // Call twice — should produce exactly one document (upsert by OrderId)
        await Infrastructure.Projectors.OrderSummaryProjector.Handle(evt, repo, CancellationToken.None);
        await Infrastructure.Projectors.OrderSummaryProjector.Handle(evt, repo, CancellationToken.None);

        var summaries = mongo.GetCollection<OrderSummary>("order_summaries");
        var count = await summaries.CountDocumentsAsync(
            Builders<OrderSummary>.Filter.Eq(s => s.OrderId, evt.OrderId));

        count.Should().Be(1);
    }

    /// <summary>
    /// Regression test for adversarial review finding: DiscountApplied must be idempotent.
    /// Replaying the same DiscountAppliedApplicationEvent twice must not double the discount.
    /// </summary>
    [Fact]
    public async Task Projector_DiscountApplied_IsIdempotentOnReplay()
    {
        var db = OrdersFixture.CreateDatabaseName();
        var mongo = fixture.MongoClient.GetDatabase(db);
        var repo = new OrderSummaryRepository(mongo);

        var orderId = Guid.NewGuid();

        // Seed summary first
        await repo.InsertIfNotExistsAsync(
            new OrderSummary { OrderId = orderId, CustomerId = Guid.NewGuid(), TotalAmount = 100m, Status = "Pending", PlacedAt = DateTimeOffset.UtcNow },
            CancellationToken.None);

        // 20% off, cumulative 20%
        var evt = new DiscountAppliedApplicationEvent(orderId, 20m, 80m, 20m);

        // Replay the event twice (simulates message redelivery)
        await Infrastructure.Projectors.OrderSummaryProjector.Handle(evt, repo, CancellationToken.None);
        await Infrastructure.Projectors.OrderSummaryProjector.Handle(evt, repo, CancellationToken.None);

        var summary = await repo.FindByOrderIdAsync(orderId);
        summary!.DiscountPercent.Should().BeApproximately(20m, 0.001m, "replaying the event must not double the discount");
        summary.TotalAmount.Should().BeApproximately(80m, 0.001m);
    }

    /// <summary>
    /// Regression test for adversarial review finding: OrderPlaced redelivery must not
    /// overwrite a summary that was already advanced to Shipped.
    /// </summary>
    [Fact]
    public async Task Projector_OrderPlaced_RedeliveryDoesNotResetShippedSummary()
    {
        var db = OrdersFixture.CreateDatabaseName();
        var mongo = fixture.MongoClient.GetDatabase(db);
        var repo = new OrderSummaryRepository(mongo);

        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var shippedAt = DateTimeOffset.UtcNow;

        // Simulate: summary was already Shipped
        var shipped = new OrderSummary
        {
            OrderId = orderId,
            CustomerId = customerId,
            TotalAmount = 50m,
            Status = "Shipped",
            PlacedAt = shippedAt.AddMinutes(-5),
            ShippedAt = shippedAt
        };
        await repo.UpsertAsync(shipped);

        // A late/retried OrderPlaced event arrives
        var lateOrderPlaced = new OrderPlacedApplicationEvent(orderId, customerId, 50m, shippedAt.AddMinutes(-5));
        await Infrastructure.Projectors.OrderSummaryProjector.Handle(lateOrderPlaced, repo, CancellationToken.None);

        var summary = await repo.FindByOrderIdAsync(orderId);
        summary!.Status.Should().Be("Shipped", "late OrderPlaced must not reset a Shipped summary back to Pending");
        summary.ShippedAt.Should().NotBeNull();
    }
}
