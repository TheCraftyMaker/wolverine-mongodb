# Task D6 — Tier 1: Entity Document Model + Frame-Branching Design (DESIGN GATE)

**Branch:** `docs/entity-document-model-design`
**Status:** ✅ Complete — design-only synthesis of D1 + D5. No code changes.
**Produced by:** Task D6 of `docs/superpowers/plans/2026-06-21-persistence-suite-completion.md`.
**Gates:** **T1.1** (`feat/entity-storage-action-persistence`). Every implementation step in T1.1 — and
the collection naming in T1.3 — depends on the decisions recorded here.
**Inputs synthesized:**
- D1 — `docs/superpowers/plans/2026-06-21-entity-persistence-discovery.md` (the verified contract + Cosmos/Raven reference).
- D5 — `docs/superpowers/plans/2026-06-21-demo-and-test-inventory.md` (collection naming, entity shapes, the test inventory the contracts must satisfy).

> **Authority of this doc.** This is the binding Tier-1 design. T1.1 implements *exactly* these
> contracts. Where this doc says "verify in T1.1" it means: the decision is fixed, but the
> *generated code* must be inspected (`codeFor<T>()` / `GeneratedCodeOutputPath`) to confirm the
> frame emits the intended sequence. If T1.1 finds an API differs from what is recorded here (D1
> found **no drift** against the pinned `external/wolverine` submodule V6.9.0), **STOP and report** —
> do not improvise a substitute Wolverine API.

---

## 0. Decision Summary (the binding calls)

| # | Decision | Resolution | LD |
|---|---|---|---|
| 1 | `CanPersist` scope | **Unconditional `true`** (matching Cosmos `:51-55` / RavenDb `:56-60`). Saga-vs-entity distinction moves to the **frame factories**, not `CanPersist`. | LD1 |
| 2 | Frame-branching rule | Branch the four typed/load factories on **`variable.VariableType.CanBeCastTo<Saga>()`** (load: `sagaType.CanBeCastTo<Saga>()`). `Saga` → existing saga frame (**UNTOUCHED**); else → new entity frame. `DetermineDeleteFrame(Variable,…)` + `DetermineStorageActionFrame` are **entity-only**. | LD1 |
| 3 | Entity write semantics | **Upsert** (`ReplaceOneAsync(IsUpsert:true)`) for Insert/Update/Store; **`DeleteOneAsync`** for Delete; **`Nothing` → no-op**. `Insert<T>` also upserts (Cosmos parity — **not** `InsertOneAsync`). **No entity optimistic concurrency** (explicit non-goal). | LD2 |
| 4a | Collection naming | **`MongoConstants.EntityCollectionName(Type) => type.Name.ToLowerInvariant()`** — un-prefixed (`Todo` → `todo`, `OrderNote` → `ordernote`). Compliance `Load`/`Persist` **and** the demo MUST use it. | LD3 |
| 4b | `_id` extraction | **`BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap` getter** — idiomatic MongoDB; **NOT** Cosmos's `entity.ToString()`. | LD3 |
| 4c | `ClearAllAsync` scope | **No** entity-collection cleanup. Entity collections are app state, not Wolverine system collections. Compliance host clears its own `todo` collection. | LD3 |
| 5 | Session enlistment / atomicity | Entity frames resolve `IClientSessionHandle` + `IMongoDatabase` + `CancellationToken` via `FindVariable`, **identically to the saga frames**. The storage-action `MethodCall` lets codegen resolve those args; the provider sets **only** `Arguments[2] = action`. Write lands inside the try-block before the single commit+flush. | — |
| 6 | `[Entity]` not-found + soft-delete | Not-found (`Required`) is **core** behavior — the load frame just returns `null`. `DetermineFrameToNullOutMaybeSoftDeleted` stays **`[]`** (Tier 3 non-goal). | — |
| 7 | Upstream readiness | Matches Cosmos/Raven (entity-generic frames + unconditional `CanPersist`). The only Mongo delta is class-map `_id` extraction instead of Cosmos's `.ToString()`. Clean upstream-contribution shape. | — |

**The saga-preservation invariant (the spine of this design):** `SagaFrames.cs` is **never edited**.
The four saga frame classes (`LoadSagaFrame`, `InsertSagaFrame`, `UpdateSagaFrame`, `DeleteSagaFrame`)
and `MongoSagaOperations` (version stamping + OCC) are untouched. Sagas keep their `Saga.Version`
optimistic concurrency exactly as shipped. T1.1 adds a *branch* in the provider's factory methods and
a *new* file `Internals/EntityFrames.cs`; it changes no saga behavior.

---

## 1. Decision 1 — `CanPersist` broadens to unconditional `true` (LD1)

