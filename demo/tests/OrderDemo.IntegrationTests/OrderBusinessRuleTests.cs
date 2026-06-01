using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using OrderDemo.Contracts.Commands;
using OrderDemo.Domain.Aggregates;
using Wolverine;

namespace OrderDemo.IntegrationTests;

/// <summary>
/// Tests that verify the domain business rules are enforced end-to-end
/// (i.e. the exception raised by the aggregate propagates back to the caller).
/// </summary>
[Collection("orders")]
public class OrderBusinessRuleTests(OrdersFixture fixture)
{
    // ── PlaceOrder guards ────────────────────────────────────────────────────

    [Fact]
    public async Task PlaceOrder_WithNoItems_ThrowsArgumentOutOfRange()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();

        var act = () => bus.InvokeAsync(new PlaceOrderCommand(Guid.NewGuid(), []));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*at least one item*");
    }

    [Fact]
    public async Task PlaceOrder_WithTooManyItems_ThrowsArgumentOutOfRange()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var items = new List<OrderItemDto>();
        foreach (var i in Enumerable.Range(1, 11))
        {
            var productId = Guid.NewGuid();
            var name = $"Item{i}";
            await OrdersFixture.SeedProductAsync(mongo, productId, name);
            items.Add(new OrderItemDto(productId, name, 1, 1m));
        }

        var act = () => bus.InvokeAsync(new PlaceOrderCommand(Guid.NewGuid(), items));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*more than 10 items*");
    }

    // ── ShipOrder guards ─────────────────────────────────────────────────────

    [Fact]
    public async Task ShipOrder_WhenAlreadyShipped_ThrowsInvalidOperation()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Widget");
        await bus.InvokeAsync(new PlaceOrderCommand(customerId, [new(productId, "Widget", 1, 10m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        await bus.InvokeAsync(new ShipOrderCommand(order.Id));

        // Shipping an already-shipped order must fail
        var act = () => bus.InvokeAsync(new ShipOrderCommand(order.Id));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot ship*'Shipped'*");
    }

    [Fact]
    public async Task ShipOrder_OrderNotFound_ThrowsInvalidOperation()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();

        var act = () => bus.InvokeAsync(new ShipOrderCommand(Guid.NewGuid()));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    // ── CancelOrder guards ───────────────────────────────────────────────────

    [Fact]
    public async Task CancelOrder_WhenAlreadyShipped_ThrowsInvalidOperation()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Widget");
        await bus.InvokeAsync(new PlaceOrderCommand(customerId, [new(productId, "Widget", 1, 10m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        await bus.InvokeAsync(new ShipOrderCommand(order.Id));

        var act = () => bus.InvokeAsync(new CancelOrderCommand(order.Id));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot cancel*'Shipped'*");
    }

    [Fact]
    public async Task CancelOrder_WhenAlreadyCancelled_ThrowsInvalidOperation()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Widget");
        await bus.InvokeAsync(new PlaceOrderCommand(customerId, [new(productId, "Widget", 1, 10m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        await bus.InvokeAsync(new CancelOrderCommand(order.Id, "first cancellation"));

        var act = () => bus.InvokeAsync(new CancelOrderCommand(order.Id, "second cancellation"));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot cancel*'Cancelled'*");
    }

    // ── ApplyDiscount guards ─────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDiscount_AboveMaxPercent_ThrowsArgumentOutOfRange()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Widget");
        await bus.InvokeAsync(new PlaceOrderCommand(customerId, [new(productId, "Widget", 1, 10m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        var act = () => bus.InvokeAsync(new ApplyDiscountCommand(order.Id, 51m));
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*at most 50 percent*");
    }

    [Fact]
    public async Task ApplyDiscount_ZeroPercent_ThrowsArgumentOutOfRange()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Widget");
        await bus.InvokeAsync(new PlaceOrderCommand(customerId, [new(productId, "Widget", 1, 10m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        var act = () => bus.InvokeAsync(new ApplyDiscountCommand(order.Id, 0m));
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*greater than 0*");
    }

    [Fact]
    public async Task ApplyDiscount_ToShippedOrder_ThrowsInvalidOperation()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var customerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await OrdersFixture.SeedProductAsync(mongo, productId, "Widget");
        await bus.InvokeAsync(new PlaceOrderCommand(customerId, [new(productId, "Widget", 1, 10m)]));

        var orders = mongo.GetCollection<Order>("orders");
        var order = await orders.Find(Builders<Order>.Filter.Eq(o => o.CustomerId, customerId))
            .FirstOrDefaultAsync();

        await bus.InvokeAsync(new ShipOrderCommand(order.Id));

        var act = () => bus.InvokeAsync(new ApplyDiscountCommand(order.Id, 10m));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot apply a discount*'Shipped'*");
    }
}
