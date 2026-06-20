using OrderDemo.Api.Endpoints;
using OrderDemo.Application.Orders;
using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Events;
using OrderDemo.Infrastructure;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.MongoDB;
using Wolverine.RabbitMQ;
using Wolverine.Transports.Tcp;

var builder = WebApplication.CreateBuilder(args);

// ─── Infrastructure: MongoDB client + domain repositories ───────────────────
var mongoConnectionString = builder.Configuration["MongoDB:ConnectionString"]!;
var databaseName          = builder.Configuration["MongoDB:DatabaseName"]!;

builder.Services.AddOrderDemoInfrastructure(mongoConnectionString);

// ─── Wolverine ───────────────────────────────────────────────────────────────
builder.Host.UseWolverine(opts =>
{
    // OrderPlacedApplicationEvent and OrderShippedApplicationEvent are handled by BOTH the
    // OrderFulfillmentSaga (start/continue) and the OrderSummaryProjector (read model). Wolverine's
    // default behavior collapses all handlers for a message type into one chain, and a saga chain
    // clears co-registered non-saga handlers — which would silently drop the projector. Separated
    // mode runs each handler independently (its own chain/queue), so the saga and the projector
    // both fire for those events. Without this, adding the saga breaks the read-model projection.
    opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

    // Scan handler assemblies beyond the entry (API) assembly
    opts.Discovery.IncludeAssembly(typeof(PlaceOrderHandler).Assembly);       // OrderDemo.Application
    opts.Discovery.IncludeAssembly(typeof(InfrastructureBootstrap).Assembly); // OrderDemo.Infrastructure (projectors)

    // ── MongoDB transactional outbox ─────────────────────────────────────────
    // Requires MongoDB to be running as a replica set (see docker-compose.yml).
    // Registers IMongoDatabase and sets up the outbox/inbox collections.
    // AutoApplyTransactions() below automatically wraps any handler whose
    // dependency tree includes IMongoDatabase in a MongoDB session + transaction.
    opts.UseMongoDbPersistence(databaseName);

    // Single-node durability by default. Set Wolverine:DurabilityMode=Balanced (plus a
    // control port) to run multiple instances against the same MongoDB + RabbitMQ —
    // see README "Running multiple instances".
    var durabilityMode = builder.Configuration["Wolverine:DurabilityMode"] ?? "Solo";
    opts.Durability.Mode = Enum.Parse<DurabilityMode>(durabilityMode);

    if (opts.Durability.Mode == DurabilityMode.Balanced)
    {
        // MongoDB has no native control transport; nodes coordinate over TCP.
        opts.UseTcpForControlEndpoint();
    }

    // ── RabbitMQ transport ───────────────────────────────────────────────────
    var rabbitHost = builder.Configuration["RabbitMQ:HostName"]!;
    var rabbitUser = builder.Configuration["RabbitMQ:UserName"]!;
    var rabbitPass = builder.Configuration["RabbitMQ:Password"]!;

    opts.UseRabbitMq(rabbit =>
        {
            rabbit.HostName = rabbitHost;
            rabbit.UserName = rabbitUser;
            rabbit.Password = rabbitPass;
        })
        .AutoProvision();   // creates exchanges + queues automatically on startup

    // ── Message routing ──────────────────────────────────────────────────────
    //
    // Application events are returned/cascaded from command handlers.
    // Wolverine routes them to the outbox, which delivers them to RabbitMQ.
    const string appEventsExchange = "order-app-events";

    opts.PublishMessage<OrderPlacedApplicationEvent>().ToRabbitExchange(appEventsExchange);
    opts.PublishMessage<OrderShippedApplicationEvent>().ToRabbitExchange(appEventsExchange);
    opts.PublishMessage<OrderCancelledApplicationEvent>().ToRabbitExchange(appEventsExchange);
    opts.PublishMessage<DiscountAppliedApplicationEvent>().ToRabbitExchange(appEventsExchange);

    // ── Saga cascade events ──────────────────────────────────────────────────
    //
    // FulfillmentShippedEvent and FulfillmentCompletedEvent are cascaded from
    // OrderFulfillmentSaga handler methods and travel through the Wolverine outbox,
    // committing atomically with the saga state update/delete.
    opts.PublishMessage<FulfillmentShippedEvent>().ToRabbitExchange(appEventsExchange);
    opts.PublishMessage<FulfillmentCompletedEvent>().ToRabbitExchange(appEventsExchange);

    // The read-side projector listens on this queue, bound to the exchange.
    // UseDurableInbox() persists each message before processing so that crashes
    // during projection are automatically recovered on restart.
    opts.ListenToRabbitQueue("order-projections",
            queue => queue.BindExchange(appEventsExchange))
        .UseDurableInbox();

    // ── Transaction policy ───────────────────────────────────────────────────
    //
    // Wraps any handler whose dependency tree includes IMongoDatabase in a MongoDB
    // session + transaction frame (see Wolverine.MongoDB / MongoDbPersistenceFrameProvider).
    // Covered handlers: PlaceOrderHandler, ShipOrderHandler, CancelOrderHandler,
    //                   ApplyDiscountHandler (all via OrderRepository → IMongoDatabase).
    opts.Policies.AutoApplyTransactions();
});

// ─── OpenAPI / Scalar ────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference(options =>
    options.WithTitle("OrderDemo — Wolverine.MongoDB + RabbitMQ"));

app.MapOrderEndpoints();

app.Run();

// Visible to potential integration test projects
public partial class Program;