### Current state
`MongoDbPersistenceFrameProvider.CanPersist` (`src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs:74-83`)
returns `entityType.CanBeCastTo<Saga>()` — **saga-scoped**. The inline comment states the reason
plainly: `DetermineStorageActionFrame` and `DetermineDeleteFrame(Variable,…)` *throw*, so advertising
generic persistence would blow up at codegen. The saga plan (R9) scoped it deliberately for exactly
this reason.

### Decision
Broaden to **unconditional `true`**, keeping `persistenceService = typeof(IMongoDatabase)`:

```csharp
public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
{
    persistenceService = typeof(IMongoDatabase);
    // Tier 1: DetermineStorageActionFrame + DetermineDeleteFrame(Variable,…) are now implemented,
    // so we advertise generic persistence like Cosmos (:51-55) and RavenDb (:56-60). The
    // saga-vs-entity distinction is handled in the frame factories below, NOT here. Required for
    // [Entity] loads, which select the provider via CanPersist(parameterType).
    return true;
}
```

### Rationale
- **Required for `[Entity]` loads.** `EntityAttribute.Modify` selects the provider via
  `rules.TryFindPersistenceFrameProvider(container, parameter.ParameterType, …)` →
  `CanPersist(parameterType)` (D1 §2, `EntityAttribute.cs:145`). A saga-scoped predicate makes
  `[Entity] Todo` silently unavailable — core finds no provider and the load never wires up.
- **Upstream-consistent.** Both document-store analogues return unconditional `true`
  (Cosmos D1 §6.1, RavenDb D1 §7.1). Mongo diverging here is the kind of inconsistency that blocks an
  upstream contribution.
- **Safe under `InsertFirstPersistenceStrategy`.** The provider is registered first
  (`WolverineMongoDbExtensions.cs:62`), so in a Mongo-only app it is always selected first — exactly
  like Cosmos/Raven in their apps. Unconditional `true` does not "steal" types from another provider
  in a single-provider host.

### The invariant this creates
Once `CanPersist` no longer filters, **the frame factories become the only place sagas and entities
diverge.** Decision 2 makes that branch explicit and total. `CanApply` (the *chain*-level predicate,
`:46-72`) is unaffected — it already returns `true` for `SagaChain` and for handlers touching
`IMongoDatabase`/`IMongoClient`/`IMongoCollection<>`/`IClientSessionHandle`/`MongoDbUnitOfWork`, which
covers the `[Entity]`/`IStorageAction<T>` handlers (they resolve `IMongoDatabase` through the
generated frames). No `CanApply` change is required for Tier 1.

---

## 2. Decision 2 — the saga-vs-entity frame-branching rule (LD1)

This is **the central design move** flagged by D1 §11 as the required branch point. Mongo is the
**only** provider that must branch, because its `DetermineInsertFrame`/`DetermineUpdateFrame` are
saga-specific (they stamp `Saga.Version` / apply OCC), whereas Cosmos and RavenDb use last-write-wins
upsert for *all* types including sagas and so never needed a branch.

### The predicate
**`variable.VariableType.CanBeCastTo<Saga>()`** for the typed write factories, and
**`sagaType.CanBeCastTo<Saga>()`** for the load factory.

- `CanBeCastTo<T>()` is the `JasperFx.Core.Reflection` extension on `Type`. It is **already imported
  and already in use** in this exact file — `CanPersist` calls `entityType.CanBeCastTo<Saga>()`
  today (`MongoDbPersistenceFrameProvider.cs:82`). `Variable.VariableType` is a `Type`, so
  `variable.VariableType.CanBeCastTo<Saga>()` is the same well-understood call. No new dependency.
- A handler entity (`Todo`, `OrderNote`) does **not** derive from `Wolverine.Persistence.Sagas.Saga`,
  so the predicate is `false` for entities and `true` for saga POCOs. This is the same test
  `CanPersist` used to gate on, now relocated to per-frame granularity.

### The branch table (final factory bodies for T1.1)

| Provider method | Called by | Saga branch (`CanBeCastTo<Saga>()` true) — **UNCHANGED** | Entity branch (false) — **NEW** |
|---|---|---|---|
| `DetermineLoadFrame(container, sagaType, sagaId)` | `[Entity]` load (`EntityAttribute.cs:165`) **and** saga load | `new LoadSagaFrame(sagaType, sagaId)` | `new LoadEntityFrame(sagaType, sagaId)` |
| `DetermineInsertFrame(saga, container)` | `Insert<T>` (`Insert.cs:26`) | `new InsertSagaFrame(saga)` | `new MongoUpsertEntityFrame(saga)` |
| `DetermineUpdateFrame(saga, container)` | `Update<T>` (`Update.cs:26`) | `new UpdateSagaFrame(saga)` | `new MongoUpsertEntityFrame(saga)` |
| `DetermineStoreFrame(saga, container)` | `Store<T>` (`Store.cs:26`) | `DetermineUpdateFrame(saga, container)` (→ `UpdateSagaFrame`) | `new MongoUpsertEntityFrame(saga)` |
| `DetermineDeleteFrame(Variable variable, container)` | `Delete<T>` (`Delete.cs:26`, single-variable) | **n/a — entity-only** | `new MongoDeleteEntityByVariableFrame(variable)` |
| `DetermineStorageActionFrame(entityType, action, container)` | `IStorageAction<T>` / `UnitOfWork<T>` (`IStorageAction.cs:27,:96`) | **n/a — entity-only** | `MethodCall` → `MongoEntityOperations.ApplyStorageActionAsync<T>` |

