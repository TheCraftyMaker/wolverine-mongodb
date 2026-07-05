# Wolverine.MongoDB

## Overview

Native MongoDB persistence provider for Wolverine's transactional inbox/outbox. Implements `IMessageStore` directly against the MongoDB .NET driver. No EF Core dependency.

**Package:** `Wolverine.MongoDB` (NuGet, currently `0.1.0-beta.7`)  
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
src/Wolverine.MongoDB.Tests/        ← Integration tests (needs Wolverine source clone)
  MongoDbSagaHost.cs                ← ISagaHost implementation for compliance suites
  string_saga_storage_compliance.cs ← StringIdentifiedSagaComplianceSpecs<MongoDbSagaHost>
  guid_saga_storage_compliance.cs   ← GuidIdentifiedSagaComplianceSpecs (+ int, long)
  saga_atomicity.cs                 ← Custom atomicity, OCC, completion, and idempotency tests
  saga_multinode.cs                 ← [Category=multinode] cross-node exactly-once saga test
demo/                               ← Separate solution, references package from CI nupkg
  src/OrderDemo.Application/Sagas/
    OrderFulfillmentSaga.cs         ← Demo saga: Guid id, start/continue/complete lifecycle
  tests/OrderDemo.IntegrationTests/
    SagaFlowTests.cs                ← 7 saga integration tests (start, ship, complete, etc.)
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

### MongoDB Collections

| Collection | Purpose |
|------------|---------|
| `wolverine_incoming_envelopes` | Inbox (idempotency, durable queues) |
| `wolverine_outgoing_envelopes` | Outbox (pending broker delivery) |
| `wolverine_dead_letters` | Failed messages + exception info |
| `wolverine_nodes` | Node registry (heartbeat, capabilities) |
| `wolverine_node_assignments` | Agent-to-node mapping |
| `wolverine_saga_<lowercased-type>` | One collection per saga type (e.g. `wolverine_saga_orderfulfillmentsaga`) |

### Key Design Decisions

