# Task D1 — Tier 1: Entity/Storage-Action API + Cosmos/Raven Reference

**Branch:** `docs/entity-persistence-discovery`  
**Status:** Complete — all Verified API Facts confirmed against pinned `external/wolverine` submodule (V6.9.0).  
**Produced by:** Task D1 (discovery, read-only). Feeds Task D6 (design gate).

---

## 1. `IPersistenceFrameProvider` — Full Tier-1 Contract

**File:** `external/wolverine/src/Wolverine/Persistence/IPersistenceFrameProvider.cs`

All line numbers confirmed exact against V6.9.0.

```csharp
// :23 — Tier-1 entry point for [Entity] loads and IStorageAction<T> routing.
bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService);

// :25 — Determines the saga's native id member type (Guid/string/int/long).
//        Also called by [Entity] load path (EntityAttribute.cs:153) to resolve the entity's id type.
Type DetermineSagaIdType(Type sagaType, IServiceContainer container);

// :26 — Called by EntityAttribute.Modify(:165) for [Entity] loads.
//        Parameter name "sagaType" is misleading — core passes the *entity* type here too.
Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId);

// :27 — Called by Insert<T>.BuildFrame(:26) → TYPED factory.
Frame DetermineInsertFrame(Variable saga, IServiceContainer container);

// :28 — Called by SagaChain's CommitUnitOfWorkFrame path (saga-specific; not Tier-1 entity path).
Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container);

// :29 — Called by Update<T>.BuildFrame(:26) → TYPED factory.
Frame DetermineUpdateFrame(Variable saga, IServiceContainer container);

// :30 — Saga-specific two-variable delete: Delete<T> does NOT call this.
Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container);

// :39 — Called by Store<T>.BuildFrame(:26) → TYPED factory.
Frame DetermineStoreFrame(Variable saga, IServiceContainer container);

// :48 — Generic single-variable delete: Delete<T>.BuildFrame(:26) calls THIS overload.
//        Mongo currently throws NotSupportedException here.
Frame DetermineDeleteFrame(Variable variable, IServiceContainer container);

// :50 — Called by IStorageAction<T>.BuildFrame(:27) and UnitOfWork<T>.BuildFrame(:96) → generic path.
//        Mongo currently throws NotSupportedException here.
Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container);

// :52 — Called by EntityAttribute only when MaybeSoftDeleted == false.
//        Cosmos, RavenDb, Mongo all return [].
Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity);

// :73-82 — Default implementation returns false (Tier 3 / Marten+EF-only).
bool TryBuildFetchSpecificationFrame(Variable specVariable, IServiceContainer container,
    out Frame? frame, out Variable? result) { frame = null; result = null; return false; }
```

---

## 2. `[Entity]` Parameter-Loading Path

**File:** `external/wolverine/src/Wolverine/Persistence/EntityAttribute.cs`

```
:145  rules.TryFindPersistenceFrameProvider(container, parameter.ParameterType, out var provider)
         ↳ calls CanPersist(parameter.ParameterType, ...) to select the provider
:153  var idType = provider.DetermineSagaIdType(parameter.ParameterType, container)
         ↳ "DetermineSagaIdType" serves double duty as entity-id-type resolver
:165  var frame = provider.DetermineLoadFrame(container, parameter.ParameterType, identity)
         ↳ identity is the id variable resolved from the message
:170  if (MaybeSoftDeleted is false)
:172      var softDeleteFrames = provider.DetermineFrameToNullOutMaybeSoftDeleted(entity)
:173      chain.Middleware.AddRange(softDeleteFrames)
         ↳ Cosmos/RavenDb/Mongo all return [] here; Marten-only feature.
```

**[Entity] not-found behavior:** Entirely managed by core (`:176-190`). When `Required == true` core
wraps the load frame in a `LoadEntityFrameBlock` that null-guards and short-circuits before the
handler. The provider's load frame just returns `null` when the document is not found; no provider
work needed for the "do not execute the handler if the entity is not found" compliance fact.

