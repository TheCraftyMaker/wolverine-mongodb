# Wolverine.MongoDB

A native MongoDB message store for [Wolverine](https://wolverinefx.net)'s
transactional inbox/outbox. Wolverine ships first-class durability providers
for PostgreSQL and SQL Server, but none for MongoDB — and the EF Core + MongoDB
EF provider path does not work because Wolverine's outbox integration assumes a
relational ADO.NET connection and SQL-managed envelope tables. This package
implements `IMessageStore` directly against the MongoDB .NET driver, giving
MongoDB-backed applications reliable, durable message delivery without EF Core.

> **Status: pre-release (`0.1.0-beta.7`).** The multinode (`DurabilityMode.Balanced`)
> path is functional and integration-tested; see [Known limitations](#known-limitations).
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
- **Wolverine 6.x** (`WolverineFx 6.9.x`). The major version of this package
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
    // Single-node deployment (default). For multi-node see "Multinode support" below.
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

`Wolverine.MongoDB` supports both single-node (`DurabilityMode.Solo`) and
multi-node (`DurabilityMode.Balanced`) deployments.

Use `DurabilityMode.Solo` for single-instance deployments — no control endpoint
is required and node coordination is minimal.

For multi-node clusters see [Multinode support](#multinode-support) below.

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

## Saga persistence

`Wolverine.MongoDB` supports [Wolverine sagas](https://wolverinefx.net/guide/durability/sagas.html)
— stateful, message-correlated workflows represented by a `Saga` subclass. No additional
registration is required; `UseMongoDbPersistence` automatically enables saga storage
alongside the inbox/outbox.

### Defining a saga

```csharp
using Wolverine;

public class OrderFulfillmentSaga : Saga
{
    // "Id" is the default Wolverine identity member convention; maps to MongoDB _id.
    public Guid Id { get; set; }

    public bool OrderPlaced { get; set; }
    public bool OrderShipped { get; set; }

    // "Start" / "Starts" is recognized as the saga-start method.
    // Assign Id here so the document is inserted on first message.
    public void Start(OrderPlacedEvent evt)
    {
        Id = evt.OrderId;
        OrderPlaced = true;
    }

    public void Handle(OrderShippedEvent evt)
    {
        OrderShipped = true;
    }

    public void Handle(DeliveryConfirmedEvent cmd)
    {
        // MarkCompleted() signals Wolverine to delete the saga document from MongoDB.
        MarkCompleted();
    }
}
```

### Supported id types

The saga identity member may be `Guid`, `string`, `int`, or `long`. MongoDB stores each
natively as its corresponding BSON type — no conversion overhead, no cross-type collision.

By convention, Wolverine uses the member named `Id`, `SagaId`, or `{SagaTypeName}Id`
as the identity. The `[SagaIdentity]` attribute may be used on a message member to
tell Wolverine which field carries the saga id.

### Optimistic concurrency

The provider uses `Saga.Version` for optimistic concurrency on updates:

- **Insert** (new saga): stamps `Version = 1`. Unguarded — concurrent double-starts fail on
  the unique `_id` index (duplicate key), so the second start retries onto the update path.
- **Update** (existing saga): captures `oldVersion`, increments `Version`, then
  `ReplaceOneAsync` with filter `(_id, oldVersion)`. Throws `SagaConcurrencyException`
  when `ModifiedCount == 0` — the saga write and the outbox roll back together.
- **Delete** (completed saga): unguarded by version — completion is terminal.

Under multi-node deployments, wire a retry policy so a losing node reloads and re-applies
its step rather than failing the message:

```csharp
opts.Policies
    .OnException<SagaConcurrencyException>()
    .Or<MongoException>(e => e.HasErrorLabel("TransientTransactionError"))
    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
```

`TransientTransactionError` covers the more common concurrent-transaction abort at the
MongoDB server layer; `SagaConcurrencyException` covers the rarer case where one writer
committed just before the other's guarded `ReplaceOneAsync` ran.

### Collections

Each saga type gets its own MongoDB collection:

```
wolverine_saga_<lowercased-type-name>
```

For example, `OrderFulfillmentSaga` → `wolverine_saga_orderfulfillmentsaga`. Collections
are created automatically on startup.

### Atomicity with the outbox

The saga state write and any outbox entries produced in the handler commit inside the same
MongoDB multi-document transaction — the same guarantee as domain writes via
`MongoDbUnitOfWork`. No session handling is needed in the saga methods; the generated
transactional frame manages the session lifecycle.

### Multiple handlers for the same message type

When a saga and a non-saga handler both consume the same message type, set
`MultipleHandlerBehavior.Separated` so each handler runs independently. Without it,
Wolverine's `SagaChain` silently drops the non-saga handler:

```csharp
opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
```

## Multinode support

`DurabilityMode.Balanced` is supported. MongoDB has no native control transport,
so a TCP control endpoint is required between nodes (mirroring Wolverine's RavenDb
provider):

```csharp
using Wolverine.Transports.Tcp;

builder.Host.UseWolverine(opts =>
{
    opts.Durability.Mode = DurabilityMode.Balanced;

    // Required: MongoDB has no native inter-node control transport.
    opts.UseTcpForControlEndpoint();

    opts.UseMongoDbPersistence("my_database");
});
```

At startup, when `DurabilityMode.Balanced` is detected, the store logs an
`Information` message confirming the mode and reminding you that synchronized
clocks are required (not a throw — the host starts normally).

### Multinode requirements

- **`opts.UseTcpForControlEndpoint()`** (or any configured control endpoint) —
  nodes use Wolverine's control channel for leader election and agent balancing.
  Without it, nodes cannot exchange control messages.
- **Synchronized node clocks** — the leader lock uses a time-based lease
  (`LockLeaseDuration`, default 1 minute). Node clocks must be synchronized to
  well within this duration. Standard NTP keeps typical server clocks within a
  few milliseconds, which is safe for the default lease.

### Multinode semantics

- **Leader election:** a lock document in `wolverine_locks` is claimed via
  `findAndModify` (compare-and-swap). Any healthy node can become leader; the
  first to atomically claim an expired or absent lock wins.
- **Scheduled messages:** claimed exactly-once via `FindOneAndUpdate` CAS —
  `Status == Scheduled && ExecutionTime <= now` — so two nodes competing for the
  same due message produce at most one execution.
- **Dead-node recovery:** on each recovery tick, each node releases envelope
  ownership held by node numbers with no live node document (crashed nodes), then
  recovers those orphaned envelopes. Envelopes owned by live nodes are never touched.
- **CAS-guarded outgoing recovery:** when recovering orphaned outgoing envelopes,
  only envelopes still globally-owned (`OwnerId == 0`) are claimed, and the claim
  uses a filter guard so a competing node that claimed an envelope between load and
  write retains it — no double-sends.
- **Node records:** the leader trims old node-event records via
  `DeleteOldNodeRecordsAsync`; the TTL index on `wolverine_node_records` provides
  a 14-day backstop.

### Tuning failover speed

`LockLeaseDuration` controls how long the leader lock is held before another node
can take over. Lower values mean faster failover but more lock renewal churn:

```csharp
opts.UseMongoDbPersistence("my_database",
    mongo => mongo.LockLeaseDuration = TimeSpan.FromSeconds(30));
```

The default is **1 minute**. `HasLeadershipLock()` returns `false` once 75% of
the lease has elapsed, so a node stops acting as leader before another can
legitimately take over.

### Multinode known limitations

- **Leadership is lease-based, not fenced.** `HasLeadershipLock()` goes `false`
  at 75% of the lease duration, so the store-layer leadership check is
  conservative. However, any side effects that do not go through the message store
  (e.g. an external HTTP call triggered by a leader-only agent) are not fenced by
  this check. If your leader-specific work only touches MongoDB collections via
  the store, you are safe; for external side effects, treat leadership as advisory
  rather than exclusive.
- **Clock skew near `LockLeaseDuration` breaks takeover ordering.** A node whose
  clock is significantly skewed relative to others may not correctly observe lease
  expiry. Keep node clocks synchronized to well within the lease duration (NTP is
  sufficient for the default 1-minute lease).
- **The `LeadershipElectionCompliance` suite is compile-gated.** The upstream
  compliance facts require the lowest-numbered surviving node to win the election
  race — an emergent property of very fast stores. Our `w:majority` lock cannot
  guarantee this ordering, so the suite stays gated behind `#if RUN_MULTINODE`,
  matching how Wolverine's own Cosmos provider gates the same facts `[Flaky]`.
  Production confidence comes from the cross-node message-guarantee tests
  (`multinode_end_to_end.cs`): exactly-once scheduled delivery and dead-node
  rescue, each verified with five consecutive green runs.

## Demo application

The [`demo/`](demo/) directory contains a full working example — a CQRS
order-management API that combines `Wolverine.MongoDB` with RabbitMQ to
demonstrate:

- Transactional outbox with `AutoApplyTransactions()`
- Durable inbox for an event-driven read-model projector
- Domain events → application events mapped inside a handler
- `IClientSessionHandle` threaded through repositories for atomicity
- Config-driven durability mode (Solo by default; `Wolverine__DurabilityMode=Balanced`
  for multi-instance runs)
- `OrderFulfillmentSaga` — a saga that tracks an order through placement, shipping, and
  delivery confirmation, exercising start / continue / complete flows and outbox atomicity

See the [demo README](demo/README.md) for setup instructions, a walkthrough, and
the multinode runbook.

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

The compliance test suite currently requires the Wolverine source, because
`WolverineFx.ComplianceTests` is not yet published to NuGet. It is vendored as a
git submodule at `external/wolverine`, pinned to the matching `WolverineFx`
version — clone with `git clone --recursive` (or run `git submodule update
--init`). Both the library and the test project project-reference it so there is
a single consistent `Wolverine.dll`. The path is overridable via the
`WOLVERINE_SOURCE` environment variable or `-p:WolverineSourcePath=...`.

CI initialises the submodule, runs the compliance suite in two separate steps
(single-node and multinode categories), then packs the library; the demo job
downloads the freshly packed nupkg and runs end-to-end integration tests against
it — no stale NuGet version is exercised.

To run only the multinode tests locally:

```bash
dotnet test src/Wolverine.MongoDB.Tests --filter "Category=multinode"
```

When the submodule is absent, the library can still be built and packed using
the `WolverineFx` NuGet package:

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
- **Multinode leadership is lease-based, not fenced.** See
  [Multinode known limitations](#multinode-known-limitations) for the fencing
  caveat and clock-skew constraint.
- **High-throughput contention.** The `findAndModify` lock approach serializes
  access per document; under very high concurrency this can bottleneck. Tune
  write concern and indexes accordingly.

## License

[MIT](LICENSE)
