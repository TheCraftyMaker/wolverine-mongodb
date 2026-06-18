# Task S1 — Repository & Convention Analysis

> **Produced by:** Task S1 of `2026-06-18-saga-persistence.md`  
> **Branch:** `docs/saga-repo-analysis`  
> **Date:** 2026-06-18  
> **Status:** Complete — all "Verified Saga API Facts" confirmed against `main`; three drift items flagged.

---

## Summary

This document maps where saga support plugs into `Wolverine.MongoDB`, confirms all "Verified
Saga API Facts" from the plan against the code on `main`, and flags any drift implementors
must know about before starting S6.

---

## 1. `MongoDbPersistenceFrameProvider.cs` — Required Changes for S6

**File:** `src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs`

### Current state (all confirmed against main)

| Member | Current behaviour |
|--------|------------------|
| `const SagaNotSupported` | `"Saga storage is not yet supported by Wolverine.MongoDB"` |
| `DetermineSagaIdType` | `throw new NotSupportedException(SagaNotSupported)` |
| `DetermineLoadFrame` | `throw new NotSupportedException(SagaNotSupported)` |
| `DetermineInsertFrame` | `throw new NotSupportedException(SagaNotSupported)` |
| `DetermineUpdateFrame` | `throw new NotSupportedException(SagaNotSupported)` |
| `DetermineStoreFrame` | `throw new NotSupportedException(SagaNotSupported)` |
| `DetermineDeleteFrame(sagaId, saga, c)` | `throw new NotSupportedException(SagaNotSupported)` |
| `CommitUnitOfWorkFrame` | `throw new NotSupportedException(SagaNotSupported)` |
| `CanPersist` | `persistenceService = typeof(IMongoDatabase); return false;` |
| `CanApply` | No `chain is SagaChain` check — only inspects handler params / service deps |
| `ApplyTransactionSupport` | Adds commit postprocessor **unconditionally** (no `SagaChain` guard) |

> **Note:** `CanPersist` does NOT throw — it returns `false`. The plan's "saga members throw"
> refers to the saga-specific codegen methods above, not to `CanPersist`.

### Three flips required (S6)

**Flip 1 — `CanApply`:** Add `if (chain is SagaChain) return true;` at the top of the method.
Without this, Wolverine never routes saga chains to this provider.

**Flip 2 — `CanPersist`:** Change `return false` to `return entityType.CanBeCastTo<Saga>()`.
Do **not** return `true` unconditionally — `DetermineStorageActionFrame` (for generic
`[Entity]`/`IStorageAction<T>` patterns) still throws, so claiming non-saga persistence
would blow up at codegen for any entity-sourced handler.

**Flip 3 — `ApplyTransactionSupport`:** Add the `if (chain is not SagaChain)` guard before
adding `CommitMongoTransactionFrame` as a postprocessor. Saga chains receive commit+flush
via `CommitUnitOfWorkFrame` — the unconditional postprocessor would double-commit them.

```csharp
// Current (line 17-27):
if (!chain.Middleware.OfType<TransactionalFrame>().Any())
{
    chain.Middleware.Add(new TransactionalFrame(chain));
    chain.Postprocessors.Add(new CommitMongoTransactionFrame());  // ← must gain SagaChain guard
}

// After S6:
if (!chain.Middleware.OfType<TransactionalFrame>().Any())
{
    chain.Middleware.Add(new TransactionalFrame(chain));
    if (chain is not SagaChain)   // saga chains get commit via CommitUnitOfWorkFrame
        chain.Postprocessors.Add(new CommitMongoTransactionFrame());
}
```

### Additional overloads in the provider (noted for completeness)

- `DetermineDeleteFrame(Variable variable, IServiceContainer container)` — single-variable
  overload for generic entity deletion. Still throws; NOT needed for saga support.
- `DetermineStorageActionFrame(Type, Variable, IServiceContainer)` — for `IStorageAction<T>`.
  Still throws; out of scope.
- `DetermineFrameToNullOutMaybeSoftDeleted(Variable entity)` — returns `[]` (no-op). Fine as-is.

---

## 2. `TransactionalFrame.cs` — Codegen Variables Available to Saga Frames

**File:** `src/Wolverine.MongoDB/Internals/TransactionalFrame.cs`

