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

- **Missing `EnvelopeId` index (perf).** `wolverine_incoming_envelopes` has no
  index on `EnvelopeId`; scheduled/dead-letter queries that filter by envelope id
  do a scan. Add an index if profiling shows it matters.

- **Unbounded `wolverine_node_records`.** Node-event records grow without bound.
  Add a TTL index or implement the `DeleteOldNodeRecordsAsync(int retainCount)`
  override (it currently uses the interface default no-op).

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
  own as `[Flaky]`.

## Surfaced during the hardening pass

- **Process-wide `DateTimeOffset` serializer.** The fix for TTL/range/ordering
  registers a `DateTimeOffsetSerializer(BsonType.DateTime)` globally (via a
  `[ModuleInitializer]`, guarded against double-registration). This mutates the
  process-wide BSON registry: if the host app or another library in the same
  process persists `DateTimeOffset` expecting the driver's default
  `[ticks, offset]` array representation, this changes their format too. Consider
  scoping the representation to this library's document types via class maps /
  a convention pack instead of a global registration.

- **Session-bound write helper for domain-write atomicity.** The
  "domain write + outbox write in one transaction" guarantee only holds if the
  handler accepts the generated `IClientSessionHandle` and threads it into its own
  MongoDB writes; the registered `IMongoDatabase` does not auto-enlist. A
  session-bound write helper (or an auto-enlisting `IMongoDatabase` wrapper) would
  make this ergonomic and less error-prone. Documented in README/CONTRIBUTING.

## Untested-but-inspected paths (add deterministic coverage later)

- Bulk `StoreIncomingAsync` non-duplicate-error rethrow branch (no clean way to
  force a non-duplicate bulk write error against a real Mongo).
- Scheduled-claim cross-node double-publish guard and the crash-between-flip-and-
  enqueue window (need a second concurrent node to exercise deterministically).
- `MoveToDeadLetterStorageAsync` poison-message serialization-failure path (hard
  to force a serialization failure on a real envelope).
