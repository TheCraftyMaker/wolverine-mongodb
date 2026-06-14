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