---

## 3. `IStorageAction<T>` Side-Effect Routing

**Files:** `external/wolverine/src/Wolverine/Persistence/IStorageAction.cs`, `Insert.cs`, `Delete.cs`, `Update.cs`, `Store.cs`

### 3.1 Routing Table

| Return type | File | `.BuildFrame` line | Provider method called | Overload |
|---|---|---|---|---|
| `Insert<T>` | `Insert.cs` | `:26` | `DetermineInsertFrame(EntityVariable(variable), container)` | Typed factory |
| `Update<T>` | `Update.cs` | `:26` | `DetermineUpdateFrame(EntityVariable(variable), container)` | Typed factory |
| `Store<T>` | `Store.cs` | `:26` | `DetermineStoreFrame(EntityVariable(variable), container)` | Typed factory |
| `Delete<T>` | `Delete.cs` | `:26` | `DetermineDeleteFrame(EntityVariable(variable), container)` | **Single-variable** (generic) |
| `IStorageAction<T>` | `IStorageAction.cs` | `:27` | `DetermineStorageActionFrame(typeof(T), EntityVariable(variable), container)` | Generic |
| `UnitOfWork<T>` | `IStorageAction.cs` | `:96` | `DetermineStorageActionFrame(typeof(T), element, container)` | Generic (foreach loop) |

### 3.2 Key observations

- **`Delete<T>` uses the single-variable overload** (`:48`), NOT the two-variable saga overload (`:30`).
  The saga delete is only reached from `SagaChain` directly (with sagaId + saga variables).
- **`Insert<T>`/`Update<T>`/`Store<T>` call the TYPED factories** — `DetermineInsertFrame`,
  `DetermineUpdateFrame`, `DetermineStoreFrame` — NOT `DetermineStorageActionFrame`.
- **`IStorageAction<T>` and `UnitOfWork<T>` call `DetermineStorageActionFrame`** — the generic path.
- Every path calls `provider.ApplyTransactionSupport(chain, container, typeof(T))` first
  (`Insert.cs:24`, `IStorageAction.cs:25`) before calling the frame factory.
- `.WrapIfNotNull(variable)` is appended to EVERY frame — core handles the "null → no-op" behavior.

### 3.3 `StorageAction` enum (`:3-29`, `StorageAction.cs`)

```csharp
Store, Delete, Nothing, Update, Insert
```

`Nothing` is the no-op case — the applier must handle it without error.

---

## 4. `StorageActionCompliance` — Acceptance Oracle

**File:** `external/wolverine/src/Testing/Wolverine.ComplianceTests/StorageActionCompliance.cs`

```csharp
// :9
public abstract class StorageActionCompliance : IAsyncLifetime

// :13-26 — InitializeAsync builds the host; providers add themselves here via configureWolverine.
//            Handlers included: TodoHandler, MarkTaskCompleteIfBrokenHandler, ExamineFirstHandler, StoreManyHandler.

// :35 — Provider supplies everything here: UseXxxPersistence, AutoApplyTransactions, DurabilityMode.Solo, etc.
protected abstract void configureWolverine(WolverineOptions opts);

// :52 — Reads the entity DIRECTLY from the store (bypasses Wolverine) to verify persistence.
//         Must target the SAME collection/table the frames write to.
public abstract Task<Todo?> Load(string id);

// :54 — Writes the entity DIRECTLY to the store (for test setup).
public abstract Task Persist(Todo todo);
```

### 4.1 Entity POCO

```csharp
// :272-277
public class Todo
{
    public string Id { get; set; } = null!;
    public string? Name { get; set; }
    public bool IsComplete { get; set; }
}
```

No `[BsonId]` or MongoDB annotations. The driver's default convention maps the `Id` property to `_id`.
**The `Load(string id)` and `Persist(Todo todo)` MUST target the same collection the frames write to.**

### 4.2 The 17 Compliance Facts

