# Wolverine.MongoDB — Implementation Handover

## Context

This project is migrating from **MassTransit to Wolverine** as the message bus. The application uses **MongoDB as its primary datastore** and requires a **transactional outbox** for reliable message delivery.

Wolverine has first-class support for transactional inbox/outbox with PostgreSQL and SQL Server. It has no MongoDB provider. The goal of this work is to implement one.

---

## Why No Existing Solution Works

**Wolverine + EF Core + MongoDB EF provider** does not work because:
- Wolverine's EF Core outbox integration calls `modelBuilder.MapWolverineEnvelopeStorage()` which maps envelope tables as relational EF entities — incompatible with MongoDB collections
- The fallback path uses `DbContext.Database.GetDbConnection()` to get an `IDbConnection` — the MongoDB EF provider does not expose a relational ADO.NET connection
- Wolverine's envelope schema (`wolverine_incoming_envelopes`, `wolverine_outgoing_envelopes`, etc.) is SQL DDL managed via Weasel — no MongoDB equivalent exists

**Conclusion:** implement a native MongoDB `IMessageStore` using the MongoDB .NET driver directly, without EF Core.

---

## What Needs to Be Built

Wolverine's durability infrastructure is abstracted behind `IMessageStore` (in `Wolverine.Persistence.Durability`), which composes four sub-interfaces:

```csharp
public interface IMessageStore
{
    IMessageInbox Inbox { get; }
    IMessageOutbox Outbox { get; }
    IDeadLetters DeadLetters { get; }
    IMessageStoreAdmin Admin { get; }
    // + IAgentFamily for node coordination
}
```

### `IMessageInbox`
Manages incoming message persistence (for durable local queues and inbox idempotency):
- Store incoming envelope before processing
- Mark as handled / delete after successful processing
- Move to dead letter on terminal failure
- Load scheduled messages due for execution
- Recover orphaned envelopes (owned by crashed nodes)

### `IMessageOutbox`
Manages outgoing messages (the outbox):
- Persist outgoing envelopes within a handler transaction
- Mark as sent / delete after successful delivery to broker
- Load undelivered outgoing messages for relay
- Schedule messages for future delivery

### `IDeadLetters`
- Store failed envelopes with exception details
- Support replay (move back to incoming)
- Paged query for dead letter management UI

### `IMessageStoreAdmin`
- Schema setup (create collections + indexes)
- Rebuild / clear (for testing)
- Stats / diagnostics
- Health check support

### Node Coordination (`IAgentFamily`)
Wolverine runs a `DurabilityAgent` that requires persistent node registry to support:
- Multi-node leader election
- Agent assignment across cluster nodes
- Heartbeats / health detection
- Orphan recovery (reassign envelopes from dead nodes)

This is the hardest part. Wolverine uses advisory locks in PostgreSQL (`pg_try_advisory_lock`) and SQL Server application locks for this. MongoDB has no advisory lock primitive — use `findAndModify` with a lock document and TTL-based expiry as an approximation.

---

## MongoDB Collections Schema

Mirror the MassTransit MongoDB outbox design, which is proven in production. Three core collections plus node coordination:

### `wolverine_incoming_envelopes`
```
{
  _id: UUID,               // envelope MessageId
  status: string,          // "Incoming" | "Scheduled" | "Handled"
  owner_id: int,           // node number that owns this (0 = any)
  execution_time: DateTime?, // for scheduled messages
  received_at: DateTime,
  attempts: int,
  body: BinData,
  message_type: string,
  content_type: string,
  source: string,
  correlation_id: string,
  reply_uri: string,
  headers: object
}
```
Indexes: `(status, owner_id)`, `(execution_time)` for scheduled polling

### `wolverine_outgoing_envelopes`
```
{
  _id: UUID,               // envelope MessageId
  owner_id: int,
  destination: string,     // URI
  deliver_by: DateTime?,
  sent_at: DateTime,
  attempts: int,
  body: BinData,
  message_type: string,
  content_type: string,
  headers: object
}
```
Indexes: `(owner_id)`, `(deliver_by)`

### `wolverine_dead_letters`
```
{
  _id: UUID,
  message_id: UUID,
  received_at: DateTime,
  execution_time: DateTime?,
  body: BinData,
  message_type: string,
  source: string,
  exception_text: string,
  exception_type: string,
  exception_source: string,
  explanation: string,
  replayable: bool
}
```

### `wolverine_nodes`
```
{
  _id: UUID,               // node id
  node_number: int,        // assigned slot number
  description: string,
  uri: string,
  started: DateTime,
  last_heartbeat: DateTime,
  capabilities: [string]
}
```

