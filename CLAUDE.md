# Wolverine.MongoDB

## Overview

Native MongoDB persistence provider for Wolverine's transactional inbox/outbox. Implements `IMessageStore` directly against the MongoDB .NET driver. No EF Core dependency.

**Package:** `Wolverine.MongoDB` (NuGet; see `Directory.Build.props` for the current version — `1.0.0` as of the [1.0.0] CHANGELOG entry)  
**Targets:** .NET 9, .NET 10  
**Dependencies:** `WolverineFx 6.x`, `MongoDB.Driver 3.x`  
**Constraint:** MongoDB must run as a replica set (transactions require it).

---

## Repository Layout

```
src/Wolverine.MongoDB/              ← Library (NuGet package)
  WolverineMongoDbExtensions.cs     ← Public API: UseMongoDbPersistence()
  MongoDbPersistenceOptions.cs      ← Public API: MongoDB-specific tuning (LockLeaseDuration)
  MongoDbUnitOfWork.cs              ← Public API: session-bound write helper
  Internals/                        ← All implementation (internal)
    SagaFrames.cs                   ← Saga codegen frames + MongoSagaOperations helpers
    EntityFrames.cs                 ← Generic entity codegen frames + MongoEntityOperations helpers
    MongoDbSagaStoreDiagnostics.cs  ← ISagaStoreDiagnostics implementation
src/Wolverine.MongoDB.Tests/        ← Integration tests (needs Wolverine source clone)
  MongoDbSagaHost.cs                ← ISagaHost implementation for compliance suites
  string_saga_storage_compliance.cs ← StringIdentifiedSagaComplianceSpecs<MongoDbSagaHost>
  guid_saga_storage_compliance.cs   ← GuidIdentifiedSagaComplianceSpecs (+ int, long)
  saga_atomicity.cs                 ← Custom atomicity, OCC, completion, and idempotency tests
  saga_multinode.cs                 ← [Category=multinode] cross-node exactly-once saga test
  storage_action_compliance.cs      ← Wolverine's upstream StorageActionCompliance ([Entity]/IStorageAction<T>)
  entity_atomicity.cs               ← Custom entity write + outbox atomicity, saga/entity coexistence
  entity_multinode.cs               ← [Category=multinode] cross-node entity persistence
  saga_store_diagnostics.cs         ← ISagaStoreDiagnostics integration coverage
  leadership_election_compliance.cs ← Upstream LeadershipElectionCompliance ([Category=multinode], un-gated)
demo/                               ← Separate solution, references package from CI nupkg
  src/OrderDemo.Application/Sagas/
    OrderFulfillmentSaga.cs         ← Demo saga: Guid id, start/continue/complete lifecycle
  src/OrderDemo.Application/Notes/
    OrderNoteHandler.cs             ← Demo [Entity]/Insert|Update|Delete<OrderNote> handlers
  src/OrderDemo.Application/Audit/
    RecordOrderAuditHandler.cs      ← Demo MongoDbUnitOfWork example (no repository layer)
  src/OrderDemo.Infrastructure/Projectors/
    FulfillmentStatusProjector.cs   ← Demo saga-cascade-event consumer (delivery-status read model)
  tests/OrderDemo.IntegrationTests/
    SagaFlowTests.cs                ← 8 saga integration tests (start, ship, complete, cascade, etc.)
    OrderNoteFlowTests.cs           ← Entity persistence flow tests
    OrderAuditTests.cs              ← MongoDbUnitOfWork atomicity tests
.github/workflows/
  ci.yml                            ← Library tests (single-node + multinode steps) + pack; demo tests against fresh nupkg
  publish.yml                       ← NuGet push on v* tag
  security.yml                      ← Trivy vulnerability scan
```

---

## How the Library Works

### Public API

Three public entry points:
- `opts.UseMongoDbPersistence(databaseName, configure?)`: one-line registration. It:
  1. Registers `MongoDbMessageStore` as `IMessageStore`
  2. Registers `IMongoDatabase` from the DI-provided `IMongoClient`
  3. Inserts `MongoDbPersistenceFrameProvider` into Wolverine's code-generation pipeline
- `MongoDbPersistenceOptions`: tuning options passed via the `configure` callback. Currently exposes `LockLeaseDuration` (default 1 minute).
- `MongoDbUnitOfWork`: session-bound write helper. Handlers accept it as a parameter; the
  generated frame constructs it from the open `IClientSessionHandle`. Every write through
  `MongoDbUnitOfWork.Collection<T>()` automatically participates in the transaction.