| # | Test name | What it proves |
|---|---|---|
| 1 | `use_insert_as_return_value` | `Insert<Todo>` return persists; no spurious cascade routing |
| 2 | `use_entity_attribute_with_id` | `[Entity] Todo` resolved by message member `Id` |
| 3 | `use_entity_attribute_with_entity_id` | `[Entity] Todo` resolved by message member `TodoId` |
| 4 | `use_entity_attribute_with_explicit_id` | `[Entity("Identity")] Todo` resolved by explicit member name |
| 5 | `use_delete_as_return_value` | `Delete<Todo>` return removes the document |
| 6 | `use_generic_action_as_insert` | `IStorageAction<Todo>` with `StorageAction.Insert` persists |
| 7 | `use_generic_action_as_delete` | `IStorageAction<Todo>` with `StorageAction.Delete` removes |
| 8 | `use_generic_action_as_update` | `IStorageAction<Todo>` with `StorageAction.Update` updates |
| 9 | `use_generic_action_as_store` | `IStorageAction<Todo>` with `StorageAction.Store` upserts |
| 10 | `do_nothing_as_generic_action` | `IStorageAction<Todo>` with `StorageAction.Nothing` leaves doc unchanged |
| 11 | `do_nothing_if_storage_action_is_null` | Null `Insert<Todo>?` return → no error, no-op |
| 12 | `do_nothing_if_generic_storage_action_is_null` | Null `IStorageAction<Todo>?` return → no error, no-op |
| 13 | `do_not_execute_the_handler_if_the_entity_is_not_found` | Missing `[Entity]` with `Required = true` → handler not called |
| 14 | `handler_not_required_entity_attributes` | Missing `[Entity(Required = false)]` → handler called, entity null |
| 15 | `entity_can_be_used_in_before_methods_implied_from_main_handler_method` | `[Entity]` from main handler method is available in `Before(Todo todo)` |
| 16 | `can_use_attribute_on_before_methods` | `[Entity]` on a `Before` method stops the chain when not found |
| 17 | `use_unit_of_work_as_return_value` | `UnitOfWork<Todo>` with multiple `Insert` actions persists all |

### 4.3 Who Subclasses `StorageActionCompliance`

Confirmed by grep across the submodule:

| Provider | File | Subclasses? |
|---|---|---|
| **RavenDb** | `Persistence/RavenDbTests/using_storage_return_types_and_entity_attributes.cs:10` | ✅ Yes |
| **Marten** | `Persistence/MartenTests/using_storage_return_types_and_entity_attributes.cs` | ✅ Yes |
| **EF Core** | `Persistence/EfCoreTests/using_storage_return_types_and_entity_attributes.cs` | ✅ Yes |
| **Polecat** | `Persistence/PolecatTests/using_storage_return_types_and_entity_attributes.cs` | ✅ Yes |
| **Cosmos** | `Persistence/CosmosDbTests/using_storage_return_types_and_entity_attributes.cs:11` | ❌ **NO** |

**OQ1 RESOLVED — Cosmos does NOT subclass `StorageActionCompliance`.** See §5 for details.

---

## 5. OQ1 Resolution: Does Cosmos Subclass `StorageActionCompliance`?

**Answer: NO.**

`CosmosDbTests/using_storage_return_types_and_entity_attributes.cs` (line 11) declares:

```csharp
[Collection("cosmosdb")]
public class using_storage_return_types_and_entity_attributes  // NO base class
```

It contains a single `[Fact]` test (`can_use_cosmosdb_ops_as_side_effects`) that:
1. Builds a fresh host via `Host.CreateDefaultBuilder().UseWolverine(...)`.
2. Invokes a `CreateDocumentHandler` that returns `ICosmosDbOp` (Cosmos-specific side-effect type).
3. Asserts only that the handler executed without error.

