# Wolverine.MongoDB

## Overview

Native MongoDB persistence provider for Wolverine's transactional inbox/outbox. Implements `IMessageStore` directly against the MongoDB .NET driver. No EF Core dependency.

**Package:** `Wolverine.MongoDB` (NuGet; see `Directory.Build.props` for the current version — `1.0.2` as of the [1.0.2] CHANGELOG entry)  
**Targets:** .NET 9, .NET 10  
**Dependencies:** `WolverineFx 6.38.0` (submodule `external/wolverine` at `V6.38.0`), `MongoDB.Driver 3.x`  
**Constraint:** MongoDB must run as a replica set (transactions require it).

---

## Repository Layout

```
src/Wolverine.MongoDB/              ← Library (NuGet package)
  WolverineMongoDbExtensions.cs     ← Public API: UseMongoDbPersistence()
  MongoDbPersistenceOptions.cs      ← Public API: tuning (LockLeaseDuration) + Map*Collection overrides
  MongoDbUnitOfWork.cs              ← Public API: session-bound write helper
  Internals/                        ← All implementation (internal)
    SagaFrames.cs                   ← Saga codegen frames + MongoSagaOperations helpers
    EntityFrames.cs                 ← Generic entity codegen frames + MongoEntityOperations helpers
    MongoDbSagaStoreDiagnostics.cs  ← ISagaStoreDiagnostics implementation
    MongoCollectionNaming.cs        ← Collection-name resolver: mappings + collision claims
    MongoDbCollectionNamePolicy.cs  ← IHandlerPolicy that claims a collection per persisted type
    MongoTransactionOptions.cs      ← The TransactionOptions every library-opened transaction carries
    EntityQueryFrames.cs            ← [All]/[FirstOrDefault]/[Queryable] read frames (non-forcing session)
    MongoDbDeduplicationStore.cs    ← IDeduplicationStore over wolverine_deduplication (opt-in)
    MongoDbRecurringMessageStore.cs ← IRecurringMessageStore over wolverine_recurring_messages (opt-in, Main only)
    ControlMessageDocument.cs       ← Document shape for wolverine_control_messages (Balanced only)
    Transport/                      ← mongocontrol transport: endpoint, sender, listener (Balanced-mode node control)
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
  transaction_write_concern.cs      ← Command-monitoring proof that every transaction is majority+journaled
  collection_naming.cs              ← Unit facts: default-name pins, collision axes, mapping API (no Docker)
  collection_name_collision_guard.cs← Host facts: collision refused at StartAsync; mapping honoured end to end
  leadership_election_compliance.cs ← Upstream LeadershipElectionCompliance ([Category=multinode], un-gated)
  exclusive_listener_recovery_compliance.cs ← Upstream ExclusiveListenerRecoveryCompliance (GH-3590)
  core_type_name_collision_compliance.cs ← Upstream CoreTypeNameCollisionCompliance (GH-3907)
  recurring_message_compliance.cs   ← Upstream RecurringMessageCompliance (hosted via a RavenDb-style Bridge; the one Weasel-bound fact is replaced, see FOLLOWUPS)
  node_reregistration.cs            ← 6.38 INodeAgentPersistence contract: heartbeat miss, reregister, atomic claim
  retry_retention.cs                ← Rescheduled retries drop keepUntil and restore the payload
  persistence_provider_precedence.cs← IsCatchAll: selective providers win in mixed persistence, both orders
  dead_letter_replayable_filter.cs  ← Tri-state Replayable filter on query/discard/replay
  outbox_batching.cs                ← WasPersistedInOutbox + the batch StoreOutgoingAsync (one bulk update, all-or-nothing)
  entity_query_attributes.cs        ← [All]/[FirstOrDefault]/[Queryable] incl. session use proven on generated source
  after_commit_integration.cs / after_commit_http.cs ← AfterCommit ordering after commit + flush
  logical_message_deduplication.cs  ← Framework-level dedup facts incl. rollback release
  recurring_messages.cs             ← MongoDB-specific recurring-store facts (cross-node pause, trigger refusal)
  node_record_pruning.cs / orphan_sweep_settings.cs ← Durability maintenance settings honoured by the agent
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
- `MongoDbPersistenceOptions`: tuning options passed via the `configure` callback. Exposes `LockLeaseDuration` (default 1 minute) and the explicit collection-mapping API `MapSagaCollection<TSaga>(name)` / `MapEntityCollection<TEntity>(name)` (plus `Type` overloads) — see "Collection-name collisions are refused, never auto-resolved".
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
| `MongoDbMessageStore.Admin.cs` | `IMessageStoreAdmin` — collection/index creation, rebuild, dead-letter `envelopeId` backfill |
| `MongoDbMessageStore.NodeAgents.cs` | `IAgentFamily` — node registry, agent assignments, node-record trimming |
| `MongoDbMessageStore.Locking.cs` | Leader election via configurable-lease findAndModify lock document |
| `MongoDbMessageStore.ScheduledMessages.cs` | Scheduled message polling with atomic claim |
| `MongoDbMessageStore.Durability.cs` | Recovery loops; CAS outgoing claim; dead-node ownership release |

### Transaction Integration

| File | Role |
|------|------|
| `MongoDbEnvelopeTransaction.cs` | `IEnvelopeTransaction` — opens session, commits outbox atomically |
| `MongoDbPersistenceFrameProvider.cs` | Code-gen: detects MongoDB types, injects transactional frame; saga members |
| `TransactionalFrame.cs` | Generated code frame: `StartSession → StartTransaction(MongoTransactionOptions.Durable) → handler → Commit` |
| `MongoTransactionOptions.cs` | The write/read concern every library-opened transaction restates (handle-level concerns do not survive into a transaction) |

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
| `wolverine_dead_letters` | Failed messages + exception info. `_id` follows `Durability.MessageIdentity` (the envelope Guid in `IdOnly`, an identity-derived Guid in `IdAndDestination`); the framework-facing envelope Guid is the separate `envelopeId` element |
| `wolverine_nodes` | Node registry (heartbeat, capabilities) |
| `wolverine_node_assignments` | Agent-to-node mapping |
| `wolverine_deduplication` | Logical-deduplication claims (`_id` = deduplication id, `expires` TTL); only when `EnableMessageDeduplication` |
| `wolverine_recurring_messages` | Recurring-schedule tracking (`_id` = schedule name); only when `EnableRecurringMessages` on the Main store |
| `wolverine_control_messages` | Inter-node control messages (Balanced only): `_id` = envelope id, `nodeId`, `body`, `expires` (TTL) |
| `wolverine_saga_<lowercased-type>` | One collection per saga type (e.g. `wolverine_saga_orderfulfillmentsaga`) |
| `<lowercased-entity-type>` | One un-prefixed collection per app entity type persisted via `[Entity]`/`Insert`/`Update`/`Store`/`Delete<T>` (e.g. `OrderNote` → `ordernote`) — application-owned, not swept by `ClearAllAsync`/`RebuildAsync` |

### Key Design Decisions

- **Hot path uses `findAndModify`** (single-doc atomic ops), not multi-doc transactions. Transactions only for handler atomicity (domain write + outbox in one commit).
- **Inbox idempotency:** unique `_id` index; duplicate insert → `DuplicateKeyException` → treated as already-processed.
- **Scheduled messages — both phases of the poll assert the same predicate:** `PublishDueScheduledMessagesAsync` captures one `now`, selects `Status == Scheduled && ExecutionTime <= now`, and the per-document `FindOneAndUpdate` claim (`TryClaimDueScheduledMessageAsync`) re-asserts **both** conjuncts against that same captured `now` — never a fresh `DateTimeOffset.UtcNow`, so the two filters truncate identically against `ExecutionTime`'s millisecond-precision BSON Date. The conjuncts guarantee different things and neither is redundant: `Status == Scheduled` gives exactly-once across competing nodes (and absorbs a concurrent `CancelAsync`, which deletes the document, leaving nothing to match); `ExecutionTime <= now` exists because `IScheduledMessages.RescheduleAsync` writes only `ExecutionTime` and leaves the status alone, so without it a message rescheduled into the future between select and claim is claimed and executed early. **Deliberate divergence from Cosmos/SQL Server/Postgres**, which all claim without rechecking anything (Cosmos's unguarded `ReplaceItemAsync` actively clobbers a concurrent reschedule) — do not "restore parity": narrowing the claim can only refuse (the document is re-selected next tick), never double-publish or lose a message. If the per-document loop is ever collapsed into a set-based `UpdateMany`, the `ExecutionTime <= now` conjunct must ride along in the batch filter. Residual (documented, not fixed — inherent to every provider): a reschedule landing *after* the claim is lost silently, because the document is already `Incoming` and `RescheduleAsync` returns `Task` with no matched count.
- **Node coordination:** lock document with configurable-lease TTL expiry via `findAndModify` (approximates PostgreSQL advisory locks). Both Solo and Balanced modes are supported.
- **Configurable leader lease:** `MongoDbPersistenceOptions.LockLeaseDuration` (default 1 min). `HasLeadershipLock()` reports `false` once 75% of the lease has elapsed — the node stops acting as leader before another can legitimately take over. Clocks must be synchronized to well within the lease duration.
- **Agent-assignment removal is node-scoped; assignment *writes* are not:** `wolverine_node_assignments` holds exactly one document per agent URI (`_id` = the URI, `nodeId` = the owner), so ownership transfers by overwriting `nodeId` — `AddAssignmentAsync`/`AssignAgentsAsync` upsert unscoped on purpose, matching Postgres's `on conflict (id) do update set node_id`. `RemoveAssignmentAsync` therefore filters on `_id` **and** `nodeId` (`MongoDbMessageStore.NodeAgents.cs`): Wolverine's only caller, `NodeAgentController.StopAgentAsync`, always passes its own `UniqueNodeId` (“remove *my* claim”) and issues the removal even when this node was never running the agent, so an unscoped delete would let a node that no longer owns an agent wipe the row that now belongs to another node — and because `LoadAllNodesAsync` attributes a URI to exactly one node, the resulting duplicate start would be invisible to Wolverine's split-brain detection. This is contract parity / defence in depth, not a fix for an observed failure: under WolverineFx 6.21.0 `ReassignAgent` awaits the old owner's `StopAgent` before issuing `AssignAgent`, and a late stop is discarded by the handler pipeline via the request/reply envelope's 60s `DeliverWithin`. All five RDBMS providers scope the delete; RavenDb and Cosmos do not (an upstream omission, not a document-store trade-off — see `FOLLOWUPS.md`). Do not “fix” the writes into node-scoped operations: that breaks ownership transfer and the `NodePersistenceCompliance` assignment facts.
- **`LoadOutgoingAsync` is owner-scoped and batch-limited:** only envelopes with `OwnerId == 0` (globally-owned / unclaimed) are returned, capped at `Durability.RecoveryBatchSize`. Envelopes owned by a live node are in-flight and must never be handed to recovery.
- **CAS-guarded outgoing recovery:** `RecoverOrphanedOutgoingAsync` uses a filter guard (`OwnerId == AnyNode`) on the claim `UpdateMany` and re-reads which ids this node actually won before enqueuing — prevents double-sends when two nodes race for the same orphaned envelopes.
- **Dead-node ownership release is two-tick-confirmed (Balanced mode only):** the orphan sweep — its own loop at `OrphanedMessageSweepPollingTime`, never created in Solo mode — calls `ReleaseDeadNodeOwnershipAsync`, which releases in bounded batches (`OrphanedMessageReleaseBatchSize` × `OrphanedMessageReleaseMaxBatchesPerCycle`, remainder on the next sweep; a tick is one sweep). It which computes the node numbers that are *owned* in `wolverine_incoming_envelopes`/`wolverine_outgoing_envelopes` but have no live `wolverine_nodes` document, and releases only the numbers that were **also** dead on the previous tick (`Filter.In(confirmed)`, never `Filter.Nin(liveSnapshot)`). Reading the owned set *before* the live set, plus monotonic never-reused node numbers (see the node-number-reuse decision below), means a number confirmed dead was dead for the whole interval — a node that registers and claims between the read and the write can never be released. Cost: a crashed node's envelopes are rescued one recovery interval later. Still runs before orphan recovery so released envelopes are re-claimable in the same tick. **A future change to reuse freed node numbers would invalidate this argument** — see the soundness comment on the method.
- **Handled markers expire via `KeepUntil` TTL:** `IncomingMessage` maps `envelope.KeepUntil` into the document. The TTL index on `keepUntil` automatically removes handled markers. Previously, `KeepUntil` was dropped, causing unbounded inbox growth.
- **Dead-letter TTL is opt-in:** `ExpirationTime` is only written when `Durability.DeadLetterQueueExpirationEnabled == true`. With the default `false`, dead letters are retained forever (matching RDBMS providers). The TTL index ignores documents without the `expirationTime` field.
- **Dead-letter identity follows `Durability.MessageIdentity` (2026-09-08):** the `_id` is `envelope.Id` in the default `IdOnly` mode and a deterministic SHA-256-derived Guid of `InboxIdentity(envelope)` in `IdAndDestination` (`DeadLetterIdentity.Derive`, selected by `MongoDbMessageStore.DeadLetterKey`); the framework-facing Guid lives in `envelopeId`, exactly as `IncomingMessage` splits the two. Before this, `MoveToDeadLetterStorageAsync` upserted on the bare envelope Guid, so in `IdAndDestination` a second failed delivery of the same Guid to a different destination silently replaced the first — while the incoming delete two lines below already used the identity-aware key. The RDBMS providers add `received_at` to the dead-letter primary key in exactly that mode (`Wolverine.Postgresql/Schema/DeadLettersTable.cs:19-26`); Cosmos/RavenDb carry the same defect this fixes. **`_id` stays a BSON Binary Guid deliberately** — re-typing it to a string would throw `Cannot deserialize a 'String' from BsonType 'Binary'` on every pre-existing document *inside cursor deserialization*, i.e. failing whole result sets, which would take out `QueryAsync` and every `ReplayDeadLettersAsync` tick. Guid-facing operations go through `ForEnvelopeIds`, which `$or`s in a pre-split branch (`_id` in ids **and** `envelopeId` missing or empty) so un-backfilled documents stay addressable; `EnsureIndexesAsync` adds an `envelopeId` index and backfills `envelopeId` from `_id` (aggregation-pipeline update, MongoDB 4.2+); the operator-facing, **non-destructive** entry point for that backfill is `MigrateAsync()` (also run at startup by the storage migration) — **not** `RebuildAsync()`, which is `ClearAllAsync()` + `EnsureIndexesAsync()` and therefore wipes the dead-letter, inbox, outbox and node collections before the backfill can see anything. Discard/replay/edit apply to **all** documents for the Guid and `DeadLetterEnvelopeByIdAsync` returns the first ordered by `receivedAt`, matching `MessageDatabase.DeadLetterAdminService.cs:176-221`; edit re-serializes per document so each keeps its own `Destination` (a shared blob would collapse both replays onto one inbox key). `ReplayDeadLettersAsync`'s three id filters are **deliberately unchanged** — they round-trip the `_id` of documents they just read, and repointing them at `envelopeId` would over-delete sibling destinations.
- **Write concerns pinned on the store — handle *and* transaction:** the `MongoDbMessageStore` constructor wraps its database handle with `WriteConcern.WMajority.With(journal: true)` and `ReadConcern.Majority`, independent of the consumer's `MongoClient` configuration. That handle-level pin covers the **sessionless** writes only: MongoDB discards collection/database concerns for any operation run inside a transaction — the individual writes are never acknowledged on their own, only `commitTransaction` is, and its concern resolves transaction options → session default transaction options → `MongoClientSettings`. So every transaction the library opens **restates** the pin via `MongoTransactionOptions.Durable` (see the next bullet). Consequence, deliberate: application writes **enlisted** in the handler transaction (`MongoDbUnitOfWork`, a raw `IClientSessionHandle`, saga and entity documents) commit at `w:majority, j:true` too, because a transaction has exactly one write concern. The app-facing `IMongoDatabase` registered by `UseMongoDbPersistence` is still **not** pinned — domain writes made *outside* the Wolverine transaction remain the application's choice. Two paths remain honestly unpinned and must not be overclaimed: a read-only `[Entity]` load that resolves no session (`LoadEntityFrame`'s session-less fallback) and `MongoDbSagaStoreDiagnostics`, both of which read off the unpinned app-facing handle outside any transaction.
- **One transaction-options constant, one store-side transaction funnel:** `MongoTransactionOptions.Durable` (`Internals/MongoTransactionOptions.cs`) is the single `TransactionOptions` every library-opened transaction carries — majority + journaled writes, majority reads. It is `public` for the same reason `MongoConstants`/`MongoSagaOperations`/`MongoEntityOperations` are: the code-generated handler references it by name and compiles into the **application's** assembly, which is not on this assembly's `InternalsVisibleTo`. There are exactly two ways to open a transaction: store-side code goes through the private `MongoDbMessageStore.InTransactionAsync` funnel (`MongoDbMessageStore.cs`), and the code-generated handler transaction is emitted by `TransactionalFrame`. `transaction_write_concern.cs` asserts, via `CommandStartedEvent` command monitoring against a deliberately weak (`w:1`/`local`) client, that **every** `commitTransaction`/`startTransaction` emitted during a live host's lifetime carries the pin — so an unpinned fourth site fails loudly. **Decision: not configurable per host.** `MongoDbPersistenceOptions` cannot reach the frame (`MongoDbPersistenceFrameProvider` is `new()`-constrained by Wolverine's `InsertFirstPersistenceStrategy<T>` and the options object is never registered in DI), and the pin already governs every non-transactional store write, so a knob would buy no availability the store does not already require. Tracked in `FOLLOWUPS.md`. ⚠️ Pre-generated codegen: consumers on `TypeLoadMode.Static` **or** `TypeLoadMode.Auto` with checked-in `Internal/Generated` keep the option-less `StartTransaction()` until they regenerate — Auto attaches a pre-generated handler type by name whenever one is present and never compares it against the current frame output, so it needs the same regeneration as Static. The two store-side sites go through `InTransactionAsync` and are pinned in every codegen mode.
- **`INodeAgentPersistence.ClearAllAsync` is intentionally narrow (T4.4):** it clears only `wolverine_nodes` and `wolverine_node_assignments` — the operational node-state surface `INodeAgentPersistence` owns. It does not touch `wolverine_counters`, `wolverine_locks`, `wolverine_node_records`, or `wolverine_agent_restrictions`. The full system reset is `IMessageStoreAdmin.ClearAllAsync`/`RebuildAsync` (`MongoDbMessageStore.Admin.cs`), which clears all twelve system collections (`wolverine_incoming_envelopes`, `wolverine_outgoing_envelopes`, `wolverine_dead_letters`, `wolverine_nodes`, `wolverine_node_assignments`, `wolverine_node_records`, `wolverine_agent_restrictions`, `wolverine_counters`, `wolverine_locks`, `wolverine_deduplication`, `wolverine_recurring_messages`, `wolverine_control_messages`) plus every `wolverine_saga_*` collection; the test harness (`AppFixture.ClearAll()`) calls `RebuildAsync()`, not the node-level method. No behavior change — documented at the call site in `MongoDbMessageStore.NodeAgents.cs`.
- **Single unkeyed `IMongoDatabase` registration (documented consumer constraint, T4.3):** `UseMongoDbPersistence` registers exactly one **unkeyed** `IMongoDatabase` pointing at `databaseName` (`WolverineMongoDbExtensions.cs:59-60`). Every code-generated frame resolves it by type — `TransactionalFrame` (`:57`, which also constructs `MongoDbUnitOfWork` at `:78`), all four saga frames and all three entity frames (`chain.FindVariable(typeof(IMongoDatabase))`), and the provider's `CanPersist` (`persistenceService = typeof(IMongoDatabase)`). An app that registers its own unkeyed `IMongoDatabase` collides with this: `Microsoft.Extensions.DependencyInjection` resolves the last registration for a single-service request, so ordering alone decides which database the frames (and the app's own injections) resolve. **Decision: document, do not switch to keyed/dedicated registration** — a keyed lookup would have to thread through every frame's `FindVariable`/`MethodCall` resolution **plus** `MongoDbUnitOfWork`, a high-blast-radius codegen change for a rare conflict (which is why T4.3 depends on both D6 and T1.1 — the change is validated against saga *and* entity codegen). App workaround: don't register a competing unkeyed `IMongoDatabase`; reuse Wolverine's, resolve a different database via `IMongoClient.GetDatabase(...)`, or register the app's own under a keyed service / wrapper type. Documented in `README.md` ("The registered `IMongoDatabase`") + `FOLLOWUPS.md`.
- **Native control transport (Balanced only):** `Initialize` registers `MongoDbControlTransport` when the store is Main, the mode is Balanced and `Transports.NodeControlEndpoint` is null, and sets it as the node control endpoint, exactly as the RavenDb and Cosmos stores do. A configured endpoint (TCP, broker control queues) always wins. One document per control message in `wolverine_control_messages`, one-second poll per node, thirty-second expiry, TTL index as the reaper. The startup `Information` line names the endpoint and reminds about clock synchronization. **Deliberate divergence from RavenDb's listener, do not restore parity:** `MongoDbControlListener.cs:75-77` filters its poll on `nodeId` **and** `expires > now`; `RavenDbControlListener.cs:88-92` filters on `NodeId` alone, with no expiry check at all. The extra predicate closes the window between a message's expiry and the TTL monitor's next sweep (up to a minute), so a stale agent command is never delivered late. Dropping it as redundant with the TTL index would reopen that window.
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
- **One collection per saga type:** `wolverine_saga_<lowercased-type-name>` (e.g. `wolverine_saga_orderfulfillmentsaga`). Idiomatic MongoDB — no cross-type `_id` collision. `ClearAllAsync`/`RebuildAsync` drop every collection matching the `wolverine_saga_` prefix. **Uniqueness precondition:** the name comes from `Type.Name`, so it is namespace-blind, generic-argument-blind and case-folded — two saga types with the same simple name would share a collection. `MongoConstants.SagaCollectionName` stays the pure default function; `MongoCollectionNaming.ResolveSaga`/`ClaimSaga` is the resolution point that layers mappings and collision detection over it, and library code never calls `MongoConstants` directly.
- **`CommitUnitOfWorkFrame` for saga chains / no double-commit:** `ApplyTransactionSupport` adds the commit postprocessor only when `chain is not SagaChain`. For saga chains the single commit+flush flows through `CommitUnitOfWorkFrame` (inlined by `SagaChain` after the saga write). Mirrors Cosmos/RavenDb.
- **`MultipleHandlerBehavior.Separated` for saga + non-saga co-handlers:** a `SagaChain` calls `Handlers.Clear()`, silently dropping co-registered non-saga handlers. When a saga and a projector consume the same message, set `opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated` so each runs independently. Required in the demo.
- **`CanPersist` is unconditional `true` (T1.1):** matching Cosmos/RavenDb, the provider now advertises persistence support for every entity type, not just `Saga` subclasses — required because `[Entity]` parameter loads key on `CanPersist(parameterType)`. The saga-vs-entity distinction moved entirely into the frame factories (see Entity Persistence Implementation above); `CanPersist` no longer gates it. `persistenceService` stays `typeof(IMongoDatabase)`.
- **Entity write semantics: upsert-only, no OCC (T1.1, D6 LD2):** `Insert<T>`/`Update<T>`/`Store<T>` all compile to the same `ReplaceOneAsync(..., IsUpsert = true)` (`MongoUpsertEntityFrame`) — entities carry no `Saga.Version`-style guard, so writes are last-write-wins, matching Cosmos's `CosmosDbUpsertFrame`. `Delete<T>` removes by id (`MongoDeleteEntityByVariableFrame`). App-level optimistic concurrency remains available via the repository pattern (a hand-guarded `ReplaceOneAsync` filter), the same path the demo's `OrderRepository` uses for the `Order` aggregate.
- **Entity collection naming + id extraction (T1.1, D6 LD3):** `MongoConstants.EntityCollectionName(Type) => type.Name.ToLowerInvariant()` — deliberately **un-prefixed** (unlike `wolverine_saga_*`) because entity collections hold application data, not Wolverine system state; `ClearAllAsync`/`RebuildAsync`'s `wolverine_saga_` sweep never touches them. The entity's `_id` value is extracted generically via `BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap` (`MongoEntityOperations.IdOf`) — not Cosmos's `entity.ToString()` coercion. Since F7 every entity operation obtains its collection through one private `entityCollection<T>` accessor that calls `MongoIdentityMapping.EnsureIdMember` first, so the member `IdOf` reads **is** Wolverine's resolved identity member and the `[Entity]` load's `Eq("_id", …)` filter keys the same one (see "Identity-member alignment"). `IdOf` itself is unchanged; its "no mapped _id member" throw is now a backstop — alignment fails loudly first. **Uniqueness precondition:** as for sagas, `Type.Name` carries no namespace, no generic arguments and no case, and entity names are additionally un-prefixed, so they share the *application's* collection namespace too. `MongoConstants.EntityCollectionName` stays the pure default function; `MongoCollectionNaming.ResolveEntity`/`ClaimEntity` is the resolution point (see the next bullet).
- **Collection-name collisions are refused, never auto-resolved:** `MongoCollectionNaming` is the single resolution point for "which collection does this type live in", and `MongoDbCollectionNamePolicy` (an `IHandlerPolicy`) claims one per persisted type. Defaults are byte-identical — `MongoConstants.SagaCollectionName`/`EntityCollectionName` are untouched, so no existing collection is renamed and no data is migrated. Two different Wolverine-persisted types resolving to one collection throws an `InvalidOperationException` naming both full type names, the collection, the database and the exact mapping call; auto-resolving (e.g. falling back to `FullName` for the "second" type) was rejected because which type is second depends on discovery order, so it would be a non-deterministic silent rename. **Hook point:** `WolverineRuntime.StartAsync` calls `Handlers.Compile(Options, _container)` (`WolverineRuntime.HostService.cs:100`) before `startMessagingTransportsAsync()`, and `HandlerGraph.Compile` runs every `IHandlerPolicy` (`HandlerGraph.cs:397`) — so the check fires before any listener starts, and in `TypeLoadMode.Static` too (`Compile` is unconditional; only frame *constructors* are skipped), which is the gap that forces `MongoIdentityMapping` to duplicate itself in the runtime accessors. An `IHostedService` (Cosmos's `CosmosDbSagaSerializationValidator` precedent) runs strictly later, after transports are live. The runtime accessors claim too, as defence in depth for any discovery shape the policy's walk misses — but only the policy fails the operator's deploy; an accessor throw happens inside an open transaction, mid-handler, which Wolverine's retry/DLQ policy would turn into a message-delivery incident. **Escape hatch:** `MongoDbPersistenceOptions.MapSagaCollection`/`MapEntityCollection`. Saga mappings must keep the `wolverine_saga_` prefix because `MongoDbMessageStore.Admin.cs`'s sweep is prefix-coupled (both sides now share `MongoCollectionNaming.IsSagaCollectionName`, so they cannot drift); entity mappings are barred from that prefix and from the nine system collection names. Unlike upstream RDBMS — which registers a `SagaTableDefinition` with the user's table name and then discards it on the runtime path (`PostgresqlMessageStore.cs:782` et al. construct `new SagaTableDefinition(typeof(T), null)`) — the mapping here is honoured by the frames, `MongoDbSagaStoreDiagnostics` and the admin sweep alike. **Process-global state, scoped by database:** both the mapping map and the claim registry are `static` (the runtime accessors are `static` and receive only an `IMongoDatabase`; threading options through them would break the `public static` `MongoSagaOperations`/`MongoEntityOperations` signatures pre-generated deployments compiled against). Both dictionaries are keyed by **database name**, so two in-process hosts on different databases never interact; two on the *same* database must agree, which they must anyway since they share the physical collections. Re-applying an identical mapping set and re-claiming an already-owned collection are lock-free no-ops (a snapshot probe and `ConcurrentDictionary.GetOrAdd` respectively), so repeated and concurrent host construction — the compliance suites, the two-host multinode fixtures — costs one dictionary probe; genuine disagreement throws. **`ApplyMappings` is all-or-nothing:** the mapping registry is an immutable snapshot swapped under a configuration-time lock only after the whole set has validated, so a rejected set is never published — publishing first and validating after would have redirected an *already running*, unmapped host away from the collection it had been writing all along, orphaning those documents with no exception on that host. The runtime `resolve` path never takes the lock. Note the limit of "disagreement": a host that configures **no** mapping is indistinguishable from one mapped to the default, so it silently inherits another host's mapping rather than clashing with it (the reverse order — default actually resolved first, mapping applied second — does throw). **Two narrower new refusals**, both silently destructive before: an entity type whose *default* name lands inside `wolverine_saga_` (dropped by every `RebuildAsync`) or on a system collection name. **Explicit non-goals:** an overlap with a collection the application's own repositories own (`GetCollection<T>("orders")`) is invisible to a handler-graph walk — documented in README, `MapEntityCollection` is the remedy; and two `IMongoClient`s on different clusters sharing a database name are treated as one namespace.
- **`ISagaStoreDiagnostics` registered unconditionally (T2.1):** `UseMongoDbPersistence` always registers `MongoDbSagaStoreDiagnostics` as a singleton — no opt-in flag, matching how RavenDb registers its implementation. The Wolverine runtime's diagnostics aggregator tolerates zero or multiple registered implementations, so unconditional registration is safe even in a mixed-provider app.
- **Diagnostics reaches internal Wolverine members via reflection, not direct calls (T2.1):** `MongoDbSagaStoreDiagnostics` needs `SagaDescriptorBuilder.Build`, `WolverineOptions.HandlerGraph`, and `HandlerGraph.Container` — all `internal` to Wolverine core. Because this provider ships as an **external** NuGet package (not on Wolverine's `[InternalsVisibleTo]` list, unlike the in-repo RavenDb/Marten/EF Core/RDBMS providers), it bridges each member through isolated, cached, non-throwing reflection, each call site carrying a `// TODO(upstream)` marker. When contributed upstream into the Wolverine repo, add `Wolverine.MongoDB` to `[InternalsVisibleTo]` and collapse each bridge to the direct member access every sibling provider uses.
- **Multinode leadership compliance un-gated (T4.5, 2026-07-05):** the upstream `LeadershipElectionCompliance` suite was compile-gated behind `#if RUN_MULTINODE` because earlier WolverineFx releases required the lowest-numbered surviving node to win a leadership-claim race our `w:majority` lock couldn't guarantee. WolverineFx 6.9.0 reworked those facts around the "any healthy node leads" model this provider already implements. Verified 5× consecutive green on net9.0 **and** net10.0 (10/10 runs, 17/17 facts each); the `#if` guard was removed and `[Trait("Category","multinode")]` now routes the suite into CI's existing multinode step with no `ci.yml` change.
- **Pre-1.0 hardening backlog — four dated document/defer decisions (T4.6, 2026-07-05):** no behavior changed; each is tracked in `FOLLOWUPS.md` with rationale and an extension point if revisited.
  - **Node-number reuse:** the node-number counter (`MongoDbMessageStore.NodeAgents.cs:16-20`) is a pure monotonic increment that never reuses a freed slot. Acceptable — node numbers are short-lived coordination identifiers, not long-lived keys. If revisited post-1.0: track the lowest free slot instead of redesigning allocation. ⚠️ **Monotonicity is now load-bearing:** the two-tick dead-node ownership release (see the dead-node bullet above) relies on "a number issued after the previous tick cannot appear in the previous tick's dead set". Reusing freed slots would invalidate step 4 of that soundness argument — re-derive it (step 3's read-ordering argument survives alone, but the total-interval guarantee does not) before changing allocation.
  - **Index migration (post-1.0, document/defer — see `FOLLOWUPS.md`):** the hardening pass added compound indexes (`MongoDbMessageStore.Admin.cs:18-64`) but `EnsureIndexesAsync` only creates indexes (plus the one-off dead-letter `envelopeId` backfill), never drops superseded single-field ones from deployments created before that pass. Harmless (old indexes stay valid, just suboptimal); a `RebuildAsync` (which recreates all indexes from scratch) is an acceptable manual remedy **only where losing data is acceptable** — it clears every system collection first. Add an explicit `Admin.MigrateAsync()` drop step only if a concrete need arises.
  - **Lease fencing token (epoch):** the lock document (`MongoDbMessageStore.Locking.cs`) has no fencing token; not needed for store-only leader work since the 75%-lease margin already mitigates the internal stale-leadership window. Track as a future hardening item only if leader-scoped **external** side effects (writes outside this store needing stale-epoch rejection) become common.
  - **Saga-specific indexes:** saga collections (`wolverine_saga_*`) have only the implicit `_id` index; the current access pattern (load/insert/update/delete by `_id`) doesn't need more. `EnsureIndexesAsync`/`RebuildAsync` (`MongoDbMessageStore.Admin.cs`) is the extension point when a concrete query pattern (e.g. filtering by status) demands secondary indexes.

- **Retry rescheduling clears `keepUntil` and restores the payload (2026-09-15):** `SchedulingUpdate` `$unset`s `keepUntil` because the TTL index on it is not status-gated (unlike the RDBMS `where status='Handled'` sweep), and rewrites `body`/`messageType`/`receivedAt` from the live envelope when it has a payload so a body-less eager handled marker (`Envelope.ForPersistedHandled`) becomes a runnable retry. Identity (`_id`, `envelopeId`) and the release to `AnyNode` are unchanged.
- **`IsCatchAll => true`:** `CanPersist` claims every type, so the provider must sort after selective providers (EF Core) in `OrderedPersistenceProviders`; without the flag whichever `UseXxx` ran last (all register via `InsertFirstPersistenceStrategy`) won every entity.
- **Outbox batch overload is all-or-nothing:** one unordered `BulkWrite` of `ReplaceOne{IsUpsert}` models inside `InTransactionAsync`; `WasPersistedInOutbox` is set only after the commit (both overloads set it only after success). Matches the RDBMS explicit-transaction + rollback and Cosmos `TransactionalBatch` expectations.
- **`[All]`/`[FirstOrDefault]`/`[Queryable]` are plain reads of the type's collection** (`EntityQueryFrames.cs`), resolving the session non-forcingly like `LoadEntityFrame`; a `Saga` type reads `wolverine_saga_*` (reads carry no version guard, so this is safe where LD4 rejects saga *writes*).
- **Logical deduplication: sessionless claim + rollback release.** Core inserts `ClaimDeduplicationIdFrame` at `Middleware[0]` (before the session exists), never hands a provider a session for the claim, and omits its release frame for transactional chains. The claim is therefore an `_id`-unique insert on its own (so a duplicate key never aborts a handler transaction), and `TransactionalFrame` emits `IMessageDeduplicator.ReleaseAsync` in its rollback catch, guarded by the claim outcome. The claim frame is `internal` upstream; `DeduplicationClaim.FindIn` reads its id/marker reflectively and fails the host build loudly if the shape changes (`TODO(upstream)`).
- **Recurring messages mirror `RdbmsRecurringMessageStore` member for member;** the paused-trigger refusal is the update predicate itself (an upsert whose `paused == false` filter misses turns into a duplicate-key insert). Main store only.
- **Node-record pruning and the orphan sweep are provider-implemented:** upstream applies `NodeRecordRetention`/`NodeRecordPruningPeriod`/`NodeEventRecordExpirationTime` and the `OrphanedMessage*` settings only in the RDBMS `DurabilityAgent`; `MongoDbDurabilityAgent` runs its own loops for them (pruning for the Main store, first pass after `min(period, 1 min)`; the sweep in Balanced mode only). The 14-day node-record TTL index stays as a backstop.

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

Tests use Testcontainers (auto-starts MongoDB replica set). Docker Desktop required. The test project is an
xUnit **v3** host (`xunit.v3`, `OutputType=Exe`, `ValueTask` `IAsyncLifetime`, every CT-accepting call passes
`TestContext.Current.CancellationToken` — the xUnit1051 analyzer is an error under `TreatWarningsAsErrors`).
`GitHubActionsTestLogger` stays on 2.4.1: 3.x targets Microsoft.Testing.Platform 2.0 and does not compile
next to the MTP 1.9.1 that xunit.v3 3.2.2 carries. A nested worktree under the main checkout still picks up a
parent `Directory.Build.rsp` if one exists; verify audit-clean restores with `-noAutoResponse`.

**CI:** the `library` job checks out with `submodules: recursive` (the Wolverine source is the
`external/wolverine` submodule, pinned to the `V6.38.0` commit — keep the pin in sync with
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
- `DurabilityMode.Balanced` is supported with no extra configuration; the native `mongocontrol` transport is registered unless another control endpoint was configured. Node clocks must be synchronized.
