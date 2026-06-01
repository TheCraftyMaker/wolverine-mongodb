# Wolverine.MongoDB — Contributor Guide

## What This Is

A native MongoDB persistence provider for [Wolverine](https://wolverinefx.net)'s transactional inbox/outbox. It implements `IMessageStore` directly against the MongoDB .NET driver — no EF Core, no relational assumptions.

**NuGet package:** `Wolverine.MongoDB`  
**Current version:** `0.1.0-beta.1`

---

## Project Structure

```
src/
  Wolverine.MongoDB/           ← The library (ships as NuGet package)
    Internals/                 ← All implementation details
    WolverineMongoDbExtensions.cs  ← Public API entry point
  Wolverine.MongoDB.Tests/     ← Compliance + integration tests
demo/                          ← Standalone demo app (separate solution)
```

### Key Source Files

| File | Purpose |
|------|---------|
| `WolverineMongoDbExtensions.cs` | `UseMongoDbPersistence()` extension method — public API |
| `Internals/MongoDbMessageStore.cs` | Core `IMessageStore` implementation (partial class) |
| `Internals/MongoDbMessageStore.Inbox.cs` | `IMessageInbox` — incoming envelope persistence |
| `Internals/MongoDbMessageStore.Outbox.cs` | `IMessageOutbox` — outgoing envelope persistence |
| `Internals/MongoDbMessageStore.DeadLetters.cs` | `IDeadLetters` — failed message storage |
| `Internals/MongoDbMessageStore.Admin.cs` | `IMessageStoreAdmin` — collection/index setup |
| `Internals/MongoDbMessageStore.NodeAgents.cs` | `IAgentFamily` — node coordination |
| `Internals/MongoDbMessageStore.Locking.cs` | Leader election via `findAndModify` lock |
| `Internals/MongoDbDurabilityAgent.cs` | Background polling (relay, scheduled, orphans) |
| `Internals/MongoDbEnvelopeTransaction.cs` | Transaction middleware for handler atomicity |
| `Internals/MongoDbPersistenceFrameProvider.cs` | Code-gen integration for `AutoApplyTransactions()` |
| `Internals/TransactionalFrame.cs` | Generated frame that wraps handler in MongoDB session |

### MongoDB Collections

| Collection | Purpose |
|------------|---------|
| `wolverine_incoming_envelopes` | Inbox — idempotency + durable local queues |
| `wolverine_outgoing_envelopes` | Outbox — pending delivery to broker |
| `wolverine_dead_letters` | Failed messages with exception details |
| `wolverine_nodes` | Node registry (heartbeats, capabilities) |
| `wolverine_node_assignments` | Agent-to-node assignments |

---

## Architecture Decisions

### Concurrency: `findAndModify` over transactions

The hot path (claiming envelopes, idempotency checks) uses single-document atomic operations (`FindOneAndUpdate`) — not multi-document transactions. Transactions are only used when a handler needs atomic domain-write + outbox-write.

### Inbox idempotency

Unique index on `_id`. Duplicate inserts throw `DuplicateKeyException` which is caught and treated as "already processed."

### Scheduled messages

`LoadScheduledToExecuteAsync` uses `FindOneAndUpdate` with a filter on `Status == Scheduled && ExecutionTime <= now` to atomically claim ownership.

### Node coordination

Leader election uses a lock document with TTL-based expiry via `findAndModify`. This approximates PostgreSQL advisory locks. It's the least mature part of the codebase.

---

## Development Setup

### Prerequisites

- .NET 9 SDK (or .NET 10)
- Docker (for Testcontainers)
- Local Wolverine source clone (until `WolverineFx.ComplianceTests` is on NuGet)

### Building

```bash
dotnet build
```

The library auto-detects a Wolverine source clone at `C:\source\external\wolverine` or the path in the `WOLVERINE_SOURCE` environment variable. Without it, only the library project builds (test project is skipped).

### Running Tests

```bash
dotnet test src/Wolverine.MongoDB.Tests/
```

Tests use Testcontainers to spin up a MongoDB replica set — no manual Docker setup needed. They require the Wolverine source clone for the compliance test base classes.

### Packing Locally

```bash
dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false
```

The `-p:UseWolverineSource=false` flag ensures the package declares a `WolverineFx` NuGet dependency rather than a project reference.

---

## Versioning

Major version tracks Wolverine's major version. Currently `0.x` (pre-release) until node coordination is production-hardened.

| `Wolverine.MongoDB` | `WolverineFx` |
|---|---|
| `0.1.x` | `6.x` |

---

## CI/CD

| Workflow | Trigger | What it does |
|----------|---------|--------------|
| `ci.yml` | Push/PR to main | Builds library + demo, runs demo integration tests |
| `publish.yml` | Tag `v*` | Packs and pushes to NuGet |
| `security.yml` | Push/PR to main | Trivy vulnerability scan |

---

## Useful Links

- [Wolverine durability docs](https://wolverinefx.net/guide/durability/)
- [MongoDB .NET driver transactions](https://www.mongodb.com/docs/drivers/csharp/current/fundamentals/transactions/)
- [MassTransit MongoDB outbox](https://github.com/MassTransit/MassTransit/tree/develop/src/Persistence/MassTransit.MongoDbIntegration/MongoDbIntegration/Outbox) (design reference)
- Wolverine source: `IMessageStore` in `src/Wolverine/Persistence/Durability/IMessageStore.cs`

