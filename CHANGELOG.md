# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The major version tracks Wolverine's major version.

## [Unreleased]

### Added
- **Explicit per-type collection mapping.** `MongoDbPersistenceOptions` gains
  `MapSagaCollection<TSaga>(name)` and `MapEntityCollection<TEntity>(name)` (plus `Type`
  overloads) to override which collection a saga or entity is stored in. A mapping is honoured
  by the generated frames, `ISagaStoreDiagnostics` and the admin sweep alike. Saga names must
  keep the `wolverine_saga_` prefix; entity names may not use it, or a system collection name.

  ```csharp
  opts.UseMongoDbPersistence("appdb", o => o
      .MapEntityCollection<Billing.Note>("billing_note")
      .MapSagaCollection<Returns.OrderSaga>("wolverine_saga_returns_ordersaga"));
  ```

### Fixed
- **Collection-name collisions are refused at startup instead of silently sharing a
  collection.** Names come from `Type.Name.ToLowerInvariant()`, so two types with the same
  simple name — different namespaces, differing only in case, or two closed constructions of
  one generic — resolved to one collection and mixed their documents undetectably. Host start
  now fails, naming both types and the mapping call to add. Default names are unchanged;
  nothing is renamed or migrated. **Breaking** for a host that is already colliding.

- **Dead letters are keyed by the message-identity unit, not the bare envelope Guid.** Under
  `MessageIdentity.IdAndDestination` two failed deliveries of one Guid to different
  destinations overwrote each other — message loss, not deduplication. Documents now carry a
  separate `envelopeId`. Existing data stays queryable and `MigrateAsync()` backfills it
  (MongoDB 4.2+). No change under the default `IdOnly`.

- **Every transaction the library opens now commits at `w:majority` (journaled) and reads at
  majority.** MongoDB discards handle-level concerns inside a transaction, so the
  code-generated handler/outbox transaction and the dead-letter move had been committing at
  the consumer's client default. Writes the application enlists in the handler transaction
  inherit the same concern. **Upgrade:** regenerate pre-generated handler code —
  `TypeLoadMode.Static` *and* `Auto` — to pick this up.

- **Agent-assignment removal now honours the node id.** `RemoveAssignmentAsync` deleted by
  agent URI alone, so a removal issued by a node that no longer owned an agent destroyed the
  row belonging to the node that did: the agent then read as unassigned and a duplicate start
  was invisible to split-brain detection. The delete now filters on node id too, matching the
  RDBMS providers. Defence in depth against a contract violation, not a reproduced outage.

- **A scheduled message rescheduled mid-poll no longer fires early.** The per-document claim
  filtered only on `(_id, Status == Scheduled)`, so a `RescheduleAsync` landing between the
  batch select and the claim did not invalidate it — the message ran immediately while
  carrying a future execution time, then disappeared from `QueryAsync`. The claim now
  re-asserts `ExecutionTime <= now` against the instant the select captured.

## [1.0.1] - 2026-07-28

### Added
- **`CustomerFeedback` demo entity — non-`Id` identity coverage.** The demo now persists an
  entity keyed by `CustomerFeedbackId` (the `{TypeName}Id` convention, with no member named
  `Id`) and asserts against MongoDB that the stored `_id` is a native BSON `Guid`, proving the
  identity-mapping fix end to end through the packaged nupkg rather than only in-repo.

### Fixed
- **Sagas keyed by any identity convention other than `Id` now persist and load correctly.**
  Wolverine resolves the identity member by its own convention while the driver recognises only
  `Id`/`id`/`_id` and `[BsonId]`. Nothing reconciled the two, so a saga keyed on e.g.
  `ShipmentId` was written with a server-generated `ObjectId`: every load returned `null` and
  every start accumulated another unfindable orphan. `MongoIdentityMapping` now aligns them at
  codegen *and* at runtime. Sagas keyed on `Id`, declared or inherited, are unaffected.

