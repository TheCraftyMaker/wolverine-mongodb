# Tier 3 — Parity Capabilities: Implement-vs-Defer Discovery

> **Task:** D3 of `2026-06-21-persistence-suite-completion.md`  
> **Status:** Complete — all four recommendations are DEFER.  
> **Branch:** `docs/parity-decisions-discovery`

---

## Overview

This document records the confirmed facts for each of the four Tier-3 parity capabilities and issues a firm **implement-vs-defer recommendation** for each. All source locations are confirmed against the pinned `external/wolverine` submodule (V6.9.0) and the local `src/Wolverine.MongoDB/` source.

The default recommendation is **DEFER** for all four, consistent with the two closest document-store analogues (Cosmos and RavenDb) which also defer all four.

---

## Capability 1 — Multi-tenancy

### Contract

```
IMessageStore.TenantIds { get; }   // List<string>
                                   // IMessageStore.cs:80
ITenantedMessageSource : ITenantedSource<IMessageStore>
                                   // IMessageStore.cs:174-176 — Task RefreshLiteAsync()
```

`TenantIds` is a plain `List<string>` property on every `IMessageStore`. `ITenantedMessageSource` is a separate interface layered on top of `IMessageStore`; only providers that implement multi-tenancy across connection strings implement it (RDBMS, Marten). The in-band path (`MultiTenantedMessageStore`) routes messages to per-tenant `IMessageStore` instances, keyed by the tenant IDs registered in this list.

### What each provider does

| Provider | `TenantIds` | Implements `ITenantedMessageSource` |
|---|---|---|
| **RDBMS (SQL Server / Postgres)** | Populated per tenant store | Yes — `SqlServerTenantedMessageStore.cs:15` (etc.) |
| **Marten** | Populated per tenant store | Yes (Marten multi-tenancy is connection-string-based) |
| **Cosmos** | `new()` — always empty list | No |
| **RavenDb** | `new()` — always empty list | No |
| **Current Mongo** | `new()` — always empty list (`MongoDbMessageStore.cs:56`) | No |

**Cosmos** (`CosmosDbMessageStore.cs:42`): `public List<string> TenantIds { get; } = new();`  
**RavenDb** (`RavenDbMessageStore.cs:35`): `public List<string> TenantIds { get; } = new();`  
**Mongo** (`MongoDbMessageStore.cs:56`): `public List<string> TenantIds { get; } = new();` ← already matches

### Recommendation: **DEFER (document-as-non-goal)**

**Rationale:** Real Wolverine multi-tenancy is connection-string-based — each tenant gets its own MongoDB database, registered as a separate `IMessageStore` instance. This is a significant architectural investment (per-tenant `MongoClient`, a `ITenantedMessageSource` implementation, routing in `UseMongoDbPersistence`). Cosmos and RavenDb both leave `TenantIds` empty and do not implement `ITenantedMessageSource`. MongoDB's document model does not provide a native cross-database fanout primitive that would make this easier than RDBMS.

**App-level workaround:** Applications that need logical multi-tenancy in MongoDB can route to a tenant-ID field in the message payload (convention-based), or register separate Wolverine instances with separate MongoDB databases per tenant. Document this pattern.

**No code change needed.** `MongoDbMessageStore.TenantIds` is already `new()` — the empty list is the correct deferred state.

---

## Capability 2 — Durable Listeners

### Contract

```csharp
// IListenerStore.cs
public interface IListenerStore
{
    Task RegisterListenerAsync(Uri uri, CancellationToken cancellationToken = default);
    Task RemoveListenerAsync(Uri uri, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Uri>> AllListenersAsync(CancellationToken cancellationToken = default);
}

// NullListenerStore — the no-op default
public sealed class NullListenerStore : IListenerStore
{
    public static NullListenerStore Instance { get; } = new();
    // All methods return Task.CompletedTask / empty list
}

// IMessageStore.Listeners:
IListenerStore Listeners { get; }   // IMessageStore.cs:110
```

`IMessageStore.Listeners` must return `NullListenerStore.Instance` when `DurabilitySettings.EnableDynamicListeners` is `false` (the default) or when the provider has no durable backing. `DynamicListenerAgentFamily` activates registered URIs as runtime listeners via their transport; the store itself is transport-agnostic — each entry is a plain `Uri`.

### What each provider does

| Provider | `Listeners` implementation |
|---|---|
| **RDBMS (SQL Server / Postgres)** | `RdbmsListenerStore` — a `wolverine_listeners` table with unique URI primary key. Gated: only built when `EnableDynamicListeners && Role==Main` (`MessageDatabase.cs:91-94`); defaults to `NullListenerStore.Instance` otherwise (`MessageDatabase.cs:162`). |
| **Marten** | Uses RDBMS path (Marten wraps a Postgres `MessageDatabase`). |
| **Cosmos** | `NullListenerStore.Instance` with "follow-up" comment (`CosmosDbMessageStore.cs:71-74`). |
| **RavenDb** | `NullListenerStore.Instance` with "follow-up" comment (`RavenDbMessageStore.cs:65-68`). |
| **Current Mongo** | `NullListenerStore.Instance` (`MongoDbMessageStore.cs:64`). ← already matches |