Final shape (mirrors D1 §11 / plan implementation shape verbatim):

```csharp
public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    => sagaType.CanBeCastTo<Saga>() ? new LoadSagaFrame(sagaType, sagaId) : new LoadEntityFrame(sagaType, sagaId);

public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    => saga.VariableType.CanBeCastTo<Saga>() ? new InsertSagaFrame(saga) : new MongoUpsertEntityFrame(saga);

public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    => saga.VariableType.CanBeCastTo<Saga>() ? new UpdateSagaFrame(saga) : new MongoUpsertEntityFrame(saga);

public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    => saga.VariableType.CanBeCastTo<Saga>() ? DetermineUpdateFrame(saga, container) : new MongoUpsertEntityFrame(saga);

// Generic single-variable delete (Delete<T> return) — was throwing NotSupportedException.
//
// Note: the saga branch of DetermineStoreFrame is inert — SagaChain never calls DetermineStoreFrame
// for sagas (it emits explicit Insert/Update/Delete; SagaChain.cs:395,423-424, per the provider
// comment at :119-121). It is retained only to satisfy the interface and route consistently to the
// version-guarded update. The live Store<T> path is therefore the entity branch → MongoUpsertEntityFrame.
public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    => new MongoDeleteEntityByVariableFrame(variable);

// Generic IStorageAction<T>/UnitOfWork<T> return — was throwing NotSupportedException.
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "MakeGenericMethod over the entity type; matches RavenDb/Cosmos providers.")]
public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
{
    var method = typeof(MongoEntityOperations)
        .GetMethod(nameof(MongoEntityOperations.ApplyStorageActionAsync))!
        .MakeGenericMethod(entityType);
    var call = new MethodCall(typeof(MongoEntityOperations), method);
    call.Arguments[2] = action; // signature: (IMongoDatabase, IClientSessionHandle, IStorageAction<T> action, CancellationToken)
    return call;
}
```

### What stays exactly as-is on the saga path

- `DetermineDeleteFrame(Variable sagaId, Variable saga, container)` — the **two-variable** saga
  overload (`:116-117`) — is untouched. Sagas reach delete only through `SagaChain` with both the id
  and saga variables; `Delete<T>` never calls it (D1 §3.2). The new single-variable overload
  (`:125-128`, currently throwing) is the only one that changes.
- `DetermineSagaIdType` (`:93-96`) is untouched. Core also calls it on the `[Entity]` path
  (`EntityAttribute.cs:153`) to resolve the entity's id type; `SagaChain.DetermineSagaIdMember`
  resolves the `Id`/`[Identity]` member of *any* POCO (saga or entity), so it already serves the
  entity path correctly — **no change needed** and none is made.
- `CommitUnitOfWorkFrame` (`:110-111`) and both `ApplyTransactionSupport` overloads (`:19-44`) are
  untouched. The `chain is not SagaChain` guard on the commit postprocessor (`:34-37`) already does
  the right thing for entity chains: a non-saga entity handler **gets** the `CommitMongoTransactionFrame`
  postprocessor (commit+flush once), which is exactly what we want.

### The `[UnconditionalSuppressMessage]` annotation
`DetermineStorageActionFrame` uses `MakeGenericMethod`, which is AOT-unsafe (IL3050). RavenDb annotates
its equivalent (D1 §7.5, `RavenDbPersistenceFrameProvider.cs:112-114`). Mongo applies the same
`[UnconditionalSuppressMessage("AOT", "IL3050", …)]`. This is a contract requirement, not optional.

---

## 3. Decision 3 — entity write semantics: upsert-for-all, no OCC (LD2)

### Decision
| Storage action | Mongo entity operation |
|---|---|
| `Insert` | `ReplaceOneAsync(session, Eq("_id", id), entity, IsUpsert:true)` — **upsert** |
| `Update` | `ReplaceOneAsync(session, Eq("_id", id), entity, IsUpsert:true)` — **upsert** |
| `Store`  | `ReplaceOneAsync(session, Eq("_id", id), entity, IsUpsert:true)` — **upsert** |
| `Delete` | `DeleteOneAsync(session, Eq("_id", id))` |
| `Nothing`| no-op (`Task.CompletedTask`) |

> In every row, **`id = IdOf(entity)`** — the class-map-extracted `_id` (Decision 4b). The helpers
> take the **entity** (or `IStorageAction<T>`), never a separate id argument; the table abbreviates
> `IdOf(entity)` to `id` for readability. See §8.2 for the authoritative `MongoEntityOperations`
> signatures.