This is NOT the generic `IStorageAction<T>` / `Insert<T>` / `Delete<T>` contract. Cosmos has a
parallel, Cosmos-specific side-effect surface (`ICosmosDbOp` / `CosmosDbOps.Store(...)`) that routes
through `ICosmosDbOutbox` and is NOT part of `StorageActionCompliance`. Cosmos also implements the
generic `DetermineInsertFrame`, `DetermineStorageActionFrame`, etc. in `CosmosDbPersistenceFrameProvider`,
but those are tested ad-hoc, not via the compliance suite.

**Implication for Mongo:** The RavenDb subclass is the cleanest tested template for T1.1.
`using_storage_return_types_and_entity_attributes.cs` in RavenDbTests (`RavenDbTests/:10`)
is 45 lines. Its `configureWolverine`, `Load`, and `Persist` are the exact methods to mirror.

---

## 6. Cosmos Reference Implementation

**File:** `external/wolverine/src/Persistence/Wolverine.CosmosDb/Internals/CosmosDbPersistenceFrameProvider.cs`

### 6.1 `CanPersist` (`:51-55`)

```csharp
public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
{
    persistenceService = typeof(Container);
    return true;  // Unconditional — no type filter
}
```

### 6.2 Frame Factories

| Method | Line | Returns |
|---|---|---|
| `DetermineInsertFrame` | `:67-70` | `new CosmosDbUpsertFrame(saga)` — **entity-generic, no OCC** |
| `DetermineUpdateFrame` | `:78-81` | `new CosmosDbUpsertFrame(saga)` — same as insert |
| `DetermineStoreFrame` | `:88-91` | delegates to `DetermineUpdateFrame` → `CosmosDbUpsertFrame` |
| `DetermineLoadFrame` | `:62-65` | `new LoadDocumentFrame(sagaType, sagaId)` |
| `DetermineDeleteFrame(sagaId, saga, ...)` | `:83-86` | `new CosmosDbDeleteDocumentFrame(sagaId, saga)` |
| `DetermineDeleteFrame(variable, ...)` | `:93-96` | `new CosmosDbDeleteByVariableFrame(variable)` — generic single-variable |
| `DetermineStorageActionFrame` | `:98-107` | `MethodCall` to `CosmosDbStorageActionApplier.ApplyAction<T>` |
| `DetermineFrameToNullOutMaybeSoftDeleted` | `:17` | `[]` |

### 6.3 `CosmosDbUpsertFrame` (`:140-163`)

```csharp
internal class CosmosDbUpsertFrame : AsyncFrame
{
    // FindVariables: chain.FindVariable(typeof(Container))
    // GenerateCode: await {container}.UpsertItemAsync({document}).ConfigureAwait(false);
}
```

### 6.4 `CosmosDbDeleteByVariableFrame` (`:193-216`)

```csharp
// GenerateCode: await {container}.DeleteItemAsync<T>({variable}.ToString(), PartitionKey.None)...
// NOTE: uses entity.ToString() as the id — a CosmosDb-specific convention, NOT idiomatic Mongo.
```

### 6.5 `CosmosDbStorageActionApplier` (`:110-138`)

```csharp
public static async Task ApplyAction<T>(Container container, IStorageAction<T> action)
{
    if (action.Entity == null) return;
    switch (action.Action)
    {
        case StorageAction.Delete:
            var deleteId = action.Entity!.ToString()!;  // ToString() as id — Cosmos convention
            try { await container.DeleteItemAsync<T>(deleteId, PartitionKey.None); }
            catch (CosmosException) { /* Best effort */ }
            break;
        case StorageAction.Insert: case StorageAction.Store: case StorageAction.Update:
            await container.UpsertItemAsync(action.Entity);
            break;
        // Nothing falls through (no-op)
    }
}
```

Provider sets `call.Arguments[1] = action` — argument slot 0 is `Container` (auto-resolved by codegen).

### 6.6 `ApplyTransactionSupport` (`:19-35`)

```csharp
// Adds TransactionalFrame; adds FlushOutgoingMessages postprocessor only if (chain is not SagaChain).
// Mongo already mirrors this exactly (MongoDbPersistenceFrameProvider.cs:34-37).
```

---

