# OrderDemo

## Overview

CQRS order-management API demonstrating `Wolverine.MongoDB` with RabbitMQ. Write side persists orders to MongoDB; application events flow through the transactional outbox to RabbitMQ; read side projects order summaries via a durable inbox consumer.

**Framework:** ASP.NET Core (.NET 9)  
**Message bus:** Wolverine 6.x  
**Persistence:** MongoDB (replica set required)  
**Transport:** RabbitMQ  
**Outbox:** `Wolverine.MongoDB` (from nuget.org)

---

## Project Layout

```
demo/
  src/
    OrderDemo.Api/              ← Entry point: Wolverine + Mongo + Rabbit wiring, endpoints
    OrderDemo.Application/      ← Command handlers (PlaceOrder, ShipOrder, CancelOrder, ApplyDiscount)
    OrderDemo.Contracts/        ← Commands + application events (shared message types)
    OrderDemo.Domain/           ← Aggregate root (Order), domain events, value objects
    OrderDemo.Infrastructure/   ← Repositories, read-model projector, DI bootstrap
  tests/
    OrderDemo.IntegrationTests/ ← Testcontainers-based end-to-end tests
  docker-compose.yml            ← MongoDB replica set + RabbitMQ
  Directory.Packages.props      ← Central package versions (independent from root)
  nuget.config                  ← Points to nuget.org
```

---

## Message Flow

```
HTTP POST /orders
  → PlaceOrderHandler
      │ (MongoDB transaction auto-applied by Wolverine)
      ├─ Persist Order aggregate (via IClientSessionHandle)
      ├─ Reserve inventory (same transaction)
      └─ Return OrderPlacedApplicationEvent
           └─ Written to wolverine_outgoing_envelopes (same transaction)
           └─ Outbox relay → RabbitMQ exchange "order-app-events"
                                 → Queue "order-projections"
                                      → OrderSummaryProjector (durable inbox)
                                           → Upsert OrderSummary read model
```

---

## Key Wolverine Configuration (Program.cs)

- `opts.UseMongoDbPersistence(databaseName)` — registers MongoDB outbox/inbox
- `opts.Durability.Mode = DurabilityMode.Solo` — single-node (no cluster coordination)
- `opts.Policies.AutoApplyTransactions()` — auto-wraps handlers using `IMongoDatabase` in a transaction
- `.UseDurableInbox()` on the projection queue — inbox persistence for at-least-once delivery
- `opts.PublishMessage<T>().ToRabbitExchange(...)` — outbox-backed publish routing

## Transaction Atomicity Pattern

Handlers accept `IClientSessionHandle` (injected by Wolverine's generated frame). All MongoDB writes must use this session to be atomic with the outbox:

```csharp
public static async Task<AppEvent> Handle(Command cmd, IClientSessionHandle session, IOrderRepository repo, ...)
{
    // session-bound write → atomic with outbox
    await repo.AddAsync(order, session, ct);
    return new AppEvent(...); // cascaded to outbox on same transaction
}
```

Repositories accept `IClientSessionHandle` on all mutating methods. Read methods don't need it.

---

## Running

```bash
docker compose up -d                    # MongoDB rs0 + RabbitMQ
dotnet run --project src/OrderDemo.Api  # http://localhost:5000
```

API docs: `http://localhost:5000/scalar/v1`

## Testing

```bash
dotnet test  # Testcontainers auto-starts Mongo + Rabbit, no manual Docker needed
```

Test classes:
- `OrderCommandHandlerTests` — handler behavior
- `OrderBusinessRuleTests` — domain rules
- `OrderProjectorTests` — read-model projection
- `OutboxAtomicityTests` — proves transactional guarantee

---

## Configuration (appsettings)

| Key | Value |
|-----|-------|
| `MongoDB:ConnectionString` | `mongodb://localhost:27017/?replicaSet=rs0&directConnection=true` |
| `MongoDB:DatabaseName` | `orderDemo` |
| `RabbitMQ:HostName` | `localhost` |
| `RabbitMQ:UserName` | `guest` |
| `RabbitMQ:Password` | `guest` |

---

## Dependencies

References `Wolverine.MongoDB` from nuget.org (version pinned in `Directory.Packages.props`). This is a separate solution from the library — no project reference to `src/Wolverine.MongoDB`.