- **Generic entity loads and writes now agree on the same identity member.** The `[Entity]`
  load filtered `_id` with Wolverine's resolved member while `Insert`/`Update`/`Store`/`Delete`
  keyed off the driver's, so writes landed under one key and reads probed another. All entity
  frames and runtime operations now align first. Entities keyed on `Id` are unaffected; the two
  shapes the driver cannot align fail at host build with an actionable message.

- **Unresolvable saga identity members now fail loudly instead of being invented.**
  `UpdateSagaFrame` fell back to `?? "Id"`, emitting generated code that referenced a member
  which might not exist — surfacing as a cryptic compile error inside the *generated* source.
  Resolution now funnels through one path that throws a clear `ArgumentException`.

- **A non-saga handler returning `Delete<TSaga>` or `IStorageAction<TSaga>` now fails at host
  build.** Both compiled cleanly and then wrote to the un-prefixed entity collection instead of
  `wolverine_saga_*`, with no `Saga.Version` guard, so the write appeared to succeed and
  affected nothing the saga machinery reads. Complete a saga with `MarkCompleted()` instead.

- **Incoming recovery claims are destination-scoped under `MessageIdentity.IdAndDestination`.**
  `ReassignIncomingAsync` filtered on the raw envelope Guid, so claiming a recovery page for one
  listener also claimed every sibling destination's document — stranding it, owned by a node
  that never enqueued it and invisible to orphan recovery. Claims now key on the document
  `_id`. No change under the default `IdOnly`.

- **Dead-node ownership release can no longer strip a live node's in-flight envelopes.** The
  release used `Filter.Nin(liveSnapshot)`, a blacklist over a stale read: a node that registered
  between the read and the write matched it, and its in-flight envelopes were released for
  reprocessing. Release is now two-tick-confirmed and names nodes positively. **Timing change:**
  a crashed node's envelopes are rescued one recovery interval later than before.

- **A batch `StoreIncomingAsync` containing a duplicate no longer strands the batch's fresh
  envelopes.** The unordered insert committed every non-duplicate document before the
  duplicate-key error surfaced; redelivery then completed the duplicate without enqueuing it,
  leaving the fresh envelopes persisted, owned, never handled and invisible to orphan recovery.
  The batch insert is now a transaction, all-or-nothing. The single-envelope overload is
  unchanged.

- **`MongoDbSagaStoreDiagnostics.ReadSagaAsync` coerces the supplied identity** to the saga's
  native id type (`Guid`/`int`/`long`/`string`) before querying, per the `ISagaStoreDiagnostics`
  contract. A string handed in for a `Guid`-keyed saga — an id rehydrated from a URL or JSON —
  previously always returned "not found".

- **`EditAndReplayAsync` no longer throws `EndOfStreamException` on a body-less poison dead
  letter.** It now rebuilds the envelope through `DeadLetterMessage.ToEnvelope()`, which guards
  against an empty body, instead of deserializing an empty byte array directly.

- **`MongoDbDurabilityAgent.StopAsync` now awaits its recovery and scheduled-job loops** rather
  than calling `SafeDispose` on still-running tasks — a silent no-op that also leaked both
  `CancellationTokenSource`s. It cancels, awaits both loops under a 5-second timeout, then
  disposes. This shrinks the window in which a recovery tick writes after ownership is released.

### Documentation
- Post-1.0.0 accuracy sweep on `CLAUDE.md` and `FOLLOWUPS.md`: the package version reference,
  the versioning-policy wording, the index-migration follow-up's "before 1.0" framing, and the
  `ClearAllAsync`/`RebuildAsync` collection count (nine system collections, not six). No
  behavior changes.

### Changed
- **Upgraded `WolverineFx` from 6.9.0 to 6.21.0** and re-pinned the `external/wolverine`
  submodule to match. No provider code changes were needed; the compliance and multinode suites
  are green on net9.0 and net10.0. Not 6.22.0 — it adds a `DeadLetterAdminCompliance` fact this
  provider does not yet satisfy, scoped as separate work.

- **One version per package across both solutions.** The library and the demo are separate
  dependabot ecosystems, so their pins had drifted (`MongoDB.Driver` 3.9.0 vs 3.10.0, and
  others). Every shared package now names a single plain version in both
  `Directory.Packages.props`, and the never-released bracketed major-version ranges are gone
  with them.