### Core Implementation (partial class `MongoDbMessageStore`)

| File | Implements |
|------|-----------|
| `MongoDbMessageStore.cs` | `IMessageStore` root, collection references, Balanced-mode startup warning |
| `MongoDbMessageStore.Inbox.cs` | `IMessageInbox` — store/mark/recover incoming envelopes |
| `MongoDbMessageStore.Outbox.cs` | `IMessageOutbox` — persist/relay/mark outgoing envelopes |
| `MongoDbMessageStore.DeadLetters.cs` | `IDeadLetters` — failed messages, replay |
| `MongoDbMessageStore.Admin.cs` | `IMessageStoreAdmin` — collection/index creation, rebuild |
| `MongoDbMessageStore.NodeAgents.cs` | `IAgentFamily` — node registry, agent assignments, node-record trimming |
| `MongoDbMessageStore.Locking.cs` | Leader election via configurable-lease findAndModify lock document |
| `MongoDbMessageStore.ScheduledMessages.cs` | Scheduled message polling with atomic claim |
| `MongoDbMessageStore.Durability.cs` | Recovery loops; CAS outgoing claim; dead-node ownership release |

### Transaction Integration

| File | Role |
|------|------|
| `MongoDbEnvelopeTransaction.cs` | `IEnvelopeTransaction` — opens session, commits outbox atomically |
| `MongoDbPersistenceFrameProvider.cs` | Code-gen: detects MongoDB types, injects transactional frame; saga members |
| `TransactionalFrame.cs` | Generated code frame: `StartSession → StartTransaction → handler → Commit` |

### Saga Implementation

| File | Role |
|------|------|
| `SagaFrames.cs` | `LoadSagaFrame`, `InsertSagaFrame`, `UpdateSagaFrame`, `DeleteSagaFrame` emitted by the provider; `MongoSagaOperations` static helpers (load/insert/update/delete on the session) |

### Entity Persistence Implementation

| File | Role |
|------|------|
| `EntityFrames.cs` | `LoadEntityFrame`, `MongoUpsertEntityFrame`, `MongoDeleteEntityByVariableFrame` emitted by the provider for non-`Saga` types; `MongoEntityOperations` static helpers (load/upsert/delete + `ApplyStorageActionAsync<T>` on the session) |

Generic `[Entity]` loads and `Insert<T>`/`Update<T>`/`Store<T>`/`Delete<T>`/`IStorageAction<T>`
return-value side effects for plain (non-saga) document types. `MongoDbPersistenceFrameProvider`'s
write/load frame factories (`DetermineInsertFrame`/`DetermineUpdateFrame`/`DetermineStoreFrame`/
`DetermineLoadFrame`) branch on `variable.VariableType.CanBeCastTo<Saga>()`: `Saga` subclasses keep
the existing version-guarded saga frames untouched; everything else routes to the entity frames
above. `DetermineDeleteFrame(Variable, …)` (the generic single-variable overload used by `Delete<T>`)
and `DetermineStorageActionFrame` (used by `IStorageAction<T>`) are entity-only — sagas use the
two-variable delete overload and never construct an `IStorageAction<T>`.

### Saga Store Diagnostics Implementation

| File | Role |
|------|------|
| `MongoDbSagaStoreDiagnostics.cs` | `ISagaStoreDiagnostics` implementation: `GetRegisteredSagasAsync`/`ReadSagaAsync`/`ListSagaInstancesAsync`, registered as a singleton by `UseMongoDbPersistence` |

Read-only, reflection-driven surface for saga-explorer tooling (mirrors `RavenDbSagaStoreDiagnostics`).
Saga descriptors are tagged `"MongoDb"` and indexed by both `FullName` and short `Name`.
`ListSagaInstancesAsync`'s `count` argument is clamped to `[0, 1000]`. Reads go directly against the
`wolverine_saga_<type>` collections the saga frames write to, matching on native `_id` (no string
coercion, unlike Cosmos/RavenDb).

### MongoDB Collections

