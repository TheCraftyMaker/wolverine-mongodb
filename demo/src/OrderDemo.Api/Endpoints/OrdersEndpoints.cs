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
        // Returns 202 Accepted + the new order id once the command is dispatched.
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
        .WithSummary("Ship an order");

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
}