- **Deduplicated inbox/outbox write definitions — behavior-preserving.** Shared update
  definitions are built once instead of being inlined per call site, and every bare owner-id
  `0` literal now reads `MongoConstants.AnyNode`. No document-schema change.

- **Store efficiency sweep — behavior-preserving.** `ReplayDeadLettersAsync` is batch-limited
  and replays a whole batch at a time, falling back to the per-letter path on a duplicate so
  idempotent replay still holds. Node-agent collection handles are cached in the constructor,
  `LoadAllNodesAsync` fetches nodes and assignments concurrently, and
  `PersistAgentRestrictionsAsync` issues one `BulkWriteAsync`.

- **Dropped the explicit `Microsoft.SourceLink.GitHub` reference.** The SDK has bundled
  SourceLink since 8.0 and an explicit reference shadows it. Verified the packed `.snupkg` still
  carries the source-document mapping and the nuspec its `<repository>` metadata. Removes a
  recurring dependabot bump with no behavior change.

## [1.0.0] - 2026-07-06

### Added
- **Generic entity persistence (`[Entity]`, `Insert<T>`/`Update<T>`/`Store<T>`/`Delete<T>`,
  `IStorageAction<T>`).** MongoDB now implements Wolverine's generic persistence surface for
  any plain document type, not just `Saga` subclasses, closing the one functional gap vs
  Cosmos and RavenDb. `[Entity]` handler parameters load a document by id before the handler
  runs; returning `Insert`/`Update`/`Store`/`Delete<T>` or an `IStorageAction<T>` persists it
  afterward, atomically with the outbox on the same MongoDB transaction session.
  - **`CanPersist` is now unconditional `true`** (previously scoped to `Saga` subclasses):
    the saga-vs-entity distinction moved into the frame factories, which branch on
    `variable.VariableType.CanBeCastTo<Saga>()`. Saga behavior, OCC, and collection naming
    are unchanged.
  - **Collection naming:** one un-prefixed collection per entity type,
    `<lowercased-type-name>` (e.g. `OrderNote` → `ordernote`), distinct from sagas'
    `wolverine_saga_` prefix, since entity collections are application data.
  - **Write semantics:** `Insert`/`Update`/`Store` all upsert (`ReplaceOneAsync(IsUpsert=true)`,
    matching Cosmos); no optimistic concurrency for plain entities, use the repository
    pattern for app-controlled OCC. The entity's `_id` is extracted via the MongoDB driver's
    class map (`BsonClassMap...IdMemberMap`), not a `.ToString()` coercion.
  - **Coverage:** Wolverine's upstream `StorageActionCompliance` suite passes (all facts) via
    `storage_action_compliance.cs`. Custom tests cover entity write + outbox atomicity, and
    saga/entity coexistence in the same handler (frame-branching regression guard). Full
    single-node suite green on net9.0 + net10.0; cross-node entity persistence verified under
    `DurabilityMode.Balanced`.
  - **Demo:** `OrderNoteHandler` demonstrates `Insert`/`[Entity]`+`Update`/`[Entity]`+`Delete`
    against a real `OrderNote` document, wired to `POST/DELETE /orders/{id}/notes` endpoints.
- **Saga store diagnostics (`ISagaStoreDiagnostics`).** MongoDB now implements Wolverine's
  read-only saga-explorer surface, matching RavenDb, and above Cosmos, which does not
  implement it, so CritterWatch and other monitoring tools can list the Mongo-owned saga
  types, read a single saga instance by id, and peek at recent instances. Registered
  automatically by `UseMongoDbPersistence`. Reads run against the `wolverine_saga_<type>`
  collections with native `_id` matching (no string coercion); `count` is clamped to
  `[0, 1000]`; descriptors are tagged `"MongoDb"`. Registration does not affect startup or
  the existing inbox/outbox/saga behavior (full single-node suite green on net9.0 + net10.0).
