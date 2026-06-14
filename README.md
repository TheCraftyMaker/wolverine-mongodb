# Wolverine.MongoDB

A native MongoDB message store for [Wolverine](https://wolverinefx.net)'s
transactional inbox/outbox. Wolverine ships first-class durability providers
for PostgreSQL and SQL Server, but none for MongoDB — and the EF Core + MongoDB
EF provider path does not work because Wolverine's outbox integration assumes a
relational ADO.NET connection and SQL-managed envelope tables. This package
implements `IMessageStore` directly against the MongoDB .NET driver, giving
MongoDB-backed applications reliable, durable message delivery without EF Core.

> **Status: pre-release (`0.1.0-beta.2`).** Node coordination is still being hardened.
> The major version tracks Wolverine's major version (`6.x` ↔ `WolverineFx 6.x`).

[![NuGet](https://img.shields.io/nuget/vpre/Wolverine.MongoDB?label=nuget)](https://www.nuget.org/packages/Wolverine.MongoDB)
[![Build](https://github.com/TheCraftyMaker/wolverine-mongodb/actions/workflows/ci.yml/badge.svg)](https://github.com/TheCraftyMaker/wolverine-mongodb/actions/workflows/ci.yml)
[![Security](https://github.com/TheCraftyMaker/wolverine-mongodb/actions/workflows/security.yml/badge.svg)](https://github.com/TheCraftyMaker/wolverine-mongodb/actions/workflows/security.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0%20%7C%2010.0-purple)](https://dotnet.microsoft.com)

## Prerequisites

- **MongoDB running as a replica set.** Multi-document transactions — used when
  writing a domain change and outgoing envelopes atomically inside a handler —
  are not available on standalone MongoDB. Atlas and any production deployment
  already satisfy this; for local development use a Docker Compose replica set.
  Standalone MongoDB is explicitly unsupported.
- **Wolverine 6.x** (`WolverineFx 6.2.x`). The major version of this package
  must match the major version of `WolverineFx`.
- **.NET 9 or .NET 10.**
- **MongoDB.Driver** 3.x (2.x also supported).

## Installation

```bash
dotnet add package Wolverine.MongoDB --prerelease
```

## Quick start

```csharp
using Wolverine;
using Wolverine.MongoDB;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// Register a MongoClient pointing at a replica set
builder.Services.AddSingleton<IMongoClient>(
    new MongoClient("mongodb://localhost:27017/?replicaSet=rs0"));

builder.Host.UseWolverine(opts =>
{
    // Required: this store only supports single-node durability.
    opts.Durability.Mode = DurabilityMode.Solo;

    // Register the MongoDB transactional outbox/inbox
    opts.UseMongoDbPersistence("my_database");

    // Automatically wrap handlers that use MongoDB types in a transaction
    opts.Policies.AutoApplyTransactions();
});
```

`UseMongoDbPersistence` resolves `IMongoClient` from the DI container, so you
can configure the client however you like (Atlas connection string, custom
`MongoClientSettings`, etc.) before the call.

### Durability mode

`Wolverine.MongoDB` currently supports single-node deployments only.
**`opts.Durability.Mode = DurabilityMode.Solo` is required.** The host will
throw `InvalidOperationException` at startup if the mode is left at the default
`DurabilityMode.Balanced`. Multi-node agent balancing is tracked in the
follow-up plan.

### Domain-write atomicity

When a handler both modifies a MongoDB document and publishes outgoing messages,
the outbox write is committed inside a single MongoDB multi-document transaction
— this is why a replica set is required.

The simplest way to enlist domain writes in the Wolverine-managed transaction is
`MongoDbUnitOfWork`. Accept it as a handler parameter and call writes through
it — the session is threaded automatically:

```csharp
public static async Task<OrderPlaced> Handle(PlaceOrder cmd, MongoDbUnitOfWork mongo, CancellationToken ct)
{
    // Every write through the unit of work participates in the outbox transaction —
    // the session cannot be forgotten.
    await mongo.Collection<Order>("orders").InsertOneAsync(new Order(cmd.OrderId), ct);
    return new OrderPlaced(cmd.OrderId);
}
```

`MongoDbUnitOfWork` exposes `InsertOneAsync`, `InsertManyAsync`, `ReplaceOneAsync`,
`UpdateOneAsync`, `UpdateManyAsync`, `DeleteOneAsync`, `DeleteManyAsync`,
`FindOneAndUpdateAsync`, and `Find` — all automatically scoped to the active
transaction session.

**Advanced / repository pattern:** if you prefer to thread the session through
your own repository layer, accept `IClientSessionHandle` directly and pass it to
every MongoDB write:

```csharp
public static async Task Handle(MyCommand command, IClientSessionHandle session,
    IMongoDatabase database)
{
    // Pass the session so this write joins the Wolverine-managed transaction.
    await database.GetCollection<Order>("orders").InsertOneAsync(session, order);
}
```

The `IMongoDatabase` registered by `UseMongoDbPersistence` does **not**
auto-enlist in the transaction. A handler that writes without the session writes
outside the transaction, so its domain change is **not** atomic with the outbox
and can be lost or duplicated on failure.

The transaction frame is applied automatically when a handler's dependency tree
includes `IMongoDatabase`, `IMongoClient`, `IMongoCollection<T>`,
`IClientSessionHandle`, or `MongoDbUnitOfWork`.

### Write durability

The message store internally pins **`w:majority` (journaled) write concern** and
**majority read concern** on all envelope collections. This is independent of
how the consumer's `MongoClient` is configured — a `w:1` client does not weaken
the durability of the inbox/outbox writes. The app-facing `IMongoDatabase`
registered by `UseMongoDbPersistence` is **not** modified; domain write concerns
remain the application's choice.

### Dead-letter retention

Dead letters are **kept forever by default**, matching the behavior of the RDBMS
providers. To opt into TTL-based expiry, set:

```csharp
opts.Durability.DeadLetterQueueExpirationEnabled = true;
opts.Durability.DeadLetterQueueExpiration = TimeSpan.FromDays(10); // default
```

When expiration is disabled (the default), the TTL index on
`wolverine_dead_letters` is a no-op — documents without an `expirationTime`
field are ignored by MongoDB's TTL background thread.

## Demo application

The [`demo/`](demo/) directory contains a full working example — a CQRS
order-management API that combines `Wolverine.MongoDB` with RabbitMQ to
demonstrate:

- Transactional outbox with `AutoApplyTransactions()`
- Durable inbox for an event-driven read-model projector
- Domain events → application events mapped inside a handler
- `IClientSessionHandle` threaded through repositories for atomicity

See the [demo README](demo/README.md) for setup instructions and a walkthrough.

## How it works

The provider stores envelopes in dedicated collections
(`wolverine_incoming_envelopes`, `wolverine_outgoing_envelopes`,
`wolverine_dead_letters`) plus node-coordination collections
(`wolverine_nodes`, `wolverine_node_assignments`). Single-document atomic
operations (`findAndModify`) handle ownership claims and idempotency rather than
relying on multi-document transactions for the hot path — the approach proven in
the MassTransit MongoDB outbox.

Collections and indexes are created automatically when Wolverine starts.

## Building and testing

The compliance test suite currently requires a local clone of the Wolverine
source, because `WolverineFx.ComplianceTests` is not yet published to NuGet. The
clone is auto-detected via the `WolverineSourcePath` MSBuild property (default
`C:\source\external\wolverine`; override with the `WOLVERINE_SOURCE` environment
variable or `-p:WolverineSourcePath=...`). When the clone is present, both the
library and the test project project-reference it so there is a single
consistent `Wolverine.dll`.

CI runs the full compliance suite against a pinned Wolverine clone (tag
`V6.2.2`) and then packs the library; the demo job downloads the freshly packed
nupkg and runs end-to-end integration tests against it — no stale NuGet version
is exercised.

When the clone is absent, the library can still be built and packed using the
`WolverineFx` NuGet package:

```
dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false
```

The test suite runs against a real MongoDB replica set spun up via
[Testcontainers](https://dotnet.testcontainers.org/). No external setup is
required beyond Docker Desktop.

## Learn more

- [Wolverine durability guide](https://wolverinefx.net/guide/durability/)
- [MongoDB .NET driver transactions](https://www.mongodb.com/docs/drivers/csharp/current/fundamentals/transactions/)

## Known limitations

- **Standalone MongoDB is not supported** — a replica set is required for
  transactions.
- **Single-node coordination only.** `DurabilityMode.Solo` is required; startup
  throws on `DurabilityMode.Balanced`. Multi-node agent balancing is deferred
  to a follow-up release.
- **Node coordination is approximate.** MongoDB has no advisory-lock primitive;
  leader election and agent assignment use a lock document with TTL-based expiry
  via `findAndModify`. This is the least mature part of the library pre-`1.0.0`.
- **Saga storage not yet supported.** Wolverine saga persistence requires
  provider-specific integration that is not yet implemented.
- **High-throughput contention.** The `findAndModify` lock approach serializes
  access per document; under very high concurrency this can bottleneck. Tune
  write concern and indexes accordingly.

## License

[MIT](LICENSE)