Insert/Update/Store all route through one `MongoUpsertEntityFrame` (and one
`MongoEntityOperations.UpsertAsync<T>`). **No entity optimistic concurrency.** Entities carry no
`Saga.Version`; the upsert is last-write-wins.

### Why upsert for `Insert<T>` (not `InsertOneAsync`)
- **Cosmos parity.** Cosmos's `Insert`/`Update`/`Store` all return `CosmosDbUpsertFrame` →
  `UpsertItemAsync` (D1 §6.2/§6.3). RavenDb's all call `StoreAsync` (D1 §7.2). Neither distinguishes
  insert from upsert.
- **The compliance facts do not require the distinction.** `use_insert_as_return_value` (fact #1)
  asserts the document is *persisted and readable* after an `Insert<Todo>` return; it does not assert
  a duplicate-key throw on a pre-existing `_id`. Upsert satisfies it.
- **Simplicity + idempotency.** A single upsert helper covers three actions and is naturally
  retry-safe — a message redelivery re-upserts the same document rather than throwing on a duplicate
  `_id`, which is the friendlier behavior for the at-least-once inbox.

`InsertOneAsync` (stricter, surfaces `DuplicateKeyException`) is **explicitly not used** for entities.
It remains the saga-insert mechanism (`MongoSagaOperations.InsertSagaAsync`) precisely because sagas
*want* the duplicate-key guard as a concurrent-double-start defense — another reason the two paths
stay separate.

### Entity OCC is a non-goal — recorded rationale
Optimistic concurrency for plain entities is **out of scope for Tier 1**:
- Cosmos and RavenDb (the closest analogues) do not provide it for entities.
- The demo's **repository pattern** (`OrderRepository.UpdateAsync`'s version-guarded `ReplaceOneAsync`)
  remains the documented path for app-controlled OCC. Apps that need concurrency control on a
  document use the repository + `IClientSessionHandle` pattern, not `[Entity]`/`IStorageAction<T>`.
- This is documented as a deliberate boundary (carry into T1.3 docs / CLAUDE.md when T1.1 lands).

---

## 4. Decision 4 — collection naming, `_id` extraction, `ClearAllAsync` scope (LD3)

### 4a. Collection naming — un-prefixed, lowercased type name
```csharp
// MongoConstants.cs — added by T1.1, alongside the existing SagaCollectionName.
public static string EntityCollectionName(Type entityType)
    => entityType.Name.ToLowerInvariant();
```

| Entity | Saga collection (existing) | Entity collection (new) |
|---|---|---|
| `Todo` (compliance) | — | `todo` |
| `OrderNote` (demo) | — | `ordernote` |
| `OrderFulfillmentSaga` | `wolverine_saga_orderfulfillmentsaga` | — |

**Rationale (from D5 §4.1):** entity collections are **application-owned**, not Wolverine system
collections, so prefixing (`wolverine_entity_todo`) would be misleading and non-idiomatic. Lowercased
type name is minimal, collision-free per type, and mirrors the saga convention's lowercasing
(`SagaCollectionName = "wolverine_saga_" + type.Name.ToLowerInvariant()`, `MongoConstants.cs:27-28`)
— the only difference is the dropped prefix.

> **Coupled pair.** The frame helpers and the test/demo readers form a coupled pair: whatever
> collection `MongoEntityOperations` writes to, the compliance `Load`/`Persist` and the demo's direct
> reads MUST target the same name **by calling `EntityCollectionName`**, never a hard-coded literal.
> If this convention is ever changed, both sides change together.

### 4b. `_id` value extraction — class map, not `ToString()`
The upsert and delete helpers need the entity's `_id` value generically (any POCO, any id type).
Resolve it via the **driver class map**:

```csharp
private static object IdOf<T>(T entity)
    => BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap?.Getter(entity)
       ?? throw new InvalidOperationException(
           $"{typeof(T).FullNameInCode()} has no mapped _id member; generic MongoDB persistence requires an Id member.");
```

- `BsonClassMap.LookupClassMap(Type)` returns the (auto-built) class map; `IdMemberMap` is the
  `BsonMemberMap` for the id member the driver's default convention selected (the `Id`/`<Type>Id`
  member, e.g. `Todo.Id`, `OrderNote.Id`); `.Getter` is the compiled `Func<object,object>` accessor.
- This is **idiomatic MongoDB** and matches how the driver itself resolves `_id` for `ReplaceOneAsync`.
  It works for **every** id type (`string`, `Guid`, `int`, `long`) with no per-type code — the same
  native-id-type story the saga path already tells.