`TransactionalFrame` opens the session+transaction, builds `MongoDbUnitOfWork`, enlists the
outbox, and wraps the chain in `try/catch`. Confirmed behaviour:

### Variables created by `TransactionalFrame` (usable by downstream frames)

| Variable | Type | Name in generated code |
|----------|------|------------------------|
| `Session` | `IClientSessionHandle` | `mongoSession` |
| `UnitOfWork` | `MongoDbUnitOfWork` | `mongoUnitOfWork` |

`Session` and `UnitOfWork` are `Variable` instances with `this` frame as creator — any
downstream frame can call `chain.FindVariable(typeof(IClientSessionHandle))` or
`chain.FindVariable(typeof(MongoDbUnitOfWork))` and will resolve these created vars.

### Variables resolved from the container by `TransactionalFrame.FindVariables`

| Variable | Source |
|----------|--------|
| `IClientSessionHandle` (opens it, creates the variable) | — |
| `IMongoClient` | DI service |
| `IMongoDatabase` | DI service |
| `CancellationToken` | generated method |
| `IMessageContext` (optional, non-service scope) | generated method |

**Key point for S6 saga frames:** `LoadSagaFrame`, `StoreSagaFrame`, and `DeleteSagaFrame`
should call `chain.FindVariable(typeof(IClientSessionHandle))` and
`chain.FindVariable(typeof(IMongoDatabase))` in their `FindVariables` overrides to resolve
the session opened by `TransactionalFrame` and the DI-registered database. Both are already
in scope because `TransactionalFrame` is added to `chain.Middleware` by
`ApplyTransactionSupport` — which fires before the saga frames.

### `CommitMongoTransactionFrame` (postprocessor)

Commits the transaction (`CommitTransactionAsync`) then flushes outgoing messages
(`FlushOutgoingMessagesAsync`). Resolves `IClientSessionHandle` and `CancellationToken`.
For S6, `CommitUnitOfWorkFrame(saga, container)` should return `new CommitMongoTransactionFrame()`.

---

## 3. `WolverineMongoDbExtensions.cs` — Registration

**File:** `src/Wolverine.MongoDB/WolverineMongoDbExtensions.cs`

Confirmed (verbatim from main):

```csharp
options.Services.AddSingleton<IMessageStore>(sp => new MongoDbMessageStore(...));
options.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));   // → IMongoDatabase

options.CodeGeneration.InsertFirstPersistenceStrategy<MongoDbPersistenceFrameProvider>();
options.CodeGeneration.ReferenceAssembly(typeof(WolverineMongoDbExtensions).Assembly);
```