- **`MongoDbUnitOfWork` demo example.** `RecordOrderAuditHandler` shows the no-repository
  write path: a handler that accepts `MongoDbUnitOfWork` directly and writes through
  `Collection<T>(name)`, with the session threaded automatically, alongside the existing
  repository + `IClientSessionHandle` example, wired to `POST /orders/{id}/audit`.
- **Saga-cascade read-model consumer in the demo.** `FulfillmentStatusProjector` consumes
  `OrderFulfillmentSaga`'s `FulfillmentShippedEvent`/`FulfillmentCompletedEvent` cascades via
  a durable local queue and maintains a `fulfillment_delivery_statuses` read model, exercising
  the full saga → outbox → consumer path end to end.

### Changed
- **Multinode leadership compliance is no longer compile-gated.** The upstream
  `LeadershipElectionCompliance` suite (previously behind `#if RUN_MULTINODE` because earlier
  WolverineFx releases required a leadership-race ordering guarantee this provider's
  `w:majority` lock could not make) now runs unconditionally as part of CI's multinode step,
  after WolverineFx 6.9.0 reworked the underlying facts around the "any healthy node leads"
  model this provider already implements. Verified 5× consecutive green on net9.0 and net10.0
  before un-gating.

## [0.1.0-beta.7] - 2026-06-21

### Added
- **MongoDB saga persistence.** Stateful Wolverine sagas (`Saga` subclasses) are now
  persisted in MongoDB via the provider's code-generation contracts (`IPersistenceFrameProvider`).
  Each saga type gets its own collection named `wolverine_saga_<lowercased-type-name>`
  (e.g. `wolverine_saga_orderfulfillmentsaga`). Collections are created automatically on
  startup.
  - **Supported id types:** `Guid`, `string`, `int`, and `long`, stored natively as the
    corresponding BSON type (not coerced to string as in Cosmos/RavenDb).
  - **Optimistic concurrency via `Saga.Version`:** insert stamps `Version = 1`; update
    uses a guarded `ReplaceOneAsync` on `(_id, oldVersion)` and throws
    `SagaConcurrencyException` when `ModifiedCount == 0`. Delete on completion is
    unguarded, matching Wolverine's lightweight SQL provider.
  - **Atomic with the outbox:** saga state writes and outbox entries commit in the same
    MongoDB multi-document transaction as the handler's domain writes.
  - **Coverage:** Wolverine's upstream compliance suites (`StringIdentifiedSagaComplianceSpecs`,
    `GuidIdentifiedSagaComplianceSpecs`, `IntIdentifiedSagaComplianceSpecs`,
    `LongIdentifiedSagaComplianceSpecs`) pass: 27 compliance facts across 4 id types on
    net9.0 + net10.0. Custom tests cover atomicity (rollback saga + outbox on failure),
    completion delete, OCC conflict, and inbox idempotency. Cross-node saga correctness
    verified with five consecutive green runs of `saga_multinode.cs` on both TFMs.
- **`OrderFulfillmentSaga` in the demo.** The demo now includes a saga that tracks an
  order through placement, shipping, and delivery confirmation. Seven integration tests
  cover: start, continue, complete (doc deleted), missing-state (`UnknownSagaException`),
  duplicate-message idempotency, across-restart state survival, and saga/projector
  coexistence via `MultipleHandlerBehavior.Separated`.

### Changed
- **Upgraded the WolverineFx baseline from 6.2.2 to 6.9.0.** Bumped `WolverineFx`
  and `WolverineFx.ComplianceTests` in `Directory.Packages.props` and moved the
  pinned `external/wolverine` compliance submodule to `V6.9.0`, so the library is
  now built, tested, and packaged against the same WolverineFx version consumers
  run. This fixes **saga persistence under WolverineFx newer than 6.2.2**: a
  library compiled against 6.2.2 was not selected as the saga persistence provider
  at runtime under 6.9.0: the saga handler ran but its state was never persisted
  (the inbox/outbox path was unaffected, which is why the regression went
  unnoticed until a saga ran against a newer runtime). Verified against 6.9.0 on
  net9.0 and net10.0: the full single-node compliance suite (150 tests) and the
  multinode end-to-end message-guarantee tests (`multinode_end_to_end`) pass. The
  multinode leadership-election compliance facts remain compile-gated behind
  `RUN_MULTINODE` by deliberate decision (see `FOLLOWUPS.md`) and are not part of
  the automated run.

