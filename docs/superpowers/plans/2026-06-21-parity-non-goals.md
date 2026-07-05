# Parity Capabilities — Non-Goals + Rationale

> **Task:** T3.1 of `2026-06-21-persistence-suite-completion.md`
> **Status:** Complete — documentation only, no code change.
> **Branch:** `docs/parity-non-goals`
> **Precedes:** builds on D3's discovery, `docs/superpowers/plans/2026-06-21-parity-decisions-discovery.md`.

---

## Purpose

Wolverine defines four persistence capabilities that RDBMS/Marten providers implement but the two closest document-store analogues — Cosmos and RavenDb — both defer. This document makes Wolverine.MongoDB's decision to also defer all four **explicit and durable**, so the suite's scope boundary is documented and upstream-defensible rather than an implicit gap.

Per D3, the recommendation for every capability is **DEFER (document-as-non-goal)**. This task confirmed each capability is still at its documented default in code (no behavioral change — see Verification below) and recorded the decision in three places:

1. `CLAUDE.md` — "Parity Capabilities — Non-Goals" under Key Design Decisions (the durable reference for contributors).
2. This document — the full contract / Cosmos / RavenDb / Marten-RDBMS / decision / rationale / workaround table.
3. `FOLLOWUPS.md` — one entry per capability, cross-linking here.

---

## Verification (no code change needed)

Confirmed directly against `main` before writing this document — all four capabilities are already at the documented default:

| Capability | Current Mongo state | File:line |
|---|---|---|
| Multi-tenancy | `TenantIds` = `new()` (always empty); no `ITenantedMessageSource` | `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs:56` |
| Durable listeners | `Listeners` = `NullListenerStore.Instance` | `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs:64` |
| Query-spec frames | `TryBuildFetchSpecificationFrame` not overridden (uses `IPersistenceFrameProvider`'s default `false`) | `src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs` (no override present) |
| Soft-delete | `DetermineFrameToNullOutMaybeSoftDeleted` returns `[]` | `src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs:158-161` |

No divergence from D3's findings was surfaced. This task is documentation only.

---

## Per-capability decision table

| Capability | Contract | Cosmos | RavenDb | Marten/RDBMS | Decision | Rationale | App-level workaround |
|---|---|---|---|---|---|---|---|
| **Multi-tenancy** | `IMessageStore.TenantIds: List<string>`; `ITenantedMessageSource : ITenantedSource<IMessageStore>` | `TenantIds = new()`, no `ITenantedMessageSource` | `TenantIds = new()`, no `ITenantedMessageSource` | Real per-tenant databases; implements `ITenantedMessageSource` | **Non-goal.** Keep `TenantIds` empty. | Real multi-tenancy is connection-string-based (one `IMessageStore`/database per tenant) — a significant architectural investment. MongoDB's document model provides no cross-database fanout primitive that makes this cheaper than the RDBMS approach, and neither document-store analogue implements it. | Route on a tenant-ID field in the message payload (convention-based), or register a separate Wolverine host with its own `IMongoDatabase` per tenant. |
| **Durable listeners** | `IListenerStore Listeners` property; `NullListenerStore.Instance` no-op default | `NullListenerStore.Instance`, "follow-up" comment | `NullListenerStore.Instance`, "follow-up" comment | `RdbmsListenerStore`, gated on `EnableDynamicListeners && Role==Main` | **Non-goal for now** — keep `NullListenerStore`. Optional follow-up shape recorded (see below). | Matches the de-facto state of both document-store analogues. Only relevant when `EnableDynamicListeners` is explicitly opted into (not the default); no known consumer need today. The cheapest of the four to implement later if that changes. | None needed while `EnableDynamicListeners` stays off (the default). |
| **Query-spec frames** | `TryBuildFetchSpecificationFrame` — default `false` | Default (`false`) | Default (`false`) | Marten and EF Core override it (compiled query objects) | **Non-goal.** Leave at the `IPersistenceFrameProvider` default. | `[FromQuerySpecification]` targets provider-private compile-time query types (`ICompiledQuery<,>`, EF's `IQueryPlan<,>`) — a Marten/EF Core-specific concept with no MongoDB equivalent. Cosmos, RavenDb, and Polecat all leave this at the default too. | Query directly via `IMongoCollection<T>`/LINQ inside the handler; no framework hook needed. |
| **Soft-delete** | `DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) : Frame[]` | Returns `[]` | Returns `[]` | Marten returns `[SetVariableToNullIfSoftDeletedFrame]`; EF Core/Polecat return `[]` | **Non-goal.** Keep returning `[]`. | Implementing this would mean prescribing an `IsDeleted`-style field convention across every entity type and building a Marten-metadata-equivalent frame — a framework-level convention MongoDB does not natively provide. Every non-Marten provider returns `[]`. | `[Entity(MaybeSoftDeleted = false)]` plus a manual null-check in the handler, or an explicit `is_deleted` filter on the load query. |

---

## Durable listeners — optional follow-up shape (not implemented)

Recorded here so a future task does not have to re-derive the design from scratch, per D3's nuance that durable listeners are the cheapest of the four to implement if demand ever appears:

- A `wolverine_listeners` MongoDB collection, one document per registered listener: `{ uri: string }`.
- A unique index on `uri` for idempotent registration.
- `RegisterListenerAsync` → `ReplaceOneAsync(filter: uri, replacement: { uri }, IsUpsert: true)`.
- `RemoveListenerAsync` → `DeleteOneAsync(filter: uri)`.
- `AllListenersAsync` → project `uri` across all documents.
- Construction gated exactly like RDBMS's `RdbmsListenerStore`: build `MongoListenerStore` only when `EnableDynamicListeners && Role == Main`; otherwise keep returning `NullListenerStore.Instance`.

This is **not being implemented now** — it is a documented, ready-to-pick-up follow-up (see `FOLLOWUPS.md`), not a scheduled task.

---

## Cross-references

- Discovery: `docs/superpowers/plans/2026-06-21-parity-decisions-discovery.md` (D3) — the full per-capability fact-finding this document summarizes into decisions.
- Plan: `docs/superpowers/plans/2026-06-21-persistence-suite-completion.md` — Task T3.1.
- `CLAUDE.md` — "Parity Capabilities — Non-Goals" (Key Design Decisions).
- `FOLLOWUPS.md` — one entry per capability, each linking back here.