- **Hot path uses `findAndModify`** (single-doc atomic ops), not multi-doc transactions. Transactions only for handler atomicity (domain write + outbox in one commit).
- **Inbox idempotency:** unique `_id` index; duplicate insert → `DuplicateKeyException` → treated as already-processed.
- **Scheduled messages:** `FindOneAndUpdate` with `Status == Scheduled && ExecutionTime <= now` for atomic claim — exactly-once across competing nodes.
- **Node coordination:** lock document with configurable-lease TTL expiry via `findAndModify` (approximates PostgreSQL advisory locks). Both Solo and Balanced modes are supported.
- **Configurable leader lease:** `MongoDbPersistenceOptions.LockLeaseDuration` (default 1 min). `HasLeadershipLock()` reports `false` once 75% of the lease has elapsed — the node stops acting as leader before another can legitimately take over. Clocks must be synchronized to well within the lease duration.
- **`LoadOutgoingAsync` is owner-scoped and batch-limited:** only envelopes with `OwnerId == 0` (globally-owned / unclaimed) are returned, capped at `Durability.RecoveryBatchSize`. Envelopes owned by a live node are in-flight and must never be handed to recovery.
- **CAS-guarded outgoing recovery:** `RecoverOrphanedOutgoingAsync` uses a filter guard (`OwnerId == AnyNode`) on the claim `UpdateMany` and re-reads which ids this node actually won before enqueuing — prevents double-sends when two nodes race for the same orphaned envelopes.
- **Dead-node ownership release (Balanced mode only):** each recovery tick calls `ReleaseDeadNodeOwnershipAsync`, which releases incoming/outgoing ownership held by node numbers with no live node document. Runs before orphan recovery so released envelopes can be re-claimed in the same tick.
- **Handled markers expire via `KeepUntil` TTL:** `IncomingMessage` maps `envelope.KeepUntil` into the document. The TTL index on `keepUntil` automatically removes handled markers. Previously, `KeepUntil` was dropped, causing unbounded inbox growth.
- **Dead-letter TTL is opt-in:** `ExpirationTime` is only written when `Durability.DeadLetterQueueExpirationEnabled == true`. With the default `false`, dead letters are retained forever (matching RDBMS providers). The TTL index ignores documents without the `expirationTime` field.
- **Write concerns pinned on the store:** the `MongoDbMessageStore` constructor wraps its database handle with `WriteConcern.WMajority.With(journal: true)` and `ReadConcern.Majority`. This is independent of the consumer's `MongoClient` configuration. The app-facing `IMongoDatabase` registered by `UseMongoDbPersistence` is **not** pinned — domain write concerns belong to the application.
- **`INodeAgentPersistence.ClearAllAsync` is intentionally narrow (T4.4):** it clears only `wolverine_nodes` and `wolverine_node_assignments` — the operational node-state surface `INodeAgentPersistence` owns. It does not touch `wolverine_counters`, `wolverine_locks`, `wolverine_node_records`, or `wolverine_agent_restrictions`. The full system reset is `IMessageStoreAdmin.ClearAllAsync`/`RebuildAsync` (`MongoDbMessageStore.Admin.cs`), which clears all six system collections plus every `wolverine_saga_*` collection; the test harness (`AppFixture.ClearAll()`) calls `RebuildAsync()`, not the node-level method. No behavior change — documented at the call site in `MongoDbMessageStore.NodeAgents.cs`.
- **Single unkeyed `IMongoDatabase` registration (documented consumer constraint, T4.3):** `UseMongoDbPersistence` registers exactly one **unkeyed** `IMongoDatabase` pointing at `databaseName` (`WolverineMongoDbExtensions.cs:59-60`). Every code-generated frame resolves it by type — `TransactionalFrame` (`:57`, which also constructs `MongoDbUnitOfWork` at `:78`), all four saga frames and all three entity frames (`chain.FindVariable(typeof(IMongoDatabase))`), and the provider's `CanPersist` (`persistenceService = typeof(IMongoDatabase)`). An app that registers its own unkeyed `IMongoDatabase` collides with this: `Microsoft.Extensions.DependencyInjection` resolves the last registration for a single-service request, so ordering alone decides which database the frames (and the app's own injections) resolve. **Decision: document, do not switch to keyed/dedicated registration** — a keyed lookup would have to thread through every frame's `FindVariable`/`MethodCall` resolution **plus** `MongoDbUnitOfWork`, a high-blast-radius codegen change for a rare conflict (which is why T4.3 depends on both D6 and T1.1 — the change is validated against saga *and* entity codegen). App workaround: don't register a competing unkeyed `IMongoDatabase`; reuse Wolverine's, resolve a different database via `IMongoClient.GetDatabase(...)`, or register the app's own under a keyed service / wrapper type. Documented in `README.md` ("The registered `IMongoDatabase`") + `FOLLOWUPS.md`.
- **Balanced-mode startup warning:** `Initialize` and `BuildAgent` call `WarnOnBalancedMode`, which logs an `Information` message (once per store lifetime) if `DurabilityMode.Balanced` is detected — reminding operators that a control endpoint and synchronized clocks are required. This is a warning, not a throw; the host starts normally.
- **`MongoDbUnitOfWork` is the recommended handler write surface:** it accepts a handler parameter and threads the active `IClientSessionHandle` into every write, making it impossible to forget the session. The raw `IClientSessionHandle` pattern remains valid for repository-based handlers.
- **Transaction frame triggers broadly:** `CanApply` returns `true` for handlers whose dependency tree (or method parameters) includes `IMongoDatabase`, `IMongoClient`, `IMongoCollection<T>`, `IClientSessionHandle`, or `MongoDbUnitOfWork`.
- **DateTimeOffset stored as UTC BSON Date:** every `DateTimeOffset`/`DateTimeOffset?` property on document types is annotated with `[BsonRepresentation(BsonType.DateTime)]`. No process-global serializer is registered — the library does not mutate the host app's BSON registry.
- **Dead-letter replay is idempotent:** if a previous replay pass crashed after re-inserting the envelope but before deleting the DLQ document, the next pass catches `DuplicateIncomingEnvelopeException` and continues. Body-less poison letters are unflagged (not retried every tick) and left queryable.
- **Saga persistence is codegen-only:** there is no separate saga storage service. `MongoDbPersistenceFrameProvider` implements all `IPersistenceFrameProvider` saga members (Load/Insert/Update/Delete/CommitUnitOfWork). The frames run on the `TransactionalFrame` session so saga state and the outbox commit atomically. `CanApply` returns `true` for `SagaChain` — required or the provider is skipped for saga chains entirely.
- **Direct document storage:** the saga POCO is stored as a MongoDB document. The identity member maps to `_id` via the driver's default Id-member convention. No envelope wrapper.
- **Native id type:** `DetermineSagaIdType` resolves the saga's identity-member type (`Guid`/`string`/`int`/`long`) via `SagaChain.DetermineSagaIdMember`. Cosmos/RavenDb are string-only; this provider stores every type natively.
- **`Saga.Version` optimistic concurrency (insert/update diverge):** insert (`InsertSagaFrame`) is unguarded and stamps `Version = 1` via `InsertOneAsync`. Update (`UpdateSagaFrame`) captures `oldVersion`, sets `Version = oldVersion + 1`, then `ReplaceOneAsync` with filter `(_id, oldVersion)`, `IsUpsert = false`; throws `SagaConcurrencyException` when `ModifiedCount == 0`. The new version is written into the POCO before the replace because MongoDB stores the saga directly (unlike RDBMS providers). Completion delete is unguarded (matches Wolverine's lightweight SQL provider `DatabaseSagaSchema`). Cosmos/RavenDb are last-write-wins; this provider's OCC matches the Marten/EF/lightweight-SQL approach.
- **One collection per saga type:** `wolverine_saga_<lowercased-type-name>` (e.g. `wolverine_saga_orderfulfillmentsaga`). Idiomatic MongoDB — no cross-type `_id` collision. `ClearAllAsync`/`RebuildAsync` drop every collection matching the `wolverine_saga_` prefix.
- **`CommitUnitOfWorkFrame` for saga chains / no double-commit:** `ApplyTransactionSupport` adds the commit postprocessor only when `chain is not SagaChain`. For saga chains the single commit+flush flows through `CommitUnitOfWorkFrame` (inlined by `SagaChain` after the saga write). Mirrors Cosmos/RavenDb.
- **`MultipleHandlerBehavior.Separated` for saga + non-saga co-handlers:** a `SagaChain` calls `Handlers.Clear()`, silently dropping co-registered non-saga handlers. When a saga and a projector consume the same message, set `opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated` so each runs independently. Required in the demo.

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
`external/wolverine` submodule, pinned to the `V6.9.0` commit — keep the pin in sync with
`WolverineFx` in `Directory.Packages.props`), runs the compliance suite in two steps
(`Category!=multinode` then `Category=multinode`), then packs the library at version `0.0.0-ci`.
The `demo` job downloads that nupkg and runs the end-to-end integration tests against it, so
every PR exercises the freshly built package.

---

## Versioning & Release

- Version in `Directory.Build.props` (set in the gate-2 release PR before tagging)
- Major version tracks Wolverine: `0.1.x` ↔ `WolverineFx 6.x`
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
