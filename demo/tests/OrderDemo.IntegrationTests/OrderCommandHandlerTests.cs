using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OrderDemo.Contracts.Commands;
using OrderDemo.Domain.Aggregates;
using Wolverine;
using Wolverine.Tracking;

namespace OrderDemo.IntegrationTests;

/// <summary>
/// Integration tests for the write-side command handlers.
///
/// These tests invoke commands via <c>bus.InvokeAsync()</c> (NOT InvokeAsync&lt;T&gt;)
/// and assert state directly in MongoDB — no projection is involved.
///
/// Each test uses its own database name (via <see cref="OrdersFixture.CreateDatabaseName"/>)
/// to ensure full isolation.
/// </summary>
[Collection("orders")]
public class OrderCommandHandlerTests(OrdersFixture fixture)
{
    // ── PlaceOrder ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlaceOrder_PersistsOrderWithPendingStatus()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var cmd = new PlaceOrderCommand(customerId, [new(Guid.NewGuid(), "Widget", 2, 9.99m)]);

        await bus.InvokeAsync(cmd);

        var orders = mongo.GetCollection<Order>("orders");
        var stored = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        stored.Should().NotBeNull();
        stored.Status.Should().Be(OrderStatus.Pending);
        stored.TotalAmount.Should().BeApproximately(19.98m, 0.001m);
        stored.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task PlaceOrder_EnvelopeProcessedEndToEnd()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);

        var customerId = Guid.NewGuid();
        var cmd = new PlaceOrderCommand(customerId, [new(Guid.NewGuid(), "Gadget", 1, 50m)]);

        // TrackActivity waits until all cascaded messages (outbox relay + projector) complete.
        // This proves the full outbox pipeline ran without needing to poll MongoDB.
        await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(cmd);

        // By this point the handler ran, the outbox wrote the envelope,
        // the relay picked it up, and the projector consumed it — all without RabbitMQ.
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();
        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();
        order.Should().NotBeNull("order must be persisted by the time TrackActivity completes");
    }

    // ── ShipOrder ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShipOrder_UpdatesStatusToShipped()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        await bus.InvokeAsync(new PlaceOrderCommand(customerId, [new(Guid.NewGuid(), "Widget", 1, 10m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();
        order.Should().NotBeNull();

        await bus.InvokeAsync(new ShipOrderCommand(order.Id));

        var shipped = await orders.Find(Builders<Order>.Filter.Eq(o => o.Id, order.Id))
            .FirstOrDefaultAsync();
        shipped.Status.Should().Be(OrderStatus.Shipped);
        shipped.ShippedAt.Should().NotBeNull();
    }

    // ── CancelOrder ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelOrder_UpdatesStatusToCancelled()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        await bus.InvokeAsync(new PlaceOrderCommand(customerId, [new(Guid.NewGuid(), "Widget", 1, 10m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        await bus.InvokeAsync(new CancelOrderCommand(order.Id, "Customer changed their mind"));

        var cancelled = await orders.Find(Builders<Order>.Filter.Eq(o => o.Id, order.Id))
            .FirstOrDefaultAsync();
        cancelled.Status.Should().Be(OrderStatus.Cancelled);
        cancelled.CancelReason.Should().Be("Customer changed their mind");
        cancelled.CancelledAt.Should().NotBeNull();
    }

    // ── ApplyDiscount ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDiscount_ReducesTotalAmount()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        // Place order: 1 × £100 = £100 total
        await bus.InvokeAsync(new PlaceOrderCommand(customerId, [new(Guid.NewGuid(), "Laptop", 1, 100m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        // Apply 10% discount → £90
        await bus.InvokeAsync(new ApplyDiscountCommand(order.Id, 10m));

        var updated = await orders.Find(Builders<Order>.Filter.Eq(o => o.Id, order.Id))
            .FirstOrDefaultAsync();
        updated.TotalAmount.Should().BeApproximately(90m, 0.001m);
        updated.DiscountPercent.Should().BeApproximately(10m, 0.001m);
    }

    [Fact]
    public async Task ApplyDiscount_Stacks_TwoDiscountsApplied()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        await bus.InvokeAsync(new PlaceOrderCommand(customerId, [new(Guid.NewGuid(), "Camera", 1, 200m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        // 10% off → 180, then 5% off 180 → 171
        await bus.InvokeAsync(new ApplyDiscountCommand(order.Id, 10m));
        await bus.InvokeAsync(new ApplyDiscountCommand(order.Id, 5m));

        var updated = await orders.Find(Builders<Order>.Filter.Eq(o => o.Id, order.Id))
            .FirstOrDefaultAsync();
        updated.TotalAmount.Should().BeApproximately(171m, 0.01m);
        updated.DiscountPercent.Should().BeApproximately(15m, 0.001m);
    }
}
