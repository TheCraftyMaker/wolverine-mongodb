# Follow-ups

Known limitations and deferred work, captured so nothing is silently dropped.
Promote to GitHub issues before the first public release.

## Deferred from the post-review hardening pass

- **`INodeAgentPersistence.ClearAllAsync` scope.** It currently clears only the
  node and assignment collections. Consider also clearing the counter, locks,
  node-records, and agent-restriction collections (or document that
  `IMessageStoreAdmin.RebuildAsync`/`ClearAllAsync` is the full reset and the
  node-level one is intentionally narrow).

- **Node-number allocation.** The counter is monotonically increasing and never
  reuses freed slots. A lowest-free-slot reuse strategy would keep node numbers
  compact across restarts.

- **Unqualified `IMongoDatabase` singleton registration.** `UseMongoDbPersistence`
  registers a single unkeyed `IMongoDatabase`. In an app that already registers
  its own `IMongoDatabase`, this is a constraint/conflict. Document as a consumer
  constraint or switch to a keyed/dedicated registration.

- **`HasLeadershipLock` external-delete edge (residual, low priority).** The 75%
  lease margin means a node stops reporting leadership well before the lease
  expires, so a competing node can take over safely. The remaining residual: if
  the lock document is deleted externally (e.g. manual MongoDB intervention) while
  a node is inside the 75% window, that node still believes it holds leadership
  until its cached `ExpiresAt` crosses the margin. Self-corrects within one lease
  duration; acceptable for the current model.

- **Multinode leadership compliance is gated (by decision, not pending work).** The
  upstream `leadership_election_compliance` facts (`leader_switchover_between_nodes`,
  `singular_agent_is_only_running_on_one`) require the lowest-numbered surviving node
  to win a leadership-claim race that Wolverine core decides by "whoever's heartbeat
  grabs the lock first" — fast stores (RavenDb/Postgres) win it emergently via
  low-latency CAS; our `w:majority+j:true` lock cannot, and no `configureNode` knob
  fixes it. **Decision (2026-06-16): declined.** "Lowest node wins" is a test
  tie-breaker, not a production requirement; the provider keeps the
  production-appropriate any-healthy-node model and the suite stays compile-gated
  behind `RUN_MULTINODE`, exactly as Wolverine's own Cosmos provider gates the same
  facts `[Flaky]`. Production confidence for multinode comes from `multinode_end_to_end.cs`
  (exactly-once scheduling + dead-node rescue, 5× green on net9.0 + net10.0).
  Full analysis + the declined code:
  `docs/superpowers/plans/2026-06-16-task6-multinode-compliance-findings.md`.

  - **Update (WolverineFx 6.9.0): worth re-evaluating.** V6.9.0 reworked these compliance facts —
    `leader_switchover_between_nodes` now uses a slow heartbeat plus an explicit `CheckAgentHealth`
    trigger (removing the lock-arrival-order race), and a new `take_over_leader_ship_if_leader_
    becomes_stale_with_racing_nodes` fact is built around the "any node may win" model this provider
    already implements. A one-off `RUN_MULTINODE` run against 6.9.0 passed **13/13 on net9.0**,
    including both formerly-flaky facts and the new racing-nodes fact. This is a single run, not the
    standing bar. Before un-gating: confirm **5× consecutive green on net9.0 + net10.0**, then remove
    the `#if RUN_MULTINODE` guard and add the suite to the CI multinode step.

- **Lease fencing token (epoch) as a future hardening option.** The leader lock
  currently has no fencing token (monotonically increasing epoch). Adding one would
  allow store writes to carry the epoch and reject writes from a node with a stale
  epoch — eliminating the "paused node acts on stale leadership for external systems"
  residual. Not needed for store-only leader work; track as a future hardening item
  if leader-scoped external side effects become common.

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

- **Demo app has no `MongoDbUnitOfWork` example.** The demo uses the repository
  pattern with explicit `IClientSessionHandle` threading, which is the fuller
  production example. Add a second handler (or a variant endpoint) that accepts
  `MongoDbUnitOfWork` directly so consumers can compare both patterns side by side.

- **Demo saga cascade events have no consumer — resolved (T4.2).** `OrderFulfillmentSaga` returns
  `FulfillmentShippedEvent` / `FulfillmentCompletedEvent`; `FulfillmentStatusProjector`
  (`demo/src/OrderDemo.Infrastructure/Projectors/FulfillmentStatusProjector.cs`) now consumes both
  via a local durable queue (`Program.cs` + `OrdersFixture`) and maintains the
  `fulfillment_delivery_statuses` read model — delivery status/timestamps `OrderSummary` does not
  track. `SagaFlowTests.ShipAndConfirmOrder_ProjectsFulfillmentDeliveryStatus` exercises the full
  saga → outbox → consumer path end to end.

- **Old single-field indexes not dropped on existing deployments.** The hardening
  pass replaced single-field `executionTime` and outgoing `ownerId` indexes with
  compound alternatives. Existing deployments keep the old indexes harmlessly (pre-1.0
  beta; no migration planned). Add an explicit index migration step before 1.0 if
  needed.

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

- **Saga-specific indexes.** The saga collections have only the implicit `_id` unique
  index. If query patterns (filtering sagas by status fields, range scans) are added
  later, per-collection indexes may be worthwhile. The `RebuildAsync` path that creates
  envelope indexes would be the right place to add them.

## Untested-but-inspected paths (add deterministic coverage later)

- Bulk `StoreIncomingAsync` non-duplicate-error rethrow branch (no clean way to
  force a non-duplicate bulk write error against a real Mongo).
- `MoveToDeadLetterStorageAsync` poison-message serialization-failure path (hard
  to force a serialization failure on a real envelope).