## [0.1.0-beta.6] - 2026-06-18

### Added
- **`DurabilityMode.Balanced` (multinode) support.** Multiple nodes can now run
  against the same MongoDB store. Requires `opts.UseTcpForControlEndpoint()` (or
  any control endpoint) and synchronized node clocks. Startup emits an `Information`
  log message confirming the mode instead of throwing.
- **`MongoDbPersistenceOptions` — MongoDB-specific persistence tuning.** Pass a
  configure callback to `UseMongoDbPersistence` to set `LockLeaseDuration`
  (default 1 minute). Example:
  `opts.UseMongoDbPersistence("db", mongo => mongo.LockLeaseDuration = TimeSpan.FromSeconds(30))`.
- **`DeleteOldNodeRecordsAsync` implementation.** The leader now trims old
  node-event records by retain count (`DeleteOldNodeRecordsAsync(int)`). The
  TTL index on `wolverine_node_records` remains a 14-day backstop.
- **Dead-node ownership release in `DurabilityMode.Balanced`.** Each recovery
  tick releases incoming and outgoing envelope ownership held by node numbers
  that have no live node document (crashed nodes), then re-runs orphan recovery
  so rescued envelopes are re-claimed in the same tick.
- **Cross-node message-guarantee tests (`multinode_end_to_end.cs`).** Two in-proc
  `Balanced`-mode hosts verify: (1) a scheduled message executes exactly once
  across competing nodes; (2) a survivor releases and recovers envelopes owned by
  a dead node. Both facts verified with five consecutive green runs on net9.0 and
  net10.0.
- **CI runs the multinode test category as a separate step.** The `library` job
  now runs `Category!=multinode` and `Category=multinode` as distinct steps so a
  cross-node flake is immediately distinguishable from a core regression.
- **Demo config-driven durability mode with multinode runbook.** The demo API
  reads `Wolverine:DurabilityMode` from configuration (default `Solo`); set it to
  `Balanced` to run multiple instances against the same MongoDB and RabbitMQ.
  See `demo/README.md` for the two-instance runbook.

### Changed

> **Behavior change:** Leader lock lease default changed from 5 minutes to 1 minute.

The previous 5-minute default made leader failover unacceptably slow and was
the root cause of leadership compliance suite flakiness. The new default of
1 minute provides reasonable failover speed for most deployments. Tune via
`MongoDbPersistenceOptions.LockLeaseDuration` if needed.

> **Behavior change:** `DurabilityMode.Balanced` no longer throws at startup.

Previously, `Initialize`, `StartScheduledJobs`, and `BuildAgent` threw
`InvalidOperationException` if `DurabilityMode.Balanced` was detected. These
now log an `Information` message and continue; the host starts normally.
`DurabilityMode.Solo` still works as before; no changes needed for existing
single-node deployments.

### Fixed
- **CAS-guarded outgoing recovery prevents cross-node double-claims.** When two
  nodes race to recover the same orphaned outgoing envelopes, the second node's
  claim `UpdateMany` now carries an `OwnerId == AnyNode` filter guard. After the
  update, only envelopes this node actually won (confirmed by a re-read) are
  enqueued, preventing duplicate sends.
- **`LoadOutgoingAsync` now returns only globally-owned envelopes, batch-limited.**
  Previously the query filtered by destination only, which caused orphan recovery
  to re-claim in-flight envelopes (duplicate sends) and load unbounded result sets.
  The query now filters `OwnerId == 0` and applies `Limit(RecoveryBatchSize)`,
  mirroring all RDBMS providers.
- **Handled inbox markers carry `KeepUntil` for TTL expiry.**
  `IncomingMessage` previously dropped `envelope.KeepUntil`, leaving handled markers
  with no expiry; the TTL index never fired and the inbox grew without bound.
  Both the lazy (`StoreIncomingAsync`) and eager (`PersistIncomingAsync`) paths
  now preserve `KeepUntil`.