The `IMongoDatabase` registered here is the **app-facing** one — NOT pinned with majority
write/read concern (that pinning is only on `MongoDbMessageStore`'s internal `_database`).
Saga frames should use this DI-registered `IMongoDatabase` — the plan's intent is that
saga writes use the app's write concern, not the durability store's.

---

## 4. `MongoConstants.cs` — Collection-Name Constants

**File:** `src/Wolverine.MongoDB/Internals/MongoConstants.cs`

Current constants (all system collections):

```
IncomingCollection         = "wolverine_incoming_envelopes"
OutgoingCollection         = "wolverine_outgoing_envelopes"
DeadLetterCollection       = "wolverine_dead_letters"
NodeCollection             = "wolverine_nodes"
NodeAssignmentCollection   = "wolverine_node_assignments"
NodeRecordCollection       = "wolverine_node_records"
AgentRestrictionCollection = "wolverine_agent_restrictions"
CounterCollection          = "wolverine_counters"
LockCollection             = "wolverine_locks"

LeaderLockId      = "leader"
ScheduledLockId   = "scheduled-jobs"
NodeCounterId     = "node_number"
AnyNode           = 0
```

**Gap for S6:** No saga-related constant or naming helper exists. S6 must add a
`SagaCollectionName(Type sagaType)` helper (e.g. returning `"wolverine_saga_" +
sanitized-type-name`) and a prefix constant (e.g. `SagaCollectionPrefix = "wolverine_saga_"`)
so `ClearAllAsync` can enumerate and drop them.

---

## 5. `MongoDbMessageStore.Admin.cs` — Collection Cleanup Gap

**File:** `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Admin.cs`

### `RebuildAsync` / `ClearAllAsync` — confirmed flow

```csharp
public async Task RebuildAsync()
{
    await ClearAllAsync();
    await EnsureIndexesAsync();
}

public async Task ClearAllAsync()
{
    await Incoming.DeleteManyAsync(FilterDefinition<IncomingMessage>.Empty);
    await Outgoing.DeleteManyAsync(FilterDefinition<OutgoingMessage>.Empty);
    await DeadLetterDocs.DeleteManyAsync(FilterDefinition<DeadLetterMessage>.Empty);
    await _database.GetCollection<BsonDocument>(MongoConstants.NodeCollection).DeleteManyAsync(...);
    await _database.GetCollection<BsonDocument>(MongoConstants.NodeAssignmentCollection).DeleteManyAsync(...);
    await _database.GetCollection<BsonDocument>(MongoConstants.NodeRecordCollection).DeleteManyAsync(...);
    await _database.GetCollection<BsonDocument>(MongoConstants.AgentRestrictionCollection).DeleteManyAsync(...);
    await _database.GetCollection<BsonDocument>(MongoConstants.CounterCollection).DeleteManyAsync(...);
    await _database.GetCollection<BsonDocument>(MongoConstants.LockCollection).DeleteManyAsync(...);
}
```

### **Critical gap (S6 required change)**

`ClearAllAsync` clears only the nine known system collections **by name**. It does NOT
enumerate or drop per-saga-type collections (e.g. `wolverine_saga_OrderFulfillmentSaga`).

`AppFixture.ClearAll()` calls `store.Admin.RebuildAsync()` before every test suite run.
The shared fixture uses `DatabaseName = "wolverine_tests"` — a **fixed** database. If saga
compliance tests write to `wolverine_saga_StringBasicWorkflow` in run 1 and `ClearAllAsync`
doesn't clean it, run 2 starts with stale saga state and facts leak.

**Required S6 fix:** At minimum, enumerate collections in the database matching
`wolverine_saga_*` and either delete their documents or drop them in `ClearAllAsync`.
Dropping (rather than `DeleteMany`) is cleaner and removes the index too, avoiding index
accumulation. The simplest safe approach:

```csharp
// In ClearAllAsync, after clearing the known system collections:
var allCollections = await _database.ListCollectionNamesAsync();
await allCollections.ForEachAsync(async name =>
{
    if (name.StartsWith(MongoConstants.SagaCollectionPrefix))
        await _database.DropCollectionAsync(name);
});
```

`EnsureIndexesAsync` (called from `RebuildAsync` after `ClearAllAsync`) does not touch saga
collections today — that is correct; sagas should not have pre-declared indexes initially
(the `_id` index is automatic).

---

## 6. `AppFixture.cs` — Test Harness Members

**File:** `src/Wolverine.MongoDB.Tests/AppFixture.cs`

Confirmed members:

| Member | Value / type |
|--------|-------------|
| `DatabaseName` | `"wolverine_tests"` (const) |
| `Client` | `IMongoClient` (initialized after container start) |
| `ConnectionString` | `string` (property, same URI used by test hosts) |
| `BuildMessageStore()` | Returns `new MongoDbMessageStore(Client, DatabaseName, new WolverineOptions())` |
| `ClearAll()` | Calls `BuildMessageStore().Admin.RebuildAsync()` |
| `[CollectionDefinition("mongodb")]` | On `MongoDbCollection : ICollectionFixture<AppFixture>` |

Container: `MongoDbBuilder("mongo:7").WithReplicaSet()` — single-instance replica set,
started once per process (shared static, semaphore-guarded). Correct for compliance tests
that require transactions.

**Note for `MongoDbSagaHost` (S9):** The saga host will use `_fixture.Client` and
`AppFixture.DatabaseName` exactly like `message_store_compliance` does — confirmed pattern.
`ClearAll()` should be called in the host's setup to reset saga collections once S6 lands
the `ClearAllAsync` fix.

---

## 7. `message_store_compliance.cs` — Compliance Pattern

**File:** `src/Wolverine.MongoDB.Tests/message_store_compliance.cs`

```csharp
[Collection("mongodb")]
public class message_store_compliance : MessageStoreCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        await _fixture.ClearAll();
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.ListenAtPort(2345).UseDurableInbox();
            }).StartAsync();
    }
}
```

**Pattern for `MongoDbSagaHost` (S9):**

```csharp
public async Task<IHost> BuildHostAsync<TSaga>()
{
    await _fixture.ClearAll();          // must await ClearAll first
    return await Host.CreateDefaultBuilder()
        .UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
            opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;  // required for compliance
            opts.CodeGeneration.GeneratedCodeOutputPath = ...;      // see CosmosDbSagaHost
            opts.Discovery.IncludeType<TSaga>();
            opts.Discovery.IncludeAssembly(typeof(StringBasicWorkflow).Assembly);
        }).StartAsync();
}
```

---

## 8. Demo OCC Precedent — `OrderRepository.UpdateAsync`

**File:** `demo/src/OrderDemo.Infrastructure/Persistence/OrderRepository.cs`

This is the direct precedent for saga optimistic concurrency (plan S8).

```csharp
public async Task UpdateAsync(Order order, IClientSessionHandle session, CancellationToken ct)
{
    // Aggregate mutation methods already called Version++ before this.
    var result = await _orders.ReplaceOneAsync(
        session,
        Builders<Order>.Filter.And(
            Builders<Order>.Filter.Eq(o => o.Id, order.Id),
            Builders<Order>.Filter.Eq(o => o.Version, order.Version - 1)),  // ← old version
        order,
        cancellationToken: ct);

    if (result.ModifiedCount == 0)
        throw new InvalidOperationException("Optimistic concurrency conflict...");
}
```

**Demo pattern:** the aggregate increments `Version` (private setter) inside its mutation
method, so `UpdateAsync` filters on `Version - 1` to find the old document.

**Saga OCC pattern (plan S8) — slight difference:** `Saga.Version` has a public setter.
The saga frame will:
1. Capture `oldVersion = saga.Version` before the handler executes.
2. Post-handler, set `saga.Version = oldVersion + 1`.
3. `ReplaceOneAsync` with filter `(_id, oldVersion)`, `IsUpsert = false`.
4. Throw `SagaConcurrencyException` if `ModifiedCount == 0`.

This is semantically identical to the demo pattern, adapted to the public-setter model.
The demo uses `InvalidOperationException`; the saga frame must use the Wolverine-native
`SagaConcurrencyException` (lives in `Saga.cs`).

---

## 9. `PlaceOrderHandler.cs` — Repository Session Pattern

**File:** `demo/src/OrderDemo.Application/Orders/PlaceOrderHandler.cs`

Confirms the `IClientSessionHandle` threading convention:

```csharp
public static async Task<OrderPlacedApplicationEvent> Handle(
    PlaceOrderCommand cmd,
    IClientSessionHandle session,   // ← injected by Wolverine's generated TransactionalFrame
    IOrderRepository orders,
    IInventoryRepository inventory,
    CancellationToken ct)
{
    await orders.AddAsync(order, session, ct);      // session-bound write
    await inventory.UpdateAsync(product, session, ct); // same session
    return new OrderPlacedApplicationEvent(...);    // cascaded to outbox atomically
}
```

Saga frames will follow the same pattern, threading the session through collection
operations — but unlike the repository pattern, the session will be threaded into the
codegen frame's `FindVariables`, not hand-passed by the developer.

---

## 10. Cross-Check: "Verified Saga API Facts" from the Plan

Each fact from the plan's "Verified Saga API Facts (local)" section checked against `main`:

| # | Plan fact | Verified? | Notes |
|---|-----------|-----------|-------|
| 1 | `SagaNotSupported` const → throws in all saga members | ✅ Confirmed | Lines 12-13 and all saga methods. `CanPersist` returns `false` (does not throw — this is correct). |
| 2 | `CanApply` matches `IClientSessionHandle`/`MongoDbUnitOfWork` params + `IMongoDatabase`/`IMongoClient`/`IMongoCollection<>` deps | ✅ Confirmed | Lines 34-53 exactly. |
| 3 | `CanApply` does **not** match `chain is SagaChain` | ✅ Confirmed | Required Flip 1 for S6. |
| 4 | `TransactionalFrame` opens `IClientSessionHandle` (`mongoSession`), builds `MongoDbUnitOfWork` | ✅ Confirmed | Lines 35-39. |
| 5 | `TransactionalFrame` enlists the outbox, wraps in `try/catch` | ✅ Confirmed | Lines 80-96. |
| 6 | `CommitMongoTransactionFrame` commits then flushes | ✅ Confirmed | Lines 134-146. |
| 7 | `IClientSessionHandle` and `IMongoDatabase` resolvable codegen variables | ✅ Confirmed | `Session` created var; `_database` found via `FindVariables`. |
| 8 | `WolverineMongoDbExtensions` registers `IMessageStore`, `IMongoDatabase`, `InsertFirstPersistenceStrategy`, `ReferenceAssembly` | ✅ Confirmed | Lines 51-63. |
| 9 | `MongoConstants.cs` — collection-name constants live here | ✅ Confirmed | 9 system collections + 3 string/int constants. |
| 10 | `MongoDbMessageStore.Admin.cs` — `RebuildAsync()` (used by `AppFixture.ClearAll()`) | ✅ Confirmed | `RebuildAsync → ClearAllAsync + EnsureIndexesAsync`. |
| 11 | `AppFixture`: `[CollectionDefinition("mongodb")]`, `DatabaseName = "wolverine_tests"`, `Client`, `ClearAll()`, `BuildMessageStore()` | ✅ Confirmed | All present. |
| 12 | `AppFixture`: `mongo:7` replica-set Testcontainer | ✅ Confirmed | Line 23-25. |
| 13 | `OrderRepository.UpdateAsync` — Version-guarded `ReplaceOneAsync`, throws on `ModifiedCount == 0` | ✅ Confirmed | Lines 25-41. Filter on `(Id, Version - 1)`. |

---

## 11. Drift & Implementation Flags

Three items diverge from the plan's framing or add precision implementors must know:

### Drift 1 — `MongoConstants.cs` has no saga-related entries yet

The plan says "collection-name constants live here" — correct, but it implies the naming
helper is **present**. It is **not yet written**. S6 must add:
- `public const string SagaCollectionPrefix = "wolverine_saga_"` (for enumeration in `ClearAllAsync`)
- `public static string SagaCollectionName(Type sagaType) => $"{SagaCollectionPrefix}{SagaTypeName(sagaType)}"`
- A `private static string SagaTypeName(Type t)` sanitizer (e.g. strip namespace, lowercase, collapse generics).

### Drift 2 — `ClearAllAsync` does NOT drop per-saga collections

The plan correctly identifies this as a required S6 change (§ "Required" in the File
Structure Overview). The code confirms it: `ClearAllAsync` enumerates only the nine named
system collections. Any `wolverine_saga_*` collection written during a test run persists
into the next run, which will fail compliance facts that assert a fresh start (e.g. `start_1`
expecting no pre-existing saga state). This cleanup **must land in S6** alongside the frame
implementation.

### Drift 3 — Demo OCC pattern vs. Saga OCC pattern (public vs. private setter)

The plan maps the saga OCC directly to `OrderRepository.UpdateAsync`. This is accurate at
a high level. The codegen difference: `Order.Version` has `private set` (incremented inside
aggregate methods before `UpdateAsync` is called), while `Saga.Version` has `public set`
(so the frame itself must capture old + increment before the replace). The frame logic
must capture `int oldVersion = saga.Version;` before the post-handler write, then set
`saga.Version = oldVersion + 1;` before calling `ReplaceOneAsync` with filter `oldVersion`.

---

## 12. Files Saga Implementation Will Touch (S6–S8)

| File | What changes |
|------|-------------|
| `Internals/MongoDbPersistenceFrameProvider.cs` | Flip 1 (`CanApply`), Flip 2 (`CanPersist`), Flip 3 (`ApplyTransactionSupport`); implement all saga frame methods |
| **New** `Internals/SagaFrames.cs` | `LoadSagaFrame`, `StoreSagaFrame` (insert + update), `DeleteSagaFrame` |
| `Internals/MongoConstants.cs` | `SagaCollectionPrefix`, `SagaCollectionName(Type)` helper |
| `Internals/MongoDbMessageStore.Admin.cs` | `ClearAllAsync` — enumerate + drop `wolverine_saga_*` collections |
| **New** `src/Wolverine.MongoDB.Tests/MongoDbSagaHost.cs` | `ISagaHost` impl (S9) |
| **New** `src/Wolverine.MongoDB.Tests/string_saga_storage_compliance.cs` | compliance subclass (S9) |