**Cosmos** (`CosmosDbMessageStore.cs:74`):  
```csharp
// Default no-op listener store. CosmosDB-backed listener registry is a
// follow-up implementation; stays a no-op while EnableDynamicListeners
// is false (default).
public IListenerStore Listeners { get; protected set; } = NullListenerStore.Instance;
```

**RavenDb** (`RavenDbMessageStore.cs:68`):  
```csharp
// Default no-op listener store; real RavenDb-backed listener registry is
// a follow-up implementation. Stays a no-op while EnableDynamicListeners
// is false (default).
public IListenerStore Listeners { get; protected set; } = NullListenerStore.Instance;
```

**Mongo** (`MongoDbMessageStore.cs:64`):  
```csharp
public IListenerStore Listeners { get; protected set; } = NullListenerStore.Instance;
```

### Recommendation: **DEFER — keep `NullListenerStore`; document; note cheap optional follow-up shape**

**Rationale:** Cosmos and RavenDb both stay at `NullListenerStore.Instance` with explicit "follow-up" comments. The current MongoDB state is already equivalent. Durable-listener support requires that `EnableDynamicListeners` be opted into by the application (not the default). There is no known Wolverine.MongoDB consumer asking for this capability today.

**No code change needed.** `MongoDbMessageStore.Listeners` is already `NullListenerStore.Instance`.

**Optional follow-up shape (if demand arises):** A `wolverine_listeners` MongoDB collection with `{ uri: string }` documents, a unique index on `uri`, and `ReplaceOneAsync(IsUpsert=true)` for idempotent registration. The gating pattern mirrors RDBMS: build `MongoListenerStore` only when `EnableDynamicListeners && Role==Main`; otherwise leave `NullListenerStore.Instance`. Add a `FOLLOWUPS.md` entry recording this shape so it is not re-designed from scratch.

---

## Capability 3 — Query-spec frames (`TryBuildFetchSpecificationFrame`)

### Contract

```csharp
// IPersistenceFrameProvider.cs:73-82 — DEFAULT implementation (returns false)
bool TryBuildFetchSpecificationFrame(
    Variable specVariable,
    IServiceContainer container,
    [NotNullWhen(true)] out Frame? frame,
    [NotNullWhen(true)] out Variable? result)
{
    frame = null;
    result = null;
    return false;
}
```

Called from `FromQuerySpecificationAttribute.Modify` (`:127-134`). When a provider returns `true`, it emits a codegen `Frame` that executes a compile-time query specification object (e.g. a Marten `ICompiledQuery<,>` or EF Core `IQueryPlan<TDbContext,TResult>`) and yields its materialized result as a new variable for downstream frames.

### What each provider does

| Provider | `TryBuildFetchSpecificationFrame` |
|---|---|
| **Marten** | Overrides — handles `ICompiledQuery<,>`, `IBatchQueryPlan<>`, `IQueryPlan<>` (`MartenPersistenceFrameProvider.cs:201-241`). |
| **EF Core** | Overrides — handles EF Core `IQueryPlan<TDbContext,TResult>` (`:66-102`). |
| **Cosmos** | Uses the default (returns `false`). |
| **RavenDb** | Uses the default (returns `false`). |
| **Polecat** | Uses the default (returns `false`). |
| **Current Mongo** | Uses the default (returns `false`) — `TryBuildFetchSpecificationFrame` is not overridden in `MongoDbPersistenceFrameProvider.cs`. |

### Recommendation: **DEFER (document-as-non-goal)**

**Rationale:** Query-spec frames exist for compile-time, strongly-typed query objects — a Marten-specific (and EF Core-specific) concept built around provider-private session types. MongoDB has no equivalent compile-time query specification protocol. The `[FromQuerySpecification]` attribute is genuinely Marten/EF-only. Cosmos, RavenDb, and Polecat all leave this at the default `false`. There is no use case for MongoDB.

**No code change needed.** The default `false` return in `IPersistenceFrameProvider` is already the correct MongoDB state.

---

## Capability 4 — Soft-delete (`DetermineFrameToNullOutMaybeSoftDeleted`)

### Contract

```csharp
// IPersistenceFrameProvider.cs:52
Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity);
```

Called from `EntityAttribute.cs:170-173` **only when** `[Entity(MaybeSoftDeleted = false)]` is not set — i.e., by default for every `[Entity]`-decorated parameter. When a provider returns a non-empty array, those frames are appended to the handler middleware; they inspect the loaded entity and null it out if it is "soft-deleted" (so core can then 404/skip the handler). Only Marten implements this; all other providers return `[]`.

