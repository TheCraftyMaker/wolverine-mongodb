# OrderDemo — Wolverine.MongoDB Demo

A runnable demo that showcases `Wolverine.MongoDB` — the MongoDB transactional inbox/outbox provider for [Wolverine](https://wolverinefx.net).

## Architecture

The demo follows a CQRS + event-driven architecture with a clear boundary between write-side and read-side:

```
  HTTP request
       │
       ▼
  PlaceOrderHandler  (write side)
       │   1. Begin MongoDB transaction (auto-applied by Wolverine)
       │   2. Persist Order aggregate to MongoDB
       │   3. Drain domain events → map to application event
       │   4. Return OrderPlacedApplicationEvent
       │      └── Wolverine writes it to the outbox (same transaction)
       │   5. Commit transaction
       │
       ▼
  MongoDB outbox relay  ──►  RabbitMQ  ──►  OrderSummaryProjector
                                                  │   (read side)
                                                  │   Updates OrderSummary
                                                  │   read-model collection
                                                  └── Durable inbox: auto-recovered on crash
```

### Domain events vs application events

| Event | Where it lives | How it travels |
|---|---|---|
| `OrderPlaced` (domain event) | Aggregate, in-memory only | Drained synchronously in the handler |
| `OrderPlacedApplicationEvent` | Wolverine outbox → MongoDB | Delivered via RabbitMQ |

Domain events never leave the handler method. Application events are persisted to the outbox *inside the same MongoDB transaction* as the domain write — guaranteeing at-least-once delivery to the broker.

### Projects

| Project | Role |
|---|---|
| `OrderDemo.Domain` | Aggregate root, domain events, value objects — no infrastructure deps |
| `OrderDemo.Contracts` | Commands, queries, application events — shared contracts |
| `OrderDemo.Application` | Wolverine command/query handlers |
| `OrderDemo.Infrastructure` | MongoDB repositories, read-model projector, infrastructure bootstrap |
| `OrderDemo.Api` | ASP.NET Core entry point — Wolverine + RabbitMQ wiring, minimal API endpoints |

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop) (for MongoDB replica set + RabbitMQ)

> **MongoDB must run as a replica set.** The transactional outbox uses multi-document transactions, which require a replica set. The provided `docker-compose.yml` configures this automatically.

## Quick start

### 1. Start infrastructure

```bash
cd demo
docker compose up -d
```

This starts:
- A single-node MongoDB 7 replica set (`rs0`) on port `27017`
- RabbitMQ 3 with the management UI on port `15672` (`guest`/`guest`)

Wait ~10 seconds for MongoDB to initialise the replica set.

### 2. Run the API

```bash
cd demo/src/OrderDemo.Api
dotnet run
```

The API starts on `http://localhost:5000`. Open `http://localhost:5000/scalar/v1` for the interactive API docs.

### 3. Try it out

**Place an order:**
```bash
curl -X POST http://localhost:5000/orders \
  -H "Content-Type: application/json" \
  -d '{"customerId":"cust-1","items":[{"productId":"prod-1","quantity":2,"unitPrice":9.99}]}'
```

**Ship the order** (use the `orderId` from the response):
```bash
curl -X POST http://localhost:5000/orders/{orderId}/ship
```

**Read the projected read model:**
```bash
curl http://localhost:5000/orders
```

### 4. Observe the outbox

Connect to MongoDB and inspect the outbox collections:

```javascript
use orderDemo
db.wolverine_outgoing_envelopes.find()   // messages waiting to be relayed
db.wolverine_incoming_envelopes.find()   // inbox messages (projector queue)
db.wolverine_dead_letters.find()         // any failures
```

## Running multiple instances (multinode)

The demo runs single-node (`Solo`) by default. To see Wolverine's multi-node
coordination (leader election, agent balancing, cross-node recovery) against
MongoDB, run two instances in `Balanced` mode:

```bash
docker compose up -d   # shared MongoDB replica set + RabbitMQ

# Terminal 1
ASPNETCORE_URLS=http://localhost:5000 Wolverine__DurabilityMode=Balanced dotnet run --project src/OrderDemo.Api

# Terminal 2
ASPNETCORE_URLS=http://localhost:5001 Wolverine__DurabilityMode=Balanced dotnet run --project src/OrderDemo.Api
```

On Windows PowerShell the env-var prefix syntax differs — set the variables in each
terminal first:

```powershell
# Terminal 1
$env:ASPNETCORE_URLS='http://localhost:5000'; $env:Wolverine__DurabilityMode='Balanced'; dotnet run --project src/OrderDemo.Api

# Terminal 2
$env:ASPNETCORE_URLS='http://localhost:5001'; $env:Wolverine__DurabilityMode='Balanced'; dotnet run --project src/OrderDemo.Api
```

Both instances register in `wolverine_nodes`; one acquires the leader lock in
`wolverine_locks`. Place orders against either port — events flow through the
shared outbox and exactly one instance projects each event. Kill one instance and
watch the survivor take over its work (node ejection + ownership release).

Requirements: synchronized clocks across instances (the leader lease tolerates
skew well under its duration) and reachable TCP control endpoints between nodes.

## How the transaction works

Wolverine's `AutoApplyTransactions()` policy detects that `PlaceOrderHandler` depends on `IOrderRepository → OrderRepository(IMongoDatabase)` and automatically wraps the handler in a `TransactionalFrame`:

