# Wolverine.MongoDB

## Overview

Native MongoDB persistence provider for Wolverine's transactional inbox/outbox. Implements `IMessageStore` directly against the MongoDB .NET driver. No EF Core dependency.

**Package:** `Wolverine.MongoDB` (NuGet, currently `0.1.0-beta.1`)  
**Targets:** .NET 9, .NET 10  
**Dependencies:** `WolverineFx 6.x`, `MongoDB.Driver 3.x`  
**Constraint:** MongoDB must run as a replica set (transactions require it).

---

## Repository Layout

```
src/Wolverine.MongoDB/              ← Library (NuGet package)
  WolverineMongoDbExtensions.cs     ← Public API: UseMongoDbPersistence()
  Internals/                        ← All implementation (internal)
src/Wolverine.MongoDB.Tests/        ← Integration tests (needs Wolverine source clone)
demo/                               ← Separate solution, references package from nuget.org
.github/workflows/
  ci.yml                            ← Build lib + demo, run demo tests
  publish.yml                       ← NuGet push on v* tag
  security.yml                      ← Trivy vulnerability scan
```

---

## How the Library Works

### Public API

One extension method: `opts.UseMongoDbPersistence(databaseName)`. It:
1. Registers `MongoDbMessageStore` as `IMessageStore`
2. Registers `IMongoDatabase` from the DI-provided `IMongoClient`
3. Inserts `MongoDbPersistenceFrameProvider` into Wolverine's code-generation pipeline

### Core Implementation (partial class `MongoDbMessageStore`)

| File | Implements |
|------|-----------|
| `MongoDbMessageStore.cs` | `IMessageStore` root, collection references |
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
| `MongoDbPersistenceFrameProvider.cs` | Code-gen: detects `IMongoDatabase` usage, injects transactional frame |
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

---

## Build & Test

```bash
# Build library only (no Wolverine source clone needed)
dotnet build src/Wolverine.MongoDB/Wolverine.MongoDB.csproj

# Build + test (requires Wolverine source clone for compliance tests)
# Clone location: C:\source\external\wolverine or WOLVERINE_SOURCE env var
dotnet test src/Wolverine.MongoDB.Tests/

# Pack for NuGet (declares WolverineFx package dependency)
dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false
```

Tests use Testcontainers (auto-starts MongoDB replica set). Docker Desktop required.

---

## Versioning & Release

- Version in `Directory.Build.props` (kept in sync via auto-bump PR after publish)
- Major version tracks Wolverine: `0.1.x` ↔ `WolverineFx 6.x`
- The publish workflow extracts version from the git tag (`-p:Version`), so the tag is the source of truth

**Release flow:**
1. Merge your branch into main (via PR or direct push)
2. Tag main from any branch:
   ```bash
   git tag v0.1.0-beta.3 origin/main
   git push origin v0.1.0-beta.3
   ```
3. The `publish.yml` workflow triggers, packs with the tag version, and pushes to NuGet
4. A PR is auto-created to bump `Directory.Build.props` to match

⚠️ **Always tag a commit on main** — the workflow runs the `publish.yml` from the tagged commit, so that commit must contain the latest workflow file.

---

## Important Constraints

- `IMongoDatabase` does NOT auto-enlist in the transaction. Handlers must accept `IClientSessionHandle` and pass it to all writes for atomicity.
- The test project depends on `WolverineFx.ComplianceTests` which is not on NuGet — requires local Wolverine source clone. CI skips library tests for now.
- `Wolverine.MongoDB.Tests` uses `UseWolverineSource` MSBuild property to switch between project-ref (local dev) and package-ref (CI/pack).

