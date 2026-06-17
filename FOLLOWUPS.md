# Follow-ups

Known limitations and deferred work, captured so nothing is silently dropped.
None of these block the 0.1.0 single-node MVP; promote to GitHub issues before
the first public release.

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

- **`HasLeadershipLock` external-delete edge (low).** `HasLeadershipLock` trusts
  the cached lease until its expiry; if the lock document is deleted externally
  mid-lease, the node still believes it holds leadership until expiry. Low risk
  (self-corrects at expiry); acceptable for single-node.

- **Multi-node agent balancing / cross-node orphan correctness.** True multi-node
  agent assignment balancing/rebalancing and end-to-end cross-node orphan recovery
  remain out of scope for 0.1.0. The `leadership_election_compliance` suite is
  compile-gated behind `#if RUN_MULTINODE`; the single-node leader-lock facts pass,
  multi-node balancing facts are flaky (deferred), mirroring how Cosmos marks its
  own as `[Flaky]`. Tracked in `docs/superpowers/plans/2026-06-09-multinode-support.md`.

- **Multinode leadership compliance is gated (by decision, not pending work).** The upstream
  `leadership_election_compliance` facts (`leader_switchover_between_nodes`,
  `singular_agent_is_only_running_on_one`) require the lowest-numbered surviving node to win a
  leadership-claim race that Wolverine core decides by "whoever's heartbeat grabs the lock
  first" — fast stores (RavenDb/Postgres) win it emergently via low-latency CAS; our
  `w:majority+j:true` lock cannot, and no `configureNode` knob fixes it (lease/heartbeat sweeps
  all worse; MassTransit has no leader election to borrow from). **Decision (2026-06-16):
  declined to chase it.** "Lowest node wins" is a test tie-breaker, not a production
  requirement; the provider keeps the production-appropriate any-healthy-node model and the
  suite stays compile-gated behind `RUN_MULTINODE`, exactly as Wolverine's own Cosmos provider
  gates the same facts `[Flaky]`. A deterministic lowest-node election *could* make them pass
  but would degrade real failover — documented as an **upstream-parity option only** (full
  analysis + the declined code):
  `docs/superpowers/plans/2026-06-16-task6-multinode-compliance-findings.md`. Production
  confidence for multinode comes from the cross-node message-guarantee tests (plan Task 7),
  which are leader-identity-independent.

- **Demo app has no `MongoDbUnitOfWork` example.** The demo uses the repository
  pattern with explicit `IClientSessionHandle` threading, which is the fuller
  production example. Add a second handler (or a variant endpoint) that accepts
  `MongoDbUnitOfWork` directly so consumers can compare both patterns side by side.

- **Old single-field indexes not dropped on existing deployments.** The hardening
  pass replaced single-field `executionTime` and outgoing `ownerId` indexes with
  compound alternatives. Existing deployments keep the old indexes harmlessly (pre-1.0
  beta; no migration planned). Add an explicit index migration step before 1.0 if
  needed.

- **`DeleteOldNodeRecordsAsync` override not implemented.** The TTL index on
  `wolverine_node_records` (added in the hardening pass) handles automatic expiry
  at 14 days. The `DeleteOldNodeRecordsAsync(int retainCount)` interface method
  still uses the no-op default. Implement if count-based pruning is needed.

## Untested-but-inspected paths (add deterministic coverage later)

- Bulk `StoreIncomingAsync` non-duplicate-error rethrow branch (no clean way to
  force a non-duplicate bulk write error against a real Mongo).
- Scheduled-claim cross-node double-publish guard and the crash-between-flip-and-
  enqueue window (need a second concurrent node to exercise deterministically).
- `MoveToDeadLetterStorageAsync` poison-message serialization-failure path (hard
  to force a serialization failure on a real envelope).
