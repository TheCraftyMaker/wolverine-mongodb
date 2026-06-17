# Wolverine.MongoDB

## Overview

Native MongoDB persistence provider for Wolverine's transactional inbox/outbox. Implements `IMessageStore` directly against the MongoDB .NET driver. No EF Core dependency.

**Package:** `Wolverine.MongoDB` (NuGet, currently `0.1.0-beta.2`)  
**Targets:** .NET 9, .NET 10  
**Dependencies:** `WolverineFx 6.x`, `MongoDB.Driver 3.x`  
**Constraint:** MongoDB must run as a replica set (transactions require it).

---

## Repository Layout

```
src/Wolverine.MongoDB/              ← Library (NuGet package)
  WolverineMongoDbExtensions.cs     ← Public API: UseMongoDbPersistence()
  MongoDbUnitOfWork.cs              ← Public API: session-bound write helper
  Internals/                        ← All implementation (internal)
src/Wolverine.MongoDB.Tests/        ← Integration tests (needs Wolverine source clone)
demo/                               ← Separate solution, references package from CI nupkg
.github/workflows/
  ci.yml                            ← Library tests + pack; demo tests against fresh nupkg
  publish.yml                       ← NuGet push on v* tag
  security.yml                      ← Trivy vulnerability scan
```

---

## How the Library Works

### Public API

Two public entry points:
- `opts.UseMongoDbPersistence(databaseName)`: one-line registration. It:
  1. Registers `MongoDbMessageStore` as `IMessageStore`
  2. Registers `IMongoDatabase` from the DI-provided `IMongoClient`
  3. Inserts `MongoDbPersistenceFrameProvider` into Wolverine's code-generation pipeline
- `MongoDbUnitOfWork`: session-bound write helper. Handlers accept it as a parameter; the
  generated frame constructs it from the open `IClientSessionHandle`. Every write through
  `MongoDbUnitOfWork.Collection<T>()` automatically participates in the transaction.

### Core Implementation (partial class `MongoDbMessageStore`)

| File | Implements |
|------|-----------|
| `MongoDbMessageStore.cs` | `IMessageStore` root, collection references, durability-mode guard |
| `MongoDbMessageStore.Inbox.cs` | `IMessageInbox` — store/mark/recover incoming envelopes |
| `MongoDbMessageStore.Outbox.cs` | `IMessageOutbox` — persist/relay/mark outgoing envelopes |
| `MongoDbMessageStore.DeadLetters.cs` | `IDeadLetters` — failed messages, replay |
| `MongoDbMessageStore.Admin.cs` | `IMessageStoreAdmin` — collection/index creation, rebuild |
| `MongoDbMessageStore.NodeAgents.cs` | `IAgentFamily` — node registry, agent assignments |
| `MongoDbMessageStore.Locking.cs` | Leader election via findAndModify lock document + TTL |
| `MongoDbMessageStore.ScheduledMessages.cs` | Scheduled message polling with atomic claim |
| `MongoDbMessageStore.Durability.cs` | `MongoDbDurabilityAgent` integration |

### Transaction Integration

| File | Role |
|------|------|
| `MongoDbEnvelopeTransaction.cs` | `IEnvelopeTransaction` — opens session, commits outbox atomically |
| `MongoDbPersistenceFrameProvider.cs` | Code-gen: detects MongoDB types, injects transactional frame |
| `TransactionalFrame.cs` | Generated code frame: `StartSession → StartTransaction → handler → Commit` |

### MongoDB Collections

| Collection | Purpose |
|------------|---------|
| `wolverine_incoming_envelopes` | Inbox (idempotency, durable queues) |
| `wolverine_outgoing_envelopes` | Outbox (pending broker delivery) |
| `wolverine_dead_letters` | Failed messages + exception info |
| `wolverine_nodes` | Node registry (heartbeat, capabilities) |
| `wolverine_node_assignments` | Agent-to-node mapping |

### Key Design Decisions