## 7. RavenDb Reference Implementation

**File:** `external/wolverine/src/Persistence/Wolverine.RavenDb/Internals/RavenDbPersistenceFrameProvider.cs`

### 7.1 `CanPersist` (`:56-60`)

```csharp
public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
{
    persistenceService = typeof(IAsyncDocumentSession);
    return true;  // Unconditional
}
```

### 7.2 Frame Factories

| Method | Line | Returns |
|---|---|---|
| `DetermineInsertFrame` | `:72-77` | `MethodCall` to `IAsyncDocumentSession.StoreAsync(null, default)` with `call.Arguments[0] = saga` |
| `DetermineUpdateFrame` | `:86-92` | Same `StoreAsync` call — no OCC distinction from insert |
| `DetermineStoreFrame` | `:99-102` | delegates to `DetermineUpdateFrame` |
| `DetermineLoadFrame` | `:67-70` | `new LoadDocumentFrame(sagaType, sagaId)` |
| `DetermineDeleteFrame(sagaId, saga, ...)` | `:94-97` | `new DeleteDocumentFrame(saga)` |
| `DetermineDeleteFrame(variable, ...)` | `:104-107` | `new DeleteDocumentFrame(variable)` — same frame, single variable |
| `DetermineStorageActionFrame` | `:115-124` | `MethodCall` to `RavenDbStorageActionApplier.ApplyAction<T>` |
| `DetermineFrameToNullOutMaybeSoftDeleted` | `:18` | `[]` |

### 7.3 `RavenDbStorageActionApplier` (`:127-145`)

```csharp
public static async Task ApplyAction<T>(IAsyncDocumentSession session, IStorageAction<T> action)
{
    if (action.Entity == null) return;
    switch (action.Action)
    {
        case StorageAction.Delete: session.Delete(action.Entity!); break;
        case StorageAction.Insert: case StorageAction.Store: case StorageAction.Update:
            await session.StoreAsync(action.Entity); break;
        // Nothing falls through (no-op)
    }
}
```

Provider sets `call.Arguments[1] = action` — argument slot 0 is `IAsyncDocumentSession` (auto-resolved).

### 7.4 `DeleteDocumentFrame` (`:148-170`)

```csharp
internal class DeleteDocumentFrame : SyncFrame
{
    // FindVariables: chain.FindVariable(typeof(IAsyncDocumentSession))
    // GenerateCode: {session}.Delete({saga});
}
```

### 7.5 `[UnconditionalSuppressMessage]` on `DetermineStorageActionFrame` (`:112-114`)

RavenDb uses `[UnconditionalSuppressMessage("AOT", "IL3050", ...)]` on `DetermineStorageActionFrame`
because `MakeGenericMethod` is AOT-unsafe. Mongo MUST apply the same annotation on its equivalent.

### 7.6 RavenDb Compliance Subclass (`:1-46`, `RavenDbTests/using_storage_return_types_and_entity_attributes.cs`)

```csharp
[Collection("raven")]
public class using_storage_return_types_and_entity_attributes : StorageActionCompliance
{
    protected override void configureWolverine(WolverineOptions opts)
    {
        var store = _fixture.StartRavenStore();
        opts.UseRavenDbPersistence();
        opts.Services.AddSingleton(store);
        opts.Policies.AutoApplyTransactions();
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.CodeGeneration.ReferenceAssembly(typeof(IRavenDbOp).Assembly);
    }

    public override async Task<Todo?> Load(string id) { /* session.LoadAsync<Todo>(id) */ }
    public override async Task Persist(Todo todo) { /* session.StoreAsync(todo) + SaveChangesAsync() */ }
}
```

This is the exact template for `storage_action_compliance : StorageActionCompliance` in T1.1.

---

## 8. Local `MongoDbPersistenceFrameProvider` — Current State

**File:** `src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs`

