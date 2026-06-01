# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The major version tracks Wolverine's major version.

## [Unreleased]

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

[Unreleased]: https://github.com/TheCraftyMaker/wolverine-mongodb/compare/v0.1.0-beta.1...HEAD
[0.1.0-beta.1]: https://github.com/TheCraftyMaker/wolverine-mongodb/releases/tag/v0.1.0-beta.1