### `wolverine_node_assignments`
```
{
  _id: string,             // agent uri
  node_id: UUID,
  started: DateTime
}
```

---

## Concurrency Design (Key Design Decision)

Do **not** use MongoDB multi-document transactions as the primary concurrency mechanism. They require a replica set, add latency, and have known contention issues under load (as observed in MassTransit production deployments).

Instead, follow the MassTransit MongoDB pattern:

**Use `findAndModify` (FindOneAndUpdate/FindOneAndReplace) for single-document atomic operations:**
- Claiming ownership of an envelope: update `owner_id` only if currently `0` (any node)
- Idempotency checks on inbox: unique index on `_id` — duplicate insert throws `MongoWriteException` with `DuplicateKey` code — catch and handle
- Lock token pattern for scheduled message polling: `findAndModify` with a `lock_token` field comparison

**Use sessions/transactions only where truly necessary:**
- When writing outgoing envelopes atomically with inbox state update inside a consumer handler
- Requires replica set (document this as a prerequisite)

**Orphan recovery:**
- Poll for envelopes where `owner_id = <node_number>` of a node whose `last_heartbeat` is stale
- Use `FindOneAndUpdate` with filter on `owner_id` to safely reassign

---

## Reference Implementation

Study the MassTransit MongoDB outbox source as the primary reference:
`https://github.com/MassTransit/MassTransit/tree/develop/src/Persistence/MassTransit.MongoDbIntegration/MongoDbIntegration/Outbox`

Key files to understand:
- `MongoDbOutboxConsumeContext` — how the inbox lock and outbox write work together
- `MongoDbOutboxExtensions` — delivery service background loop
- `OutboxMessage.cs` — document shape

The MassTransit schema maps almost directly:

| MassTransit | Wolverine equivalent |
|---|---|
| `InboxState` collection | `wolverine_incoming_envelopes` (subset of fields) |
| `OutboxMessage` collection | `wolverine_outgoing_envelopes` |
| `OutboxState` collection | Part of node coordination |
| `LockToken` on InboxState | `owner_id` + `findAndModify` on envelopes |

The main difference: Wolverine's node coordination is more involved than anything MassTransit has — MassTransit doesn't do distributed agent assignment.

---

## Prerequisites / Constraints

- MongoDB must run as a **replica set** (not standalone). Transactions are not available on standalone. This is already the case in any production MongoDB deployment (Atlas, etc.). Document this clearly.
- Target **MongoDB driver 2.x / 3.x** (`MongoDB.Driver` NuGet)
- No EF Core dependency in this provider
- The provider should register via a `WolverineOptions` extension method, e.g.:

```csharp
builder.UseWolverine(opts =>
{
    opts.PersistMessagesWithMongoDB(connectionString, databaseName);
    opts.Services.AddSingleton<IMongoClient>(new MongoClient(connectionString));
});
```

---

## Package Structure

Create a new project: `Wolverine.MongoDB`

Dependencies:
- `WolverineFx` (core)
- `MongoDB.Driver`
- No `WolverineFx.RDBMS` — that base class is SQL-only and not reusable

---

## Implementation Order

Suggested sequence to get to a working state incrementally:

1. **Schema setup** — `IMessageStoreAdmin.RebuildAsync()` creates collections and indexes. Verify with a simple integration test against a local replica set (Docker).

2. **Outbox write** — implement `IMessageOutbox.AppendAsync()`. This is the hot path. Write a test that persists an envelope and reads it back.

3. **Outbox relay** — implement `IMessageOutbox.LoadOutgoingAsync()` and `MarkAsSentAsync()`. This is what the `DurabilityAgent` polls.

4. **Inbox** — implement `IMessageInbox.StoreIncomingAsync()`, `MarkIncomingAsync()`, and `MoveToDeadLetterStorageAsync()`.

5. **Scheduled messages** — `IMessageInbox.LoadScheduledToExecuteAsync()` with `findAndModify` ownership claim.

6. **Node coordination** — `IAgentFamily` implementation. Heartbeat writes, stale node detection, orphan reassignment. This is the most complex piece — leave it for last and test it under simulated node failure.

7. **Transactional middleware hook** — custom Wolverine middleware that opens a MongoDB session, runs the handler, and commits atomically (domain write + outbox write in one transaction). This is how you replace `opts.Policies.AutoApplyTransactions()` for MongoDB.

---

## Known Risks