```csharp
// generated pseudocode
await using var session = client.StartSession();
session.StartTransaction();
try
{
    var result = await PlaceOrderHandler.Handle(cmd, session, repository, ct);
    // outbox envelope written to wolverine_outgoing_envelopes using same session
    await session.CommitTransactionAsync();
}
catch { await session.AbortTransactionAsync(); throw; }
```

The `IClientSessionHandle` is available as a parameter in handler methods — repositories thread it through all write operations so they participate in the transaction.

## OrderFulfillmentSaga

The demo includes an `OrderFulfillmentSaga` that tracks an order through its lifecycle.
It starts automatically when `OrderPlacedApplicationEvent` is published by `PlaceOrderHandler`,
continues when the order ships, and completes when delivery is confirmed.

```
PlaceOrderHandler → OrderPlacedApplicationEvent ──► OrderFulfillmentSaga.Start(...)
                                                 └─► OrderSummaryProjector (read model)

ShipOrderHandler  → OrderShippedApplicationEvent ──► OrderFulfillmentSaga.Handle(...)
                                                  └─► OrderSummaryProjector (read model)

POST /confirm-delivery → ConfirmDeliveryCommand ──► OrderFulfillmentSaga.Handle(...)
                                                     └─► MarkCompleted() → saga doc deleted
```

The saga document is stored in `wolverine_saga_orderfulfillmentsaga`. Each step is
automatically wrapped in the Wolverine-managed MongoDB transaction so saga state and
outbox entries commit atomically. No manual session handling is needed in the saga
methods.

### Trying the saga flows

**Place an order** (starts the saga — same as before):
```bash
curl -X POST http://localhost:5000/orders \
  -H "Content-Type: application/json" \
  -d '{"customerId":"cust-1","items":[{"productId":"prod-1","quantity":2,"unitPrice":9.99}]}'
```

**Ship the order** (continues the saga):
```bash
curl -X POST http://localhost:5000/orders/{orderId}/ship
```

**Confirm delivery** (completes the saga — document is deleted):
```bash
curl -X POST http://localhost:5000/orders/{orderId}/confirm-delivery
```

**Observe the saga collection:**
```javascript
use orderDemo
db.wolverine_saga_orderfulfillmentsaga.find()   // shows in-progress sagas
// (empty after confirm-delivery — MarkCompleted() deletes the document)
```

### Saga + projector coexistence

`OrderFulfillmentSaga` and `OrderSummaryProjector` both handle `OrderPlacedApplicationEvent`
and `OrderShippedApplicationEvent`. `Program.cs` sets:

```csharp
opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
```

This tells Wolverine to run each handler independently. Without it, `SagaChain` would
silently drop the projector and the read model would stop updating.

## Key patterns demonstrated

| Pattern | Where to look |
|---|---|
| Domain event → application event mapping | `PlaceOrderHandler.cs` |
| MongoDB transaction via `IClientSessionHandle` | `OrderRepository.cs` + `PlaceOrderHandler.cs` |
| Outbox auto-apply policy | `Program.cs` — `opts.Policies.AutoApplyTransactions()` |
| Durable inbox for projector | `Program.cs` — `.UseDurableInbox()` |
| Read model upsert (idempotent) | `OrderSummaryRepository.cs` |
| Handler in non-entry assembly | `Program.cs` — `opts.Discovery.IncludeAssembly(...)` |
| Saga start / continue / complete | `OrderFulfillmentSaga.cs` |
| Saga + projector co-handlers | `Program.cs` — `MultipleHandlerBehavior.Separated` |

## Session-bound writes

The demo's repository pattern (explicit `IClientSessionHandle` threading through
repository methods) is the full-control approach — repositories own the session
lifetime contract and are testable in isolation.

For handlers that write directly to MongoDB collections without a repository
layer, `MongoDbUnitOfWork` is a lighter-weight alternative — accept it as a
handler parameter and the session is threaded automatically:

```csharp
// No repository needed — the unit of work threads the session for you.
public static async Task<OrderPlaced> Handle(PlaceOrder cmd, MongoDbUnitOfWork mongo, CancellationToken ct)
{
    await mongo.Collection<Order>("orders").InsertOneAsync(new Order(cmd.OrderId), ct);
    return new OrderPlaced(cmd.OrderId);
}
```

See the library [README](../README.md#domain-write-atomicity) for the full
`MongoDbUnitOfWork` API.

## Configuration

| Key | Default (local dev) | Docker value |
|---|---|---|
| `MongoDB:ConnectionString` | `mongodb://localhost:27017/?replicaSet=rs0&directConnection=true` | `mongodb://mongo1:27017/?replicaSet=rs0&directConnection=true` |
| `MongoDB:DatabaseName` | `orderDemo` | `orderDemo` |
| `RabbitMQ:HostName` | `localhost` | `rabbitmq` |
| `RabbitMQ:UserName` | `guest` | `guest` |
| `RabbitMQ:Password` | `guest` | `guest` |

Settings live in `appsettings.json` (Docker defaults) and `appsettings.Development.json` (local dev overrides).

## Stopping infrastructure

```bash
docker compose down -v   # -v removes the MongoDB data volume
```
