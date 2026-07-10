# Follow-ups

Known limitations and deferred work, captured so nothing is silently dropped.
Promote to GitHub issues before the first public release.

## Deferred from the post-review hardening pass

- **`INodeAgentPersistence.ClearAllAsync` scope — resolved (T4.4, 2026-07-05: documented as
  intentionally narrow).** It clears only the node and assignment collections
  (`MongoDbMessageStore.NodeAgents.cs`) — the operational surface `INodeAgentPersistence`
  owns. It does **not** clear the counter, locks, node-records, or agent-restriction
  collections. `IMessageStoreAdmin.RebuildAsync`/`ClearAllAsync`
  (`MongoDbMessageStore.Admin.cs`) is the full system reset: it clears all six system
  collections plus every `wolverine_saga_*` collection, and is what the test harness
  (`AppFixture.ClearAll()` → `RebuildAsync()`) actually calls. No code change; the
  boundary is now documented at the `ClearAllAsync` call site.

- **Node-number reuse — decision (T4.6, 2026-07-05: document/defer).** The counter
  (`wolverine_counters` / `node_number`, `MongoDbMessageStore.NodeAgents.cs:16-20`) is a pure
  monotonic increment; once a node deregisters, its slot is freed from `wolverine_nodes` but the
  counter value never decreases, so the next registering node gets `count + 1`, not the freed
  slot. **Decision: acceptable, deferred post-1.0.** Node numbers are short-lived coordination
  identifiers, not long-lived external keys, so unbounded (if slow) growth is not a correctness
  or resource concern at pre-1.0 scale. **If revisited:** track the lowest free slot (e.g. a
  sorted set of released numbers consulted before incrementing the counter) rather than
  redesigning the allocation scheme — no code change now.