| Collection | Purpose |
|------------|---------|
| `wolverine_incoming_envelopes` | Inbox (idempotency, durable queues) |
| `wolverine_outgoing_envelopes` | Outbox (pending broker delivery) |
| `wolverine_dead_letters` | Failed messages + exception info |
| `wolverine_nodes` | Node registry (heartbeat, capabilities) |
| `wolverine_node_assignments` | Agent-to-node mapping |
| `wolverine_saga_<lowercased-type>` | One collection per saga type (e.g. `wolverine_saga_orderfulfillmentsaga`) |
| `<lowercased-entity-type>` | One un-prefixed collection per app entity type persisted via `[Entity]`/`Insert`/`Update`/`Store`/`Delete<T>` (e.g. `OrderNote` → `ordernote`) — application-owned, not swept by `ClearAllAsync`/`RebuildAsync` |

### Key Design Decisions

- **Hot path uses `findAndModify`** (single-doc atomic ops), not multi-doc transactions. Transactions only for handler atomicity (domain write + outbox in one commit).
- **Inbox idempotency:** unique `_id` index; duplicate insert → `DuplicateKeyException` → treated as already-processed.
- **Scheduled messages:** `FindOneAndUpdate` with `Status == Scheduled && ExecutionTime <= now` for atomic claim — exactly-once across competing nodes.
- **Node coordination:** lock document with configurable-lease TTL expiry via `findAndModify` (approximates PostgreSQL advisory locks). Both Solo and Balanced modes are supported.
- **Configurable leader lease:** `MongoDbPersistenceOptions.LockLeaseDuration` (default 1 min). `HasLeadershipLock()` reports `false` once 75% of the lease has elapsed — the node stops acting as leader before another can legitimately take over. Clocks must be synchronized to well within the lease duration.
- **`LoadOutgoingAsync` is owner-scoped and batch-limited:** only envelopes with `OwnerId == 0` (globally-owned / unclaimed) are returned, capped at `Durability.RecoveryBatchSize`. Envelopes owned by a live node are in-flight and must never be handed to recovery.
- **CAS-guarded outgoing recovery:** `RecoverOrphanedOutgoingAsync` uses a filter guard (`OwnerId == AnyNode`) on the claim `UpdateMany` and re-reads which ids this node actually won before enqueuing — prevents double-sends when two nodes race for the same orphaned envelopes.
- **Dead-node ownership release is two-tick-confirmed (Balanced mode only):** each recovery tick calls `ReleaseDeadNodeOwnershipAsync`, which computes the node numbers that are *owned* in `wolverine_incoming_envelopes`/`wolverine_outgoing_envelopes` but have no live `wolverine_nodes` document, and releases only the numbers that were **also** dead on the previous tick (`Filter.In(confirmed)`, never `Filter.Nin(liveSnapshot)`). Reading the owned set *before* the live set, plus monotonic never-reused node numbers (see the node-number-reuse decision below), means a number confirmed dead was dead for the whole interval — a node that registers and claims between the read and the write can never be released. Cost: a crashed node's envelopes are rescued one recovery interval later. Still runs before orphan recovery so released envelopes are re-claimable in the same tick. **A future change to reuse freed node numbers would invalidate this argument** — see the soundness comment on the method.
- **Handled markers expire via `KeepUntil` TTL:** `IncomingMessage` maps `envelope.KeepUntil` into the document. The TTL index on `keepUntil` automatically removes handled markers. Previously, `KeepUntil` was dropped, causing unbounded inbox growth.
- **Dead-letter TTL is opt-in:** `ExpirationTime` is only written when `Durability.DeadLetterQueueExpirationEnabled == true`. With the default `false`, dead letters are retained forever (matching RDBMS providers). The TTL index ignores documents without the `expirationTime` field.
- **Write concerns pinned on the store:** the `MongoDbMessageStore` constructor wraps its database handle with `WriteConcern.WMajority.With(journal: true)` and `ReadConcern.Majority`. This is independent of the consumer's `MongoClient` configuration. The app-facing `IMongoDatabase` registered by `UseMongoDbPersistence` is **not** pinned — domain write concerns belong to the application.
- **`INodeAgentPersistence.ClearAllAsync` is intentionally narrow (T4.4):** it clears only `wolverine_nodes` and `wolverine_node_assignments` — the operational node-state surface `INodeAgentPersistence` owns. It does not touch `wolverine_counters`, `wolverine_locks`, `wolverine_node_records`, or `wolverine_agent_restrictions`. The full system reset is `IMessageStoreAdmin.ClearAllAsync`/`RebuildAsync` (`MongoDbMessageStore.Admin.cs`), which clears all nine system collections (`wolverine_incoming_envelopes`, `wolverine_outgoing_envelopes`, `wolverine_dead_letters`, `wolverine_nodes`, `wolverine_node_assignments`, `wolverine_node_records`, `wolverine_agent_restrictions`, `wolverine_counters`, `wolverine_locks`) plus every `wolverine_saga_*` collection; the test harness (`AppFixture.ClearAll()`) calls `RebuildAsync()`, not the node-level method. No behavior change — documented at the call site in `MongoDbMessageStore.NodeAgents.cs`.
- **Single unkeyed `IMongoDatabase` registration (documented consumer constraint, T4.3):** `UseMongoDbPersistence` registers exactly one **unkeyed** `IMongoDatabase` pointing at `databaseName` (`WolverineMongoDbExtensions.cs:59-60`). Every code-generated frame resolves it by type — `TransactionalFrame` (`:57`, which also constructs `MongoDbUnitOfWork` at `:78`), all four saga frames and all three entity frames (`chain.FindVariable(typeof(IMongoDatabase))`), and the provider's `CanPersist` (`persistenceService = typeof(IMongoDatabase)`). An app that registers its own unkeyed `IMongoDatabase` collides with this: `Microsoft.Extensions.DependencyInjection` resolves the last registration for a single-service request, so ordering alone decides which database the frames (and the app's own injections) resolve. **Decision: document, do not switch to keyed/dedicated registration** — a keyed lookup would have to thread through every frame's `FindVariable`/`MethodCall` resolution **plus** `MongoDbUnitOfWork`, a high-blast-radius codegen change for a rare conflict (which is why T4.3 depends on both D6 and T1.1 — the change is validated against saga *and* entity codegen). App workaround: don't register a competing unkeyed `IMongoDatabase`; reuse Wolverine's, resolve a different database via `IMongoClient.GetDatabase(...)`, or register the app's own under a keyed service / wrapper type. Documented in `README.md` ("The registered `IMongoDatabase`") + `FOLLOWUPS.md`.
- **Balanced-mode startup warning:** `Initialize` and `BuildAgent` call `WarnOnBalancedMode`, which logs an `Information` message (once per store lifetime) if `DurabilityMode.Balanced` is detected — reminding operators that a control endpoint and synchronized clocks are required. This is a warning, not a throw; the host starts normally.
- **`MongoDbUnitOfWork` is the recommended handler write surface:** it accepts a handler parameter and threads the active `IClientSessionHandle` into every write, making it impossible to forget the session. The raw `IClientSessionHandle` pattern remains valid for repository-based handlers.
- **Transaction frame triggers broadly:** `CanApply` returns `true` for handlers whose dependency tree (or method parameters) includes `IMongoDatabase`, `IMongoClient`, `IMongoCollection<T>`, `IClientSessionHandle`, or `MongoDbUnitOfWork`.
- **DateTimeOffset stored as UTC BSON Date:** every `DateTimeOffset`/`DateTimeOffset?` property on document types is annotated with `[BsonRepresentation(BsonType.DateTime)]`. **No serializer, convention, or convention pack is ever registered process-globally** — the library does not change how the host app serializes any type. The one registry interaction it does make is narrow and additive: a per-type `BsonClassMap` id-member alignment for saga/entity types Wolverine persists whose identity member the driver would not otherwise map to `_id` (see "Identity-member alignment" below).
- **Dead-letter replay is idempotent:** if a previous replay pass crashed after re-inserting the envelope but before deleting the DLQ document, the next pass catches `DuplicateIncomingEnvelopeException` and continues. Body-less poison letters are unflagged (not retried every tick) and left queryable.
- **Saga persistence is codegen-only:** there is no separate saga storage service. `MongoDbPersistenceFrameProvider` implements all `IPersistenceFrameProvider` saga members (Load/Insert/Update/Delete/CommitUnitOfWork). The frames run on the `TransactionalFrame` session so saga state and the outbox commit atomically. `CanApply` returns `true` for `SagaChain` — required or the provider is skipped for saga chains entirely.
- **Direct document storage:** the saga POCO is stored as a MongoDB document, no envelope wrapper. Wolverine's resolved identity member is what maps to `_id` — via the driver's own convention when the member is named `Id`/`id`/`_id` or carries `[BsonId]`, and via the library's per-type class-map alignment otherwise (see "Identity-member alignment").
- **Identity-member alignment (`MongoIdentityMapping`, F6/F7):** Wolverine resolves a document's identity member by *its* convention (`SagaChain.DetermineSagaIdMember`: `[SagaIdentity]` → `{TypeName}Id` → `{Name-minus-Saga}Id` → `SagaId` → `Id`); the MongoDB driver resolves `_id` by *its own* (`NamedIdMemberConvention`: only `Id`/`id`/`_id`, plus `[BsonId]`, inherited members included). Before 1.0.1 nothing reconciled the two, so a saga or entity keyed on e.g. `ShipmentId` was written with a **server-generated `ObjectId` `_id`** and could never be loaded back — silent data corruption. `MongoIdentityMapping.EnsureIdMember` bridges them. It first asks what the driver *will* resolve, by walking the type's base chain most-derived-first and reading each level's registered class map, or auto-mapping a throwaway one (**not** a single unfrozen probe of the document type — `AutoMap` maps only a class's own declared members, so that reports nothing for an identity member declared on a base class, which is the shape of every upstream compliance saga). If the driver already resolves the same member, the helper **does nothing at all and leaves the BSON registry untouched** — that covers every `Id`-keyed type whether the member is declared on the type or inherited (i.e. every consumer that worked before 1.0.1, byte-identical) and every `[BsonId]`-annotated type. Only when the driver disagrees does it register one additive per-type `BsonClassMap` (`AutoMap()` + `MapIdMember(resolvedMember)`); the driver's `Freeze()` then normalizes that member's element name to `_id`, so the frames' `Eq("_id", …)` filters and the written documents agree. Three misconfigurations throw a precise `InvalidOperationException` instead of corrupting or deferring: a class map already registered for the type naming a different id member (the app owns its own maps — we only assert agreement); an identity member declared on a **base** type, which the driver refuses to map from the subclass's map (remedy: `[BsonId]` on it, or register a class map for the declaring type — either fixes every subclass at once); and a **different**, base-declared member already occupying `_id`, which would otherwise register cleanly and then fail on the type's first write. Called at codegen time from every saga/entity frame constructor **and** at runtime from the `MongoSagaOperations`/`MongoEntityOperations` collection accessors — the runtime leg is required because `TypeLoadMode.Static` never constructs frames (`HandlerChain.cs:309-325` attaches pre-generated types without calling `AssembleTypes`). This is **not** the process-global serializer/convention mutation the library forswears: no serializer, no convention, no convention pack, and no behavior change for any type Wolverine does not persist.
- **Saga types are rejected on the generic storage-action paths (LD4, F7):** a non-saga handler returning `Delete<TSaga>` or `IStorageAction<TSaga>` throws `InvalidOperationException` at codegen (host build — the guards sit in `DetermineDeleteFrame(Variable, …)` and `DetermineStorageActionFrame`, which Wolverine reaches eagerly from `HandlerGraph.Compile` → `SideEffectPolicy`). Nothing upstream guards this (`Delete.cs:22-26`, `IStorageAction.cs:23-27`, `Storage.cs:63-74` are all gated only by `CanPersist`, which this provider hardcodes `true`), and before 1.0.1 those returns silently targeted the un-prefixed **entity** collection (`orderfulfillmentsaga`) instead of `wolverine_saga_orderfulfillmentsaga`, with no `Saga.Version` guard. Routing them to the saga frames was rejected: without a `SagaChain` there is no captured `oldVersion`, so it would trade a visible bug for silent OCC corruption. Sagas are completed with `MarkCompleted()` from a saga handler. No sibling provider supports this path for sagas either.
- **Native id type:** `DetermineSagaIdType` resolves the saga's identity-member type (`Guid`/`string`/`int`/`long`) via `SagaChain.DetermineSagaIdMember`. Cosmos/RavenDb are string-only; this provider stores every type natively.
- **`Saga.Version` optimistic concurrency (insert/update diverge):** insert (`InsertSagaFrame`) is unguarded and stamps `Version = 1` via `InsertOneAsync`. Update (`UpdateSagaFrame`) captures `oldVersion`, sets `Version = oldVersion + 1`, then `ReplaceOneAsync` with filter `(_id, oldVersion)`, `IsUpsert = false`; throws `SagaConcurrencyException` when `ModifiedCount == 0`. The new version is written into the POCO before the replace because MongoDB stores the saga directly (unlike RDBMS providers). Completion delete is unguarded (matches Wolverine's lightweight SQL provider `DatabaseSagaSchema`). Cosmos/RavenDb are last-write-wins; this provider's OCC matches the Marten/EF/lightweight-SQL approach.
- **One collection per saga type:** `wolverine_saga_<lowercased-type-name>` (e.g. `wolverine_saga_orderfulfillmentsaga`). Idiomatic MongoDB — no cross-type `_id` collision. `ClearAllAsync`/`RebuildAsync` drop every collection matching the `wolverine_saga_` prefix.
- **`CommitUnitOfWorkFrame` for saga chains / no double-commit:** `ApplyTransactionSupport` adds the commit postprocessor only when `chain is not SagaChain`. For saga chains the single commit+flush flows through `CommitUnitOfWorkFrame` (inlined by `SagaChain` after the saga write). Mirrors Cosmos/RavenDb.
- **`MultipleHandlerBehavior.Separated` for saga + non-saga co-handlers:** a `SagaChain` calls `Handlers.Clear()`, silently dropping co-registered non-saga handlers. When a saga and a projector consume the same message, set `opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated` so each runs independently. Required in the demo.
- **`CanPersist` is unconditional `true` (T1.1):** matching Cosmos/RavenDb, the provider now advertises persistence support for every entity type, not just `Saga` subclasses — required because `[Entity]` parameter loads key on `CanPersist(parameterType)`. The saga-vs-entity distinction moved entirely into the frame factories (see Entity Persistence Implementation above); `CanPersist` no longer gates it. `persistenceService` stays `typeof(IMongoDatabase)`.
- **Entity write semantics: upsert-only, no OCC (T1.1, D6 LD2):** `Insert<T>`/`Update<T>`/`Store<T>` all compile to the same `ReplaceOneAsync(..., IsUpsert = true)` (`MongoUpsertEntityFrame`) — entities carry no `Saga.Version`-style guard, so writes are last-write-wins, matching Cosmos's `CosmosDbUpsertFrame`. `Delete<T>` removes by id (`MongoDeleteEntityByVariableFrame`). App-level optimistic concurrency remains available via the repository pattern (a hand-guarded `ReplaceOneAsync` filter), the same path the demo's `OrderRepository` uses for the `Order` aggregate.
- **Entity collection naming + id extraction (T1.1, D6 LD3):** `MongoConstants.EntityCollectionName(Type) => type.Name.ToLowerInvariant()` — deliberately **un-prefixed** (unlike `wolverine_saga_*`) because entity collections hold application data, not Wolverine system state; `ClearAllAsync`/`RebuildAsync`'s `wolverine_saga_` sweep never touches them. The entity's `_id` value is extracted generically via `BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap` (`MongoEntityOperations.IdOf`) — not Cosmos's `entity.ToString()` coercion. Since F7 every entity operation obtains its collection through one private `entityCollection<T>` accessor that calls `MongoIdentityMapping.EnsureIdMember` first, so the member `IdOf` reads **is** Wolverine's resolved identity member and the `[Entity]` load's `Eq("_id", …)` filter keys the same one (see "Identity-member alignment"). `IdOf` itself is unchanged; its "no mapped _id member" throw is now a backstop — alignment fails loudly first.
- **`ISagaStoreDiagnostics` registered unconditionally (T2.1):** `UseMongoDbPersistence` always registers `MongoDbSagaStoreDiagnostics` as a singleton — no opt-in flag, matching how RavenDb registers its implementation. The Wolverine runtime's diagnostics aggregator tolerates zero or multiple registered implementations, so unconditional registration is safe even in a mixed-provider app.
- **Diagnostics reaches internal Wolverine members via reflection, not direct calls (T2.1):** `MongoDbSagaStoreDiagnostics` needs `SagaDescriptorBuilder.Build`, `WolverineOptions.HandlerGraph`, and `HandlerGraph.Container` — all `internal` to Wolverine core. Because this provider ships as an **external** NuGet package (not on Wolverine's `[InternalsVisibleTo]` list, unlike the in-repo RavenDb/Marten/EF Core/RDBMS providers), it bridges each member through isolated, cached, non-throwing reflection, each call site carrying a `// TODO(upstream)` marker. When contributed upstream into the Wolverine repo, add `Wolverine.MongoDB` to `[InternalsVisibleTo]` and collapse each bridge to the direct member access every sibling provider uses.
- **Multinode leadership compliance un-gated (T4.5, 2026-07-05):** the upstream `LeadershipElectionCompliance` suite was compile-gated behind `#if RUN_MULTINODE` because earlier WolverineFx releases required the lowest-numbered surviving node to win a leadership-claim race our `w:majority` lock couldn't guarantee. WolverineFx 6.9.0 reworked those facts around the "any healthy node leads" model this provider already implements. Verified 5× consecutive green on net9.0 **and** net10.0 (10/10 runs, 17/17 facts each); the `#if` guard was removed and `[Trait("Category","multinode")]` now routes the suite into CI's existing multinode step with no `ci.yml` change.
- **Pre-1.0 hardening backlog — four dated document/defer decisions (T4.6, 2026-07-05):** no behavior changed; each is tracked in `FOLLOWUPS.md` with rationale and an extension point if revisited.
  - **Node-number reuse:** the node-number counter (`MongoDbMessageStore.NodeAgents.cs:16-20`) is a pure monotonic increment that never reuses a freed slot. Acceptable — node numbers are short-lived coordination identifiers, not long-lived keys. If revisited post-1.0: track the lowest free slot instead of redesigning allocation. ⚠️ **Monotonicity is now load-bearing:** the two-tick dead-node ownership release (see the dead-node bullet above) relies on "a number issued after the previous tick cannot appear in the previous tick's dead set". Reusing freed slots would invalidate step 4 of that soundness argument — re-derive it (step 3's read-ordering argument survives alone, but the total-interval guarantee does not) before changing allocation.
  - **Index migration (post-1.0, document/defer — see `FOLLOWUPS.md`):** the hardening pass added compound indexes (`MongoDbMessageStore.Admin.cs:18-64`) but `EnsureIndexesAsync` only creates indexes, never drops superseded single-field ones from deployments created before that pass. Harmless (old indexes stay valid, just suboptimal); a `RebuildAsync` (which recreates all indexes from scratch) is an acceptable manual remedy. Add an explicit `Admin.MigrateAsync()` drop step only if a concrete need arises.
  - **Lease fencing token (epoch):** the lock document (`MongoDbMessageStore.Locking.cs`) has no fencing token; not needed for store-only leader work since the 75%-lease margin already mitigates the internal stale-leadership window. Track as a future hardening item only if leader-scoped **external** side effects (writes outside this store needing stale-epoch rejection) become common.
  - **Saga-specific indexes:** saga collections (`wolverine_saga_*`) have only the implicit `_id` index; the current access pattern (load/insert/update/delete by `_id`) doesn't need more. `EnsureIndexesAsync`/`RebuildAsync` (`MongoDbMessageStore.Admin.cs`) is the extension point when a concrete query pattern (e.g. filtering by status) demands secondary indexes.

### Parity Capabilities — Non-Goals

Four RDBMS/Marten-only Wolverine capabilities are deliberately **not implemented**, matching the two closest document-store analogues, Cosmos and RavenDb, which also defer all four. Each is already at its correctly-deferred default; no code exists to remove. See `docs/superpowers/plans/2026-06-21-parity-non-goals.md` for the full contract-by-contract writeup.

- **Multi-tenancy (non-goal).** `MongoDbMessageStore.TenantIds` stays `new()` (always empty) and the provider does not implement `ITenantedMessageSource`. Real Wolverine multi-tenancy is connection-string-based (one `IMessageStore` per tenant database) — a significant architectural investment that Cosmos and RavenDb also skip. **App-level workaround:** route on a tenant-ID field in the message payload, or register a separate Wolverine host (with its own `IMongoDatabase`) per tenant.
- **Durable listeners (non-goal for now).** `MongoDbMessageStore.Listeners` stays `NullListenerStore.Instance`, matching Cosmos/RavenDb's "follow-up" state. Durable listener persistence only matters when `DurabilitySettings.EnableDynamicListeners` is opted into (not the default), and no consumer has asked for it. **Optional follow-up shape**, if demand appears: a `wolverine_listeners` collection with a `{ uri: string }` document and a unique index on `uri`, upserted via `ReplaceOneAsync(IsUpsert=true)`; gate construction on `EnableDynamicListeners && Role==Main` exactly like `RdbmsListenerStore`, otherwise keep returning `NullListenerStore.Instance`.
- **Query-spec frames (non-goal).** `TryBuildFetchSpecificationFrame` is not overridden, so it uses `IPersistenceFrameProvider`'s default (`false`). This is a Marten/EF Core-specific concept for compile-time query objects (`ICompiledQuery<,>`, EF's `IQueryPlan<,>`) with no MongoDB analogue. Cosmos, RavenDb, and Polecat all leave it at the default too.
- **Soft-delete (non-goal).** `DetermineFrameToNullOutMaybeSoftDeleted` returns `[]`. Only Marten implements this (a `SetVariableToNullIfSoftDeletedFrame` reading Marten-specific document metadata); EF Core, Polecat, Cosmos, and RavenDb all return `[]` too. Implementing it would mean prescribing an `IsDeleted`-style field convention across every entity type. **App-level workaround:** `[Entity(MaybeSoftDeleted = false)]` plus a manual check in the handler, or an explicit `is_deleted` filter on the load query.

---

## Build & Test

```bash
# Build library only (no Wolverine source clone needed)
dotnet build src/Wolverine.MongoDB/Wolverine.MongoDB.csproj

# Build + test (compliance tests project-ref the Wolverine submodule at external/wolverine)
# Initialise it first: git submodule update --init   (or clone with --recursive)
# Override the path if needed: WOLVERINE_SOURCE env var or -p:WolverineSourcePath=...
dotnet test src/Wolverine.MongoDB.Tests/

# Run only multinode tests (requires Docker, heavier setup — two in-proc Balanced hosts)
dotnet test src/Wolverine.MongoDB.Tests --filter "Category=multinode"

# Pack for NuGet (declares WolverineFx package dependency)
dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false
```

Tests use Testcontainers (auto-starts MongoDB replica set). Docker Desktop required.

**CI:** the `library` job checks out with `submodules: recursive` (the Wolverine source is the
`external/wolverine` submodule, pinned to the `V6.21.0` commit — keep the pin in sync with
`WolverineFx` in `Directory.Packages.props`), runs the compliance suite in two steps
(`Category!=multinode` then `Category=multinode`), then packs the library at version `0.0.0-ci`.
The `demo` job downloads that nupkg and runs the end-to-end integration tests against it, so
every PR exercises the freshly built package.

---

## Versioning & Release

- Version in `Directory.Build.props` (set in the gate-2 release PR before tagging)
- Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html); per `CHANGELOG.md`, the major version tracks Wolverine's major version
- The publish workflow extracts version from the git tag (`-p:Version`), so the tag is the source of truth

**Release flow (via the `release` agent):**
1. Invoke the release agent with intent, e.g. "cut the next beta" or "release 0.1.0-beta.6".
2. Approve the proposed version (gate 1).
3. Review and merge the CHANGELOG + version-bump PR it opens (gate 2).
4. The agent tags `main`, the `publish.yml` workflow packs + pushes to NuGet and
   creates the GitHub Release from the CHANGELOG section, and the agent verifies
   NuGet + the GitHub Release before reporting.

The **git tag is the version source of truth** for the pack. `Directory.Build.props`
is bumped in the gate-2 PR *before* tagging, so the tagged commit already matches —
there is no post-publish auto-bump PR.

Day-to-day, add notes under `## [Unreleased]` in `CHANGELOG.md` as you merge work.

⚠️ **Always tag a commit on main** — the workflow runs from the tagged commit, so
that commit must contain the latest workflow file and the matching CHANGELOG section.

---

## Important Constraints

- `IMongoDatabase` does NOT auto-enlist in the transaction. Prefer `MongoDbUnitOfWork` as a handler parameter; alternatively, accept `IClientSessionHandle` and pass it to every MongoDB write for atomicity.
- The test project depends on `WolverineFx.ComplianceTests` which is not on NuGet — requires local Wolverine source clone. CI resolves this by checking out the Wolverine source at the pinned tag.
- `Wolverine.MongoDB.Tests` uses `UseWolverineSource` MSBuild property to switch between project-ref (local dev) and package-ref (CI/pack).
- `DurabilityMode.Balanced` is supported. It requires `opts.UseTcpForControlEndpoint()` (or any control endpoint) and synchronized node clocks. The host logs a startup warning but does not throw.