| Member | Lines | Current behavior |
|---|---|---|
| `ApplyTransactionSupport` | `:19-39` | Adds `TransactionalFrame`; skips `CommitMongoTransactionFrame` for `SagaChain` |
| `CanPersist` | `:74-83` | Returns `entityType.CanBeCastTo<Saga>()` — **saga-scoped only** |
| `DetermineSagaIdType` | `:93-96` | `SagaChain.DetermineSagaIdMember` (native Guid/int/long/string) |
| `DetermineLoadFrame` | `:98-99` | Returns `new LoadSagaFrame(sagaType, sagaId)` — saga-specific |
| `DetermineInsertFrame` | `:107-108` | Returns `new InsertSagaFrame(saga)` — stamps `Version = 1` |
| `CommitUnitOfWorkFrame` | `:110-111` | Returns `new CommitMongoTransactionFrame()` |
| `DetermineUpdateFrame` | `:113-114` | Returns `new UpdateSagaFrame(saga)` — OCC by version |
| `DetermineDeleteFrame(sagaId, saga, ...)` | `:116-117` | Returns `new DeleteSagaFrame(sagaId, saga)` |
| `DetermineStoreFrame` | `:122-123` | Delegates to `DetermineUpdateFrame` |
| `DetermineDeleteFrame(variable, ...)` | `:125-128` | **`throw new NotSupportedException(...)`** |
| `DetermineStorageActionFrame` | `:130-133` | **`throw new NotSupportedException(...)`** |
| `DetermineFrameToNullOutMaybeSoftDeleted` | `:135-138` | Returns `[]` (matches Cosmos/RavenDb) |

---

## 9. Local `SagaFrames.cs` — Template for Entity Frames

**File:** `src/Wolverine.MongoDB/Internals/SagaFrames.cs`

The saga frames are the exact structural template for entity frames. Key pattern:

```csharp
internal class LoadSagaFrame : AsyncFrame
{
    // 1. FindVariables: resolves IClientSessionHandle, IMongoDatabase, CancellationToken
    // 2. GenerateCode: emits a call to MongoSagaOperations.LoadSagaAsync<T, TId>(...)
}
```

Static helper `MongoSagaOperations` centralizes driver calls. `MongoEntityOperations` (T1.1) will mirror this.

**Key difference from entity frames:**
- `InsertSagaFrame`: stamps `saga.Version = 1` via `MongoSagaOperations.InsertSagaAsync` (`:52-59`) — SAGA-SPECIFIC.
- `UpdateSagaFrame`: OCC via version filter in `MongoSagaOperations.UpdateSagaAsync` (`:74-100`) — SAGA-SPECIFIC.
- Entity frames will NOT do either of these — upsert replaces both.

---

## 10. Local `MongoConstants.cs` — Current State

**File:** `src/Wolverine.MongoDB/Internals/MongoConstants.cs`

```csharp
public const string SagaCollectionPrefix = "wolverine_saga_";
public static string SagaCollectionName(Type sagaType)
    => $"{SagaCollectionPrefix}{sagaType.Name.ToLowerInvariant()}";
```

`EntityCollectionName(Type)` does NOT yet exist. T1.1 adds it (recommendation: un-prefixed,
e.g., `type.Name.ToLowerInvariant()` → `"todo"` for the compliance `Todo` entity).

---

## 11. Design Tension — THE Required Branch Point

> **This is the central design tension for T1.1, flagged as required by D1.**

Mongo's `DetermineInsertFrame` and `DetermineUpdateFrame` are **saga-specific**:
- `DetermineInsertFrame` → `InsertSagaFrame` — stamps `Saga.Version = 1`
- `DetermineUpdateFrame` → `UpdateSagaFrame` — OCC by version, throws `SagaConcurrencyException`

Cosmos's and RavenDb's equivalents are **entity-generic** (no saga knowledge):
- Cosmos: all three (`Insert`/`Update`/`Store`) → `CosmosDbUpsertFrame` (a plain `UpsertItemAsync`)
- RavenDb: all three → `StoreAsync` (no version check)

