# Persistence Suite Completion — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. The companion prompts file is `docs/superpowers/plans/2026-06-21-persistence-suite-completion-prompts.md`.

**Goal:** Take `Wolverine.MongoDB` from "inbox + outbox + saga complete" to a **feature-complete, upstream-ready** Wolverine persistence provider by closing the four triaged tiers: (1) generic entity / `IStorageAction<T>` side-effect persistence — the one real functional gap vs Cosmos & Raven; (2) optional `ISagaStoreDiagnostics` (CritterWatch / saga-explorer surface, matching Raven); (3) explicit *implement-vs-document-as-non-goal* decisions for the four RDBMS/Marten-only parity capabilities (multi-tenancy, durable listeners, query-spec frames, soft-delete); (4) minor hardening + the already-tracked `FOLLOWUPS.md` items. Deliver each with library tests in Wolverine's style (subclassing the upstream compliance specs where they exist), demo coverage where user-facing, and verification that inbox + outbox + single-node + multi-node + saga keep working alongside the new capability. End state: a clean candidate for a future upstream `Wolverine.MongoDb` contribution.

**Architecture:** Like saga support, generic entity persistence is **entirely code-generation** through the existing `MongoDbPersistenceFrameProvider : IPersistenceFrameProvider` — there is no new storage service. Wolverine core owns the `[Entity]` parameter-loading path and the `IStorageAction<T>`/`Insert<T>`/`Update<T>`/`Store<T>`/`Delete<T>` return-value side-effect path; the provider supplies the read/write frames woven into the generated handler and run on the **existing `TransactionalFrame` session** (so a domain write commits atomically with the outbox, exactly as saga state does today). The central design move is that the insert/update/store/**load** frame factories must **branch saga-vs-entity**: sagas keep their `Saga.Version` optimistic concurrency untouched; plain entities get simple upsert/insert/delete frames (mirroring Cosmos's `CosmosDbUpsertFrame`). `ISagaStoreDiagnostics` (Tier 2) is a read-only reflection-driven surface registered in DI, mirroring `RavenDbSagaStoreDiagnostics`. Tier 3 is decision-and-documentation work (default: document-as-non-goal, matching Cosmos/Raven). Tier 4 is a fan-out of small, mostly-independent hardening/demo/follow-up tasks.

**Tech Stack:** .NET 9/10, MongoDB.Driver 3.x, WolverineFx 6.9.0 (pinned `external/wolverine` submodule — keep in sync with `Directory.Packages.props`), JasperFx.CodeGeneration (frames), xUnit + Shouldly (library) / xUnit + FluentAssertions (demo), Testcontainers.MongoDb (replica set).

**PREREQUISITE:** All prior plans are merged to `main`: Solo Hardening (`2026-06-09-solo-hardening.md`), Multinode (`2026-06-09-multinode-support.md`), and Saga Persistence (`2026-06-18-saga-persistence.md`). This plan builds directly on what they delivered: the transactional frame (`TransactionalFrame` + `CommitMongoTransactionFrame`), `MongoDbUnitOfWork`, `MongoDbPersistenceOptions`, the recovery loops, the `AppFixture` harness, the `MongoDbSagaHost` + saga compliance subclasses, the `[Category=multinode]` CI split, and the config-driven demo durability mode. **The saga frames (`SagaFrames.cs`) are the primary template for the Tier-1 entity frames.**

---

## Verified API Facts (established by discovery — do NOT re-derive or invent)

Read directly from the pinned `external/wolverine` submodule (V6.9.0) and the local source during planning. Tasks below reference these; an implementing session should *confirm* each against the submodule (a one-line grep / Read) rather than reinventing it. **If an API differs from what is recorded here, STOP and report — do not improvise a substitute.**

### Wolverine core — the provider contract (`external/wolverine/src/Wolverine/Persistence/IPersistenceFrameProvider.cs`)

The full interface has ~12 members. Tier-relevant signatures (line numbers approximate — confirm):
- `bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)` — `:23`. Filters which entity types this provider claims; consulted by `TryFindPersistenceFrameProvider` for BOTH `[Entity]` loads and storage actions.
- `Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)` — `:26`. Used for sagas **and** `[Entity]` parameter loads.
- `Frame DetermineInsertFrame(Variable saga, IServiceContainer container)` — `:27`.
- `Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)` — `:29`.
- `Frame DetermineStoreFrame(Variable saga, IServiceContainer container)` — `:39`.
- `Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)` — saga (two-variable) overload.
- `Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)` — `:48`, the **generic single-variable** overload (Mongo currently throws here).
- `Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)` — `:50` (Mongo currently throws here).
- `Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity)` — `:52` (Tier 3 soft-delete; Mongo returns `[]`).
- `bool TryBuildFetchSpecificationFrame(Variable specVariable, IServiceContainer container, out Frame? frame, out Variable? result)` — `:73-82`, **default implementation returns `false`** (Tier 3 query-spec).

### Wolverine core — the `[Entity]` parameter-loading path (`external/wolverine/src/Wolverine/Persistence/EntityAttribute.cs`)

- `EntityAttribute : WolverineParameterAttribute, IDataRequirement` with `bool Required = true`, `bool MaybeSoftDeleted = true`, `OnMissing OnMissing = OnMissing.Simple404`. Optional ctor arg names the id member.
- `:145` core calls `rules.TryFindPersistenceFrameProvider(container, parameter.ParameterType, out var provider)` — keyed on `CanPersist(parameterType)`.
- `:165` core calls `provider.DetermineLoadFrame(container, parameter.ParameterType, identity)` — `identity` is the id pulled from the message.
- `:170-173` **only when `MaybeSoftDeleted == false`** core appends `provider.DetermineFrameToNullOutMaybeSoftDeleted(entity)` to the chain middleware.

### Wolverine core — the `IStorageAction<T>` side-effect path

- `external/wolverine/src/Wolverine/Persistence/IStorageAction.cs:16-31` — `interface IStorageAction<T> : ISideEffectAware { StorageAction Action; T Entity; }`. Its `static BuildFrame(...)` at `:27` calls `provider.DetermineStorageActionFrame(typeof(T), new EntityVariable(variable), container).WrapIfNotNull(variable)` (the `WrapIfNotNull` gives core's "do nothing if the action variable is null" behavior for free). `UnitOfWork<T>` (`:39`) is a list of `IStorageAction<T>`.
- `StorageAction` enum (`external/wolverine/src/Wolverine/Persistence/StorageAction.cs:3-29`): `Store, Delete, Nothing, Update, Insert`.
- Concrete records each route to the **typed** factory (NOT `DetermineStorageActionFrame`):
  - `Insert<T>.BuildFrame` → `provider.DetermineInsertFrame(new EntityVariable(variable), container)` (`Insert.cs:26`).
  - `Delete<T>.BuildFrame` → `provider.DetermineDeleteFrame(new EntityVariable(variable), container)` — the **single-variable** overload (`Delete.cs:26`).
  - `Update<T>`/`Store<T>` analogous (→ `DetermineUpdateFrame`/`DetermineStoreFrame`).
- All four (and the generic path) first call `provider.ApplyTransactionSupport(chain, container, typeof(T))` (`Insert.cs:24`, `IStorageAction.cs:26`), so the `TransactionalFrame` (and thus a resolvable `IClientSessionHandle`) is present before the write frame runs.

### Wolverine core — the acceptance oracle (`external/wolverine/src/Testing/Wolverine.ComplianceTests/StorageActionCompliance.cs`)

- `public abstract class StorageActionCompliance : IAsyncLifetime` (`:9`).
- Host built in `InitializeAsync` (`:13-28`) with `opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TodoHandler)).IncludeType(typeof(MarkTaskCompleteIfBrokenHandler)).IncludeType(typeof(ExamineFirstHandler)).IncludeType(typeof(StoreManyHandler))` then `configureWolverine(opts)`. The handlers under test are **upstream**; the provider only supplies frames.
- `protected abstract void configureWolverine(WolverineOptions opts)` (`:35`).
- `public abstract Task<Todo?> Load(string id)` (`:52`) and `public abstract Task Persist(Todo todo)` (`:54`) — read/write the entity **directly from the store** to verify persistence independently of Wolverine. They MUST target the same collection the generated frames write to.
- Entity POCO `Todo { string Id; string? Name; bool IsComplete; }` (id member named `Id`, **no** `[BsonId]`). The driver's default convention maps `Id` → `_id`.
- ~17 `[Fact]` scenarios: `use_insert_as_return_value`, `use_entity_attribute_with_id` / `_with_entity_id` / `_with_explicit_id`, `use_delete_as_return_value`, `use_generic_action_as_insert`/`_update`/`_store`/`_delete`, `do_nothing_as_generic_action`, `do_nothing_if_storage_action_is_null` / `_if_generic_storage_action_is_null`, `do_not_execute_the_handler_if_the_entity_is_not_found`, `handler_not_required_entity_attributes`, `entity_can_be_used_in_before_methods_...`, `can_use_attribute_on_before_methods`, `use_unit_of_work_as_return_value`.
- **Subclassed by** RavenDb (`external/wolverine/src/Persistence/RavenDbTests/using_storage_return_types_and_entity_attributes.cs:10`), Marten (`MartenTests/...:9`), EF Core, Polecat. **Cosmos appears NOT to subclass it** (implemented-but-not-compliance-tested) — confirm in D1; RavenDb is the cleanest tested template either way.

### Wolverine core — the closest *tested* template: RavenDb storage actions

`external/wolverine/src/Persistence/Wolverine.RavenDb/Internals/RavenDbPersistenceFrameProvider.cs`:
- `CanPersist` (`:56-60`): `persistenceService = typeof(IAsyncDocumentSession); return true;` — **unconditional true**.
- `DetermineStorageActionFrame` (`:115-124`): builds a `MethodCall` to `RavenDbStorageActionApplier.ApplyAction<T>(...).MakeGenericMethod(entityType)` and sets `call.Arguments[1] = action`.
- `DetermineDeleteFrame(Variable variable, ...)` (`:104-107`) → `new DeleteDocumentFrame(variable)`; `DeleteDocumentFrame` (`:148-170`) emits `{session}.Delete({entity});`.
- `RavenDbStorageActionApplier.ApplyAction<T>(IAsyncDocumentSession session, IStorageAction<T> action)` (`:127-145`): null-guards `action.Entity`; `Delete → session.Delete(entity)`; `Insert/Store/Update → await session.StoreAsync(entity)`; `Nothing` falls through (no-op).

### Wolverine core — the Cosmos template (entity-generic frames)

`external/wolverine/src/Persistence/Wolverine.CosmosDb/Internals/CosmosDbPersistenceFrameProvider.cs`:
- `CanPersist` (`:51-55`): `persistenceService = typeof(Container); return true;`.
- **Entity-generic** write frames (no saga assumptions): `DetermineInsertFrame`/`DetermineUpdateFrame`/`DetermineStoreFrame` all return `new CosmosDbUpsertFrame(saga)` (`:67-91`), which emits `await {container}.UpsertItemAsync({document})` (`:140-163`).
- `DetermineDeleteFrame(sagaId, saga, ...)` → `CosmosDbDeleteDocumentFrame` (`:83-86`); `DetermineDeleteFrame(variable, ...)` → `CosmosDbDeleteByVariableFrame` (`:93-96`), which deletes by `{variable}.ToString()` as the id convention (`:193-216`).
- `DetermineStorageActionFrame` (`:98-107`) → `MethodCall` to `CosmosDbStorageActionApplier.ApplyAction<T>(Container, IStorageAction<T>)` (`:110-138`); `Delete → DeleteItemAsync` (best-effort catch), `Insert/Store/Update → UpsertItemAsync`.
- `ApplyTransactionSupport` (`:19-30`): adds `TransactionalFrame`; adds flush postprocessor **only** `if (chain is not SagaChain)`. (Mongo already mirrors this — `MongoDbPersistenceFrameProvider.cs:34-37`.)

### Wolverine core — Tier 2: `ISagaStoreDiagnostics`

- Interface `external/wolverine/src/Wolverine/Persistence/Sagas/ISagaStoreDiagnostics.cs:22-63` (namespace `Wolverine.Persistence.Sagas`), three async members:
  - `Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)`
  - `Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)`
  - `Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count, CancellationToken ct)`
- `RavenDbSagaStoreDiagnostics` (`external/wolverine/src/Persistence/Wolverine.RavenDb/Internals/RavenDbSagaStoreDiagnostics.cs:30-204`): ctor `(IWolverineRuntime runtime, IDocumentStore store)` (`:37-41`); lazy saga index built by walking `runtime.Options.HandlerGraph.Chains.OfType<SagaChain>().Select(c => c.SagaType)` and filtering by `provider.CanPersist(sagaType, container, out _)`, indexed by `FullName` **and** `Name` (`:161-203`); reflection (`MakeGenericMethod`) to load/query a saga by its runtime type; clamps list `count` to `[0,1000]`; serializes saga state for the UI.
- Registration (`external/wolverine/src/Persistence/Wolverine.RavenDb/WolverineRavenDbExtensions.cs:33-36`): `options.Services.AddSingleton<ISagaStoreDiagnostics>(s => new RavenDbSagaStoreDiagnostics(s.GetRequiredService<IWolverineRuntime>(), s.GetRequiredService<IDocumentStore>()));`
- Runtime aggregation: `WolverineRuntime.cs:41,66-68,269` builds a `Lazy<ISagaStoreDiagnostics>` wrapping `AggregateSagaStoreDiagnostics(container.Services.GetServices<ISagaStoreDiagnostics>())`; exposed as `IWolverineRuntime.SagaStorage` (`IWolverineRuntime.cs:44-53`). `AggregateSagaStoreDiagnostics.cs:28-104` fans out / routes by saga-type name. **Cosmos does NOT register `ISagaStoreDiagnostics`.**
- **No unified compliance spec** — each provider has its own integration test (e.g. `RavenDbTests/raven_saga_store_diagnostics_tests.cs:28+`); the aggregator has stub-based tests in `CoreTests/Acceptance/saga_store_diagnostics_tests.cs`.
- `SagaDescriptor` / `SagaInstanceState` / `SagaDescriptorBuilder.Build(handlerGraph, sagaType, providerTag)` are core types — confirm their exact shapes in D2.

### Wolverine core — Tier 3: parity capabilities (closest analogues Cosmos/Raven all DEFER)

- **Multi-tenancy:** `ITenantedMessageSource : ITenantedSource<IMessageStore>` (`IMessageStore.cs:174-177`); `IMessageStore.TenantIds` (`:80`). RDBMS (`SqlServerTenantedMessageStore.cs:15`) + Marten implement real per-tenant databases. **Cosmos & RavenDb leave `TenantIds` empty** (`CosmosDbMessageStore.cs:42`, `RavenDbMessageStore.cs:35`) — no `ITenantedMessageSource`.
- **Durable listeners:** `IListenerStore { RegisterListenerAsync; RemoveListenerAsync; AllListenersAsync; }` (`IListenerStore.cs:15-35`); `NullListenerStore.Instance` no-op (`:43-55`); exposed via `IMessageStore.Listeners` (`:110`). RDBMS implements `RdbmsListenerStore` (`RdbmsListenerStore.cs:25`), built only when `EnableDynamicListeners && Role==Main` (`MessageDatabase.cs:91`). **Cosmos & RavenDb both return `NullListenerStore.Instance`** with explicit "follow-up" comments (`CosmosDbMessageStore.cs:74`, `RavenDbMessageStore.cs:68`). **Mongo today also uses `NullListenerStore` — confirm in D3.**
- **Query-spec frames:** `TryBuildFetchSpecificationFrame` default `false` (`IPersistenceFrameProvider.cs:73-82`); called from `FromQuerySpecificationAttribute.Modify` (`:127-134`). Only **Marten** (`:201-241`) and **EF Core** (`:66-102`) override it (compile-time query objects). **Cosmos, RavenDb, Polecat use the default.**
- **Soft-delete:** `DetermineFrameToNullOutMaybeSoftDeleted` (`:52`), called from `EntityAttribute.cs:170-173`. Only **Marten** implements (`SetVariableToNullIfSoftDeletedFrame`, `MartenPersistenceFrameProvider.cs:196-199,244-279`). **EF Core, Polecat, Cosmos, RavenDb all return `[]`** (`CosmosDbPersistenceFrameProvider.cs:17`, `RavenDbPersistenceFrameProvider.cs:18`) — exactly what Mongo returns today (`MongoDbPersistenceFrameProvider.cs:135-138`).

### Local — the files to change (`src/Wolverine.MongoDB/`)

- `Internals/MongoDbPersistenceFrameProvider.cs` — `CanPersist` is saga-scoped (`:74-83`); `DetermineDeleteFrame(Variable, IServiceContainer)` throws (`:125-128`); `DetermineStorageActionFrame` throws (`:130-133`); `DetermineFrameToNullOutMaybeSoftDeleted` returns `[]` (`:135-138`); saga frame factories at `:98-123`; `ApplyTransactionSupport` skips the commit postprocessor for `SagaChain` (`:34-37`).
- `Internals/SagaFrames.cs` — `MongoSagaOperations` (`LoadSagaAsync<TSaga,TId> where TSaga:class` `:33-41`; `InsertSagaAsync` stamps `Version=1` `:52-59`; `UpdateSagaAsync` OCC `:74-100`; `DeleteSagaAsync` `:108-117`) + the four `*SagaFrame` classes. **The frames resolve `IClientSessionHandle` + `IMongoDatabase` + `CancellationToken` via `FindVariable` and call into the static helper** — the exact template for entity frames.
- `Internals/MongoConstants.cs` — `SagaCollectionPrefix = "wolverine_saga_"` (`:25`), `SagaCollectionName(Type) => prefix + type.Name.ToLowerInvariant()` (`:27-28`).
- `Internals/MongoDbMessageStore.Admin.cs` — `ClearAllAsync` clears system collections and drops every `wolverine_saga_*` collection by prefix (`:83-87`); `RebuildAsync` (`:10-14`).
- `WolverineMongoDbExtensions.cs` — registers `IMessageStore`, an **unkeyed** `IMongoDatabase` (`:59-60`), `InsertFirstPersistenceStrategy<MongoDbPersistenceFrameProvider>()` + `ReferenceAssembly` (`:62-63`).

### Local — the test + demo harness

- `src/Wolverine.MongoDB.Tests/AppFixture.cs` — `[CollectionDefinition("mongodb")]` (`:55`), `DatabaseName = "wolverine_tests"` (`:9`), `IMongoClient Client` (`:15`), `ClearAll() → RebuildAsync()` (`:48-52`), `BuildMessageStore()` (`:45-46`), `mongo:7` `.WithReplicaSet()` Testcontainer (`:23-27`), `net9.0;net10.0` + `UseWolverineSource` switch.
- Custom-test pattern (e.g. `saga_atomicity.cs`): `[Collection("mongodb")]` (`:43`), `TypeLoadMode.Dynamic` (`:45`), `Discovery.DisableConventionalDiscovery().IncludeType(...)` (`:66-68`), `UseMongoDbPersistence(AppFixture.DatabaseName)` (`:71`), `LocalQueueFor<T>().UseDurableInbox()` (`:76-77`), `host.TrackActivity().Timeout(...).InvokeMessageAndWaitAsync(...)` (`:104-105`), Shouldly asserts (`:109-114`), direct Mongo read (`:89-91`).
- `src/Wolverine.MongoDB.Tests/MongoDbSagaHost.cs` — the saga compliance host (`BuildHostAsync<TSaga>` `:27-59`; Solo `:36`; `TypeLoadMode.Dynamic` `:45`; `AddSingleton<IMongoClient>(Client)` `:57`; `UseMongoDbPersistence` `:58`; `LoadState` overloads `:62-68`; `LoadById<T,TId>` direct read `:74-79`) — the structural template for an entity-compliance host.
- Demo (`demo/`): `OrderDemo.{Api,Application,Contracts,Domain,Infrastructure}` + `OrderDemo.IntegrationTests`. `Program.cs` — `UseMongoDbPersistence` (`:40`), `MultipleHandlerBehavior.Separated` (`:29`), `Discovery.IncludeAssembly` (`:32-33`), RabbitMQ (`:59-65`), `AutoApplyTransactions` (`:101`). `PlaceOrderHandler` takes `IClientSessionHandle` + repositories. `OrderRepository.UpdateAsync` is the version-guarded `ReplaceOneAsync` OCC precedent. `OrderSummaryProjector` is the read-model precedent. `OrdersFixture` — `CreateDatabaseName()` (`:63`), `CreateHostAsync` (`:80-128`), Solo (`:91`), `MultipleHandlerBehavior.Separated` (`:98`), `LocalQueueFor<T>().UseDurableInbox()` (`:114-121`), `AutoApplyTransactions` (`:123`), FluentAssertions. **No `MongoDbUnitOfWork` example handler exists yet** (FOLLOWUPS) — Tier 4.
- `FOLLOWUPS.md` — the Tier-4 backlog: unkeyed `IMongoDatabase`; `INodeAgentPersistence.ClearAllAsync` scope; node-number reuse; pre-1.0 index migration; optional lease fencing token; `IListenerStore` still `NullListenerStore`; demo `MongoDbUnitOfWork` example; demo saga-cascade consumer; saga-specific indexes; multinode leadership compliance re-eval (one-off 13/13; needs 5× green before un-gating); `ISagaStoreDiagnostics` not implemented.

---

## Lead Open Design Decisions (resolved in D6 for Tier 1; per-item in D3 for Tier 3)

**LD1 — `CanPersist` scope for generic entities.** The saga plan deliberately scoped `CanPersist` to `Saga` subclasses (R9) *because* `DetermineStorageActionFrame` threw — advertising generic persistence then would blow up at codegen. Once Tier 1 implements the storage-action and delete-by-variable frames, that constraint is gone. **Recommendation: broaden `CanPersist` to unconditional `true`** (matching Cosmos `:51-55` and RavenDb `:56-60`), and make the **frame factories** branch saga-vs-entity. This is the upstream-consistent choice and is required for `[Entity]` loads (which key on `CanPersist(parameterType)`). The provider is registered via `InsertFirstPersistenceStrategy`, so in a Mongo-only app it is always selected first — exactly like Cosmos/Raven in their apps. (Alternative considered: keep a narrower predicate. Rejected — it would make `[Entity]`/`IStorageAction<T>` silently unavailable for app types and diverge from Cosmos/Raven. D6 records the final call.)

**LD2 — entity write semantics: upsert-for-all vs insert/replace split.** Sagas need divergent insert (stamp `Version`) vs update (OCC). **Plain entities do NOT carry `Saga.Version`**, so the cleanest, Cosmos-faithful choice is **upsert for Insert/Update/Store** (`ReplaceOneAsync(IsUpsert=true)`) and `DeleteOne` for Delete — no optimistic concurrency. **Recommendation: mirror Cosmos** (`CosmosDbUpsertFrame` upserts for all three write actions). Entity OCC is explicitly a **non-goal** for Tier 1 (the demo's repository pattern remains the path for app-controlled OCC; document it). D6 records this and whether `Insert<T>` uses `InsertOneAsync` (stricter, surfaces duplicate-key) or upsert (Cosmos parity) — default **upsert** for parity and simplicity, since the compliance facts do not require insert-vs-upsert distinction.

**LD3 — entity collection naming + id extraction.** Sagas use `wolverine_saga_<lowered-type>`. App entities should use an idiomatic, **un-prefixed** convention (recommend `MongoConstants.EntityCollectionName(Type) => type.Name.ToLowerInvariant()`, e.g. `todo`). The compliance host's `Load`/`Persist` and the demo must target the **same** collection the frames write to. For upsert/delete the frames need the entity's `_id` value generically: resolve it via the driver class map — `BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap` getter (idiomatic MongoDB), NOT Cosmos's `.ToString()` hack. D6 finalizes the naming convention and the id-extraction mechanism (and whether `[Entity]` collection cleanup belongs in `ClearAllAsync`).

**LD4 — Tier 3, four explicit implement-vs-defer calls (D3).** Default for all four is **document-as-non-goal with rationale**, because Cosmos & RavenDb — the two closest document-store analogues — defer all four. The single nuance is durable listeners, where a no-op `NullListenerStore` is already the de-facto state and a real impl is cheap; D3 recommends keep-as-`NullListenerStore` + document, with a clearly-scoped optional follow-up. See Tier 3 below for the per-item recommendation.

---

## Git & PR Workflow (one branch + one PR per task)

Identical mechanics to the saga + multinode plans — see `2026-06-09-multinode-support.md` → "Git & PR Workflow". Summary:

```bash
# Start: isolated worktree off CURRENT main (includes merged prerequisite tasks)
rtk git fetch origin
rtk git worktree add .worktrees/<branch-name> -b <branch-name> origin/main
cd .worktrees/<branch-name>          # every later step runs HERE, incl. the Commit step

# ... execute the task's steps, ending with its Commit step ...

rtk git push -u origin <branch-name>
rtk gh pr create --base main --head <branch-name> \
  --title "<PR title>" \
  --body "<what was missing, what changed, how it's tested. Reference docs/superpowers/plans/2026-06-21-persistence-suite-completion.md Task <id>.>"
rtk gh pr checks --watch

# After MERGE: drop the worktree from the main checkout
cd <repo-root>
rtk git worktree remove .worktrees/<branch-name>
```

- `--head <branch-name>` is required. Commit messages end with `Co-Authored-By: Claude <noreply@anthropic.com>`; PR bodies end with the `🤖 Generated with [Claude Code](https://claude.com/claude-code)` line.
- **A dependent task starts only after its dependency's PR is merged.** Each PR must be independently green: the task's own tests + the full library suite (and the demo suite where the task touches it).
- **Verify PR check state with `gh pr view --json statusCheckRollup`** (the rtk `--watch` tail can mislead). After an `Internals` API change, delete the gitignored stale codegen (`rm -rf src/Wolverine.MongoDB.Tests/Internal/Generated`) before a clean local build.
- When finishing a task, **update this plan doc** in the task's PR (tick step checkboxes + the status-table row).

---

## Task Table

| Task | Branch | PR title | Depends on | Blocking status | Model |
|---|---|---|---|---|---|
| **D1** ✅ | `docs/entity-persistence-discovery` | docs: Tier 1 — entity/storage-action API + Cosmos/Raven reference | Prereqs merged | **Done — PR #106 merged** | Sonnet |
| **D2** ✅ | `docs/saga-diagnostics-discovery` | docs: Tier 2 — ISagaStoreDiagnostics API + Raven reference | Prereqs merged | **Done — PR #110 merged** | Sonnet |
| **D3** ✅ | `docs/parity-decisions-discovery` | docs: Tier 3 — parity capabilities + implement-vs-defer recommendation | Prereqs merged | **Completed** | Sonnet |
| **D4** ✅ | `docs/tier4-followups-audit` | docs: Tier 4 — FOLLOWUPS audit + multinode un-gate scoping | Prereqs merged | **Done — PR #108 merged** | Sonnet |
| **D5** ✅ | `docs/demo-and-test-inventory` | docs: demo flow design + cross-tier test inventory | Prereqs merged | **Done** — PR #109 | Sonnet |
| **D6** ✅ | `docs/entity-document-model-design` | docs: Tier 1 — entity document model + frame-branching design (GATE) | **D1, D5** | **Done** — unblocks T1.1 | **Opus / Fable 5** |
| **T1.1** ✅ | `feat/entity-storage-action-persistence` | feat: generic entity + IStorageAction persistence | **D6** | **Done** — unblocks T1.2/T1.3/T4.3 | **Opus / Fable 5** |
| **T1.2** ✅ | `test/entity-atomicity-coexistence` | test: entity atomicity + saga/entity coexistence regression | **T1.1** | **Done** — 4 facts green net9/net10; full single-node suite 171 | **Opus / Fable 5** |
| **T1.3** ✅ | `demo/entity-and-storage-action` | demo: `[Entity]`/`IStorageAction` handler + safety-net tests | **T1.1** | **Done** — 39/39 demo tests green | Sonnet |
| **T2.1** ✅ | `feat/saga-store-diagnostics` | feat: MongoDbSagaStoreDiagnostics + registration | **D2** | **Done** — unblocks T2.2 | **Opus / Fable 5** |
| **T2.2** ✅ | `test/saga-store-diagnostics` | test: MongoDb saga store diagnostics | **T2.1** | **Done** — 6/6 facts green net9/net10; full single-node suite 177 | Sonnet |
| **T3.1** ✅ | `docs/parity-non-goals` | docs: parity capabilities — non-goals + rationale (+ optional listener stub note) | **D3** | **Done** | Sonnet |
| **T4.1** ✅ | `demo/unit-of-work-example` | demo: MongoDbUnitOfWork example handler + test | **D5** | **Done** — RecordOrderAuditHandler + OrderAuditTests, 41/41 demo suite green | Sonnet |
| **T4.2** ✅ | `demo/saga-cascade-consumer` | demo: fulfillment read-model projector for saga cascades | **D5** | **Done** — FulfillmentStatusProjector + SagaFlowTests cascade fact, 42/42 demo suite green | Sonnet |
| **T4.3** ✅ | `feat/mongo-database-registration` | feat/docs: resolve unkeyed IMongoDatabase registration | **D6, T1.1** | **Done** — route (a): documented as a consumer constraint (README + CLAUDE + FOLLOWUPS); no code change | **Opus / Fable 5** |
| **T4.4** ✅ | `feat/node-clearall-scope` | feat: INodeAgentPersistence.ClearAllAsync scope | **D4** | **Done** — documented as intentionally narrow (D4's classification); no code-behavior change; 177/177 net9/net10 | Sonnet |
| **T4.5** | `test/multinode-leadership-ungate` | test: re-evaluate + un-gate multinode leadership compliance | **D4** | Blocked by: D4 | **Opus 4.8** |
| **T4.6** | `docs/pre-1.0-hardening-backlog` | docs: pre-1.0 hardening backlog (node reuse, index migration, fencing, saga indexes) | **D4** | Blocked by: D4 | Sonnet |
| **V1** | `test/suite-completion-regression` | test: full cross-feature regression sweep | **T1.1–T4.6 merged** | Blocked by: all impl/test tasks | Sonnet |
| **V2** | `docs/suite-completion-sweep` | docs: suite completion + upstream-contribution notes | **T1.1–T4.6 merged** | Blocked by: all impl tasks (drafted in parallel) | Sonnet |
| **V3** | *(no branch/PR)* | final verification on `main` (+ optional release) | **V1, V2 merged** | Blocked by: V1, V2 | Sonnet |

> **Recommended execution order.** The five discovery tasks (D1–D5) run **fully in parallel** the moment the plan PR merges. D6 (the Tier-1 design gate) follows D1+D5. **T1.1 is the single most important task and the head of the critical path.** Tiers 2, 3, and 4 are **independent tracks** that need only their own discovery (D2/D3/D4/D5) — they can proceed in parallel with the entire Tier-1 chain. Once T1.1 merges, T1.2/T1.3 and T4.3 fan out. V1/V2 close out once everything is merged; V3 is the final on-`main` gate.

## Model Guidance

The risk is **code-generation correctness + frame ordering** (Tier 1, Tier 2) and **flaky multi-node timing** (T4.5), not transcription (Agent `model`: `sonnet`, `opus`, `fable`):

- **Opus / Fable 5 mandatory** for **D6, T1.1, T1.2**: branching the frame factories saga-vs-entity without regressing saga OCC, emitting correct entity frames that run on the transaction session, and proving atomicity is iterative work that requires reading the generated handler source (`codeFor<T>()` / `GeneratedCodeOutputPath`). A subtly wrong frame compiles and passes *some* compliance facts while corrupting atomicity or silently breaking sagas.
- **Opus / Fable 5** for **T2.1** (reflection + handler-graph walking; AOT-annotation correctness) and **T4.3** (changing `IMongoDatabase` registration affects every saga/entity frame's variable resolution — high blast radius).
- **Opus 4.8** for **T4.5**: cross-node leadership timing is the same flaky-diagnosis class as multinode Task 7 and saga S14 — reason about *why* before turning a knob; no skips, no retries-as-bandaid, no assertion-weakening; **5× consecutive green on net9.0 + net10.0** before un-gating.
- **Sonnet** for D1–D5 (research/writing), T1.3/T2.2 (tests against a green oracle), T3.1/T4.1/T4.2/T4.4/T4.6 (docs/demo/well-specified), V1/V2/V3.
- **Do not use Haiku** anywhere in this plan.
- **Escalation rule:** two non-obvious verification failures, or a broken plan assumption (an API that differs from the Verified Facts), means **stop and report** — re-dispatch on Fable 5 with the failure context rather than improvising a different Wolverine API. For T1.1/T2.1/T4.5 specifically, if green cannot be reached after the listed levers, write up the generated code + failing facts and stop.
- **Code review between tasks:** Opus/Fable 5, with extra scrutiny on T1.1/T1.2/T2.1/T4.3 — atomicity/regression bugs that pass a green suite are exactly what review must catch.

---

## File Structure Overview

| File | Change | Task |
|---|---|---|
| `src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs` | Broaden `CanPersist`; branch `DetermineInsertFrame`/`DetermineUpdateFrame`/`DetermineStoreFrame`/`DetermineLoadFrame` saga-vs-entity; implement `DetermineDeleteFrame(Variable,…)` + `DetermineStorageActionFrame` | T1.1 |
| `src/Wolverine.MongoDB/Internals/EntityFrames.cs` | **New** — `MongoEntityOperations` (load/upsert/insert/delete + `ApplyStorageActionAsync<T>`) + `LoadEntityFrame`, `MongoUpsertEntityFrame`, `MongoDeleteEntityByVariableFrame` (mirror `SagaFrames.cs`) | T1.1 |
| `src/Wolverine.MongoDB/Internals/MongoConstants.cs` | Add `EntityCollectionName(Type)` (un-prefixed convention) | T1.1 |
| `src/Wolverine.MongoDB/WolverineMongoDbExtensions.cs` | Register `ISagaStoreDiagnostics` (T2.1); resolve/keep `IMongoDatabase` registration (T4.3) | T2.1, T4.3 |
| `src/Wolverine.MongoDB/Internals/MongoDbSagaStoreDiagnostics.cs` | **New** — `ISagaStoreDiagnostics` impl (mirror `RavenDbSagaStoreDiagnostics`) | T2.1 |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.NodeAgents.cs` | `ClearAllAsync` scope decision | T4.4 |
| `src/Wolverine.MongoDB.Tests/storage_action_compliance.cs` | **New** — single self-contained `: StorageActionCompliance` subclass with `configureWolverine`/`Load`/`Persist` (mirrors RavenDb's `using_storage_return_types_and_entity_attributes.cs`; **no** separate host file — `StorageActionCompliance` is not generic over a host like `ISagaHost`) | T1.1 |
| `src/Wolverine.MongoDB.Tests/entity_atomicity.cs` | **New** — entity write + outbox atomicity; saga/entity coexistence regression | T1.2 |
| `src/Wolverine.MongoDB.Tests/saga_store_diagnostics.cs` | **New** — diagnostics integration test (mirror `raven_saga_store_diagnostics_tests`) | T2.2 |
| `src/Wolverine.MongoDB.Tests/leadership_election_compliance.cs` | Remove `#if RUN_MULTINODE` guard if 5× green | T4.5 |
| `demo/src/OrderDemo.{Contracts,Application,Infrastructure,Api}/...` | Entity + storage-action handler (T1.3); `MongoDbUnitOfWork` handler (T4.1); fulfillment projector (T4.2) | T1.3, T4.1, T4.2 |
| `demo/tests/OrderDemo.IntegrationTests/...` | Safety-net tests for the above | T1.3, T4.1, T4.2 |
| `README.md`, `CLAUDE.md`, `FOLLOWUPS.md`, `CHANGELOG.md`, `demo/README.md`, `demo/CLAUDE.md` | Tier 3 non-goals (T3.1), Tier 4 backlog (T4.6), final sweep + upstream notes (V2) | T3.1, T4.6, V2 |
| `.github/workflows/ci.yml` | Add multinode leadership to the multinode step (T4.5); confirm coverage (V1) | T4.5, V1 |
| `docs/superpowers/plans/2026-06-21-*.md` | This plan, the prompts, and the D1–D6 discovery/design notes | all |

---

## Phase 0 — Discovery & Design (parallelizable)

### Task D1: Tier 1 — entity/storage-action API + Cosmos/Raven reference

- **Goal:** Produce a precise, current map of the generic-persistence contract and the reference implementations, so the Tier-1 design and implementation never guess.
- **Scope:** Read-only confirmation against the pinned submodule + local source of the "Verified API Facts" Tier-1 entries. Confirm: the `Insert<T>`/`Delete<T>`/`Update<T>`/`Store<T>` → typed-factory routing vs `IStorageAction<T>` → `DetermineStorageActionFrame`; the `[Entity]` load path (`EntityAttribute.cs:145,165,170-173`); the `StorageActionCompliance` member signatures + the 17 facts + the upstream `TodoHandler` set; **whether `CosmosDbTests` subclasses `StorageActionCompliance`** (the agent did not find one — resolve it); the Cosmos (`CosmosDbUpsertFrame`, `CosmosDbDeleteByVariableFrame`, `CosmosDbStorageActionApplier`) and RavenDb (`DeleteDocumentFrame`, `RavenDbStorageActionApplier`) shapes. No code changes.
- **Expected output:** `docs/superpowers/plans/2026-06-21-entity-persistence-discovery.md` — signatures + file:line for every Tier-1 member; the routing table (which return type → which provider method); the compliance contract; an explicit note on the one design tension: **Mongo's `DetermineInsertFrame`/`DetermineUpdateFrame` are saga-specific (`InsertSagaFrame`/`UpdateSagaFrame`) and must branch saga-vs-entity** (Cosmos's are generic). Flags any drift from the Verified Facts.
- **Files/areas likely to change:** docs only.
- **Dependencies:** none (prereqs merged).
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Read/confirm `IPersistenceFrameProvider.cs`, `EntityAttribute.cs`, `IStorageAction.cs`, `Insert.cs`/`Delete.cs`/`Update.cs`/`Store.cs`, `StorageAction.cs`, `StorageActionCompliance.cs`.
- [x] **Step 2:** Read/confirm `CosmosDbPersistenceFrameProvider.cs` + `RavenDbPersistenceFrameProvider.cs` storage-action members; locate the RavenDb (`using_storage_return_types_and_entity_attributes.cs`) compliance subclass and check for a Cosmos one.
- [x] **Step 3:** Read/confirm the local `MongoDbPersistenceFrameProvider.cs` throwing members + `SagaFrames.cs` template; write the notes doc; flag drift. Commit (`docs: Tier 1 entity/storage-action discovery`).

### Task D2: Tier 2 — `ISagaStoreDiagnostics` API + Raven reference

- **Goal:** Pin the exact `ISagaStoreDiagnostics` contract, the supporting core types, and the RavenDb implementation + registration the Mongo impl will mirror.
- **Scope:** Read-only. Confirm the interface (`ISagaStoreDiagnostics.cs:22-63`); the exact shapes of `SagaDescriptor`, `SagaInstanceState`, and `SagaDescriptorBuilder.Build(...)`; `RavenDbSagaStoreDiagnostics` (ctor, saga-index construction, reflection dispatch, count clamp, state serialization); the DI registration (`WolverineRavenDbExtensions.cs:33-36`); the runtime aggregation (`WolverineRuntime.cs` + `AggregateSagaStoreDiagnostics`); the per-provider test pattern (`raven_saga_store_diagnostics_tests.cs`). Note that **there is no unified compliance spec** and Cosmos does not implement it.
- **Expected output:** `docs/superpowers/plans/2026-06-21-saga-diagnostics-discovery.md` — signatures + file:line; a mapping of each RavenDb method to its MongoDB equivalent (RavenDb `IDocumentStore` → Mongo `IMongoClient`/`IMongoDatabase`; `session.LoadAsync<TSaga>` → `collection.Find(Eq("_id", id))`; `session.Query<TSaga>().Take(n)` → `collection.Find(Empty).Limit(n)`); how the Mongo saga collection name (`wolverine_saga_<lowered>`) is derived per saga type; the registration line to add.
- **Files/areas likely to change:** docs only.
- **Dependencies:** none.
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Read `ISagaStoreDiagnostics.cs`, `SagaDescriptor`/`SagaInstanceState`/`SagaDescriptorBuilder`, `RavenDbSagaStoreDiagnostics.cs`, `WolverineRavenDbExtensions.cs`, `AggregateSagaStoreDiagnostics.cs`, `raven_saga_store_diagnostics_tests.cs`.
- [x] **Step 2:** Write the notes doc with the RavenDb→MongoDB method mapping. Commit (`docs: Tier 2 ISagaStoreDiagnostics discovery`).

### Task D3: Tier 3 — parity capabilities + implement-vs-defer recommendation

- **Goal:** For each of the four parity capabilities, record the exact contract, what Cosmos/Raven do, and a firm **implement-vs-defer recommendation** with rationale.
- **Scope:** Read-only confirmation of the Verified Facts Tier-3 entries: multi-tenancy (`ITenantedMessageSource`, `IMessageStore.TenantIds`); durable listeners (`IListenerStore`, `NullListenerStore`, `IMessageStore.Listeners`, `RdbmsListenerStore`); query-spec (`TryBuildFetchSpecificationFrame` default + Marten/EF overrides); soft-delete (`DetermineFrameToNullOutMaybeSoftDeleted` + Marten's `SetVariableToNullIfSoftDeletedFrame`). **Confirm what Mongo's `MongoDbMessageStore` currently exposes for `TenantIds` and `Listeners`** (does it already return `NullListenerStore`? empty `TenantIds`?).
- **Expected output:** `docs/superpowers/plans/2026-06-21-parity-decisions-discovery.md` — a table per capability (contract / Cosmos / Raven / Marten-RDBMS / **current Mongo** / recommendation). The recommendations to validate in T3.1:
  - **Multi-tenancy → DEFER (document-as-non-goal).** Cosmos/Raven leave `TenantIds` empty; RDBMS multi-tenancy is connection-string-based. Document app-level tenant-field routing as the path.
  - **Durable listeners → DEFER, keep `NullListenerStore` (document).** Cosmos/Raven both no-op with "follow-up" comments. Note the cheap optional follow-up shape (a `wolverine_listeners` collection, URI unique key) if demand appears.
  - **Query-spec frames → DEFER (document-as-non-goal).** Marten/EF-only (compile-time query objects); no document-store analogue.
  - **Soft-delete → DEFER (document-as-non-goal).** Only Marten implements; would require prescribing a field convention. Document the app-level `is_deleted` pattern.
- **Files/areas likely to change:** docs only.
- **Dependencies:** none.
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Confirm each interface + which providers implement vs default; read the current `MongoDbMessageStore` `TenantIds`/`Listeners` members.
- [x] **Step 2:** Write the per-capability table + recommendation. Commit (`docs: Tier 3 parity decisions discovery`).

### Task D4: Tier 4 — FOLLOWUPS audit + multinode un-gate scoping

- **Goal:** Triage every `FOLLOWUPS.md` item against the current code so each Tier-4 task has a precise, verified starting point, and scope the multinode-leadership un-gate.
- **Scope:** Read-only. For each FOLLOWUPS item (unkeyed `IMongoDatabase`; `ClearAllAsync` scope; node-number reuse; index migration; lease fencing; `IListenerStore`; demo `MongoDbUnitOfWork`; demo saga-cascade consumer; saga indexes; multinode leadership re-eval): confirm the current behavior in code (file:line) and classify as **implement / document-as-non-goal / verify**. For the multinode item, read `2026-06-16-task6-multinode-compliance-findings.md` and the gated `leadership_election_compliance.cs`, and define the exact un-gate procedure (which `#if RUN_MULTINODE` guard, how to run 5× per TFM, which CI step to add the suite to).
- **Expected output:** `docs/superpowers/plans/2026-06-21-tier4-followups-audit.md` — a table mapping each FOLLOWUPS item → Tier-4 task (T4.1–T4.6) → classification → file:line evidence; the multinode un-gate runbook.
- **Files/areas likely to change:** docs only.
- **Dependencies:** none.
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Walk `FOLLOWUPS.md`; confirm each item's current state in code.
- [x] **Step 2:** Write the audit table + multinode un-gate runbook. Commit (`docs: Tier 4 FOLLOWUPS audit`).

### Task D5: Demo flow design + cross-tier test inventory

- **Goal:** Design the user-facing demo additions and a complete test inventory across all tiers, so demo contracts/handlers/fixtures and test skeletons can be authored early, in parallel with implementation.
- **Scope:** Design-only. Specify:
  - **Tier-1 demo:** a small entity that is naturally written via `[Entity]` + `IStorageAction<T>` rather than the repository pattern — recommend an **`OrderNote`** (or `CustomerProfile`) with `Guid`/`string` id, handlers `Handle(AddOrderNoteCommand) → Insert<OrderNote>`, `Handle(EditOrderNoteCommand, [Entity] OrderNote) → Update<OrderNote>`, `Handle(DeleteOrderNoteCommand, [Entity] OrderNote) → Delete<OrderNote>`. This showcases the new surface side-by-side with the existing repository + `IClientSessionHandle` pattern.
  - **Tier-4 demo:** the `MongoDbUnitOfWork` example handler (a variant write path — e.g. a `RecordOrderAuditCommand` handler taking `MongoDbUnitOfWork` and writing through `Collection<T>()`), and the fulfillment read-model projector consuming the saga's `FulfillmentShippedEvent`/`FulfillmentCompletedEvent` cascades (records a delivery status the `OrderSummary` does not track).
  - **Test inventory:** map each capability → library test (subclass compliance where it exists: `StorageActionCompliance` for Tier 1; per-provider integration test for Tier 2; none for Tier 3/most Tier 4) and/or demo test (file + assertion). Confirm the entity collection-naming convention the host/`Load`/`Persist` must mirror (feeds D6).
- **Expected output:** `docs/superpowers/plans/2026-06-21-demo-and-test-inventory.md` — entity/UoW/projector contracts + handler signatures, and a flow→test table.
- **Files/areas likely to change:** docs only.
- **Dependencies:** none to start; **refine the entity shape after D6** (collection naming / id type).
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Draft the Tier-1 entity + Tier-4 UoW/projector contracts + handler signatures, reusing existing demo events where possible.
- [x] **Step 2:** Build the cross-tier flow→test table. Commit (`docs: demo flow design + cross-tier test inventory`).

### Task D6: Tier 1 — entity document model + frame-branching design (DESIGN GATE)

- **Goal:** Make and record the binding Tier-1 decisions every implementation step depends on: `CanPersist` scope, the saga-vs-entity frame-branching rule, entity write semantics, collection naming, id extraction, session enlistment, and the soft-delete/`[Entity]`-not-found behavior.
- **Scope:** Design-only synthesis of D1 + D5. Resolve LD1–LD3 explicitly:
  1. **`CanPersist`:** broaden to unconditional `true` (LD1). State the saga-preservation invariant: the **frame factories** branch, not `CanPersist`.
  2. **Frame-branching rule:** in `DetermineInsertFrame`/`DetermineUpdateFrame`/`DetermineStoreFrame`/`DetermineLoadFrame`, branch on `variable.VariableType.CanBeCastTo<Saga>()` (resp. `sagaType.CanBeCastTo<Saga>()` for load) — `Saga` → existing saga frame (untouched); else → new entity frame. `DetermineDeleteFrame(Variable, …)` (entity overload) and `DetermineStorageActionFrame` are entity-only (sagas use the two-variable delete + explicit insert/update).
  3. **Entity write semantics (LD2):** upsert (`ReplaceOneAsync(IsUpsert=true)`) for Insert/Update/Store; `DeleteOne` for Delete; `Nothing` → no-op. **No entity OCC** (explicit non-goal; repository pattern remains the OCC path). State whether `Insert<T>` uses `InsertOneAsync` or upsert (default upsert, Cosmos parity).
  4. **Collection naming + id extraction (LD3):** `MongoConstants.EntityCollectionName(Type) => type.Name.ToLowerInvariant()` (un-prefixed). Id value via `BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap` getter. The compliance host's `Load`/`Persist` and the demo MUST use `EntityCollectionName`. Decide whether `[Entity]` collections need `ClearAllAsync` cleanup (recommend **no** — the compliance host clears its own `todo` collection; app entity collections are not Wolverine system collections).
  5. **Session enlistment / atomicity:** entity frames resolve the `TransactionalFrame`'s `IClientSessionHandle` + `IMongoDatabase` + `CancellationToken` exactly like the saga frames; the `DetermineStorageActionFrame` `MethodCall` lets Wolverine resolve those args (set only `Arguments[i] = action`). Confirm the write lands inside the try-block before commit so the entity write + outbox commit atomically (verify in generated code in T1.1).
  6. **`[Entity]` not-found + soft-delete:** the not-found behavior (`Required`) is **core** — the provider load frame just returns `null`; no provider work needed. `DetermineFrameToNullOutMaybeSoftDeleted` stays `[]` (Tier 3 non-goal). Confirm the `do_not_execute_the_handler_if_the_entity_is_not_found` fact passes with a null-returning load frame.
  7. **Upstream-readiness:** note that this matches Cosmos/Raven (entity-generic frames + unconditional `CanPersist`) — a clean upstream contribution; the Mongo delta is `_id`-class-map id extraction instead of Cosmos's `.ToString()`.
- **Expected output:** `docs/superpowers/plans/2026-06-21-entity-document-model-design.md` — decisions 1–7 with rationale and the exact frame/collection contracts T1.1 implements.
- **Files/areas likely to change:** docs only.
- **Dependencies:** **D1, D5.**
- **Blocking status:** **Blocked by: D1, D5.**

- [x] **Step 1:** Synthesize D1 + D5; resolve decisions 1–7.
- [x] **Step 2:** Write the design doc; state the chosen options unambiguously. Commit (`docs: Tier 1 entity document model + frame-branching design`).

---

## Phase 1 — Tier 1: Generic Entity / Side-Effect Persistence (CRITICAL PATH)

### Task T1.1: Generic entity + `IStorageAction<T>` persistence

- **Goal:** Turn the throwing generic-persistence stubs into working frames so `[Entity]` parameter loads and `Insert<T>`/`Update<T>`/`Store<T>`/`Delete<T>`/`IStorageAction<T>` return values persist through MongoDB inside the existing transaction — **without regressing saga behavior**. Pass the upstream `StorageActionCompliance` suite.
- **Scope:** `MongoDbPersistenceFrameProvider` (broaden `CanPersist`; branch the frame factories; implement `DetermineDeleteFrame(Variable,…)` + `DetermineStorageActionFrame`), a new `Internals/EntityFrames.cs` (mirror `SagaFrames.cs`), `MongoConstants.EntityCollectionName`, and — **in this branch so the PR ships green** — the `storage_action_compliance` subclass (a single self-contained file; `StorageActionCompliance` is configured via `configureWolverine`/`Load`/`Persist` overrides — there is **no** separate host file like `MongoDbSagaHost`). **Preserve all saga/inbox/outbox/transaction behavior.**
- **Expected output:** A handler returning `Insert<Todo>` etc., or taking `[Entity] Todo`, generates code that — inside the `TransactionalFrame` session — loads/upserts/deletes the entity then commits+flushes atomically. `storage_action_compliance : StorageActionCompliance` is green (all ~17 facts). All four saga compliance suites + `saga_atomicity` + `saga_optimistic_concurrency` still green. Full library suite green on net9.0 + net10.0.
- **Files/areas likely to change:** `MongoDbPersistenceFrameProvider.cs`, new `Internals/EntityFrames.cs`, `MongoConstants.cs`, new `src/Wolverine.MongoDB.Tests/storage_action_compliance.cs`.
- **Dependencies:** **D6.**
- **Blocking status:** **Blocked by: D6.**

Implementation shape (adapt from Cosmos + the existing `SagaFrames.cs`; confirm generated order with `codeFor<T>()`):

```csharp
// MongoDbPersistenceFrameProvider.cs — broaden CanPersist (saga preservation moves to the frame factories)
public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
{
    persistenceService = typeof(IMongoDatabase);
    // Now that DetermineStorageActionFrame + DetermineDeleteFrame(Variable,…) are implemented,
    // advertise generic persistence like Cosmos (:51-55) and RavenDb (:56-60). The saga-vs-entity
    // distinction is handled in the frame factories below, NOT here. Required for [Entity] loads,
    // which key on CanPersist(parameterType).
    return true;
}

// Branch each write/load factory: Saga subclasses keep the version-aware saga frames untouched.
public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    => saga.VariableType.CanBeCastTo<Saga>() ? new InsertSagaFrame(saga) : new MongoUpsertEntityFrame(saga);

public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    => saga.VariableType.CanBeCastTo<Saga>() ? new UpdateSagaFrame(saga) : new MongoUpsertEntityFrame(saga);

public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    => saga.VariableType.CanBeCastTo<Saga>() ? DetermineUpdateFrame(saga, container) : new MongoUpsertEntityFrame(saga);

public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    => sagaType.CanBeCastTo<Saga>() ? new LoadSagaFrame(sagaType, sagaId) : new LoadEntityFrame(sagaType, sagaId);

// Generic single-variable delete (Delete<T> return) — was throwing.
public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    => new MongoDeleteEntityByVariableFrame(variable);

// Generic IStorageAction<T> return — was throwing. Mirror Cosmos's MethodCall pattern.
public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
{
    var method = typeof(MongoEntityOperations).GetMethod(nameof(MongoEntityOperations.ApplyStorageActionAsync))!
        .MakeGenericMethod(entityType);
    var call = new MethodCall(typeof(MongoEntityOperations), method);
    call.Arguments[2] = action; // (IMongoDatabase, IClientSessionHandle, IStorageAction<T> action, CancellationToken)
    return call;
}
```

```csharp
// Internals/EntityFrames.cs — mirrors MongoSagaOperations/SagaFrames.cs. Operations run on the
// TransactionalFrame session so an entity write commits atomically with the outbox. Entities have
// NO Saga.Version, so writes are plain upserts (Cosmos parity) — no optimistic concurrency.
public static class MongoEntityOperations
{
    public static Task<T> LoadAsync<T, TId>(IMongoDatabase db, IClientSessionHandle session, TId id, CancellationToken ct)
        where T : class
        => db.GetCollection<T>(MongoConstants.EntityCollectionName(typeof(T)))
             .Find(session, Builders<T>.Filter.Eq("_id", id)).FirstOrDefaultAsync(ct);

    public static Task UpsertAsync<T>(IMongoDatabase db, IClientSessionHandle session, T entity, CancellationToken ct)
    {
        var collection = db.GetCollection<T>(MongoConstants.EntityCollectionName(typeof(T)));
        return collection.ReplaceOneAsync(session, Builders<T>.Filter.Eq("_id", IdOf(entity)), entity,
            new ReplaceOptions { IsUpsert = true }, ct);
    }

    public static Task DeleteAsync<T>(IMongoDatabase db, IClientSessionHandle session, T entity, CancellationToken ct)
        => db.GetCollection<T>(MongoConstants.EntityCollectionName(typeof(T)))
             .DeleteOneAsync(session, Builders<T>.Filter.Eq("_id", IdOf(entity)), cancellationToken: ct);

    public static Task ApplyStorageActionAsync<T>(IMongoDatabase db, IClientSessionHandle session,
        IStorageAction<T> action, CancellationToken ct)
    {
        if (action.Entity is null) return Task.CompletedTask;
        return action.Action switch
        {
            StorageAction.Delete => DeleteAsync(db, session, action.Entity, ct),
            StorageAction.Insert or StorageAction.Update or StorageAction.Store => UpsertAsync(db, session, action.Entity, ct),
            _ => Task.CompletedTask, // Nothing
        };
    }

    // _id value, idiomatic-Mongo (NOT Cosmos's entity.ToString()): the driver class map knows the id member.
    private static object IdOf<T>(T entity)
        => MongoDB.Bson.Serialization.BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap?.Getter(entity)
           ?? throw new InvalidOperationException(
               $"{typeof(T).FullNameInCode()} has no mapped _id member; generic MongoDB persistence requires an Id member.");
}
// LoadEntityFrame / MongoUpsertEntityFrame / MongoDeleteEntityByVariableFrame: AsyncFrame classes that
// FindVariable(IClientSessionHandle, IMongoDatabase, CancellationToken) and emit a call to the helper
// above — structurally identical to LoadSagaFrame/UpdateSagaFrame/DeleteSagaFrame in SagaFrames.cs.
```

```csharp
// src/Wolverine.MongoDB.Tests/storage_action_compliance.cs — single self-contained file (StorageActionCompliance
// is NOT generic over a host like ISagaHost; configure it via configureWolverine/Load/Persist directly).
[Collection("mongodb")]
public class storage_action_compliance : StorageActionCompliance
{
    private readonly AppFixture _fixture;
    public storage_action_compliance(AppFixture fixture) => _fixture = fixture;

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
        opts.UseMongoDbPersistence(AppFixture.DatabaseName);
        opts.Policies.AutoApplyTransactions();
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
    }

    // Load/Persist target the SAME collection the frames write to: MongoConstants.EntityCollectionName(typeof(Todo)).
    public override Task<Todo?> Load(string id) => _fixture.Client.GetDatabase(AppFixture.DatabaseName)
        .GetCollection<Todo>(MongoConstants.EntityCollectionName(typeof(Todo)))
        .Find(Builders<Todo>.Filter.Eq("_id", id)).FirstOrDefaultAsync()!;
    public override Task Persist(Todo todo) => _fixture.Client.GetDatabase(AppFixture.DatabaseName)
        .GetCollection<Todo>(MongoConstants.EntityCollectionName(typeof(Todo)))
        .ReplaceOneAsync(Builders<Todo>.Filter.Eq("_id", todo.Id), todo, new ReplaceOptions { IsUpsert = true });
    // NOTE: clear the Todo collection before each fact (override initialize() or drop it in configureWolverine).
}
```

- [x] **Step 1:** Add `MongoConstants.EntityCollectionName`; write `EntityFrames.cs` (`MongoEntityOperations` + the three frame classes, mirroring `SagaFrames.cs`).
- [x] **Step 2:** Apply the provider flips (broaden `CanPersist`; branch the four factories; implement `DetermineDeleteFrame(Variable,…)` + `DetermineStorageActionFrame`).
- [x] **Step 3:** Dumped generated code for `Insert<Todo>`, `[Entity] Todo` (update/delete/store-action), `UnitOfWork<Todo>`, and saga handlers via `codeFor<T>()`. Confirmed the entity load/upsert/delete/apply-storage-action runs **inside the try-block on the session, before the single commit+flush**, and saga handlers still emit the unchanged `MongoSagaOperations.*` frames. One refinement: `LoadEntityFrame` resolves the session non-forcingly (`TryFindVariable(IClientSessionHandle, NotServices)`) so a **read-only `[Entity]`** handler with no outbox transaction reads off `IMongoDatabase` directly (mirrors RavenDb/Cosmos reading off their DI-registered session/container).
- [x] **Step 4:** Authored `storage_action_compliance.cs` (single self-contained `: StorageActionCompliance` subclass); all 17 facts green on net9.0 + net10.0.
- [x] **Step 5:** Full single-node suite green (167 tests) on **net9.0 and net10.0** — all four saga compliance suites + `saga_atomicity` + `saga_optimistic_concurrency` + inbox/outbox + the new entity compliance. Commit (`feat: generic entity + IStorageAction persistence`).

> **Coordination note (mirrors saga S6/S9):** ship the compliance host + subclass **in this branch** so T1.1 lands already-green; T1.2 then adds the custom atomicity/coexistence tests rather than the compliance oracle.

### Task T1.2: Entity atomicity + saga/entity coexistence regression

- **Goal:** Cover what `StorageActionCompliance` does not: that an entity write + outbox commit/roll back **atomically**, and that a handler doing BOTH a saga write and an entity write stays consistent — the regression that protects saga behavior from the Tier-1 branching.
- **Scope:** Custom `[Collection("mongodb")]` tests on `AppFixture`, modeled on `saga_atomicity.cs`:
  1. **Entity atomicity:** a handler that returns `Insert<T>` **and** cascades an outgoing message via a durable local queue; force a post-write failure and assert **neither** the entity doc **nor** the outgoing envelope persisted (rolled back together); the success path persists both. (Prove the cascade by its downstream effect, exactly as saga S11 did — re-use that deterministic technique.)
  2. **Coexistence regression:** a single handler that writes saga state (a `Saga` subclass) and an entity (via `[Entity]`/`Store<T>`); assert the saga still gets `Version`-stamped/OCC behavior and the entity persists — proving the frame-branching did not cross wires.
  3. **`[Entity]` not-found:** a handler with a required `[Entity]` for a missing id does not execute (core behavior, but assert it end-to-end through the Mongo load frame).
- **Expected output:** All three green; no regression to any compliance suite or the full suite (both TFMs).
- **Files/areas likely to change:** `src/Wolverine.MongoDB.Tests/entity_atomicity.cs` (+ a small in-test entity + saga).
- **Dependencies:** **T1.1.**
- **Blocking status:** **Partially blocked by: T1.1** (skeleton authorable against D6; green requires T1.1).

- [x] **Step 1:** Skeleton + entity-atomicity success/failure tests; verified each fails for the right reason before it passes (dropped the cascade → success RED on the `NoteCascadeHandler.Reserved` assertion; dropped the throw → failure RED on `Should.ThrowAsync<InvalidOperationException>`). Also mutated the coexistence handler (entity written under a different id) → RED on the entity assertion while the `Version.ShouldBe(2)` assertion still passed, proving the saga-update and entity branches are exercised independently.
- [x] **Step 2:** Added the coexistence regression (single saga handler mutates saga state + returns `Store<CoexistEntity>`; asserts Version stamp/increment 1→2, deterministic stale-version `SagaConcurrencyException`, and entity persistence) + the `[Entity]`-not-found test (required `[Entity]` for a missing id skips the handler end-to-end through the Mongo load frame, with a positive control). All 4 facts green; full single-node suite (171) green on net9.0 + net10.0; entity_atomicity stable across 3 consecutive runs. Commit (`test: entity atomicity + saga/entity coexistence regression`).

### Task T1.3: Demo `[Entity]`/`IStorageAction` handler + safety-net tests

- **Goal:** Add a realistic generic-persistence flow to the demo as a living reference and a regression safety net, showcasing the `[Entity]`/`IStorageAction<T>` surface alongside the existing repository pattern.
- **Scope:** Per D5's design. New contracts + an `OrderNote` (or `CustomerProfile`) entity; handlers `Handle(AddOrderNoteCommand) → Insert<OrderNote>`, `Handle(EditOrderNoteCommand, [Entity] OrderNote) → Update<OrderNote>`, `Handle(DeleteOrderNoteCommand, [Entity] OrderNote) → Delete<OrderNote>`; `Program.cs` discovery/routing. Keep entity writes inside the Wolverine transaction (the generated frames handle the session — no manual session). Preserve all existing order flows. New `OrderNoteFlowTests.cs` on `OrdersFixture` (add/edit/delete, `[Entity]`-not-found path), reading the `ordernote` collection directly to verify persistence.
- **Expected output:** Demo builds against a Tier-1-enabled package (local pack or CI `0.0.0-ci` nupkg) and runs; the entity flows are green in `OrderDemo.IntegrationTests`; existing demo tests unaffected.
- **Files/areas likely to change:** `demo/src/OrderDemo.Contracts/...`, `demo/src/OrderDemo.Application/...`, `demo/src/OrderDemo.Domain/...` (the entity), `demo/src/OrderDemo.Api/Program.cs`, `demo/tests/OrderDemo.IntegrationTests/OrderNoteFlowTests.cs`.
- **Dependencies:** contracts/handlers/test **skeletons** need only D5; **build/run requires T1.1 merged and packed**.
- **Blocking status:** **Partially blocked by: T1.1** (author skeletons early; compile/run once a Tier-1 package exists).

- [x] **Step 1:** Add the entity + contracts + handlers; wire discovery/routing in `Program.cs`. Build against a Tier-1 package (local pack or CI nupkg).
- [x] **Step 2:** Add `OrderNoteFlowTests.cs`; run the demo suite green. Commit (`demo: [Entity]/IStorageAction handler + tests`).

---

## Phase 2 — Tier 2: `ISagaStoreDiagnostics` (independent track)

### Task T2.1: `MongoDbSagaStoreDiagnostics` + registration

- **Goal:** Implement the read-only saga-explorer surface (matching RavenDb; Cosmos does not have it) so the CritterWatch / dashboard tooling can list saga types, read a saga by id, and peek recent instances against MongoDB.
- **Scope:** New `Internals/MongoDbSagaStoreDiagnostics.cs` implementing `ISagaStoreDiagnostics`, mirroring `RavenDbSagaStoreDiagnostics`: ctor `(IWolverineRuntime runtime, IMongoClient client)` (resolve the configured database name — pass it through from `UseMongoDbPersistence`); a lazy saga index from `runtime.Options.HandlerGraph.Chains.OfType<SagaChain>().Select(c => c.SagaType)` filtered by `provider.CanPersist(sagaType, container, out _)`, indexed by `FullName` + `Name`; reflection (`MakeGenericMethod`) to `Find(Eq("_id", id))` (read) and `Find(Empty).Limit(count)` (list) against `MongoConstants.SagaCollectionName(sagaType)`; clamp `count` to `[0,1000]`; build `SagaInstanceState`/`SagaDescriptor` via the core helpers (`SagaDescriptorBuilder.Build(handlerGraph, sagaType, "MongoDb")`). Register in `UseMongoDbPersistence`. Apply the same `[UnconditionalSuppressMessage]` AOT annotations RavenDb uses on the reflection dispatch.
- **Expected output:** `runtime.SagaStorage.GetRegisteredSagasAsync(...)` returns the Mongo-owned sagas tagged `"MongoDb"`; `ReadSagaAsync`/`ListSagaInstancesAsync` return instances; the full library suite stays green (registration must not break startup).
- **Files/areas likely to change:** new `src/Wolverine.MongoDB/Internals/MongoDbSagaStoreDiagnostics.cs`, `WolverineMongoDbExtensions.cs` (registration), possibly `MongoDbPersistenceOptions`/`MongoDbMessageStore` to surface the database name + provider instance.
- **Dependencies:** **D2.**
- **Blocking status:** **Blocked by: D2** (independent of Tier 1 — runs in parallel with the whole Tier-1 chain).

- [x] **Step 1:** Implemented `MongoDbSagaStoreDiagnostics` mirroring `RavenDbSagaStoreDiagnostics`: lazy double-checked saga index (keyed by `FullName` + `Name`) filtered by `MongoDbPersistenceFrameProvider.CanPersist`; reflective `MakeGenericMethod` dispatch to typed private helpers `readSagaAsync<TSaga>` (`Find(Eq("_id", id))`) and `querySagasAsync<TSaga>` (`Find(Empty).Limit(count)`) against `MongoConstants.SagaCollectionName`; `count` clamped to `[0,1000]`; native `_id` matching (no `.ToString()` coercion) with identity extraction via `BsonClassMap...IdMemberMap.Getter`; `SagaInstanceState`/`SagaDescriptor` via the core helpers tagged `"MongoDb"`; RavenDb-parity `[UnconditionalSuppressMessage]` AOT annotations on the reflection dispatch + STJ. **Design deviation (confirmed with user, upstream-move-first):** `SagaDescriptorBuilder.Build`, `WolverineOptions.HandlerGraph`, and `HandlerGraph.Container` are all `internal` and unreachable from this **external** package (not on Wolverine's `[InternalsVisibleTo]`, unlike RavenDb/Marten/EF/RDBMS which call them directly). Reached via an isolated, cached, non-throwing reflective bridge (the three `Resolve*` methods) with a `TODO(upstream)` to collapse each to the direct member access every sibling provider uses once contributed into Wolverine.
- [x] **Step 2:** Registered `AddSingleton<ISagaStoreDiagnostics>` in `UseMongoDbPersistence` (mirrors `WolverineRavenDbExtensions.cs:33-36`; takes `IMongoClient` + the threaded `databaseName`, resolving the DB handle itself). Library builds clean (0 warnings, net9.0 + net10.0). Full single-node suite green — **342 tests passed** on both TFMs; registration did not break startup or any existing test. Commit (`feat: MongoDbSagaStoreDiagnostics + registration`).

### Task T2.2: MongoDb saga store diagnostics test

- **Goal:** Prove the diagnostics surface against a real Mongo replica set, mirroring `raven_saga_store_diagnostics_tests`.
- **Scope:** New `src/Wolverine.MongoDB.Tests/saga_store_diagnostics.cs` (`[Collection("mongodb")]`, on `AppFixture`): build a host with a known saga (reuse a compliance saga or a small in-test one), start an instance, then assert `GetRegisteredSagasAsync` includes the saga tagged `"MongoDb"`, `ReadSagaAsync(typeName, id)` returns the instance (by both FullName and short Name routing), and `ListSagaInstancesAsync` returns it; assert unknown-type returns null/empty.
- **Expected output:** All facts green; full suite green (both TFMs).
- **Files/areas likely to change:** `src/Wolverine.MongoDB.Tests/saga_store_diagnostics.cs`.
- **Dependencies:** **T2.1.**
- **Blocking status:** **Blocked by: T2.1.**

- [x] **Step 1:** Added `src/Wolverine.MongoDB.Tests/saga_store_diagnostics.cs` (`[Collection("mongodb")]`, on `AppFixture`), mirroring `raven_saga_store_diagnostics_tests`: a small in-test `DiagSaga`/`StartDiagSaga`, `Dynamic` codegen, `DisableConventionalDiscovery().IncludeType(typeof(DiagSaga))`. Six facts: registered-saga-types tagged `"MongoDb"`; `ReadSagaAsync` by both `FullName` and short `Name` routing; `ReadSagaAsync` null for a missing instance; `ListSagaInstancesAsync` returns recent instances; unknown saga-type name returns null/empty.
- [x] **Step 2:** All 6 new facts green on net9.0 + net10.0. Full single-node suite green — **177 tests** on both TFMs. Commit (`test: MongoDb saga store diagnostics`).

---

## Phase 3 — Tier 3: Parity Decisions (independent track)

### Task T3.1: Parity capabilities — non-goals + rationale

- **Goal:** Make every Tier-3 implement-vs-defer call explicit and durable, so the suite's scope boundary is documented and upstream-defensible (matching Cosmos/Raven).
- **Scope:** Per D3's recommendations (default: **document-as-non-goal** for all four). Document — in `CLAUDE.md` (key design decisions) and a short `docs/superpowers/plans/2026-06-21-parity-non-goals.md` + `FOLLOWUPS.md` cross-links — for each capability: the contract, what Cosmos/Raven do, the decision, the rationale, and the app-level workaround:
  - **Multi-tenancy → non-goal.** `TenantIds` stays empty (as Cosmos/Raven). App-level tenant-field routing is the path.
  - **Durable listeners → non-goal for now; keep `NullListenerStore`.** Matches Cosmos/Raven "follow-up" state. Record the cheap optional implementation shape (a `wolverine_listeners` collection, URI unique key, `EnableDynamicListeners && Role==Main` gate) as a tracked follow-up — **do not implement** unless a consumer needs it.
  - **Query-spec frames → non-goal.** `TryBuildFetchSpecificationFrame` stays default-`false` (Marten/EF-only).
  - **Soft-delete → non-goal.** `DetermineFrameToNullOutMaybeSoftDeleted` stays `[]` (only Marten implements). Document the app-level `is_deleted`-field pattern.
  - **Verification:** confirm no code change is needed (the four are already at their documented defaults); this task is documentation only unless D3 surfaced a divergence.
- **Expected output:** Accurate non-goal documentation + a one-line `FOLLOWUPS.md` entry per deferred item; no behavioral change. (If D3 recommends implementing durable listeners after all, that becomes a separate scoped task — default is defer.)
- **Files/areas likely to change:** `CLAUDE.md`, `FOLLOWUPS.md`, new `docs/.../2026-06-21-parity-non-goals.md`.
- **Dependencies:** **D3.**
- **Blocking status:** **Blocked by: D3** (independent of Tiers 1/2/4).

- [x] **Step 1:** Confirm each capability is at its documented default in code (no behavioral change).
- [x] **Step 2:** Write the non-goal doc + `CLAUDE.md`/`FOLLOWUPS.md` entries. Commit (`docs: parity capabilities non-goals + rationale`).

---

## Phase 4 — Tier 4: Hardening + Tracked Follow-ups (independent track, fan-out)

### Task T4.1: Demo `MongoDbUnitOfWork` example handler + test

- **Goal:** Close the FOLLOWUPS gap "demo has no `MongoDbUnitOfWork` example" — show the recommended unit-of-work write surface side-by-side with the repository + `IClientSessionHandle` pattern.
- **Scope:** Per D5. A new handler (or variant endpoint) that accepts `MongoDbUnitOfWork` and writes through `Collection<T>()` — e.g. `Handle(RecordOrderAuditCommand, MongoDbUnitOfWork uow)` writing an audit doc. Wire discovery/routing. New demo test asserting the write committed atomically with the outbox (and rolled back on failure).
- **Expected output:** Demo builds + runs; the UoW flow is green; existing demo tests unaffected.
- **Files/areas likely to change:** `demo/src/OrderDemo.{Contracts,Application,Api}/...`, `demo/tests/OrderDemo.IntegrationTests/...`.
- **Dependencies:** **D5** (uses the existing `MongoDbUnitOfWork` — independent of Tier-1 impl).
- **Blocking status:** **Blocked by: D5.**

- [x] **Step 1:** Add the `MongoDbUnitOfWork` handler + contract + routing.
- [x] **Step 2:** Add the demo test (atomic-commit + rollback). Run the demo suite green. Commit (`demo: MongoDbUnitOfWork example handler + test`).

### Task T4.2: Demo fulfillment read-model projector (saga cascade consumer)

- **Goal:** Close the FOLLOWUPS gap "demo saga cascade events have no consumer" — exercise the full saga → outbox → consumer path end to end.
- **Scope:** Per D5. A projector `Handle(FulfillmentShippedEvent)` / `Handle(FulfillmentCompletedEvent)` recording a delivery status/timestamp the `OrderSummary` does not track; a matching local-queue route in `OrdersFixture` (and `Program.cs`); an assertion in the saga flow tests (or a new test) that the projection updates. Use `MultipleHandlerBehavior.Separated` (already set) so the saga and any co-handler run independently.
- **Expected output:** The saga's cascade events are consumed and projected; demo suite green.
- **Files/areas likely to change:** `demo/src/OrderDemo.Infrastructure/Projectors/...`, `demo/src/OrderDemo.Api/Program.cs`, `demo/tests/OrderDemo.IntegrationTests/OrdersFixture.cs` + `SagaFlowTests.cs`.
- **Dependencies:** **D5** (uses the existing saga; independent of Tier-1 impl).
- **Blocking status:** **Blocked by: D5.**

- [x] **Step 1:** Add the fulfillment projector + read-model field + route.
- [x] **Step 2:** Add/extend a test asserting the projection updates from the saga cascade. Run the demo suite green. Commit (`demo: fulfillment read-model projector for saga cascades`).

### Task T4.3: Resolve unkeyed `IMongoDatabase` registration

- **Goal:** Address the FOLLOWUPS constraint that `UseMongoDbPersistence` registers a single **unkeyed** `IMongoDatabase` (`WolverineMongoDbExtensions.cs:59-60`), conflicting with an app that registers its own.
- **Scope:** Decide and implement/document. **Recommended default: document as a consumer constraint** — because the saga AND entity frames resolve `IMongoDatabase` via `chain.FindVariable(typeof(IMongoDatabase))`, switching to a keyed/dedicated registration would require the codegen frames (and `MongoDbUnitOfWork`) to resolve a keyed service, a high-blast-radius change to variable resolution. If D6/D4 instead favor a dedicated type, introduce a thin internal wrapper (e.g. `WolverineMongoDatabase`) the frames resolve, leaving the app's `IMongoDatabase` untouched — but only if the codegen impact is contained. **Coordinate with T1.1** (the entity frames are new consumers of the resolved database).
- **Expected output:** Either (a) clear documentation of the constraint in README/CLAUDE + a FOLLOWUPS resolution note, or (b) a dedicated-registration implementation with all saga/entity/UoW frames + the full suite green on both TFMs. Default (a).
- **Files/areas likely to change:** `WolverineMongoDbExtensions.cs`, `SagaFrames.cs`/`EntityFrames.cs`/`MongoDbUnitOfWork.cs` (only if (b)), README/CLAUDE/FOLLOWUPS.
- **Dependencies:** **D6, T1.1** (the entity frames must exist so a registration change is validated against both saga + entity codegen).
- **Blocking status:** **Partially blocked by: D6, T1.1.**

- [x] **Step 1:** Made the call — **route (a): document**. D6 (needs no new registration) and D4 (classified "document-as-constraint (default)") both declined a dedicated type; neither favored (b). Verified every code-generated path resolves the single unkeyed `IMongoDatabase` by type (`TransactionalFrame.cs:57` + `MongoDbUnitOfWork` ctor `:78`; all saga + entity frames via `chain.FindVariable(typeof(IMongoDatabase))`), confirming the keyed route's high blast radius. No code changed.
- [x] **Step 2:** Route (a), so no suite run required (docs-only, zero code paths touched; PR CI still runs the full suite). Documented the outcome in `README.md` ("The registered `IMongoDatabase`" + a Known-limitations bullet), `CLAUDE.md` (Key Design Decisions), and `FOLLOWUPS.md` (resolution note). Commit (`feat/docs: resolve unkeyed IMongoDatabase registration`).

### Task T4.4: `INodeAgentPersistence.ClearAllAsync` scope

- **Goal:** Resolve the FOLLOWUPS ambiguity: the node-level `ClearAllAsync` clears only node + assignment collections.
- **Scope:** Per D4. Either widen it to also clear counter/locks/node-records/agent-restrictions, **or** document that `IMessageStoreAdmin.RebuildAsync`/`ClearAllAsync` is the full reset and the node-level one is intentionally narrow. Recommend aligning behavior with the documented intent (whichever D4 finds is correct); add/adjust a focused test if behavior changes.
- **Expected output:** Consistent, documented `ClearAllAsync` scope; full suite green.
- **Files/areas likely to change:** `MongoDbMessageStore.NodeAgents.cs`, possibly a node-persistence test, FOLLOWUPS/CLAUDE.
- **Dependencies:** **D4.**
- **Blocking status:** **Blocked by: D4.**

- [x] **Step 1:** Decided per D4's classification (document-as-narrow-by-design): `ClearAllAsync` stays narrow — `IMessageStoreAdmin.RebuildAsync`/`ClearAllAsync` is the full reset, and the test harness already calls that path. Documented the boundary with a comment at the `ClearAllAsync` call site (`MongoDbMessageStore.NodeAgents.cs`), `FOLLOWUPS.md`, and `CLAUDE.md`.
- [x] **Step 2:** No behavior change, so no new test needed; confirmed full suite still green: 177/177 net9.0 + 177/177 net10.0. Commit (`feat: INodeAgentPersistence.ClearAllAsync scope`).

### Task T4.5: Re-evaluate + un-gate multinode leadership compliance

- **Goal:** Determine whether the `leadership_election_compliance` suite — gated behind `#if RUN_MULTINODE` and a one-off 13/13 pass against WolverineFx 6.9.0 — can be un-gated and added to CI's multinode step.
- **Scope:** Per D4's un-gate runbook and `2026-06-16-task6-multinode-compliance-findings.md`. Run the gated suite **5× consecutively on net9.0 + net10.0** under `RUN_MULTINODE`. If all 25 runs green, remove the `#if RUN_MULTINODE` guard and add the suite to the CI multinode step (`.github/workflows/ci.yml`). If any run flakes, **do not un-gate** — write up the failure mode and leave the gate, updating the FOLLOWUPS note. Same hard rules as multinode Task 7 / saga S14: no skips, no retries-as-bandaid, no assertion-weakening.
- **Expected output:** Either an un-gated, CI-wired suite with 5× green proof on both TFMs, or a documented decision to keep the gate with the observed failure.
- **Files/areas likely to change:** `src/Wolverine.MongoDB.Tests/leadership_election_compliance.cs`, `.github/workflows/ci.yml`, `FOLLOWUPS.md`, `multinode-leadership-model-decision` memory.
- **Dependencies:** **D4** (+ multinode infra already on main).
- **Blocking status:** **Blocked by: D4** (independent of Tiers 1/2/3).

- [ ] **Step 1:** Run the gated suite 5× per TFM under `RUN_MULTINODE`; record every result.
- [ ] **Step 2:** If 5× green both TFMs → remove the guard + add to CI; else document and keep the gate. Commit (`test: re-evaluate multinode leadership compliance`).

### Task T4.6: Pre-1.0 hardening backlog (document/defer bundle)

- **Goal:** Resolve the remaining small FOLLOWUPS items as explicit, dated decisions in one bundle so nothing is silently dropped before 1.0.
- **Scope:** Per D4, for each: **node-number reuse** (monotonic counter never reuses freed slots — recommend document-as-acceptable + track a lowest-free-slot strategy for post-1.0); **pre-1.0 index migration** (old single-field indexes harmless on existing beta deployments — document; add a migration step only if needed); **lease fencing token / epoch** (document as a future hardening option, not needed for store-only leader work); **saga-specific indexes** (only the implicit `_id` index today — document the `RebuildAsync` extension point; add per-collection indexes only when query patterns demand). Each becomes a clear FOLLOWUPS/CLAUDE entry with the decision + rationale.
- **Expected output:** Updated `FOLLOWUPS.md` + `CLAUDE.md` with a dated decision per item; no behavioral change unless an item is chosen to implement.
- **Files/areas likely to change:** `FOLLOWUPS.md`, `CLAUDE.md`.
- **Dependencies:** **D4.**
- **Blocking status:** **Blocked by: D4.**

- [ ] **Step 1:** Write the decision per item (default: document/defer with rationale).
- [ ] **Step 2:** Commit (`docs: pre-1.0 hardening backlog decisions`).

---

## Phase 5 — Integration, Documentation & Verification

### Task V1: Full cross-feature regression sweep

- **Goal:** Prove inbox, outbox, single-node, multi-node, saga, saga-diagnostics, AND the new generic entity persistence all work together with no regression.
- **Scope:** On a verification branch off current `main` (after T1.1–T4.6 merged): full library suite (both TFMs); `--filter "Category=multinode"` 5× (all pass — including the newly un-gated leadership suite if T4.5 added it); `dotnet pack` (package-ref build); demo build + integration suite (incl. the new entity, UoW, and projector tests against the fresh nupkg). Confirm CI's `library` job runs the new `storage_action_compliance` + `saga_store_diagnostics` in its single-node step and the demo job runs the new demo tests against the fresh nupkg. Add a CI change only if a coverage gap is found.
- **Expected output:** A clean regression report; any gap fixed (or filed). No new product behavior.
- **Files/areas likely to change:** none expected (`.github/workflows/ci.yml` only if a gap is found).
- **Dependencies:** **T1.1–T4.6 merged.**
- **Blocking status:** **Blocked by: all impl/test tasks.**

- [ ] **Step 1:** Run all suites + pack + demo; record results.
- [ ] **Step 2:** Confirm CI coverage; fix any gap; report. Commit only if CI changed.

### Task V2: Documentation sweep + upstream-contribution notes

- **Goal:** Document the completed suite truthfully and finalize the upstream-contribution framing.
- **Scope:** Update `README.md` (generic entity persistence: `[Entity]` loads + `Insert/Update/Store/Delete<T>`/`IStorageAction<T>` returns, the per-type collection convention, upsert/no-OCC semantics + the repository pattern as the OCC path; `ISagaStoreDiagnostics` availability; the Tier-3 non-goals), `CLAUDE.md` (file map + key design decisions: entity frames branch saga-vs-entity, entity collection naming, diagnostics, parity non-goals), `CHANGELOG.md` (`### Added` entity persistence + saga diagnostics under `## [Unreleased]`), `FOLLOWUPS.md` (close resolved items; keep deferred ones), `demo/README.md` + `demo/CLAUDE.md` (the new entity/UoW/projector flows). Add/extend the **upstream-contribution notes**: the `Wolverine.MongoDb` target, the compliance subclasses to bring (`StorageActionCompliance` + the saga specs), the diagnostics impl, and the deliberate deltas vs Cosmos/Raven (native saga id + `SagaConcurrencyException`; class-map `_id` extraction for entities). Every claim verified against `main`.
- **Expected output:** Accurate docs; an upstream checklist covering the full suite.
- **Files/areas likely to change:** the six docs above (+ the contribution-notes doc).
- **Dependencies:** **T1.1–T4.6 merged** (drafts can start in parallel; finalize against merged `main`).
- **Blocking status:** **Blocked by: all impl tasks** (drafted in parallel).

- [ ] **Step 1:** Edit each doc; truth-check against code on `main`. Commit (`docs: suite completion + upstream contribution notes`).

### Task V3: Final verification on `main` (+ optional release)

- **Goal:** Confirm the merged result is clean and decide on release.
- **Scope:** On `main` after V1/V2 merge: full suite (both TFMs); multinode 5× (incl. leadership if un-gated); pack; demo suite; CI green; history review (one PR per task D1–V2). Optionally invoke the `release` agent (only if the user wants a release; otherwise leave under `## [Unreleased]`).
- **Expected output:** A clean final report; release only if requested.
- **Files/areas likely to change:** none (release handled by the `release` agent if invoked).
- **Dependencies:** **V1, V2 merged.**
- **Blocking status:** **Blocked by: V1, V2.**

- [ ] **Step 1:** Run all verifications on `main`; report. File anything red — do not fix in this session.
- [ ] **Step 2 (optional):** If releasing, follow CLAUDE.md "Versioning & Release" via the `release` agent.

---

## Risks, Assumptions & Open Questions

| # | Risk / assumption | Mitigation |
|---|---|---|
| R1 | **Frame-branching regresses sagas.** Broadening `CanPersist` to `true` routes ALL entity types (incl. sagas) through the factories; a wrong branch could send a saga down the entity (no-OCC) path or vice-versa. | T1.1 Step 3 dumps generated code for a saga handler and asserts it is **unchanged**; T1.2's coexistence regression is the standing oracle. The branch predicate is the precise `variable.VariableType.CanBeCastTo<Saga>()` used by Wolverine's own providers. |
| R2 | **Entity atomicity.** The entity write must run on the `TransactionalFrame` session inside the try-block, before the commit, or it is not atomic with the outbox. | The entity frames resolve `IClientSessionHandle` exactly like the saga frames; `ApplyTransactionSupport` is invoked by `Insert<T>`/`IStorageAction<T>.BuildFrame` before the write. T1.1 Step 3 verifies in generated code; T1.2 is the regression test. |
| R3 | **Generic `_id` extraction.** Upsert/delete need the entity's `_id` value without knowing the id member at compile time. | Use `BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap` getter (idiomatic Mongo) — NOT Cosmos's `entity.ToString()`. Throw a clear error if no id member is mapped. D6 confirms; the `Todo` compliance POCO (member `Id`) exercises it. |
| R4 | **`CanPersist` over-claims in a mixed app.** Unconditional `true` makes Mongo claim every entity type. | This matches Cosmos/Raven exactly and is correct for a Mongo-only app (the provider is `InsertFirstPersistenceStrategy`). Documented as a consumer note in V2; not a regression vs the closest analogues. |
| R5 | **`StorageActionCompliance` collection coupling.** The host's `Load`/`Persist` must read/write the exact collection the frames target, or every fact fails confusingly. | D6 fixes the `EntityCollectionName` convention; the host uses the same helper (shown in T1.1). Clear the entity collection per fact (the shared fixture DB is reused). |
| R6 | **Tier 2 reflection + AOT.** `MongoDbSagaStoreDiagnostics` uses `MakeGenericMethod`; trimming/AOT may warn. | Mirror RavenDb's `[UnconditionalSuppressMessage]` annotations exactly; D2 records them. Dynamic codegen path only (the provider already requires it). |
| R7 | **`IMongoDatabase` registration change (T4.3) is high-blast-radius.** Switching to keyed/dedicated registration touches every frame's variable resolution + `MongoDbUnitOfWork`. | Default to **document-as-constraint** (no code change). Only implement a dedicated wrapper if D4/D6 find it contained; T4.3 depends on T1.1 so it is validated against both saga + entity codegen. |
| R8 | **T4.5 multinode flakiness.** The one-off 13/13 may not hold 5×. | Opus 4.8; reason-before-knob; 5× bar per TFM; **do not un-gate** on any flake — document and keep the gate (the production model is already validated by `multinode_end_to_end.cs`). |
| R9 | **Demo depends on a packed library (T1.3).** Tier-1 demo code can't compile until a Tier-1-enabled package exists. | Author skeletons against D5 early; build/run gated on T1.1 merged (local pack or CI `0.0.0-ci`). |
| R10 | **WolverineFx version drift.** The library packs against the pinned submodule baseline (6.9.0) but the demo runs the package — a provider-selection no-op across a binary gap silently skips persistence (the saga S13 lesson). | Keep `Directory.Packages.props` ⇔ submodule pin ⇔ demo package version aligned; V1 builds the demo against the fresh nupkg so a gap surfaces. |
| **OQ1** | Does `CosmosDbTests` actually subclass `StorageActionCompliance`? (The discovery agent found Raven/Marten/EF/Polecat but not Cosmos.) | Resolve in **D1**. Either way RavenDb is the tested template and the spec exists; this only affects which provider's test we cite as precedent. |
| **OQ2** | Entity `Insert<T>`: `InsertOneAsync` (surfaces duplicate-key) or `ReplaceOneAsync(upsert)` (Cosmos parity)? | Default **upsert** (Cosmos parity, compliance-sufficient). D6 records; revisit only if a fact distinguishes. |
| **OQ3** | Implement durable listeners (Tier 3 B) now, or defer? | Default **defer + document** (matches Cosmos/Raven). D3 may recommend implementing if a consumer needs durable local queues across restarts; then split out a scoped task. |
| **OQ4** | Register `ISagaStoreDiagnostics` (Tier 2) unconditionally, or behind an opt-in? | Default **unconditional** (RavenDb registers it in `UseRavenDbPersistence`). D2 confirms; the aggregator tolerates multiple/zero implementations. |
| **OQ5** | Release a suite-complete NuGet version (V3) now or stay `[Unreleased]`? | Default **stay `[Unreleased]`**; release only on explicit user request. |

## Acceptance Criteria

- The upstream `StorageActionCompliance` suite passes (all ~17 facts) via `storage_action_compliance : StorageActionCompliance` — **no skips, retries, or weakened assertions**. `[Entity]` loads and `Insert/Update/Store/Delete<T>` + `IStorageAction<T>` returns all persist through MongoDB.
- Custom T1.2 tests prove: an entity write + outbox commit/roll back **atomically**; a handler doing both a saga write and an entity write keeps saga `Version`/OCC behavior intact (coexistence regression); a required `[Entity]` for a missing id skips the handler.
- **All four saga compliance suites + `saga_atomicity` + `saga_optimistic_concurrency` + `saga_multinode` remain green** — Tier-1 frame-branching does not regress saga behavior.
- `ISagaStoreDiagnostics` is implemented and registered; `runtime.SagaStorage` lists/reads/peeks Mongo sagas tagged `"MongoDb"` (T2.2 green).
- Tier 3 capabilities are documented as explicit non-goals (with the app-level workaround) — `TenantIds` empty, `NullListenerStore`, `TryBuildFetchSpecificationFrame` default, `DetermineFrameToNullOutMaybeSoftDeleted` `[]` — matching Cosmos/Raven; no silent gaps.
- Every Tier-4 item is resolved as a dated decision (implemented or documented): `MongoDbUnitOfWork` demo + test; saga-cascade consumer demo + test; `IMongoDatabase` registration decision; `ClearAllAsync` scope; multinode leadership un-gate decision (with 5× proof if un-gated); node-reuse/index-migration/fencing/saga-index decisions.
- Full library suite (both TFMs), `dotnet pack`, demo suite, and CI (`library` + `demo`) are green; inbox/outbox/single-node/multi-node/saga behavior is unchanged. No process-global serializer registration is introduced; existing public API is preserved (new behavior, not new required API).
- Docs (README/CLAUDE/CHANGELOG/FOLLOWUPS/demo docs) accurately describe the completed suite; the upstream-contribution checklist covers entity persistence + diagnostics + the deliberate deltas vs Cosmos/Raven.

---

## Dependency Map

**Immediate parallel tasks (start the moment the plan PR merges) — five concurrent sessions:**
- **D1** Tier 1 discovery · **D2** Tier 2 discovery · **D3** Tier 3 discovery · **D4** Tier 4 audit · **D5** demo + test inventory

**Blocked by discovery/design:**
- **D6** ← D1, D5 *(the Tier-1 design gate)*
- **T1.1** ← D6 *(head of the critical path)*
- **T2.1** ← D2 · **T3.1** ← D3 · **T4.4/T4.5/T4.6** ← D4 · **T4.1/T4.2** ← D5

**Blocked by implementation:**
- **T1.2** ← T1.1 · **T1.3** ← T1.1 (skeleton ← D5)
- **T2.2** ← T2.1
- **T4.3** ← D6, T1.1 *(validated against both saga + entity codegen)*

**Final verification tasks:**
- **V1** ← all of T1.1–T4.6 merged · **V2** ← all impl tasks merged (drafted in parallel) · **V3** ← V1, V2 merged

```
plan PR ─┬─► D1 ─┐
         │       ├─► D6 ──► T1.1 ─┬─► T1.2
         ├─► D5 ─┘                ├─► T1.3
         │   └────► T4.1, T4.2    └─► T4.3 (also ← D6)
         ├─► D2 ──► T2.1 ──► T2.2
         ├─► D3 ──► T3.1
         └─► D4 ──► T4.4 / T4.5 / T4.6
   {T1.*, T2.*, T3.1, T4.*} ──► V1 ─┐
                                V2 ─┴─► V3
```

## Critical Path Analysis

**Minimum sequence to deliver the headline gap (generic entity persistence, library-proven):**
`plan PR → {D1, D5} → D6 → T1.1`.
This is the irreducible core: discover the contract (D1), design the entity shape (D5 feeds D6), commit the branching design (D6), implement + prove against the upstream `StorageActionCompliance` oracle (T1.1 ships green). Everything else extends, hardens, or is an independent track.

**Full-scope critical path:**
`… → D6 → T1.1 → {T1.2, T1.3, T4.3} → V1 → V2 → V3`. The longest chain runs through Tier 1 (`D1/D5 → D6 → T1.1 → T1.3` demo build-and-test, or `→ T4.3` registration), converging at `V1 → V2 → V3`. **Tier 1 is the schedule risk** — D6 and T1.1 are the only Opus/Fable codegen-correctness gates on the main line; start D1 + D5 immediately so D6 is unblocked first.

**Tasks that can be delayed without affecting the critical path:**
- **Tiers 2, 3, and 4 are entirely off the Tier-1 critical path** — they need only their own discovery (D2/D3/D4/D5) and run as parallel lanes. They merge before V1 but do not gate T1.1.
- **T4.5 (multinode un-gate)** is independent verification; if it flakes, it is documented-and-kept-gated without blocking anything (the production model is already validated).
- **V2 docs** can be drafted in parallel throughout and finalized last.
- **int/long-id and durable-listener implementations** are explicit deferrals, not on any path.

**Opportunities to compress wall-clock via parallelism:**
- Run **D1+D2+D3+D4+D5** as five concurrent sessions on day one.
- The moment **D6** merges, **T1.1** starts; concurrently the Tier-2 lane (`D2→T2.1→T2.2`), the Tier-3 lane (`D3→T3.1`), and the Tier-4 lanes (`D4→T4.4/T4.5/T4.6`, `D5→T4.1/T4.2`) all proceed independently.
- The moment **T1.1** merges, **T1.2**, **T1.3**, and **T4.3** fan out (disjoint files: library test vs demo vs extension registration).
- Fold the compliance host + subclass into **T1.1's** branch (the S6/S9 lesson) so T1.1 ships already-green, removing a serialization point.
- With full fan-out the realistic span is: 1 unit (D1–D5) → 1 unit (D6) → 1 unit (T1.1) → ~1–2 units (T1.2/T1.3/T4.3 + the parallel Tier-2/3/4 lanes overlapping) → 1 unit (V1/V2) → V3 — i.e. the 21 tasks collapse to about **5–6 sequential "waves,"** dominated by the `D6 → T1.1 → T1.3` Tier-1 chain.
