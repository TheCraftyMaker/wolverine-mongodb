# OrderDemo — Contributor Guide

## What This Is

A reference application demonstrating `Wolverine.MongoDB` in a realistic CQRS + event-driven architecture. It uses Wolverine as the message bus with MongoDB for persistence and RabbitMQ as the transport.

This is **not** a library — it's a standalone runnable application that serves as both documentation and an integration test bed for the `Wolverine.MongoDB` NuGet package.

---

## Project Structure

```
demo/
  src/
    OrderDemo.Api/              ← ASP.NET Core entry point, Wolverine wiring, endpoints
    OrderDemo.Application/      ← Command/query handlers (Wolverine handlers)
    OrderDemo.Contracts/        ← Commands, application events (shared message types)
    OrderDemo.Domain/           ← Aggregate roots, domain events, value objects
    OrderDemo.Infrastructure/   ← MongoDB repositories, read-model projector, DI bootstrap
  tests/
    OrderDemo.IntegrationTests/ ← End-to-end tests via Testcontainers
  docker-compose.yml            ← MongoDB replica set + RabbitMQ
  OrderDemo.slnx                ← Solution file
```

### Key Files

| File | Purpose |
|------|---------|
| `src/OrderDemo.Api/Program.cs` | Wolverine + MongoDB + RabbitMQ configuration |
| `src/OrderDemo.Application/Orders/PlaceOrderHandler.cs` | Core handler showing transactional outbox |
| `src/OrderDemo.Infrastructure/Persistence/OrderRepository.cs` | Repository threading `IClientSessionHandle` for atomicity |
| `src/OrderDemo.Infrastructure/Projectors/OrderSummaryProjector.cs` | Event-driven read model (durable inbox consumer) |
| `tests/OrderDemo.IntegrationTests/OutboxAtomicityTests.cs` | Proves outbox transactional guarantee |

---

## Architecture

```
HTTP → PlaceOrderHandler (write side)
         │  1. Begin MongoDB transaction (auto-applied by Wolverine)
         │  2. Persist Order aggregate
         │  3. Drain domain events → map to application event
         │  4. Return application event (Wolverine writes to outbox, same tx)
         │  5. Commit transaction
         ▼
MongoDB outbox relay → RabbitMQ → OrderSummaryProjector (read side)
                                       └── Durable inbox: auto-recovered on crash
```

### Domain Events vs Application Events

- **Domain events** (`OrderPlaced`, `OrderShipped`) live on the aggregate, in-memory only
- **Application events** (`OrderPlacedApplicationEvent`) are persisted to the outbox and delivered via RabbitMQ
- Mapping happens in the handler — domain events never leave the process boundary

---

## Development Setup

### Prerequisites

- .NET 9 SDK
- Docker Desktop

### Running Locally

```bash
cd demo
docker compose up -d          # MongoDB replica set + RabbitMQ
cd src/OrderDemo.Api
dotnet run                    # http://localhost:5000
```

API docs at `http://localhost:5000/scalar/v1`.

### Running Tests

```bash
cd demo
dotnet test
```

Tests use Testcontainers — they start their own MongoDB replica set and RabbitMQ. No manual Docker setup needed (Docker Desktop must be running).

### Stopping Infrastructure

```bash
docker compose down -v
```

---

## Configuration

Settings in `appsettings.json` / `appsettings.Development.json`:

| Key | Default (local dev) |
|-----|---------------------|
| `MongoDB:ConnectionString` | `mongodb://localhost:27017/?replicaSet=rs0&directConnection=true` |
| `MongoDB:DatabaseName` | `orderDemo` |
| `RabbitMQ:HostName` | `localhost` |
| `RabbitMQ:UserName` | `guest` |
| `RabbitMQ:Password` | `guest` |

---

## Key Patterns Demonstrated

| Pattern | Where |
|---------|-------|
| Transactional outbox with `AutoApplyTransactions()` | `Program.cs` |
| `IClientSessionHandle` for domain-write atomicity | `OrderRepository.cs`, handlers |
| Durable inbox for event consumer | `Program.cs` — `.UseDurableInbox()` |
| Domain event → application event mapping | `PlaceOrderHandler.cs` |
| Idempotent read-model upsert | `OrderSummaryRepository.cs` |
| Handler discovery across assemblies | `Program.cs` — `opts.Discovery.IncludeAssembly(...)` |

---

## Dependencies

This demo references `Wolverine.MongoDB` from **nuget.org** (not a local project reference). The version is pinned in `Directory.Packages.props`.

---

## Useful Links

- [Wolverine docs](https://wolverinefx.net)
- [Wolverine.MongoDB README](../README.md)
- [MongoDB .NET driver](https://www.mongodb.com/docs/drivers/csharp/current/)