- **Contention on `InboxState` under load** — reported by MassTransit users on high-throughput scenarios. The `findAndModify` lock approach serializes access per document, which is fine for normal throughput but can bottleneck at high concurrency. Mitigate with appropriate write concern settings and index tuning.
- **Replica set requirement** — if the app currently runs against standalone MongoDB (e.g. local dev), this is a breaking change for the dev environment. Use a Docker Compose replica set setup for local development.
- **`IAgentFamily` interface stability** — Wolverine is actively developed and this internal interface has evolved between versions. Pin to a specific Wolverine version and explicitly test on upgrade.
- **No `NullMessageStore` fallback** — unlike the SQL providers, there's no existing test harness. Build integration tests against a real MongoDB replica set from the start; in-memory mocking will miss concurrency bugs.

---

## Open Source Setup

### Repository

- **GitHub repo name:** `wolverine-mongodb` (kebab-case is standard)
- **NuGet package ID:** `Wolverine.MongoDB`
- **License:** MIT — matches what JasperFx/Wolverine uses

### Versioning Strategy

Pin the major version to Wolverine's major version so compatibility is immediately obvious:

| `Wolverine.MongoDB` | `WolverineFx` |
|---|---|
| `5.x` | `5.x` |
| `6.x` | `6.x` |

Start at `0.1.0`. Do not publish `1.0.0` until node coordination is solid and tested. Being explicit about maturity builds more trust than overstating it.

### Required Repository Files

```
CLAUDE.md              ← this file
README.md
LICENSE                ← MIT
.gitignore
CHANGELOG.md           ← start now, even if empty
CONTRIBUTING.md        ← brief, low-barrier
```

### README Structure

The README should cover in order:
1. One-paragraph explanation of the problem and what this solves
2. Prerequisites (replica set, Wolverine version, .NET version)
3. Quick start code snippet — people decide in 30 seconds
4. Link to Wolverine docs for broader context
5. Known limitations (standalone MongoDB not supported, etc.)

### NuGet Package Configuration

```xml
<PropertyGroup>
  <PackageId>Wolverine.MongoDB</PackageId>
  <Version>0.1.0</Version>
  <Description>MongoDB message store for Wolverine transactional inbox/outbox</Description>
  <PackageTags>wolverine;mongodb;outbox;messaging;dotnet</PackageTags>
  <PackageProjectUrl>https://github.com/[you]/wolverine-mongodb</PackageProjectUrl>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <RepositoryUrl>https://github.com/[you]/wolverine-mongodb</RepositoryUrl>
  <PackageReadmeFile>README.md</PackageReadmeFile>
</PropertyGroup>
```

### GitHub Actions — NuGet Publish on Tag

```yaml
on:
  push:
    tags: ['v*']
```

Store the NuGet API key as a GitHub Actions secret (`NUGET_API_KEY`).

### GitHub Repository Topics

Add these topics on the GitHub repo settings page for discoverability:
`wolverine`, `mongodb`, `dotnet`, `outbox-pattern`, `csharp`, `transactional-outbox`

### Community Steps (in order)

1. **Open a discussion on the Wolverine GitHub repo** before writing much code — let Jeremy Miller know it exists. He may flag internal interfaces worth knowing about before you commit to an approach, and it prevents duplicate effort if someone else is building the same thing. Don't ask permission; just make him aware.

2. **Post in the Wolverine Discord** once you have something working (`discord.gg/WMxrvegf8H` — linked from the Wolverine docs). This is where potential users actually hang out.

3. **Submit to `awesome-dotnet`** or similar lists after the first stable release — low effort, some discoverability value.

---

### Library purpose

The consuming code will install a nuget package created for this library and register Mongo DB as the transactional outbox store using extension methods.

## Useful Links

- Wolverine `IMessageStore` interface: `src/Wolverine/Persistence/Durability/IMessageStore.cs`
- Wolverine `NullMessageStore` (reference for interface shape): `src/Wolverine/Persistence/Durability/NullMessageStore.cs`
- Wolverine PostgreSQL implementation (closest analog): `src/Persistence/Wolverine.Postgresql/PostgresqlMessageStore.cs`
- Wolverine RDBMS base (understand the shape, don't inherit): `src/Persistence/Wolverine.RDBMS/MessageDatabase.cs`
- MassTransit MongoDB outbox: `src/Persistence/MassTransit.MongoDbIntegration/MongoDbIntegration/Outbox/`
- Wolverine durability docs: https://wolverinefx.net/guide/durability/
- MongoDB .NET driver transactions: https://www.mongodb.com/docs/drivers/csharp/current/fundamentals/transactions/
