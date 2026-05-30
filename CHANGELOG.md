# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The major version tracks Wolverine's major version.

## [Unreleased]

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
- Wolverine compliance test suite (63 tests) via Testcontainers replica-set
  fixture; all tests green.

### Notes
- The compliance test suite uses a local Wolverine source clone until
  `WolverineFx.ComplianceTests` is published to NuGet.
- Replica set is required; standalone MongoDB is not supported.

[Unreleased]: https://github.com/TheCraftyMaker/wolverine-mongodb/commits/main