- **Unqualified `IMongoDatabase` singleton registration — resolved (T4.3, 2026-07-05: documented as a consumer constraint).**
  `UseMongoDbPersistence` registers a single unkeyed `IMongoDatabase` (`WolverineMongoDbExtensions.cs:59-60`).
  An app that also registers its own unkeyed `IMongoDatabase` collides with it (DI resolves the last
  registration for a single-service request). **Decision: document, do NOT switch to keyed/dedicated
  registration.** Every code-generated frame resolves the database by type — `TransactionalFrame`
  (which also builds `MongoDbUnitOfWork`), the saga frames, and the entity frames all use
  `chain.FindVariable(typeof(IMongoDatabase))` — so a keyed/dedicated registration would force a keyed
  lookup through every frame's variable resolution plus `MongoDbUnitOfWork`: a high-blast-radius codegen
  change for a rare conflict (validated against both saga AND entity codegen — the reason T4.3 depends on
  D6 + T1.1). The constraint and the app-level workarounds (reuse Wolverine's handle; resolve a different
  database via `IMongoClient.GetDatabase(...)`; register the app's own under a keyed service / wrapper type)
  are documented in `README.md` ("The registered `IMongoDatabase`") and `CLAUDE.md` (Key Design Decisions).
  Revisit only if a real consumer needs a second unkeyed `IMongoDatabase` and a thin `WolverineMongoDatabase`
  wrapper the frames resolve proves contained.

- **`HasLeadershipLock` external-delete edge (residual, low priority).** The 75%
  lease margin means a node stops reporting leadership well before the lease
  expires, so a competing node can take over safely. The remaining residual: if
  the lock document is deleted externally (e.g. manual MongoDB intervention) while
  a node is inside the 75% window, that node still believes it holds leadership
  until its cached `ExpiresAt` crosses the margin. Self-corrects within one lease
  duration; acceptable for the current model.

- **Multinode leadership compliance — UN-GATED (2026-07-05, T4.5).** The upstream
  `leadership_election_compliance` facts were historically compile-gated behind `#if RUN_MULTINODE`:
  against WolverineFx 6.2.2, `leader_switchover_between_nodes` and the dependent
  `singular_agent_is_only_running_on_one` required the lowest-numbered surviving node to win a
  leadership-claim race that core decided by lock-arrival order, which our `w:majority+j:true` lock
  lost ~half the time; no `configureNode` knob fixed it (decision 2026-06-16: **declined** the
  "lowest live node wins" change — a test tie-breaker, not a production requirement, and it would
  degrade real failover). Full analysis + the declined code:
  `docs/superpowers/plans/2026-06-16-task6-multinode-compliance-findings.md`.

  - **Resolution (WolverineFx 6.9.0).** V6.9.0 reworked these facts — `leader_switchover_between_nodes`
    now uses a slow heartbeat plus an explicit `CheckAgentHealth` trigger (removing the
    lock-arrival-order race), and the new `take_over_leader_ship_if_leader_becomes_stale_with_racing_nodes`
    fact is built around the "any healthy node leads" model this provider already implements — so
    un-gating did **not** require the declined lowest-node election. Verified **5× consecutive green
    on BOTH net9.0 and net10.0** (10/10 runs of the full `Category=multinode` suite, 17/17 each
    including all 13 leadership facts) on 2026-07-05. The `#if RUN_MULTINODE` guard was removed; the
    suite's `[Trait("Category","multinode")]` routes it into CI's existing multinode step (no `ci.yml`
    change — that step already runs `dotnet test --filter "Category=multinode"`). Production confidence
    also continues to come from `multinode_end_to_end.cs` (exactly-once scheduling + dead-node rescue).
    One net9.0 run showed a single `fan_out_message_to_all_nodes` duration outlier (~16 min vs ~10 s,
    still green, error-free log) attributed to a local Docker/host scheduling stall — net10.0 ran the
    identical suite 5× at max 8 s/test, corroborating an environment transient, not a coordination defect.

- **Lease fencing token (epoch) — decision (T4.6, 2026-07-05: document/defer).** The leader lock
  document (`MongoDbMessageStore.Locking.cs`: `Id`, `NodeId`, `ExpiresAt`) has no fencing token
  (monotonically increasing epoch). A fencing token would let store writes carry the epoch and
  reject writes from a node holding a stale epoch, closing the residual where a paused node still
  believes it holds leadership for a brief window (see the `HasLeadershipLock` external-delete
  edge above). **Decision: not needed for store-only leader work; deferred as a future hardening
  option.** The 75%-lease margin already mitigates the stale-leadership window for internal
  coordination (the leader stops acting before a competitor can legitimately take over); a fencing
  token only earns its complexity once leader-scoped **external** side effects (writes to a
  system outside this store that must reject stale-epoch callers) become common. No code change.

- **`IListenerStore` still `NullListenerStore` — non-goal for now, cheap optional follow-up shape recorded.**
  `MongoDbMessageStore.Listeners` returns `NullListenerStore.Instance`, matching Cosmos/RavenDb's
  own "follow-up" state (T3.1, `docs/superpowers/plans/2026-06-21-parity-non-goals.md`). No
  consumer has asked for durable listener persistence. **If demand appears:** a `wolverine_listeners`
  collection with `{ uri: string }` documents, a unique index on `uri`, idempotent registration via
  `ReplaceOneAsync(IsUpsert=true)`, gated on `EnableDynamicListeners && Role==Main` (mirroring
  `RdbmsListenerStore`) — otherwise keep `NullListenerStore.Instance`.

- **Multi-tenancy — non-goal (documented).** `TenantIds` stays empty and `ITenantedMessageSource`
  is not implemented, matching Cosmos/RavenDb. See
  `docs/superpowers/plans/2026-06-21-parity-non-goals.md` for the rationale and the app-level
  tenant-field-routing workaround.

- **Query-spec frames (`TryBuildFetchSpecificationFrame`) — non-goal (documented).** Left at the
  `IPersistenceFrameProvider` default (`false`); Marten/EF Core-only concept with no MongoDB
  analogue. See `docs/superpowers/plans/2026-06-21-parity-non-goals.md`.

- **Soft-delete (`DetermineFrameToNullOutMaybeSoftDeleted`) — non-goal (documented).** Returns `[]`,
  matching every provider except Marten. See `docs/superpowers/plans/2026-06-21-parity-non-goals.md`
  for the app-level `is_deleted` workaround.

- **Demo app has no `MongoDbUnitOfWork` example — resolved (T4.1).** `RecordOrderAuditHandler`
  (`demo/src/OrderDemo.Application/Audit/RecordOrderAuditHandler.cs`) accepts `MongoDbUnitOfWork`
  directly and writes an `OrderAuditEntry` through `Collection<T>("order_audit_entries")` —
  no repository layer — wired to `POST /orders/{id}/audit`. `OrderAuditTests` proves the write
  rolls back with the surrounding transaction (`cmd.ForceFailure` hook, mirroring
  `saga_atomicity.cs`'s pattern). Sits alongside the existing repository +
  `IClientSessionHandle` example so consumers can compare both patterns side by side.

- **Demo saga cascade events have no consumer — resolved (T4.2).** `OrderFulfillmentSaga` returns
  `FulfillmentShippedEvent` / `FulfillmentCompletedEvent`; `FulfillmentStatusProjector`
  (`demo/src/OrderDemo.Infrastructure/Projectors/FulfillmentStatusProjector.cs`) now consumes both
  via a local durable queue (`Program.cs` + `OrdersFixture`) and maintains the
  `fulfillment_delivery_statuses` read model — delivery status/timestamps `OrderSummary` does not
  track. `SagaFlowTests.ShipAndConfirmOrder_ProjectsFulfillmentDeliveryStatus` exercises the full
  saga → outbox → consumer path end to end.

- **Index migration — decision (T4.6, 2026-07-05; re-affirmed post-1.0 2026-07-10: document/defer).**
  The hardening pass replaced single-field `executionTime` and outgoing `ownerId` indexes with
  compound alternatives (`MongoDbMessageStore.Admin.cs:18-64`). Deployments created before that
  pass keep the old single-field indexes in place — MongoDB does not drop superseded indexes
  automatically, and `EnsureIndexesAsync` only creates, never drops. **Decision: harmless,
  deferred.** The old indexes remain valid (just suboptimal for the new query patterns) and impose
  no correctness issue; a `RebuildAsync` (which recreates all indexes from scratch) is an
  acceptable manual remedy if desired. This was originally framed as a "before 1.0" checkpoint —
  the package shipped [1.0.0] on 2026-07-06 with no migration step added, so this is now a
  standing post-1.0 decision, not a lapsed pre-1.0 TODO. **If revisited:** add an explicit
  `Admin.MigrateAsync()` step that drops the specific superseded index names before
  `EnsureIndexesAsync` runs — no migration step exists today.

## Deferred from saga persistence (S6–S14)

- **`ISagaStoreDiagnostics` — implemented (T2.1, PR #131), with an upstream-contribution caveat.**
  MongoDB now implements this saga-explorer interface (RavenDb does; Cosmos does not) in
  `Internals/MongoDbSagaStoreDiagnostics.cs`, registered by `UseMongoDbPersistence`. Functional
  test coverage lands in T2.2 (`test/saga-store-diagnostics`).
  - **⚠️ Address when contributing this provider upstream to Wolverine.** The implementation reaches
    three Wolverine core members that are `internal` and therefore inaccessible from this **external**
    package — it is not on Wolverine's `[assembly: InternalsVisibleTo]` list, unlike the in-repo
    RavenDb/Marten/EF Core/RDBMS providers, which call them directly: `SagaDescriptorBuilder.Build`,
    `WolverineOptions.HandlerGraph`, and `HandlerGraph.Container`. They are bridged via isolated,
    cached, non-throwing reflection in the three `Resolve*` methods of `MongoDbSagaStoreDiagnostics`,
    each carrying a `// TODO(upstream)` marker. When this provider is moved into the Wolverine repo,
    add `Wolverine.MongoDB` to Wolverine's `[InternalsVisibleTo]` (alongside `Wolverine.RavenDb`) and
    collapse each reflective bridge to the direct member access every sibling provider uses — e.g.
    `buildDescriptor` becomes the one-liner
    `SagaDescriptorBuilder.Build(_runtime.Options.HandlerGraph, sagaType, "MongoDb")`. Until then, the
    reflection is pinned to and validated against the `external/wolverine` submodule (WolverineFx 6.x).

- **Saga-specific indexes — decision (T4.6, 2026-07-05: document/defer).** `EnsureIndexesAsync`
  (`MongoDbMessageStore.Admin.cs:18-64`) creates indexes on the system collections only (incoming,
  outgoing, dead-letter, node-records); saga collections (`wolverine_saga_*`) get only the
  implicit MongoDB `_id` unique index from the driver. **Decision: sufficient today, deferred.**
  The current saga access pattern is exclusively load/insert/update/delete by `_id` (via
  `MongoSagaOperations` in `SagaFrames.cs`), which the implicit index already serves. Secondary
  indexes (filtering sagas by status fields, range scans) are only worth the write-amplification
  cost once a concrete query pattern demands one. **Extension point:** `EnsureIndexesAsync` /
  `RebuildAsync` (`MongoDbMessageStore.Admin.cs`) is where per-collection saga indexes belong when
  that need arises — no code change now.

## Untested-but-inspected paths (add deterministic coverage later)

- Bulk `StoreIncomingAsync` non-duplicate-error rethrow branch (no clean way to
  force a non-duplicate bulk write error against a real Mongo).
- `MoveToDeadLetterStorageAsync` poison-message serialization-failure path (hard
  to force a serialization failure on a real envelope).