### What each provider does

| Provider | `DetermineFrameToNullOutMaybeSoftDeleted` |
|---|---|
| **Marten** | Returns `[new SetVariableToNullIfSoftDeletedFrame(entity)]` (`MartenPersistenceFrameProvider.cs:196-199`). The frame calls `IDocumentSession.MetadataForAsync(entity)` and sets `entity = null` if `metadata.Deleted == true`. |
| **EF Core** | Returns `[]` (no soft-delete convention). |
| **Polecat** | Returns `[]`. |
| **Cosmos** | Returns `[]` (`CosmosDbPersistenceFrameProvider.cs:17`). |
| **RavenDb** | Returns `[]` (`RavenDbPersistenceFrameProvider.cs:18`). |
| **Current Mongo** | Returns `[]` (`MongoDbPersistenceFrameProvider.cs:135-138`). ← already matches |

**Cosmos** (`CosmosDbPersistenceFrameProvider.cs:17`):  
```csharp
public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];
```

**RavenDb** (`RavenDbPersistenceFrameProvider.cs:18`):  
```csharp
public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];
```

**Mongo** (`MongoDbPersistenceFrameProvider.cs:135-138`):  
```csharp
public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity)
{
    return [];
}
```

### Recommendation: **DEFER (document-as-non-goal)**

**Rationale:** Implementing soft-delete requires prescribing a field convention (e.g. `IsDeleted: bool`) across all entity types and building a Marten-metadata-equivalent codegen frame that reads that field post-load. This is not a capability MongoDB provides natively; it would be a framework-level convention imposed on the application. Cosmos, RavenDb, EF Core, and Polecat all return `[]`. The app-level alternative (`[Entity(MaybeSoftDeleted = false)]` + a manual null-check in the handler, or an explicit `is_deleted` filter on the load query) is straightforward and gives applications full control over the convention.

**No code change needed.** `DetermineFrameToNullOutMaybeSoftDeleted` already returns `[]`.

---

## Summary Table

| Capability | Contract | Cosmos | RavenDb | Marten/RDBMS | Current Mongo | Recommendation |
|---|---|---|---|---|---|---|
| **Multi-tenancy** | `IMessageStore.TenantIds: List<string>`; `ITenantedMessageSource` | `TenantIds = new()`, no `ITenantedMessageSource` | `TenantIds = new()`, no `ITenantedMessageSource` | Real per-tenant databases, implements `ITenantedMessageSource` | `TenantIds = new()` (`:56`), no `ITenantedMessageSource` | **DEFER** — document-as-non-goal; app-level tenant-field routing is the path |
| **Durable listeners** | `IListenerStore Listeners` property; `NullListenerStore.Instance` as no-op | `NullListenerStore.Instance` with "follow-up" comment (`:74`) | `NullListenerStore.Instance` with "follow-up" comment (`:68`) | `RdbmsListenerStore` gated on `EnableDynamicListeners && Role==Main`; otherwise `NullListenerStore.Instance` | `NullListenerStore.Instance` (`:64`) | **DEFER** — keep `NullListenerStore`; note cheap optional follow-up shape in `FOLLOWUPS.md` |
| **Query-spec frames** | `TryBuildFetchSpecificationFrame` — default returns `false` | Default (returns `false`) | Default (returns `false`) | Marten overrides (compiled queries); EF Core overrides | Default (returns `false`, not overridden) | **DEFER** — document-as-non-goal; Marten/EF-only concept |
| **Soft-delete** | `DetermineFrameToNullOutMaybeSoftDeleted` — returns `Frame[]` | Returns `[]` (`:17`) | Returns `[]` (`:18`) | Marten: `[SetVariableToNullIfSoftDeletedFrame]`; others: `[]` | Returns `[]` (`:135-138`) | **DEFER** — document-as-non-goal; app-level `is_deleted` pattern is the path |

---

## What T3.1 needs to do

T3.1 (`docs/parity-non-goals`) is the follow-on documentation task that:

1. **Confirms no code change is needed** — all four capabilities are already at their correctly-deferred defaults in the current `MongoDbMessageStore` and `MongoDbPersistenceFrameProvider`.
2. Adds per-capability entries to `FOLLOWUPS.md`: multi-tenancy (hard non-goal), durable listeners (cheap optional follow-up — record the `wolverine_listeners` collection + unique-URI shape), query-spec frames (hard non-goal), soft-delete (hard non-goal).
3. Adds a "Parity Capabilities — Non-Goals" section to `CLAUDE.md` (Key Design Decisions) with a rationale paragraph each.
4. Optionally produces a brief `docs/superpowers/plans/2026-06-21-parity-non-goals.md` cross-linking to this discovery doc.

The only nuance is the durable-listeners follow-up: T3.1 should **not implement** it, but should record the implementation shape precisely enough that a future task can pick it up without re-doing the design.