- **Dead-letter replay is now idempotent and per-document fault-tolerant.**
  A crash between the re-insert and the DLQ delete previously left the next replay
  tick throwing `DuplicateIncomingEnvelopeException`, aborting the whole batch
  permanently. The loop now catches the duplicate and falls through to delete the
  DLQ document, converging the state. Body-less poison dead letters are unflagged
  (not retried every tick) and remain queryable.
- **Write concerns pinned on the message store.**
  The store constructor now wraps its database handle with
  `WriteConcern.WMajority.With(journal: true)` and `ReadConcern.Majority`,
  independent of the consumer's `MongoClient` configuration. A `w:1` client no
  longer weakens inbox/outbox durability.
- **Transaction frame applied to `IMongoCollection<T>`, `IMongoClient`, and
  `IClientSessionHandle` handlers.** Previously only handlers whose dependency
  tree contained `IMongoDatabase` received the transactional frame. Handlers
  injecting `IMongoCollection<T>` silently ran without a transaction; handlers
  declaring `IClientSessionHandle` directly failed code generation.

## [0.1.0-beta.5] - 2026-06-14

### Added
- **CI runs the full compliance test suite on every PR.** The `library` job checks
  out the Wolverine source at tag `V6.2.2` and runs
  `dotnet test src/Wolverine.MongoDB.Tests` with `UseWolverineSource=true`. The
  `demo` job downloads the freshly packed nupkg (`0.0.0-ci`) and runs the
  end-to-end integration tests against it, so no stale NuGet version is exercised.
- **`MongoDbUnitOfWork` — session-bound write helper.** Handlers can accept
  `MongoDbUnitOfWork` as a parameter instead of (or alongside) `IClientSessionHandle`.
  Writes through `uow.Collection<T>("name")` automatically participate in the
  handler's transaction; the session cannot be forgotten. `SessionBoundCollection<T>`
  exposes `InsertOneAsync`, `InsertManyAsync`, `ReplaceOneAsync`, `UpdateOneAsync`,
  `UpdateManyAsync`, `DeleteOneAsync`, `DeleteManyAsync`, `FindOneAndUpdateAsync`,
  and `Find`.
- **Compound and TTL indexes on all envelope collections.** New indexes:
  - Incoming: `(Status, ExecutionTime)` for scheduled-message poll; `EnvelopeId`
    for reassignment/reschedule; `(OwnerId, ReceivedAt)` for orphan recovery;
    `KeepUntil` TTL.
  - Outgoing: `(OwnerId, Destination)` serving the fixed `LoadOutgoingAsync`;
    existing `Destination` and `DeliverBy` indexes retained.
  - Dead letters: `ExpirationTime` TTL (no-op when field absent, i.e. expiration
    disabled); `SentAt`, `MessageType`, `ExceptionType`, `Replayable` indexes.
  - Node records: `Timestamp` TTL (14-day retention).
- **Server-side aggregation for `SummarizeAllAsync` / `SummarizeAsync`.** Dead-letter
  and scheduled-message summary methods now use `$group` pipelines instead of
  loading all documents into the application process.
- **Release automation: `release` agent + GitHub Releases.** A
  `.claude/agents/release.md` agent proposes the next version, prepares a CHANGELOG +
  version-bump PR (gated on human approval and merge), then tags, monitors the publish
  workflow, and verifies the NuGet push and the GitHub Release. `publish.yml` now
  creates a GitHub Release from the released version's `CHANGELOG.md` section, extracted
  by `.github/scripts/extract-changelog.sh`.

### Changed

> **Behavior change:** Dead letters no longer expire by default.

Previously, `MoveToDeadLetterStorageAsync` unconditionally stamped `ExpirationTime`,
causing TTL deletion after 10 days under the library's default settings: silent
data loss. Now, `ExpirationTime` is only written when
`opts.Durability.DeadLetterQueueExpirationEnabled = true` (Wolverine's default is
`false`). Existing deployments that relied on automatic expiry must opt in explicitly.

> **Behavior change:** Startup now throws on `DurabilityMode.Balanced`.

