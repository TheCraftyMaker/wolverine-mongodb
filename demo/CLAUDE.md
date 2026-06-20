# OrderDemo

## Overview

CQRS order-management API demonstrating `Wolverine.MongoDB` with RabbitMQ. Write side persists orders to MongoDB; application events flow through the transactional outbox to RabbitMQ; read side projects order summaries via a durable inbox consumer.

**Framework:** ASP.NET Core (.NET 9)  
**Message bus:** Wolverine 6.x  
**Persistence:** MongoDB (replica set required)  
**Transport:** RabbitMQ  
**Outbox:** `Wolverine.MongoDB` (consumed from the CI-packed nupkg in CI; from nuget.org for local dev)

---

## Project Layout

```
demo/
  src/
    OrderDemo.Api/              ← Entry point: Wolverine + Mongo + Rabbit wiring, endpoints
    OrderDemo.Application/      ← Command handlers (PlaceOrder, ShipOrder, CancelOrder, ApplyDiscount)
                                   Sagas/OrderFulfillmentSaga.cs ← saga: Guid id, start/continue/complete
    OrderDemo.Contracts/        ← Commands + application events + saga trigger/continue/complete messages
    OrderDemo.Domain/           ← Aggregate root (Order), domain events, value objects
    OrderDemo.Infrastructure/   ← Repositories, read-model projector, DI bootstrap
  tests/
    OrderDemo.IntegrationTests/ ← Testcontainers-based end-to-end tests
                                   SagaFlowTests.cs ← 7 saga integration flows
  docker-compose.yml            ← MongoDB replica set + RabbitMQ
  Directory.Packages.props      ← Central package versions (independent from root)
  nuget.config                  ← Points to nuget.org (local-ci source added by CI)
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
- Durability mode is config-driven (`Wolverine:DurabilityMode`, default `Solo`); `Balanced` enables multi-instance coordination with a TCP control endpoint
- `opts.Policies.AutoApplyTransactions()` — auto-wraps handlers using `IMongoDatabase` in a transaction
- `.UseDurableInbox()` on the projection queue — inbox persistence for at-least-once delivery
- `opts.PublishMessage<T>().ToRabbitExchange(...)` — outbox-backed publish routing
- `opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated` — **required** once the
  `OrderFulfillmentSaga` shares `OrderPlacedApplicationEvent`/`OrderShippedApplicationEvent` with the
  `OrderSummaryProjector`. Wolverine builds one chain per message type and a saga chain clears
  co-registered non-saga handlers, so without `Separated` mode the saga would silently drop the
  projector and the read model would stop updating. Separated mode runs each handler independently.

## Transaction Atomicity Pattern

The demo uses the **repository pattern** with explicit `IClientSessionHandle` threading.
Handlers accept `IClientSessionHandle` (injected by Wolverine's generated frame) and pass it
to repository methods so all MongoDB writes are scoped to the same transaction:

```csharp
public static async Task<AppEvent> Handle(Command cmd, IClientSessionHandle session, IOrderRepository repo, ...)
{
    // session-bound write → atomic with outbox
    await repo.AddAsync(order, session, ct);
    return new AppEvent(...); // cascaded to outbox on same transaction
}
```

Repositories accept `IClientSessionHandle` on all mutating methods. Read methods don't need it.

**Alternative — `MongoDbUnitOfWork`:** for handlers that write directly to collections without
a repository layer, the library provides `MongoDbUnitOfWork` as a handler parameter. It
threads the session automatically so it cannot be forgotten. The demo does not use it (the
repository pattern is the fuller example), but it is documented in the library
[README](../README.md#domain-write-atomicity).

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
- `SagaFlowTests` — 7 saga integration flows (start, continue, complete, missing-state,
  duplicate-message idempotency, across-restart state survival, saga/projector coexistence)

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

References `Wolverine.MongoDB` from nuget.org for local development (version pinned in
`Directory.Packages.props`). In CI, the `demo` job downloads the nupkg packed from the
same commit (`0.0.0-ci`) and adds it as a local NuGet source, so the integration tests
always exercise the freshly built library rather than a previously published version. This
is a separate solution from the library — no project reference to `src/Wolverine.MongoDB`.