- **Hot path uses `findAndModify`** (single-doc atomic ops), not multi-doc transactions. Transactions only for handler atomicity (domain write + outbox in one commit).
- **Inbox idempotency:** unique `_id` index; duplicate insert → `DuplicateKeyException` → treated as already-processed.
- **Scheduled messages:** `FindOneAndUpdate` with `Status == Scheduled && ExecutionTime <= now` for atomic claim.
- **Node coordination:** lock document with TTL expiry via `findAndModify` (approximates PostgreSQL advisory locks). Least mature subsystem.
- **`LoadOutgoingAsync` is owner-scoped and batch-limited:** only envelopes with `OwnerId == 0` (globally-owned / unclaimed) are returned, capped at `Durability.RecoveryBatchSize`. Envelopes owned by a live node are in-flight and must never be handed to recovery.
- **Handled markers expire via `KeepUntil` TTL:** `IncomingMessage` maps `envelope.KeepUntil` into the document. The TTL index on `keepUntil` automatically removes handled markers. Previously, `KeepUntil` was dropped, causing unbounded inbox growth.
- **Dead-letter TTL is opt-in:** `ExpirationTime` is only written when `Durability.DeadLetterQueueExpirationEnabled == true`. With the default `false`, dead letters are retained forever (matching RDBMS providers). The TTL index ignores documents without the `expirationTime` field.
- **Write concerns pinned on the store:** the `MongoDbMessageStore` constructor wraps its database handle with `WriteConcern.WMajority.With(journal: true)` and `ReadConcern.Majority`. This is independent of the consumer's `MongoClient` configuration. The app-facing `IMongoDatabase` registered by `UseMongoDbPersistence` is **not** pinned — domain write concerns belong to the application.
- **Durability mode guard:** `Initialize`, `StartScheduledJobs`, and `BuildAgent` all call `AssertSupportedDurabilityMode`, which throws `InvalidOperationException` if `DurabilityMode.Balanced` is set. `DurabilityMode.Solo` is required; multi-node support is tracked in `FOLLOWUPS.md`.
- **`MongoDbUnitOfWork` is the recommended handler write surface:** it accepts a handler parameter and threads the active `IClientSessionHandle` into every write, making it impossible to forget the session. The raw `IClientSessionHandle` pattern remains valid for repository-based handlers.
- **Transaction frame triggers broadly:** `CanApply` returns `true` for handlers whose dependency tree (or method parameters) includes `IMongoDatabase`, `IMongoClient`, `IMongoCollection<T>`, `IClientSessionHandle`, or `MongoDbUnitOfWork`.
- **DateTimeOffset stored as UTC BSON Date:** every `DateTimeOffset`/`DateTimeOffset?` property on document types is annotated with `[BsonRepresentation(BsonType.DateTime)]`. No process-global serializer is registered — the library does not mutate the host app's BSON registry.
- **Dead-letter replay is idempotent:** if a previous replay pass crashed after re-inserting the envelope but before deleting the DLQ document, the next pass catches `DuplicateIncomingEnvelopeException` and continues. Body-less poison letters are unflagged (not retried every tick) and left queryable.

---

## Build & Test

```bash
# Build library only (no Wolverine source clone needed)
dotnet build src/Wolverine.MongoDB/Wolverine.MongoDB.csproj

# Build + test (compliance tests project-ref the Wolverine submodule at external/wolverine)
# Initialise it first: git submodule update --init   (or clone with --recursive)
# Override the path if needed: WOLVERINE_SOURCE env var or -p:WolverineSourcePath=...
dotnet test src/Wolverine.MongoDB.Tests/

# Pack for NuGet (declares WolverineFx package dependency)
dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false
```

Tests use Testcontainers (auto-starts MongoDB replica set). Docker Desktop required.

**CI:** the `library` job checks out with `submodules: recursive` (the Wolverine source is the
`external/wolverine` submodule, pinned to the `V6.2.2` commit — keep the pin in sync with
`WolverineFx` in `Directory.Packages.props`), runs the full compliance suite, then packs the
library at version `0.0.0-ci`. The `demo` job downloads that nupkg and runs the end-to-end
integration tests against it, so every PR exercises the freshly built package.

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
- `DurabilityMode.Solo` is required. The host throws at startup on `DurabilityMode.Balanced`.