`Wolverine.MongoDB` only supports single-node (`DurabilityMode.Solo`) deployments.
Previously, a consumer who forgot to set `Solo` got a subtly broken cluster.
`Initialize`, `StartScheduledJobs`, and `BuildAgent` now throw
`InvalidOperationException` if `DurabilityMode.Balanced` is detected. Set
`opts.Durability.Mode = DurabilityMode.Solo` in your host configuration.

- **Per-property BSON `DateTime` representation instead of a process-global serializer.**
  All `DateTimeOffset`/`DateTimeOffset?` fields on document types are now annotated
  with `[BsonRepresentation(BsonType.DateTime)]`. The `MongoSerializerRegistration`
  class and its `[ModuleInitializer]` call have been removed. The library no longer
  mutates the host application's BSON registry.
- **Release flow bumps version + CHANGELOG before tagging.** `Directory.Build.props`
  and the `CHANGELOG.md` version section are now set in the release PR on `main`
  before the tag is pushed, so the tagged commit is self-consistent. The previous
  post-publish auto-bump PR has been removed.

## [0.1.0-beta.2] - 2026-06-08

### Added
- CI workflow with separate library build and demo integration test jobs.
- Trivy security scanning workflow with SARIF upload to GitHub Security tab.
- Dependabot configuration for NuGet and GitHub Actions dependencies.
- `SECURITY.md` with private vulnerability reporting guidance.
- Repository ruleset enforcing PR reviews and status checks (owner bypass).
- Secret scanning and push protection enabled.
- NuGet, Build, Tests, Security, License, and .NET badges in README.

### Changed
- Demo app now references `Wolverine.MongoDB` from nuget.org (removed local feed).
- README updated for published beta: installation instructions, demo reference,
  modern quick-start snippet.
- `CLAUDE.md` rewritten as a contributor-facing guide (was implementation notes).
- Added `demo/CLAUDE.md` contributor guide for the demo application.

### Fixed
- `.devswarm/` directory no longer tracked in git.
- Publish workflow now also pushes symbol packages (`.snupkg`).

## [0.1.0-beta.1] - 2026-06-01

### Added
- Project scaffolding and DevSwarm parallel-workspace configuration.
- Repository setup: README, MIT license, contributing guide, and NuGet publish
  workflow. (Pre-existed from the config workspace and was reconciled here.)
- Central build configuration (`Directory.Build.props`, `Directory.Packages.props`)
  and the `Wolverine.MongoDB.sln` solution.
- Library project `Wolverine.MongoDB` (net9.0;net10.0) and test project
  `Wolverine.MongoDB.Tests` (net9.0).
- CI workflow (`ci.yml`); reconciled the NuGet publish workflow to pack only the
  library project.
- MongoDB document types and envelope mapping (`IncomingMessage`, `OutgoingMessage`,
  `DeadLetterMessage`, `NodeDocument`, `AgentAssignmentDocument`,
  `NodeRecordDocument`, `AgentRestrictionDocument`, `LockDocument`,
  `NodeCounterDocument`) covering all persistence collections.
- `IMessageStoreAdmin` implementation: automatic creation of all MongoDB
  collections (`wolverine_incoming_envelopes`, `wolverine_outgoing_envelopes`,
  `wolverine_dead_letters`, `wolverine_nodes`, `wolverine_node_assignments`) and
  required indexes on startup; `RebuildAsync` and `ClearAllAsync` for test
  teardown.
- `IMessageInbox` implementation: store incoming envelopes with duplicate-key
  detection for idempotency; mark as handled/delete on success; recover orphaned
  envelopes from crashed nodes.
- `IMessageOutbox` implementation: persist outgoing envelopes; relay polling via
  `LoadOutgoingAsync`; mark sent/delete after confirmed delivery.
- Scheduled-message support: `LoadScheduledToExecuteAsync` with
  `findAndModify`-based ownership claim so scheduled envelopes are claimed
  exactly once across competing nodes.
- `IDeadLetters` implementation: store failed envelopes with exception details;
  paged query; replay (move back to incoming queue).
