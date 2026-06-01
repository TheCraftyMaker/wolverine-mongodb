using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Queries;
using OrderDemo.Infrastructure.Persistence;
using Wolverine;

namespace OrderDemo.Api.Endpoints;

/// <summary>
/// Minimal API endpoint registration for the Orders module.
/// Dispatches commands and queries to their Wolverine handlers via <see cref="IMessageBus"/>.
/// </summary>
public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/orders").WithTags("Orders");

        // POST /orders — place a new order
        orders.MapPost("/", async (PlaceOrderCommand cmd, IMessageBus bus) =>
        {
            await bus.InvokeAsync(cmd);
            return Results.Accepted();
        })
        .WithSummary("Place a new order")
        .WithDescription(
            "Creates a new order and publishes OrderPlacedApplicationEvent via the transactional outbox → RabbitMQ. " +
            "The read model is updated asynchronously once the projector consumes the event.");

        // POST /orders/{id}/ship — ship an existing order
        orders.MapPost("/{id:guid}/ship", async (Guid id, IMessageBus bus) =>
        {
            await bus.InvokeAsync(new ShipOrderCommand(id));
            return Results.Accepted();
        })
        .WithSummary("Ship a pending order");

        // POST /orders/{id}/cancel — cancel a pending order
        orders.MapPost("/{id:guid}/cancel", async (Guid id, CancelBody? body, IMessageBus bus) =>
        {
            await bus.InvokeAsync(new CancelOrderCommand(id, body?.Reason));
            return Results.Accepted();
        })
        .WithSummary("Cancel a pending order");

        // POST /orders/{id}/discount — apply a discount to a pending order
        orders.MapPost("/{id:guid}/discount", async (Guid id, DiscountBody body, IMessageBus bus) =>
        {
            await bus.InvokeAsync(new ApplyDiscountCommand(id, body.DiscountPercent));
            return Results.Accepted();
        })
        .WithSummary("Apply a percentage discount (0–50%) to a pending order");

        // GET /orders — list all orders (read model)
        orders.MapGet("/", async (IMessageBus bus) =>
        {
            var summaries = await bus.InvokeAsync<IReadOnlyList<OrderSummary>>(new GetOrdersQuery());
            return Results.Ok(summaries);
        })
        .WithSummary("List all orders (read model)")
        .WithDescription("Returns the denormalized OrderSummary read model updated by the RabbitMQ projector.");

        return app;
    }

    public sealed record CancelBody(string? Reason);
    public sealed record DiscountBody(decimal DiscountPercent);
}