- It is **explicitly not** Cosmos's `entity.ToString()` hack (D1 §6.4/§6.5,
  `CosmosDbDeleteByVariableFrame`/`CosmosDbStorageActionApplier`). `ToString()` happens to work for
  Cosmos because of a Cosmos-specific id convention; for Mongo it would be wrong (a POCO's
  `ToString()` is its type name unless overridden). **This class-map extraction is the one genuine
  Mongo delta from the Cosmos/Raven reference** (see Decision 7).

The `null`-coalescing throw gives a clear, early error if an app registers `[Entity]`/`IStorageAction<T>`
for a type with no resolvable id member, instead of silently filtering on a wrong/empty `_id`.

### 4c. `ClearAllAsync` does NOT touch entity collections
`IMessageStoreAdmin.ClearAllAsync` (`MongoDbMessageStore.Admin.cs:83-87`) clears the system collections
and drops every `wolverine_saga_*` collection by prefix. It **must not** drop entity collections
(`todo`, `ordernote`, …) — those are application state, not Wolverine-managed.

**Decision: no change to `ClearAllAsync`.** Consequences for tests:
- The **compliance subclass** clears its own `todo` collection in its lifecycle (override
  `InitializeAsync`/`DisposeAsync`, or drop the collection in `configureWolverine`) — the
  `StorageActionCompliance` facts each assume a clean slate. T1.1 owns this (see §6).
- The **demo** likewise reads/cleans `ordernote` itself (D5 §1.6); one DB per test already isolates it.