- Single-node node coordination: lease-based leader election using a
  `findAndModify` lock document with TTL expiry; heartbeat writes; stale-node
  detection; `IAgentFamily` implementation for the Wolverine `DurabilityAgent`.
- `MongoDbDurabilityAgent`: background polling loop covering outbox relay,
  scheduled-message dispatch, orphan recovery, and node heartbeats.
- `MongoDbEnvelopeTransaction`: transactional middleware that opens a MongoDB
  client session, runs the handler, and commits the domain write plus outbox
  write atomically in a single multi-document transaction.
- `UseMongoDbPersistence(databaseName)` extension method on `WolverineOptions`
  for one-line registration; resolves `IMongoClient` from the DI container.
- Code-generation integration (`MongoDbPersistenceFrameProvider`,
  `TransactionalFrame`) so handlers using `IMongoDatabase` participate in the
  transactional outbox automatically via Wolverine's source-generation pipeline.
- Wolverine compliance test suite via Testcontainers replica-set fixture;
  all tests green (86 tests after the post-review hardening pass).

### Fixed
Post-review hardening pass (adversarial review of the 0.1.0 implementation):
- **Inbox dedup key honors `Durability.MessageIdentity`**: under the default
  `IdOnly`, a redelivery to a different destination is now correctly deduped by
  envelope id; `IdAndDestination` keeps them distinct.
- **`RescheduleExistingEnvelopeForRetryAsync`** now updates the existing inbox
  document instead of inserting (no longer throws a duplicate-key error on retry).
- **Outbox orphan recovery**: the durability agent now reassigns and re-sends
  orphaned outgoing envelopes (`OwnerId == AnyNode`), not just incoming ones.
- **Transactional session disposal**: the generated handler now disposes the
  MongoDB session via `await using`, preventing session leaks.
- **Dead-letter replay** now actually moves replayable messages back to the
  incoming collection (previously it only flagged them).
- **`DateTimeOffset` persisted as UTC BSON `Date`** (process-wide serializer
  registration): fixes TTL expiry, dead-letter time-range filtering/paging,
  node-record ordering, and scheduled-message UTC comparison.
- **Bulk `StoreIncomingAsync`** now rethrows non-duplicate write errors instead
  of silently swallowing them.
- **Async transaction rollback** (`AbortTransactionAsync` is awaited).
- **Heartbeats no longer write phantom node documents**: a heartbeat for an
  unknown node re-registers it (Postgres-style) rather than upserting a
  half-populated record.
- **Eager-idempotency inside the outbox transaction** no longer aborts the
  session (duplicate detected via a transaction-consistent read instead of a
  failing insert).
- **Scheduled-message claim** guarded with a `Status == Scheduled` filter and
  crash-safe ordering so a due message is published at most once and is never
  stranded.
- **Atomic dead-letter move** (DLQ upsert + incoming delete in one transaction,
  with poison-message serialization guarded) and **compare-and-swap incoming
  reassignment** (only still-unclaimed envelopes are reassigned).

### Notes
- The compliance test suite uses a local Wolverine source clone until
  `WolverineFx.ComplianceTests` is published to NuGet.
- Replica set is required; standalone MongoDB is not supported.

[Unreleased]: https://github.com/TheCraftyMaker/wolverine-mongodb/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/TheCraftyMaker/wolverine-mongodb/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/TheCraftyMaker/wolverine-mongodb/compare/v0.1.0-beta.7...v1.0.0
[0.1.0-beta.7]: https://github.com/TheCraftyMaker/wolverine-mongodb/compare/v0.1.0-beta.6...v0.1.0-beta.7
[0.1.0-beta.6]: https://github.com/TheCraftyMaker/wolverine-mongodb/compare/v0.1.0-beta.2...v0.1.0-beta.6
[0.1.0-beta.2]: https://github.com/TheCraftyMaker/wolverine-mongodb/compare/v0.1.0-beta.1...v0.1.0-beta.2
[0.1.0-beta.1]: https://github.com/TheCraftyMaker/wolverine-mongodb/releases/tag/v0.1.0-beta.1
