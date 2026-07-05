# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The major version tracks Wolverine's major version.

## [Unreleased]

### Added
- **Saga store diagnostics (`ISagaStoreDiagnostics`).** MongoDB now implements Wolverine's
  read-only saga-explorer surface — matching RavenDb, and above Cosmos, which does not
  implement it — so CritterWatch and other monitoring tools can list the Mongo-owned saga
  types, read a single saga instance by id, and peek at recent instances. Registered
  automatically by `UseMongoDbPersistence`. Reads run against the `wolverine_saga_<type>`
  collections with native `_id` matching (no string coercion); `count` is clamped to
  `[0, 1000]`; descriptors are tagged `"MongoDb"`. Registration does not affect startup or
  the existing inbox/outbox/saga behavior (full single-node suite green on net9.0 + net10.0).

## [0.1.0-beta.7] - 2026-06-21

### Added
- **MongoDB saga persistence.** Stateful Wolverine sagas (`Saga` subclasses) are now
  persisted in MongoDB via the provider's code-generation contracts (`IPersistenceFrameProvider`).
  Each saga type gets its own collection named `wolverine_saga_<lowercased-type-name>`
  (e.g. `wolverine_saga_orderfulfillmentsaga`). Collections are created automatically on
  startup.
  - **Supported id types:** `Guid`, `string`, `int`, and `long` — stored natively as the
    corresponding BSON type (not coerced to string as in Cosmos/RavenDb).
  - **Optimistic concurrency via `Saga.Version`:** insert stamps `Version = 1`; update
    uses a guarded `ReplaceOneAsync` on `(_id, oldVersion)` and throws
    `SagaConcurrencyException` when `ModifiedCount == 0`. Delete on completion is
    unguarded, matching Wolverine's lightweight SQL provider.
  - **Atomic with the outbox:** saga state writes and outbox entries commit in the same
    MongoDB multi-document transaction as the handler's domain writes.
  - **Coverage:** Wolverine's upstream compliance suites (`StringIdentifiedSagaComplianceSpecs`,
    `GuidIdentifiedSagaComplianceSpecs`, `IntIdentifiedSagaComplianceSpecs`,
    `LongIdentifiedSagaComplianceSpecs`) pass — 27 compliance facts across 4 id types on
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
  at runtime under 6.9.0 — the saga handler ran but its state was never persisted
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
now log an `Information` message and continue — the host starts normally.
`DurabilityMode.Solo` still works as before; no changes needed for existing
single-node deployments.

### Fixed
- **CAS-guarded outgoing recovery prevents cross-node double-claims.** When two
  nodes race to recover the same orphaned outgoing envelopes, the second node's
  claim `UpdateMany` now carries an `OwnerId == AnyNode` filter guard. After the
  update, only envelopes this node actually won (confirmed by a re-read) are
  enqueued — preventing duplicate sends.
- **`LoadOutgoingAsync` now returns only globally-owned envelopes, batch-limited.**
  Previously the query filtered by destination only, which caused orphan recovery
  to re-claim in-flight envelopes (duplicate sends) and load unbounded result sets.
  The query now filters `OwnerId == 0` and applies `Limit(RecoveryBatchSize)`,
  mirroring all RDBMS providers.
- **Handled inbox markers carry `KeepUntil` for TTL expiry.**
  `IncomingMessage` previously dropped `envelope.KeepUntil`, leaving handled markers
  with no expiry — the TTL index never fired and the inbox grew without bound.
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

### Added
- **CI runs the full compliance test suite on every PR.** The `library` job checks
  out the Wolverine source at tag `V6.2.2` and runs
  `dotnet test src/Wolverine.MongoDB.Tests` with `UseWolverineSource=true`. The
  `demo` job downloads the freshly packed nupkg (`0.0.0-ci`) and runs the
  end-to-end integration tests against it, so no stale NuGet version is exercised.
- **`MongoDbUnitOfWork` — session-bound write helper.** Handlers can accept
  `MongoDbUnitOfWork` as a parameter instead of (or alongside) `IClientSessionHandle`.
  Writes through `uow.Collection<T>("name")` automatically participate in the
  handler's transaction — the session cannot be forgotten. `SessionBoundCollection<T>`
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
causing TTL deletion after 10 days under the library's default settings — silent
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
- **Inbox dedup key honors `Durability.MessageIdentity`** — under the default
  `IdOnly`, a redelivery to a different destination is now correctly deduped by
  envelope id; `IdAndDestination` keeps them distinct.
- **`RescheduleExistingEnvelopeForRetryAsync`** now updates the existing inbox
  document instead of inserting (no longer throws a duplicate-key error on retry).
- **Outbox orphan recovery** — the durability agent now reassigns and re-sends
  orphaned outgoing envelopes (`OwnerId == AnyNode`), not just incoming ones.
- **Transactional session disposal** — the generated handler now disposes the
  MongoDB session via `await using`, preventing session leaks.
- **Dead-letter replay** now actually moves replayable messages back to the
  incoming collection (previously it only flagged them).
- **`DateTimeOffset` persisted as UTC BSON `Date`** (process-wide serializer
  registration) — fixes TTL expiry, dead-letter time-range filtering/paging,
  node-record ordering, and scheduled-message UTC comparison.
- **Bulk `StoreIncomingAsync`** now rethrows non-duplicate write errors instead
  of silently swallowing them.
- **Async transaction rollback** (`AbortTransactionAsync` is awaited).
- **Heartbeats no longer write phantom node documents** — a heartbeat for an
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

[Unreleased]: https://github.com/TheCraftyMaker/wolverine-mongodb/compare/v0.1.0-beta.7...HEAD
[0.1.0-beta.7]: https://github.com/TheCraftyMaker/wolverine-mongodb/compare/v0.1.0-beta.6...v0.1.0-beta.7
[0.1.0-beta.6]: https://github.com/TheCraftyMaker/wolverine-mongodb/compare/v0.1.0-beta.2...v0.1.0-beta.6
[0.1.0-beta.2]: https://github.com/TheCraftyMaker/wolverine-mongodb/compare/v0.1.0-beta.1...v0.1.0-beta.2
[0.1.0-beta.1]: https://github.com/TheCraftyMaker/wolverine-mongodb/releases/tag/v0.1.0-beta.1