(There is no entity-collection prefix to key off, by design — un-prefixed naming is what keeps app
collections out of Wolverine's blast radius.)

---

## 5. Decision 5 — session enlistment & atomicity

### Entity frames resolve the transaction session exactly like saga frames
The new frame classes in `Internals/EntityFrames.cs` mirror `SagaFrames.cs` line-for-line in how they
acquire the transaction context. Every saga frame's `FindVariables` resolves the same three variables
(confirmed in `SagaFrames.cs:141-151,182-192,232-242,276-286`):

```csharp
public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
{
    _session     = chain.FindVariable(typeof(IClientSessionHandle)); yield return _session;
    _database    = chain.FindVariable(typeof(IMongoDatabase));       yield return _database;
    _cancellation = chain.FindVariable(typeof(CancellationToken));   yield return _cancellation;
}
```

`IClientSessionHandle` resolves to the session the `TransactionalFrame` opened (every storage-action
and `[Entity]` path first calls `ApplyTransactionSupport`, which guarantees the `TransactionalFrame`
— and thus a resolvable session — is present before the write frame runs; D1 §3.2,
`IStorageAction.cs:25`/`Insert.cs:24`). Running every entity read/write **on that session** is what
makes the entity write and the outbox commit atomic — identical to how saga state commits atomically
today.

### The `MethodCall` arg-index for `DetermineStorageActionFrame`
`MongoEntityOperations.ApplyStorageActionAsync<T>` has signature
`(IMongoDatabase db, IClientSessionHandle session, IStorageAction<T> action, CancellationToken ct)`.
In the generated `MethodCall`, codegen auto-resolves `db` (0), `session` (1), and `ct` (3) from the
chain's variables; the provider sets **only** `call.Arguments[2] = action`.

> **Index contract.** `Arguments[2]` is correct **for this 4-arg signature**. It is *not* a copy of
> Cosmos/Raven's `Arguments[1]` — those helpers are 2-arg `(persistenceService, action)`, so their
> action sits at index 1 (D1 §6.5/§7.3). Mongo's helper takes both `db` and `session` ahead of
> `action`, pushing it to index 2. Keep the signature and the index in lock-step; if T1.1 reorders the
> helper parameters, the index must move with them.

### Generated-code shape T1.1 must verify (`codeFor<T>()`)
For a handler returning `Insert<Todo>` (entity branch), the generated handler body should contain,
**inside the `TransactionalFrame` try-block, before the single commit+flush**, a line of the form:

```csharp
// (entity upsert on the session, inside the outbox transaction)
await Wolverine.MongoDB.Internals.MongoEntityOperations.UpsertAsync<…Todo>(database, session, todo, cancellationToken).ConfigureAwait(false);
```

and the commit+flush (`CommitMongoTransactionFrame`, added as the postprocessor because the entity
chain `is not SagaChain`) appears **after** it. For a handler taking `[Entity] Todo`, a
`MongoEntityOperations.LoadAsync<Todo, string>(database, session, id, cancellationToken)` line appears,
with core's null-guard wrapping it (Decision 6).

**Verification is a hard gate in T1.1 (its Step 3):** dump the generated code for one `Insert<Todo>`
handler and one `[Entity] Todo` handler and confirm (a) the entity op is on `session` inside the
try-block before commit, and (b) a **saga handler's generated code is byte-for-byte unchanged** from
before the branch. A subtly mis-ordered frame compiles and passes some facts while breaking atomicity.

---

## 6. Decision 6 — `[Entity]` not-found is core behavior; soft-delete stays `[]`

### `[Entity]` not-found
The "do not execute the handler if a required `[Entity]` is missing" behavior is **entirely owned by
Wolverine core** (D1 §2, `EntityAttribute.cs:176-190`). When `Required == true` (the default), core
wraps the load frame in its own null-guard / short-circuit block. **The provider's load frame just
returns `null` when the document is not found** — no provider work needed.

`MongoEntityOperations.LoadAsync<T,TId>` returns `FirstOrDefaultAsync(...)`, which yields `null` for a
missing `_id` — exactly the contract core expects (it mirrors `MongoSagaOperations.LoadSagaAsync`,
whose `null` return is the "start new saga" branch, `SagaFrames.cs:28-41`).

Therefore the following compliance facts pass with a **null-returning load frame** and **no extra
provider code**:
- `do_not_execute_the_handler_if_the_entity_is_not_found` (Required=true, missing → handler skipped),
- `handler_not_required_entity_attributes` (Required=false, missing → handler runs with `null`),
- `can_use_attribute_on_before_methods` (missing → before-method stops the chain).

### Soft-delete
`DetermineFrameToNullOutMaybeSoftDeleted` stays **`[]`** (`MongoDbPersistenceFrameProvider.cs:135-138`,
unchanged). Core only calls it when `MaybeSoftDeleted == false` (D1 §2, `EntityAttribute.cs:170-173`).
Only Marten implements a real frame; Cosmos, RavenDb, EF Core, Polecat all return `[]`. Soft-delete is
a **Tier 3 documented non-goal** — no Tier-1 work. `TryBuildFetchSpecificationFrame` likewise stays at
its default `false` (Marten/EF-only; Tier 3 non-goal). Neither is touched by T1.1.

---

## 7. Decision 7 — upstream readiness

This design is deliberately a **clean upstream-contribution shape**:
- **Unconditional `CanPersist`** → matches Cosmos (`:51-55`) and RavenDb (`:56-60`).
- **Entity-generic upsert/delete frames + a `MethodCall` storage-action applier** → structurally the
  same as `CosmosDbUpsertFrame`/`CosmosDbStorageActionApplier` (D1 §6) and RavenDb's
  `DeleteDocumentFrame`/`RavenDbStorageActionApplier` (D1 §7).
- **`[UnconditionalSuppressMessage("AOT","IL3050")]`** on the `MakeGenericMethod` factory → matches
  RavenDb's AOT annotation.

**The single intentional Mongo delta** vs the reference providers is **`_id` extraction via the BSON
class map** (`IdMemberMap.Getter`) instead of Cosmos's `entity.ToString()`. This is *more* correct for
MongoDB (it honors the driver's id convention and works for all native id types) and is the natural
thing a reviewer would expect from a Mongo provider. The saga-vs-entity *branch* in the factories is
the other Mongo-specific element, but it is an internal implementation detail (it preserves Mongo's
saga OCC, which Cosmos/Raven lack) and does not change the public contract.

Net: a future `Wolverine.MongoDb` upstream contribution can present these frames with no surprising
divergences; the OCC branch is explainable as "Mongo keeps the Marten/EF/lightweight-SQL-style saga
OCC, so its saga and entity write paths differ where Cosmos/Raven's coincide."

---

## 8. The exact frame & collection contracts T1.1 implements

This section is the implementation checklist the gate produces. T1.1 builds **exactly** this.

### 8.1 `MongoConstants.cs` — add
```csharp
public static string EntityCollectionName(Type entityType)
    => entityType.Name.ToLowerInvariant();
```

### 8.2 `Internals/EntityFrames.cs` — **new file**, mirroring `SagaFrames.cs`
`MongoEntityOperations` static helper (no `Version`, no OCC; upsert/delete keyed on class-map `_id`):

```csharp
public static class MongoEntityOperations
{
    public static Task<T> LoadAsync<T, TId>(
        IMongoDatabase db, IClientSessionHandle session, TId id, CancellationToken ct) where T : class
        => db.GetCollection<T>(MongoConstants.EntityCollectionName(typeof(T)))
             .Find(session, Builders<T>.Filter.Eq("_id", id)).FirstOrDefaultAsync(ct);

    public static Task UpsertAsync<T>(
        IMongoDatabase db, IClientSessionHandle session, T entity, CancellationToken ct)
        => db.GetCollection<T>(MongoConstants.EntityCollectionName(typeof(T)))
             .ReplaceOneAsync(session, Builders<T>.Filter.Eq("_id", IdOf(entity)), entity,
                 new ReplaceOptions { IsUpsert = true }, ct);

    public static Task DeleteAsync<T>(
        IMongoDatabase db, IClientSessionHandle session, T entity, CancellationToken ct)
        => db.GetCollection<T>(MongoConstants.EntityCollectionName(typeof(T)))
             .DeleteOneAsync(session, Builders<T>.Filter.Eq("_id", IdOf(entity)), cancellationToken: ct);

    public static Task ApplyStorageActionAsync<T>(
        IMongoDatabase db, IClientSessionHandle session, IStorageAction<T> action, CancellationToken ct)
    {
        if (action.Entity is null) return Task.CompletedTask;
        return action.Action switch
        {
            StorageAction.Delete                                          => DeleteAsync(db, session, action.Entity, ct),
            StorageAction.Insert or StorageAction.Update or StorageAction.Store => UpsertAsync(db, session, action.Entity, ct),
            _ => Task.CompletedTask, // Nothing
        };
    }

    private static object IdOf<T>(T entity)
        => BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap?.Getter(entity)
           ?? throw new InvalidOperationException(
               $"{typeof(T).FullNameInCode()} has no mapped _id member; generic MongoDB persistence requires an Id member.");
}
```

Three `AsyncFrame` classes — **same `FindVariables` (session/db/ct) and `GenerateCode` structure as
the saga frames**, emitting a single `await MongoEntityOperations.<Method><…generic args…>(database,
session, …, cancellationToken).ConfigureAwait(false);`:

| Frame | Ctor | Emits | Notes |
|---|---|---|---|
| `LoadEntityFrame` | `(Type entityType, Variable id)` | `var <e> = await …LoadAsync<TEntity, TId>(db, session, id, ct)…;` | `TId = id.VariableType`; creates a `Variable(entityType, this)` (the loaded entity), like `LoadSagaFrame.Saga`. Returns `null` when absent (Decision 6). |
| `MongoUpsertEntityFrame` | `(Variable entity)` | `await …UpsertAsync<TEntity>(db, session, entity, ct)…;` | `TEntity = entity.VariableType`. Used for Insert/Update/Store entity branch. |
| `MongoDeleteEntityByVariableFrame` | `(Variable entity)` | `await …DeleteAsync<TEntity>(db, session, entity, ct)…;` | The `Delete<T>` variable **is the entity** (`EntityVariable(variable)`, D1 §3.2); `_id` extracted via class map inside `DeleteAsync` — **not** `ToString()`. |

(Driver methods `Find`/`FirstOrDefaultAsync`/`ReplaceOneAsync`/`DeleteOneAsync` are extension methods,
so — exactly as the saga frames document, `SagaFrames.cs:10-18` — the calls live in
`MongoEntityOperations` and the generated code carries no `using MongoDB.Driver;`.)

### 8.3 `MongoDbPersistenceFrameProvider.cs` — change
- `CanPersist` → `return true;` (Decision 1).
- `DetermineLoadFrame`/`DetermineInsertFrame`/`DetermineUpdateFrame`/`DetermineStoreFrame` → add the
  `CanBeCastTo<Saga>()` branch (Decision 2 table).
- `DetermineDeleteFrame(Variable, container)` → `new MongoDeleteEntityByVariableFrame(variable)`
  (was throwing).
- `DetermineStorageActionFrame` → the annotated `MethodCall` with `Arguments[2] = action`
  (was throwing).
- Remove/retire the `GenericPersistenceNotSupported` constant once both throwing members are
  implemented.
- **Do not touch** `DetermineSagaIdType`, `CommitUnitOfWorkFrame`, the two-variable
  `DetermineDeleteFrame`, `ApplyTransactionSupport`, `CanApply`, or `DetermineFrameToNullOutMaybeSoftDeleted`.

### 8.4 `src/Wolverine.MongoDB.Tests/storage_action_compliance.cs` — **new file** (shipped in T1.1)
A single self-contained `: StorageActionCompliance` subclass (there is **no** separate host file —
`StorageActionCompliance` is not generic over an `ISagaHost`). `configureWolverine` =
`AddSingleton<IMongoClient>(_fixture.Client)` + `UseMongoDbPersistence(AppFixture.DatabaseName)` +
`AutoApplyTransactions()` + `DurabilityMode.Solo` + `TypeLoadMode.Dynamic`. `Load`/`Persist` target
**`MongoConstants.EntityCollectionName(typeof(Todo))`** (`= "todo"`) and clear that collection per
fact (Decision 4c). Mirrors `RavenDbTests/using_storage_return_types_and_entity_attributes.cs`
(D1 §7.6). Must pass all ~17 facts (D1 §4.2).

### 8.5 Files that must NOT change in T1.1
`SagaFrames.cs` (the four saga frame classes + `MongoSagaOperations`); the saga compliance suites;
`saga_atomicity.cs`; `TransactionalFrame.cs`/`CommitMongoTransactionFrame`; `MongoDbUnitOfWork.cs`;
`WolverineMongoDbExtensions.cs` (the Tier-1 entity surface needs no new registration — it rides the
existing `InsertFirstPersistenceStrategy<MongoDbPersistenceFrameProvider>()`).

---

## 9. Acceptance criteria the contracts must satisfy (for T1.1 / T1.2)

From D5 §5.1, mapped to the decisions above:

- **All ~17 `StorageActionCompliance` facts green** (D1 §4.2 / D5 §5.1) against the `todo` collection:
  insert/update/store/delete return values, generic `IStorageAction<T>` actions including `Nothing`,
  null-action no-ops, `UnitOfWork<T>` multi-action, and the four `[Entity]` resolution + not-found
  facts (Decisions 3 + 6).
- **No saga regression:** all four saga compliance suites + `saga_atomicity` + saga OCC stay green,
  and the generated saga code is unchanged (Decision 2 invariant; verified in T1.1 Step 3).
- **Entity atomicity (T1.2):** entity write + outbox commit/roll back together — provable because the
  entity write runs on the `TransactionalFrame` session before commit (Decision 5).
- **Coexistence (T1.2):** a handler that writes both a saga (Saga subclass) and an entity keeps saga
  OCC *and* persists the entity — proving the branch did not cross wires.
- **Full library suite green on net9.0 + net10.0.**

> After an `Internals` API change, delete the gitignored stale codegen
> (`rm -rf src/Wolverine.MongoDB.Tests/Internal/Generated`) before a clean local build — a recorded
> gotcha for this repo. (T1.1 concern; noted here so the gate is self-contained.)

---

## 10. Open questions left for T1.1 (all bounded, none blocking)

These are *implementation-confirmation* items, not design choices — each has a fixed expected answer:

1. **Generic constraints on `MongoEntityOperations` helpers.** `LoadAsync<T,TId>` needs `where T : class`
   (matches `MongoSagaOperations.LoadSagaAsync`). `UpsertAsync`/`DeleteAsync`/`ApplyStorageActionAsync`
   take a `T entity`/`IStorageAction<T>`; confirm they compile without a `class` constraint, or add the
   minimal constraint the driver/`IStorageAction<T>` require. Expected: a `class` constraint where
   `GetCollection<T>` needs it; no behavioral impact.
2. **`Insert.For`/`Update.For`/`Delete.For` vs constructor.** D5 §1.4 notes the demo uses `Insert.For(entity)`;
   confirm the exact static-factory vs `new Insert<T>(entity)` API against the submodule when wiring
   the demo (T1.3). Not a frame-provider concern — the provider only sees the resulting `Insert<T>`/etc.
3. **Generated-code inspection.** Confirm via `codeFor<T>()` the entity op is on `session` inside the
   try-block before commit, and the saga handler code is unchanged (Decision 5; T1.1 Step 3 — the hard gate).
4. **`DetermineSagaIdType` on a non-Saga POCO.** This design (Decision 2, §2) relies on the single
   dual-purpose method `DetermineSagaIdType` (→ `SagaChain.DetermineSagaIdMember`) resolving the id
   member of a plain entity (e.g. `Todo`/`OrderNote`) on the `[Entity]` load path
   (`EntityAttribute.cs:153`), per D1's verified facts (§1 `:25`, §2). It is asserted on D1's authority
   (the `external/wolverine` submodule is not checked out in this docs worktree). When T1.1 dumps the
   generated `[Entity] Todo` load frame, confirm the id-type resolution succeeds for the non-Saga POCO
   — if it instead threw `DetermineSagaIdType`'s `ArgumentException` (`:95`) that would be an
   *entity-path* failure (never a saga regression — sagas are unaffected), to be reported per the
   escalation rule.

None of these change a Decision 1–7 call; they are verified during implementation and reported if they
surprise.

---

## 11. Traceability

| Decision | LD | D1 evidence | D5 evidence |
|---|---|---|---|
| 1 — unconditional `CanPersist` | LD1 | §6.1, §7.1 (Cosmos/Raven), §8 (current saga-scoped) | — |
| 2 — frame branching on `CanBeCastTo<Saga>()` | LD1 | §11 (the required branch point), §8 | §7 (Guid id entity) |
| 3 — upsert-for-all, no OCC | LD2 | §6.2–6.5, §7.2–7.3, §9 (saga insert/update diverge) | §1.2, §7 |
| 4a — `EntityCollectionName` un-prefixed | LD3 | §10 | §4.1, §7 |
| 4b — class-map `_id` (not `ToString()`) | LD3 | §6.4–6.5 (Cosmos `ToString()` hack) | — |
| 4c — no `ClearAllAsync` entity cleanup | LD3 | — | §4.3 |
| 5 — session enlistment / atomicity | — | §3.2, §9; local `SagaFrames.cs` | §1.4 |
| 6 — `[Entity]` not-found core; soft-delete `[]` | — | §2, §1 (interface), §6.2/§7.2 (`[]`) | §5.1 |
| 7 — upstream readiness | — | §6, §7, §7.5 | — |

---

*End of D6 design gate. T1.1 implements §8 exactly; T1.3 reuses §4a for the demo collection name.*
