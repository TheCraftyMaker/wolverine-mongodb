# Wolverine.MongoDB

A native MongoDB message store for [Wolverine](https://wolverinefx.net)'s
transactional inbox/outbox. Wolverine ships first-class durability providers
for PostgreSQL and SQL Server, but none for MongoDB, and the EF Core + MongoDB
EF provider path does not work because Wolverine's outbox integration assumes a
relational ADO.NET connection and SQL-managed envelope tables. This package
implements `IMessageStore` directly against the MongoDB .NET driver, giving
MongoDB-backed applications reliable, durable message delivery without EF Core.

> **Status: `1.0.0`** (released 2026-07-06). The multinode (`DurabilityMode.Balanced`)
> path is functional and integration-tested; see [Known limitations](#known-limitations).

[![NuGet](https://img.shields.io/nuget/v/Wolverine.MongoDB?label=nuget)](https://www.nuget.org/packages/Wolverine.MongoDB)
[![Build](https://github.com/TheCraftyMaker/wolverine-mongodb/actions/workflows/ci.yml/badge.svg)](https://github.com/TheCraftyMaker/wolverine-mongodb/actions/workflows/ci.yml)
[![Security](https://github.com/TheCraftyMaker/wolverine-mongodb/actions/workflows/security.yml/badge.svg)](https://github.com/TheCraftyMaker/wolverine-mongodb/actions/workflows/security.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0%20%7C%2010.0-purple)](https://dotnet.microsoft.com)

## Prerequisites

- **MongoDB running as a replica set.** Multi-document transactions (used when
  writing a domain change and outgoing envelopes atomically inside a handler)
  are not available on standalone MongoDB. Atlas and any production deployment
  already satisfy this; for local development use a Docker Compose replica set.
  Standalone MongoDB is explicitly unsupported.
- **WolverineFx 6.38.0 or later** (the exact pin is in `Directory.Packages.props`; the
  `external/wolverine` submodule tracks the same release).
- **.NET 9 or .NET 10.**
- **MongoDB.Driver** 3.x.

## Installation

```bash
dotnet add package Wolverine.MongoDB
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

Use `DurabilityMode.Solo` for single-instance deployments: no control endpoint
is required and node coordination is minimal.

For multi-node clusters see [Multinode support](#multinode-support) below.

### Domain-write atomicity

When a handler both modifies a MongoDB document and publishes outgoing messages,
the outbox write is committed inside a single MongoDB multi-document transaction,
which is why a replica set is required.

The simplest way to enlist domain writes in the Wolverine-managed transaction is
`MongoDbUnitOfWork`. Accept it as a handler parameter and call writes through
it; the session is threaded automatically:

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
`FindOneAndUpdateAsync`, and `Find`, all automatically scoped to the active
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
**majority read concern** on all envelope collections, independent of how the
consumer's `MongoClient` is configured.

That handle-level pin covers the *sessionless* writes only. MongoDB **discards**
collection- and database-level concerns for anything run inside a transaction:
the individual writes are never acknowledged on their own, only
`commitTransaction` is, and that command's concern comes from the transaction
options — falling back to the consumer's `MongoClient` settings when none are
supplied. So every transaction this library opens **restates** the pin
(`MongoTransactionOptions.Durable`): the code-generated handler/outbox
transaction, the batch inbox store, and the dead-letter move. A `w:1` client
therefore weakens neither the sessionless inbox/outbox writes nor the
transactional ones.

One consequence is deliberate and worth knowing: the handler transaction is
shared with the application's own enlisted writes — `MongoDbUnitOfWork`, a raw
`IClientSessionHandle`, saga and entity documents — and a transaction has
exactly one write concern, so those commit at `w:majority, j:true` too. A
handler that enlists in the outbox transaction opts into its commit semantics.
Writes the application makes **outside** that transaction, through the
app-facing `IMongoDatabase` (which is still **not** modified), remain entirely
the application's choice.

Two read paths are honestly *not* covered: a read-only `[Entity]` load that
resolves no session falls back to a session-less read on the unpinned app-facing
handle, and `MongoDbSagaStoreDiagnostics` acquires its own unpinned handle. Both
are reads, and neither is part of the durability guarantee.

The transaction concern is a single non-configurable constant today — there is
no per-host override (see `FOLLOWUPS.md`). It imposes no new availability floor:
a replica set that cannot satisfy `w:majority` already cannot serve this store,
because every non-transactional inbox/outbox/recovery write goes through the
pinned handle.

> **Upgrading:** the frame's generated code changed. Any consumer with
> pre-generated handler code compiled into the application assembly must
> **regenerate** before the handler transaction picks this up — that means
> `TypeLoadMode.Static` *and* `TypeLoadMode.Auto`, which also attaches a
> pre-generated handler type by name when it finds one and never compares it
> against the current frame output. Until then the stale handler keeps the
> option-less `StartTransaction()` and commits at the client default. The two
> store-side transactions (inbox batch, dead-letter move) are not generated
> code and are pinned regardless of codegen mode.

### Dead-letter retention

Dead letters are **kept forever by default**, matching the behavior of the RDBMS
providers. To opt into TTL-based expiry, set:

```csharp
opts.Durability.DeadLetterQueueExpirationEnabled = true;
opts.Durability.DeadLetterQueueExpiration = TimeSpan.FromDays(10); // default
```

When expiration is disabled (the default), the TTL index on
`wolverine_dead_letters` is a no-op: documents without an `expirationTime`
field are ignored by MongoDB's TTL background thread.

### Dead letters and `MessageIdentity`

With Wolverine's default `opts.Durability.MessageIdentity = MessageIdentity.IdOnly`
nothing here needs your attention: one envelope id means one dead letter, and the
document's `_id` is the envelope's own `Guid`.

If your app opts into `MessageIdentity.IdAndDestination` — the modular-monolith
case, where the same message id arrives on several listening endpoints and each
delivery is processed separately — then each failed delivery gets **its own**
dead-letter document, distinguished by `receivedAt`, the same way the inbox
already keeps one document per destination. Because the `IDeadLetters` API
addresses dead letters only by `Guid`:

- `QueryAsync` and `SummarizeAllAsync` show every delivery, one entry per
  destination.
- `DeadLetterEnvelopeByIdAsync(id)` can only return one; it returns the first
  ordered by `receivedAt`.
- Discard, replay and `EditAndReplayAsync` by message id affect **every**
  delivery of that id. This matches the RDBMS providers.

Upgrading is transparent: dead letters written by earlier versions stay
queryable, discardable, replayable and editable, and the next startup that runs
storage migration backfills them with the new `envelopeId` field. The backfill is
non-destructive — it only copies `_id` into `envelopeId` on documents that lack
it — and it needs MongoDB 4.2 or later. To run it on demand, call
`IMessageStoreAdmin.MigrateAsync()`. Do **not** reach for `RebuildAsync()`: that
is a full reset, not a migration — it deletes every dead letter and every pending
inbox/outbox envelope before recreating the indexes, so there is nothing left to
backfill.

### The registered `IMongoDatabase`

`UseMongoDbPersistence("my_database")` registers a single **unkeyed**
`IMongoDatabase` (pointing at `my_database`) in the container. Every
code-generated handler frame (the transaction frame, saga and `[Entity]`
persistence, and the `MongoDbUnitOfWork` write surface) resolves the database
through this one registration. The database the generated code writes to is
therefore fixed at registration time and shared with any `IMongoDatabase` you
inject into your own handlers.

Because the registration is unkeyed, **an app that also registers its own
unkeyed `IMongoDatabase` collides with it.** `Microsoft.Extensions.DependencyInjection`
resolves the *last* registration for a single-service request, so registration
order alone would decide which database the Wolverine frames (and your own
injections) resolve, a subtle way for writes to land in the wrong database. If
your app needs a database handle of its own, do **not** register a second
unkeyed `IMongoDatabase`. Instead:

- reuse the one Wolverine registers (it points at the persistence database), or
- inject `IMongoClient` and call `client.GetDatabase("other_database")` for a
  different database, or
- register your database under a **keyed** service (or a small wrapper type) and
  resolve that explicitly, leaving the unkeyed `IMongoDatabase` to Wolverine.

This is a deliberate, documented consumer constraint. The generated frames
resolve `IMongoDatabase` by type, so a keyed or dedicated registration would be
a high-blast-radius change to code generation for a rare conflict, not worth it
while a single Wolverine database is the overwhelmingly common case.

## Saga persistence

`Wolverine.MongoDB` supports [Wolverine sagas](https://wolverinefx.net/guide/durability/sagas.html)
(stateful, message-correlated workflows represented by a `Saga` subclass). No additional
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
natively as its corresponding BSON type: no conversion overhead, no cross-type collision.

By convention, Wolverine uses the member named `Id`, `SagaId`, or `{SagaTypeName}Id`
as the identity. The `[SagaIdentity]` attribute may be used on a message member to
tell Wolverine which field carries the saga id.

### Optimistic concurrency

The provider uses `Saga.Version` for optimistic concurrency on updates:

- **Insert** (new saga): stamps `Version = 1`. Unguarded: concurrent double-starts fail on
  the unique `_id` index (duplicate key), so the second start retries onto the update path.
- **Update** (existing saga): captures `oldVersion`, increments `Version`, then
  `ReplaceOneAsync` with filter `(_id, oldVersion)`. Throws `SagaConcurrencyException`
  when `ModifiedCount == 0`; the saga write and the outbox roll back together.
- **Delete** (completed saga): unguarded by version: completion is terminal.

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

The name comes from the **simple** type name, so it carries no namespace, no generic
arguments and no case. Two saga types called `OrderSaga` in different namespaces, two
differing only in case, or two closed constructions of one open generic
(`Box<int>` and `Box<string>` are both `` box`1 ``) would therefore resolve to the same
collection and silently mix their documents. Wolverine.MongoDB detects that while it
compiles the handler graph and **refuses to start the host**, naming both types, the shared
collection and the mapping call to add. Resolve it with an explicit mapping:

```csharp
opts.UseMongoDbPersistence("appdb", o =>
    o.MapSagaCollection<Returns.OrderSaga>("wolverine_saga_returns_ordersaga"));
```

A saga mapping must keep the `wolverine_saga_` prefix — `IMessageStoreAdmin.ClearAllAsync`
and `RebuildAsync` sweep saga collections by that prefix, so a saga stored outside it would
silently stop being cleared. Mapping a type does not move documents that were already
written elsewhere.

### Atomicity with the outbox

The saga state write and any outbox entries produced in the handler commit inside the same
MongoDB multi-document transaction, the same guarantee as domain writes via
`MongoDbUnitOfWork`. No session handling is needed in the saga methods; the generated
transactional frame manages the session lifecycle.

### Multiple handlers for the same message type

When a saga and a non-saga handler both consume the same message type, set
`MultipleHandlerBehavior.Separated` so each handler runs independently. Without it,
Wolverine's `SagaChain` silently drops the non-saga handler:

```csharp
opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
```

## Entity persistence

`Wolverine.MongoDB` also implements Wolverine's generic persistence surface: `[Entity]`
parameter loading and `Insert<T>`/`Update<T>`/`Store<T>`/`Delete<T>`/`IStorageAction<T>`
return-value side effects, for any plain document type, not just `Saga` subclasses. No
additional registration is required.

```csharp
using Wolverine.Persistence;

// Insert<T> — create a new document. Wolverine's generated frame upserts it
// into the "ordernote" collection inside the active transaction.
public static Insert<OrderNote> Handle(AddOrderNoteCommand cmd)
    => new(new OrderNote { Id = Guid.NewGuid().ToString(), Text = cmd.Text });

// [Entity] loads the document by id before the handler runs; Update<T>/Delete<T>
// persist the mutated (or removed) document after it returns.
public static Update<OrderNote> Handle(EditOrderNoteCommand cmd, [Entity("NoteId")] OrderNote note)
{
    note.Text = cmd.NewText;
    return new Update<OrderNote>(note);
}
```

- **Collection naming:** each entity type gets its own collection named
  `<lowercased-type-name>` (e.g. `OrderNote` → `ordernote`), un-prefixed, unlike the
  `wolverine_saga_` sagas, because entity collections are application-owned data, not a
  Wolverine system collection. `IMessageStoreAdmin.ClearAllAsync`/`RebuildAsync` never
  touch them.

  As for sagas, the name comes from the **simple** type name — no namespace, no generic
  arguments, no case — so `Ordering.Note` and `Billing.Note`, `Metric` and `METRIC`, and
  `Box<int>` and `Box<string>` all resolve to one collection. There is no type
  discriminator and entity writes upsert without a version guard, so such types would
  silently overwrite each other. Wolverine.MongoDB detects this while it compiles the
  handler graph and **refuses to start the host**. Resolve it with an explicit mapping:

  ```csharp
  opts.UseMongoDbPersistence("appdb", o =>
      o.MapEntityCollection<Billing.Note>("billing_note"));
  ```

  Because entity collections are deliberately un-prefixed, they also share the
  application's own collection namespace. If your repositories already own the lowercased
  type name — or already hold the same aggregate under a differently-cased or pluralised
  name, e.g. `GetCollection<Order>("orders")` next to an `[Entity] Order` that resolves to
  `order` — map the entity explicitly. **The startup check cannot see this case:** an
  application's own `GetCollection<T>("...")` literal is not in the handler graph. Entity
  mappings may not sit inside the `wolverine_saga_` prefix (an administrative rebuild would
  drop that data) or take one of Wolverine's system collection names.
- **Write semantics: upsert, no optimistic concurrency.** `Insert`/`Update`/`Store` all
  compile to the same upserting write (`ReplaceOneAsync` with `IsUpsert = true`); `Delete`
  removes by the entity's id. Plain entities do not carry a `Saga.Version`-style guard, so
  concurrent writes are last-write-wins, matching Wolverine's Cosmos/RavenDb providers. If
  your handler needs optimistic concurrency, use the repository pattern (accept
  `IClientSessionHandle` directly and guard your own `ReplaceOneAsync` filter on a version
  field), the same pattern the demo's `OrderRepository` uses for the `Order` aggregate.
- **Id extraction:** the entity's `_id` value is read generically via the MongoDB driver's
  class map (`BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap`), the same convention the
  driver itself uses to determine `_id`, not a `.ToString()` coercion.
- **`[Entity]` not-found behavior** follows Wolverine core's `EntityAttribute` defaults
  (`Required = true` skips the handler with a 404-style outcome when the entity is missing;
  `MaybeSoftDeleted` is not applicable, see [Known limitations](#known-limitations)).
- Entity writes run on the same MongoDB session as saga and outbox writes, so they commit
  atomically with everything else the handler does.

See the demo's [`OrderNoteHandler`](demo/src/OrderDemo.Application/Notes/OrderNoteHandler.cs)
for a complete `Insert`/`Update`/`Delete` example wired to HTTP endpoints.

### Whole-collection reads: `[All]`, `[FirstOrDefault]`, `[Queryable]`

WolverineFx 6.38 added three read-only parameter attributes; all three are supported because
every type has its own collection here.

```csharp
public static ColorsCounted Handle(CountColors cmd, [All] IReadOnlyList<Color> colors)
    => new(colors.Count);

// Null when nothing is stored — there is deliberately no Required/404 branch on this one.
public static AlertDefaultsRead Handle(ReadAlertDefaults cmd, [FirstOrDefault] AlertDefaults? defaults)
    => new(defaults?.Threshold ?? -1);

// The escape hatch: the driver's own LINQ provider over the collection. Not portable.
public static async Task<PopularColorsFound> Handle(FindPopularColors cmd,
    [Queryable] IQueryable<Color> colors, CancellationToken ct)
    => new((await colors.Where(x => x.Hits >= cmd.Minimum).Select(x => x.Name).ToListAsync(ct)).ToArray());
```

- When the handler is transactional (it writes through a storage action, takes
  `MongoDbUnitOfWork`, …) the read runs on the outbox session and sees that transaction's own
  writes; a read-only handler reads session-less and no transaction is forced open.
- Reads honour an explicit `MapEntityCollection` mapping, and a `Saga` type is read from its
  `wolverine_saga_*` collection.

### Mixed persistence

The provider is a **catch-all** (`IsCatchAll`): it claims every entity type. Wolverine consults
selective providers such as EF Core (which only claim the types mapped in a registered
`DbContext`) *before* catch-alls, regardless of registration order, so in an application that
also registers EF Core the EF-mapped entities keep resolving to EF Core and everything else to
MongoDB.

## Logical message deduplication

Opt in with `opts.Durability.EnableMessageDeduplication = true` and mark handlers
`[Deduplicated]` (or use `opts.MessageDeduplication` / `[DeduplicationIdentity]` to derive the
id). Claims live in `wolverine_deduplication`, keyed by the deduplication id, so the collection's
own `_id` uniqueness is the atomic claim: twenty nodes racing for one id produce exactly one
winner. The stored expiry (`Durability.DeduplicationWindow`) is honoured to the instant — a claim
whose window has passed is taken over — and a TTL index reaps stale claims. With the flag off the
store is `NullDeduplicationStore` and nothing is provisioned.

**Transactional handlers.** Wolverine weaves the claim in *before* the persistence provider opens
a session and gives providers no way to claim inside the handler transaction; on the RDBMS
providers a rolled-back transactional handler therefore leaves its claim behind and the retry is
refused as a duplicate. Here the claim is sessionless and the MongoDB transaction frame **releases
it when the transaction rolls back**, so the retry runs. A duplicate-key error on the claim can
never abort a handler transaction because the claim is not part of one.

## Durable recurring messages

Registering a schedule (`opts.Schedules.ScheduleRecurring<T>("0 * * * *")`) turns on
`IRecurringMessageStore` tracking in `wolverine_recurring_messages` (one document per schedule,
Main store only). The recurring agent records the pre-scheduled occurrence's envelope ids and
deduplication id, re-publishes a cancelled occurrence under the same deduplication id, and a
restarted or failed-over host adopts its predecessor's pending occurrence instead of publishing a
second one. `IRecurringScheduleControl.PauseAsync` marks the document **and deletes the tracked
scheduled envelopes in the same transaction**, so pausing from any node stops the next occurrence
immediately; resume never back-fills; a manual trigger is refused while paused. Exactly-once
beyond that (same occurrence → same deduplication id → one handling) rests on the deduplication
store above.

## Saga store diagnostics

`Wolverine.MongoDB` implements Wolverine's read-only `ISagaStoreDiagnostics` surface (the
interface CritterWatch and other saga-explorer tooling use), matching RavenDb. Cosmos does
not implement it. It is registered automatically by `UseMongoDbPersistence`; no extra setup
is needed. Saga descriptors are tagged `"MongoDb"`, and reads target the same
`wolverine_saga_<type>` collections the saga frames write to, matching by native `_id` (no
string coercion). `ListSagaInstancesAsync` clamps its `count` argument to `[0, 1000]`.

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
clocks are required (not a throw; the host starts normally).

### Multinode requirements

- **`opts.UseTcpForControlEndpoint()`** (or any configured control endpoint):
  nodes use Wolverine's control channel for leader election and agent balancing.
  Without it, nodes cannot exchange control messages.
- **Synchronized node clocks**: the leader lock uses a time-based lease
  (`LockLeaseDuration`, default 1 minute). Node clocks must be synchronized to
  well within this duration. Standard NTP keeps typical server clocks within a
  few milliseconds, which is safe for the default lease.

### Multinode semantics

- **Leader election:** a lock document in `wolverine_locks` is claimed via
  `findAndModify` (compare-and-swap). Any healthy node can become leader; the
  first to atomically claim an expired or absent lock wins.
- **Scheduled messages:** claimed via `FindOneAndUpdate` CAS
  (`Status == Scheduled && ExecutionTime <= now`, re-asserting the same predicate and
  the same instant the batch select used). The two conjuncts buy two different
  guarantees: the status check makes two nodes competing for the same due message
  produce at most one execution, and the execution-time check means a message
  rescheduled while a poll is already in flight is left alone rather than executed
  early — it is simply picked up again once its new time arrives. Caveat: a
  reschedule issued *after* a message has already been claimed does not take effect,
  and `IScheduledMessages.RescheduleAsync` returns no matched count, so the caller is
  not told.
- **Dead-node recovery:** a dedicated sweep (every
  `Durability.OrphanedMessageSweepPollingTime`, Balanced mode only) releases envelope
  ownership held by node numbers with no live node document (crashed nodes) — a number must
  be observed dead on two consecutive sweeps, and the release is bounded to
  `OrphanedMessageReleaseBatchSize` documents per write and
  `OrphanedMessageReleaseMaxBatchesPerCycle` writes per sweep, the remainder following on the
  next sweep. The recovery loop then recovers the orphaned envelopes. Envelopes owned by live
  nodes are never touched.
- **CAS-guarded outgoing recovery:** when recovering orphaned outgoing envelopes,
  only envelopes still globally-owned (`OwnerId == 0`) are claimed, and the claim
  uses a filter guard so a competing node that claimed an envelope between load and
  write retains it, so there are no double-sends.
- **Node records:** the Main store's durability agent prunes node-event records on
  `Durability.NodeRecordPruningPeriod` (first pass after at most one minute): records older
  than `NodeEventRecordExpirationTime` are deleted and the rest trimmed to
  `NodeRecordRetention` when that is positive. The TTL index on `wolverine_node_records`
  keeps its 14-day backstop.

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
- **`LeadershipElectionCompliance` runs unconditionally in CI.** Earlier
  WolverineFx releases required the lowest-numbered surviving node to win the
  election race (a property our `w:majority` lock could not guarantee), so the
  suite was compile-gated behind `#if RUN_MULTINODE`. WolverineFx 6.9.0 reworked
  those facts around the "any healthy node leads" model this provider already
  implements, so the gate was removed after 5 consecutive green runs on both
  net9.0 and net10.0 (10/10 runs, 17/17 facts each). Production confidence also
  continues to come from the cross-node message-guarantee tests
  (`multinode_end_to_end.cs`): exactly-once scheduled delivery and dead-node
  rescue, each verified with five consecutive green runs.

## Demo application

The [`demo/`](demo/) directory contains a full working example: a CQRS
order-management API that combines `Wolverine.MongoDB` with RabbitMQ to
demonstrate:

- Transactional outbox with `AutoApplyTransactions()`
- Durable inbox for an event-driven read-model projector
- Domain events → application events mapped inside a handler
- `IClientSessionHandle` threaded through repositories for atomicity
- Config-driven durability mode (Solo by default; `Wolverine__DurabilityMode=Balanced`
  for multi-instance runs)
- `OrderFulfillmentSaga`: a saga that tracks an order through placement, shipping, and
  delivery confirmation, exercising start / continue / complete flows and outbox atomicity

See the [demo README](demo/README.md) for setup instructions, a walkthrough, and
the multinode runbook.

## How it works

The provider stores envelopes in dedicated collections
(`wolverine_incoming_envelopes`, `wolverine_outgoing_envelopes`,
`wolverine_dead_letters`) plus node-coordination collections
(`wolverine_nodes`, `wolverine_node_assignments`) and, when opted in,
`wolverine_deduplication` and `wolverine_recurring_messages`. Single-document atomic
operations (`findAndModify`) handle ownership claims and idempotency rather than
relying on multi-document transactions for the hot path, the approach proven in
the MassTransit MongoDB outbox.

Collections and indexes are created automatically when Wolverine starts.

## Building and testing

The compliance test suite comes from the Wolverine source, vendored as a git
submodule at `external/wolverine` and pinned to the same release as the
`WolverineFx` package (`V6.38.0`): clone with `git clone --recursive` (or run
`git submodule update --init`). Both the library and the test project
project-reference it so there is a single consistent `Wolverine.dll`; the path is
overridable via the `WOLVERINE_SOURCE` environment variable or
`-p:WolverineSourcePath=...`. `WolverineFx.ComplianceTests` is also published on
NuGet, so a checkout without the submodule builds against the package. The test
project is an xUnit v3 test host (the compliance suites are built on
`xunit.v3.extensibility.core`); the demo stays on xUnit 2.

CI initialises the submodule, runs the compliance suite in two separate steps
(single-node and multinode categories), then packs the library; the demo job
downloads the freshly packed nupkg and runs end-to-end integration tests against
it, so no stale NuGet version is exercised.

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

- **Standalone MongoDB is not supported**: a replica set is required for
  transactions.
- **Multinode leadership is lease-based, not fenced.** See
  [Multinode known limitations](#multinode-known-limitations) for the fencing
  caveat and clock-skew constraint.
- **`UseMongoDbPersistence` registers a single unkeyed `IMongoDatabase`.** An app
  that registers its own unkeyed `IMongoDatabase` conflicts with it. See
  [The registered `IMongoDatabase`](#the-registered-imongodatabase) for the
  workarounds.
- **A value returned from an `AfterCommit` method is not a cascading message** in
  WolverineFx 6.38 (post-commit frames are plain method calls). Publish through
  `IMessageBus` from the hook instead; the message rides the end-of-pipeline flush,
  not the committed transaction's outbox.
- **One upstream compliance fact cannot pass here:**
  `RecurringMessageCompliance.the_opt_in_is_schema_neutral_for_hosts_without_schedules`
  casts the store to Weasel's `IDatabase` to enumerate tables. Its behaviour is covered by
  `recurring_messages.the_opt_in_is_schema_neutral`.
- **High-throughput contention.** The `findAndModify` lock approach serializes
  access per document; under very high concurrency this can bottleneck. Tune
  write concern and indexes accordingly.
- **Four RDBMS/Marten-only capabilities are explicit non-goals**, matching the
  closest document-store analogues (Cosmos, RavenDb): multi-tenancy (route on a
  tenant-id field in your message payload, or run a separate host per tenant),
  durable listeners (`IListenerStore` stays `NullListenerStore`, only matters if
  you opt into `EnableDynamicListeners`), query-spec frames
  (`ICompiledQuery<,>`-style compile-time queries, a Marten/EF Core concept with
  no MongoDB analogue), and soft-delete (`[Entity(MaybeSoftDeleted = false)]` plus
  a manual `is_deleted` filter is the app-level equivalent). See `CLAUDE.md`
  ("Parity Capabilities: Non-Goals") for the full rationale per capability.

## License

[MIT](LICENSE)