**Consequence:** When T1.1 broadens `CanPersist` to unconditional `true`, handlers returning
`Insert<MyEntity>` or `Update<MyEntity>` will route to `DetermineInsertFrame`/`DetermineUpdateFrame` —
which currently emit saga-specific frames. A plain entity has no `Saga.Version`, so this will:
1. Fail to compile/run (`InsertSagaFrame` calls `MongoSagaOperations.InsertSagaAsync<TSaga>` where
   `TSaga : Saga` — a plain entity does not satisfy the constraint).
2. Even if it compiled, it would stamp `Version = 1` and apply OCC on entity writes — wrong behavior.

**Required fix in T1.1:** Branch on `variable.VariableType.CanBeCastTo<Saga>()` in each factory:

```csharp
public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    => saga.VariableType.CanBeCastTo<Saga>() ? new InsertSagaFrame(saga) : new MongoUpsertEntityFrame(saga);

public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    => saga.VariableType.CanBeCastTo<Saga>() ? new UpdateSagaFrame(saga) : new MongoUpsertEntityFrame(saga);

public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    => saga.VariableType.CanBeCastTo<Saga>() ? DetermineUpdateFrame(saga, container) : new MongoUpsertEntityFrame(saga);

public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    => sagaType.CanBeCastTo<Saga>() ? new LoadSagaFrame(sagaType, sagaId) : new LoadEntityFrame(sagaType, sagaId);

// These two are entity-only (sagas never reach them):
public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    => new MongoDeleteEntityByVariableFrame(variable);

public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    => /* MethodCall to MongoEntityOperations.ApplyStorageActionAsync<T> */;
```

This is NOT required in Cosmos or RavenDb because those providers never had saga-specific OCC in
their frame factories (both use last-write-wins upsert for all entity types including sagas).
**Mongo is the only provider that must branch.**

---

## 12. `WolverineMongoDbExtensions.cs` — Registration

**File:** `src/Wolverine.MongoDB/WolverineMongoDbExtensions.cs`

```
:51-56  IMessageStore registered as singleton (MongoDbMessageStore)
:59-60  IMongoDatabase registered as unkeyed singleton
            ↳ FOLLOWUPS: conflicts with an app that registers its own IMongoDatabase (T4.3)
:62     InsertFirstPersistenceStrategy<MongoDbPersistenceFrameProvider>()
:63     ReferenceAssembly(typeof(WolverineMongoDbExtensions).Assembly)
```

The `InsertFirstPersistenceStrategy` registration ensures Mongo's provider is consulted first in
apps that might have multiple providers in the pipeline — exactly like Cosmos/RavenDb apps.

---

## 13. Verified API Facts — Drift Report

All Verified API Facts from `2026-06-21-persistence-suite-completion.md` confirmed against V6.9.0.
No drift found. Specific confirmations:

| Plan claim | Confirmed |
|---|---|
| `IPersistenceFrameProvider.cs` all line numbers (`:23,:25-30,:39,:48,:50,:52,:73-82`) | ✅ Exact |
| `EntityAttribute.cs` `:145,:165,:170-173` | ✅ Exact |
| `IStorageAction.cs:16-31` interface + `:27` calls `DetermineStorageActionFrame` + `:39` `UnitOfWork<T>` | ✅ Exact |
| `Insert.cs:26` → `DetermineInsertFrame` | ✅ Exact |
| `Delete.cs:26` → `DetermineDeleteFrame(variable, container)` single-variable | ✅ Exact |
| `StorageAction.cs:3-29` enum (Store/Delete/Nothing/Update/Insert) | ✅ Exact |
| `StorageActionCompliance.cs:9` abstract class + `:35` configureWolverine + `:52,:54` Load/Persist | ✅ Exact |
| 17 compliance facts | ✅ Confirmed — exact names listed in §4.2 |
| Cosmos `CanPersist` `:51-55` unconditional | ✅ Exact |
| Cosmos `DetermineInsertFrame`/`DetermineUpdateFrame`/`DetermineStoreFrame` all → `CosmosDbUpsertFrame` | ✅ Exact |
| Cosmos `CosmosDbDeleteByVariableFrame` `:193-216` | ✅ Exact (`:193-216`) |
| Cosmos `DetermineStorageActionFrame` `:98-107` | ✅ Exact |
| Cosmos `CosmosDbStorageActionApplier` `:110-138` | ✅ Exact |
| RavenDb `CanPersist` `:56-60` unconditional | ✅ Exact |
| RavenDb `DetermineStorageActionFrame` `:115-124` | ✅ Exact |
| RavenDb `DetermineDeleteFrame(variable, ...)` `:104-107` → `DeleteDocumentFrame` | ✅ Exact |
| RavenDb `RavenDbStorageActionApplier` `:127-145` | ✅ Exact |
| RavenDb `DeleteDocumentFrame` `:148-170` | ✅ Exact |
| MongoDb `CanPersist` `:74-83` saga-scoped | ✅ Exact |
| MongoDb `DetermineDeleteFrame(Variable,...)` throws `:125-128` | ✅ Exact |
| MongoDb `DetermineStorageActionFrame` throws `:130-133` | ✅ Exact |
| MongoDb `DetermineFrameToNullOutMaybeSoftDeleted` returns `[]` `:135-138` | ✅ Exact |
| MongoDb saga frame factories at `:98-123` | ✅ Exact |
| MongoDb `ApplyTransactionSupport` `SagaChain` guard `:34-37` | ✅ Exact |
| **OQ1: CosmosDbTests subclasses StorageActionCompliance** | ✅ **Resolved: NO** (§5) |

### One minor addition to the Verified Facts

The plan's interface signature list omits `CommitUnitOfWorkFrame` (`:28`):

```csharp
Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container);  // :28
```

This is not a Tier-1 entity concern (it is saga-chain-specific), but it IS in the interface and
Mongo already implements it correctly (returns `new CommitMongoTransactionFrame()`). Listed here for
completeness; no action needed in T1.1.

---

## 14. Summary for D6 / T1.1

The Tier-1 implementation requires:

1. **Broaden `CanPersist`** — unconditional `true` (matching Cosmos/RavenDb). Required for `[Entity]` loads.

2. **Branch the four typed factories** on `CanBeCastTo<Saga>()`:
   - Saga path: existing frames, untouched.
   - Entity path: new `MongoUpsertEntityFrame` (upsert) + `LoadEntityFrame` (null-returning load).

3. **Implement `DetermineDeleteFrame(Variable, IServiceContainer)`** → `MongoDeleteEntityByVariableFrame`
   (mirrors `DeleteDocumentFrame` in RavenDb).

4. **Implement `DetermineStorageActionFrame`** → `MethodCall` to `MongoEntityOperations.ApplyStorageActionAsync<T>`
   (mirrors `CosmosDbStorageActionApplier.ApplyAction<T>`). Apply `[UnconditionalSuppressMessage("AOT", "IL3050")]`.

5. **Add `MongoConstants.EntityCollectionName(Type)`** — un-prefixed: `type.Name.ToLowerInvariant()`.

6. **`DetermineFrameToNullOutMaybeSoftDeleted` stays `[]`** — no change needed.

7. **`TryBuildFetchSpecificationFrame` stays default `false`** — no change needed.

8. **The compliance subclass** (`storage_action_compliance : StorageActionCompliance`) mirrors
   `RavenDbTests/using_storage_return_types_and_entity_attributes.cs` with:
   - `configureWolverine`: `AddSingleton<IMongoClient>`, `UseMongoDbPersistence`, `AutoApplyTransactions`, Solo, Dynamic.
   - `Load(id)`: `collection.Find(Eq("_id", id)).FirstOrDefaultAsync()` on `MongoConstants.EntityCollectionName(typeof(Todo))`.
   - `Persist(todo)`: `ReplaceOneAsync(Eq("_id", todo.Id), todo, IsUpsert=true)` on the same collection.
