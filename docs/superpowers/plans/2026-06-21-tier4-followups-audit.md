# Tier 4 — FOLLOWUPS Audit + Multinode Un-Gate Scoping

> **Task D4** of `docs/superpowers/plans/2026-06-21-persistence-suite-completion.md`
>
> Read-only audit of every `FOLLOWUPS.md` item: confirms current behavior in code (file:line),
> classifies each as **implement / document-as-non-goal / verify**, and maps to the Tier-4 task
> that resolves it. Includes the complete multinode un-gate runbook for T4.5.

---

## 1. FOLLOWUPS Item Mapping Table

| # | FOLLOWUPS item | Current behavior (file:line) | Classification | Task |
|---|---|---|---|---|
| 1 | Unkeyed `IMongoDatabase` registration | `WolverineMongoDbExtensions.cs:59-60` — `AddSingleton(sp => client.GetDatabase(name))`, unkeyed | document-as-constraint (default) or implement wrapper | **T4.3** |
| 2 | `INodeAgentPersistence.ClearAllAsync` scope | `MongoDbMessageStore.NodeAgents.cs:177-181` — clears only `wolverine_nodes` + `wolverine_node_assignments` | document-as-narrow-by-design | **T4.4** |
| 3 | Node-number reuse | `MongoDbMessageStore.NodeAgents.cs:16-20` — `Inc(Count, 1)` with `IsUpsert = true`; counter never resets freed slots | document-as-non-goal (post-1.0) | **T4.6** |
| 4 | Pre-1.0 index migration | `MongoDbMessageStore.Admin.cs:18-64` — creates current compound indexes; old single-field indexes on beta deployments left in place | document-as-non-goal (pre-1.0 beta) | **T4.6** |
| 5 | Lease fencing token / epoch | `MongoDbMessageStore.Locking.cs:1-74` — `LockDocument` carries `Id`, `NodeId`, `ExpiresAt`; no `Epoch`/fencing field | document-as-non-goal (future hardening) | **T4.6** |
| 6 | `IListenerStore` still `NullListenerStore` | `MongoDbMessageStore.cs:64` — `Listeners { get; } = NullListenerStore.Instance` | document-as-non-goal (Tier 3, matches Cosmos/Raven) | **T3.1** |
| 7 | Demo `MongoDbUnitOfWork` example | demo/CLAUDE.md confirms no UoW example; `PlaceOrderHandler` uses `IClientSessionHandle` only | implement | **T4.1** |
| 8 | Demo saga cascade consumer | `OrderFulfillmentSaga.cs:63-68,75-81` — returns `FulfillmentShippedEvent`/`FulfillmentCompletedEvent`; no handler wired in demo | implement | **T4.2** |
| 9 | Saga-specific indexes | `MongoDbMessageStore.Admin.cs:18-64` — indexes created only for system collections; saga collections (`wolverine_saga_*`) have only the implicit `_id` unique index | document-as-non-goal (add via `RebuildAsync` when query patterns demand) | **T4.6** |
| 10 | Multinode leadership compliance re-eval | `leadership_election_compliance.cs:1,61` — `#if RUN_MULTINODE` / `#endif` guard; one-off 13/13 passed against 6.9.0 | verify (5× green per TFM before un-gate) | **T4.5** |
| 11 | `ISagaStoreDiagnostics` not implemented | `WolverineMongoDbExtensions.cs` — no `ISagaStoreDiagnostics` registration; no implementation class | implement (Tier 2) | **T2.1** |

> **Out-of-D4-scope item (in FOLLOWUPS but not audited here):** `HasLeadershipLock external-delete
> edge` — already documented as acceptable (self-corrects within one lease); no code change. Belongs
> to T4.6's "document/defer" bundle.

---

## 2. Per-Item Evidence & Classification Rationale

### Item 1 — Unkeyed `IMongoDatabase` registration → T4.3

**Evidence:**
```csharp
// WolverineMongoDbExtensions.cs:59-60
options.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
```

The call is `AddSingleton<IMongoDatabase>(...)` — an **unkeyed** registration. An app that
registers its own `IMongoDatabase` will either conflict or shadow this one.

**Classification:** document-as-constraint (default) — the saga and entity frames resolve
`IMongoDatabase` via `chain.FindVariable(typeof(IMongoDatabase))`; switching to a keyed
registration is a high-blast-radius codegen change. Alternative is a thin `WolverineMongoDatabase`
wrapper the frames resolve instead, but only if D6/T1.1 find it contained. T4.3 makes the call.

---

### Item 2 — `INodeAgentPersistence.ClearAllAsync` scope → T4.4

**Evidence:**
```csharp
// MongoDbMessageStore.NodeAgents.cs:177-181
public async Task ClearAllAsync(CancellationToken cancellationToken)
{
    await NodeDocs.DeleteManyAsync(FilterDefinition<NodeDocument>.Empty, cancellationToken);
    await AssignmentDocs.DeleteManyAsync(FilterDefinition<AgentAssignmentDocument>.Empty, cancellationToken);
}
```

Clears ONLY `wolverine_nodes` + `wolverine_node_assignments`. Does **not** clear:
- `wolverine_node_records` (event logs)
- `wolverine_agent_restrictions`
- `wolverine_counters` (node-number counter)
- `wolverine_locks` (leader/scheduled-job lock documents)

Compare with `IMessageStoreAdmin.ClearAllAsync` (`MongoDbMessageStore.Admin.cs:67-88`), which
clears ALL six system collections plus all `wolverine_saga_*` collections — the full reset.
The test harness (`AppFixture.ClearAll()`) calls `RebuildAsync()` → `Admin.ClearAllAsync`, so
compliance tests always get the full clear.

**Classification:** document-as-narrow-by-design — the node-level `ClearAllAsync` is invoked by
`INodeAgentPersistence` consumers for operational node-state reset, not system reset. The FOLLOWUPS
ambiguity resolves as: `Admin.RebuildAsync()`/`ClearAllAsync()` is the full reset; the node-level
one is intentionally narrow. T4.4 documents this boundary explicitly.

---

### Item 3 — Node-number reuse → T4.6

**Evidence:**
```csharp
// MongoDbMessageStore.NodeAgents.cs:16-20
var counter = await Counters.FindOneAndUpdateAsync(
    Builders<NodeCounterDocument>.Filter.Eq(x => x.Id, MongoConstants.NodeCounterId),
    Builders<NodeCounterDocument>.Update.Inc(x => x.Count, 1),
    new FindOneAndUpdateOptions<NodeCounterDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
    cancellationToken);
node.AssignedNodeNumber = counter.Count;
```

The counter (`wolverine_counters` / `node_number` id) is a pure monotonic increment — once a node
is deregistered its slot is freed from `wolverine_nodes` but the counter value never decreases.
The next registering node gets `count + 1`, not the freed slot.

**Classification:** document-as-non-goal — lowest-free-slot reuse is deferred post-1.0. The
monotonic model is simple, correct, and matches Wolverine's usage pattern (node numbers are
short-lived identifiers for multi-node coordination, not long-lived external keys). T4.6 adds
a dated decision entry to FOLLOWUPS/CLAUDE.

---

### Item 4 — Pre-1.0 index migration → T4.6

**Evidence:**
```csharp
// MongoDbMessageStore.Admin.cs:18-64 — current indexes
// Incoming: compound (Status+OwnerId), compound (Status+ExecutionTime), compound (OwnerId+ReceivedAt),
//           Ascending(EnvelopeId), TTL(KeepUntil)
// Outgoing: compound (OwnerId+Destination), Ascending(Destination), Ascending(DeliverBy)
// DeadLetter: Ascending(SentAt/MessageType/ExceptionType/Replayable), TTL(ExpirationTime)
// NodeRecords: TTL(Timestamp, 14 days)
```

The hardening pass that introduced compound indexes did not drop the old single-field
`executionTime` and `ownerId` indexes from collections on existing beta deployments. Those old
indexes are harmless (still valid, just suboptimal for the new query patterns) — no correctness
issue.

**Classification:** document-as-non-goal (pre-1.0 beta, no migration planned). An explicit
migration step in `Admin.MigrateAsync()` is an option before 1.0 if requested. T4.6 records
the decision.

---

### Item 5 — Lease fencing token / epoch → T4.6

**Evidence:**
```csharp
// MongoDbMessageStore.Locking.cs — LockDocument fields resolved from usage:
// Id (string), NodeId (Guid), ExpiresAt (DateTime)
// No Epoch, no Version, no fencing-token field.

// TryAttainAsync emits:
var update = Builders<LockDocument>.Update
    .Set(x => x.NodeId, NodeId)
    .Set(x => x.ExpiresAt, now.Add(_persistenceOptions.LockLeaseDuration))
    .SetOnInsert(x => x.Id, lockId);
// HasLeadershipLock (Locking.cs:51-59): checks NodeId + 75%-lease margin; no epoch check.
```

The lock document has no fencing token (monotonically increasing epoch). A fencing token would
allow store writes to carry the epoch and reject writes from a node with a stale epoch, closing
the "paused node acts on stale leadership for external systems" residual.

**Classification:** document-as-non-goal — not needed for store-only leader work; the 75%-lease
margin already mitigates the stale-leadership window for internal coordination. Track as a future
hardening item if leader-scoped external side effects become common. T4.6 documents this.

---

### Item 6 — `IListenerStore` still `NullListenerStore` → T3.1

**Evidence:**
```csharp
// MongoDbMessageStore.cs:64
public IListenerStore Listeners { get; protected set; } = NullListenerStore.Instance;
```

The `IListenerStore` interface is satisfied by the no-op `NullListenerStore.Instance`. Listener
persistence (durable local queues surviving restarts) is not implemented.

**Classification:** document-as-non-goal (Tier 3 parity decision) — this is a **Tier 3** item
(parity capabilities), not a Tier 4 item. Cosmos (`CosmosDbMessageStore.cs:74`) and RavenDb
(`RavenDbMessageStore.cs:68`) both return `NullListenerStore.Instance` with explicit "follow-up"
comments — the same state. T3.1 formalizes the defer + records the cheap optional implementation
shape as a tracked FOLLOWUPS entry.

---

### Item 7 — Demo `MongoDbUnitOfWork` example → T4.1

**Evidence:**

Demo CLAUDE.md explicitly states: *"The demo does not use it (the repository pattern is the
fuller example), but it is documented in the library README."*

`PlaceOrderHandler` accepts `IClientSessionHandle` directly — there is no handler in
`demo/src/OrderDemo.Application/` that accepts `MongoDbUnitOfWork`.

**Classification:** implement — T4.1 adds a new handler (e.g. `RecordOrderAuditCommand`) that
accepts `MongoDbUnitOfWork` and writes through `Collection<T>()`. Per D5's design spec.

---

### Item 8 — Demo saga cascade consumer → T4.2

**Evidence:**
```csharp
// OrderFulfillmentSaga.cs:63-68
public FulfillmentShippedEvent Handle(OrderShippedApplicationEvent evt) { ... return new FulfillmentShippedEvent(...); }

// OrderFulfillmentSaga.cs:75-81
public FulfillmentCompletedEvent Handle(ConfirmDeliveryCommand cmd) { ... return new FulfillmentCompletedEvent(...); }
```

The saga returns cascade events, but no consumer is wired. From the saga source:
*"Returns FulfillmentShippedEvent to illustrate that a saga handler may cascade a message — this
demo wires no consumer for it."*

**Classification:** implement — T4.2 adds a fulfillment read-model projector consuming
`FulfillmentShippedEvent`/`FulfillmentCompletedEvent` (recording delivery status/timestamp the
`OrderSummary` does not track) plus a matching route and assertion. Per D5's design spec.

---

### Item 9 — Saga-specific indexes → T4.6

**Evidence:**
```csharp
// MongoDbMessageStore.Admin.cs:67-88 (ClearAllAsync / RebuildAsync / EnsureIndexesAsync)
// EnsureIndexesAsync creates indexes on: Incoming, Outgoing, DeadLetterDocs, RecordDocs
// No per-saga-type index creation. Saga collections (wolverine_saga_*) have only the
// implicit _id unique index from the driver.
```

The `RebuildAsync` path (`Admin.cs:10-14`) calls `ClearAllAsync` then `EnsureIndexesAsync`, but
`EnsureIndexesAsync` does not touch saga collections. Each saga collection gets only the MongoDB
implicit `_id` unique index.

**Classification:** document-as-non-goal — the implicit `_id` index is sufficient for the
current load/insert/update-by-id access pattern. Per-collection secondary indexes (e.g.
filtering sagas by status fields or range scans) are meaningful only when query patterns demand
them; when they do, `EnsureIndexesAsync` is the correct extension point. T4.6 documents the
extension point and defers until a concrete query pattern exists.

---

### Item 10 — Multinode leadership compliance re-eval → T4.5

**Evidence:**
```csharp
// leadership_election_compliance.cs:1   → #if RUN_MULTINODE
// leadership_election_compliance.cs:34  → [Trait("Category", "multinode")]
// leadership_election_compliance.cs:36  → public class leadership_election_compliance : LeadershipElectionCompliance
// leadership_election_compliance.cs:61  → #endif
```

The suite is compile-gated (`#if RUN_MULTINODE`). Local invocation requires
`-p:DefineConstants=RUN_MULTINODE`. The CI multinode step (`ci.yml:40-45`) runs
`--filter "Category=multinode"` but does NOT pass `DefineConstants=RUN_MULTINODE`, so the class
is excluded from CI compilation entirely.

**Background:** Task 6 of the multinode plan could not reach 5× green on WolverineFx 6.2.2 because
`leader_switchover_between_nodes` and `singular_agent_is_only_running_on_one` required the
lowest-numbered surviving node to win a lock-arrival-order race that a `w:majority+j:true` MongoDB
store loses ~50% of the time. Full analysis:
`docs/superpowers/plans/2026-06-16-task6-multinode-compliance-findings.md`.

**6.9.0 update (from FOLLOWUPS.md):** WolverineFx 6.9.0 reworked the compliance facts:
- `leader_switchover_between_nodes` now uses a slow heartbeat + explicit `CheckAgentHealth` trigger,
  eliminating the lock-arrival-order race.
- A new `take_over_leader_ship_if_leader_becomes_stale_with_racing_nodes` fact is built around
  the "any node may win" model this provider already implements.
- A one-off `RUN_MULTINODE` run passed **13/13 on net9.0** (new total: 13 facts including the
  new one).

This is a single-run result, not the standing bar. T4.5 must confirm 5× consecutive green on
both net9.0 and net10.0 before un-gating.

**Classification:** verify — see the runbook below.

---

### Item 11 — `ISagaStoreDiagnostics` not implemented → T2.1

**Evidence:**
```csharp
// WolverineMongoDbExtensions.cs — no ISagaStoreDiagnostics registration
// No file in src/Wolverine.MongoDB/Internals/ implements ISagaStoreDiagnostics
```

RavenDb implements and registers `RavenDbSagaStoreDiagnostics` (`ISagaStoreDiagnostics`) in its
`UseRavenDbPersistence` extension (exposes saga-explorer tooling for the Wolverine dashboard).
Cosmos does not implement it. MongoDB follows Cosmos's current state.

**Classification:** implement (Tier 2, independent of Tier 4) — T2.1 adds
`MongoDbSagaStoreDiagnostics` mirroring the RavenDb implementation, requiring D2's discovery
doc for the exact API mapping.

---

## 3. Multinode Un-Gate Runbook (for T4.5)

### Guard locations

```
src/Wolverine.MongoDB.Tests/leadership_election_compliance.cs
  Line 1:  #if RUN_MULTINODE          ← outer #if
  Line 34: [Trait("Category", "multinode")]   ← already inside; survives guard removal
  Line 36: public class leadership_election_compliance : LeadershipElectionCompliance
  Line 61: #endif                     ← matching #endif
```

To un-gate: **remove lines 1 and 61** (the `#if` / `#endif` pair). The `using` directives
on lines 2–7 are inside the guard; they become unconditional after removal (safe). The
`[Trait("Category", "multinode")]` at line 34 already ensures the class routes to the CI
multinode step — no further editing needed.

The `configureNode` override (`lines 45-54`) uses:
- `LockLeaseDuration = TimeSpan.FromSeconds(5)` — the plan's standard lease; acceptable since
  the 6.9.0 rework removed the lock-race sensitivity.

### Pre-gate verification — run 5× per TFM

All runs must pass in a **single consecutive sequence per TFM** with no interleaving of other
multinode tests. Each run exercises the full `Category=multinode` suite (including the
existing `multinode_end_to_end.cs` tests) so regressions to existing facts are caught.

Compile the suite with `DefineConstants=RUN_MULTINODE` **while the guard is still present**
for the first 5-run verification. Only remove the guard after confirming green.

**net9.0 — run 5× (all must pass):**
```bash
# From repo root (with Wolverine submodule initialized)
for i in 1 2 3 4 5; do
  dotnet test src/Wolverine.MongoDB.Tests/Wolverine.MongoDB.Tests.csproj \
    -c Release -p:UseWolverineSource=true \
    -p:DefineConstants=RUN_MULTINODE \
    --filter "Category=multinode" \
    -f net9.0 \
    --logger "console;verbosity=normal"
  echo "=== Run $i net9.0 complete ==="
done
```

**net10.0 — run 5× (all must pass):**
```bash
for i in 1 2 3 4 5; do
  dotnet test src/Wolverine.MongoDB.Tests/Wolverine.MongoDB.Tests.csproj \
    -c Release -p:UseWolverineSource=true \
    -p:DefineConstants=RUN_MULTINODE \
    --filter "Category=multinode" \
    -f net10.0 \
    --logger "console;verbosity=normal"
  echo "=== Run $i net10.0 complete ==="
done
```

**Acceptance bar:** all 10 runs green (5× net9.0 + 5× net10.0), no skips, no retries, no
assertion-weakening, no timeout-lengthening. Any single failure = do not un-gate; document
the observed failure and keep the gate (update FOLLOWUPS.md with the 6.9.0 result and the
new failure mode).

### Facts covered (6.9.0 compliance suite — 13 facts)

The 13 facts include the two formerly-flaky ones and the new racing-nodes fact:
- `leader_switchover_between_nodes` (formerly flaky at 6.2.2 — key watch fact)
- `singular_agent_is_only_running_on_one` (was coupled to the above)
- `take_over_leader_ship_if_leader_becomes_stale` (was fixable at 6.2.2)
- `take_over_leader_ship_if_leader_becomes_stale_with_racing_nodes` (**new** at 6.9.0)
- remaining 9 facts (had been green consistently at 6.2.2)

### Un-gate steps (after 5×-green confirmation)

1. Remove lines 1 and 61 from `leadership_election_compliance.cs`.
2. Build the project **without** `DefineConstants=RUN_MULTINODE` to confirm the file compiles:
   ```bash
   dotnet build src/Wolverine.MongoDB.Tests -p:UseWolverineSource=true
   ```
3. Run the full library suite (single-node + multinode) **once** to confirm no regressions:
   ```bash
   dotnet test src/Wolverine.MongoDB.Tests -c Release -p:UseWolverineSource=true \
     --logger "console;verbosity=normal"
   ```
4. **No ci.yml change is needed.** The CI multinode step (`ci.yml:40-45`) already runs
   `--filter "Category=multinode"`. Once the `#if` guard is removed, the class is compiled
   unconditionally and its `[Trait("Category", "multinode")]` routes it into that step
   automatically.
5. Update `FOLLOWUPS.md` (close the multinode leadership item) and the memory file
   `multinode-leadership-model-decision`.

### Hard rules (same as Tasks 7/S14)

- **No skips.** All 13 facts must run and pass.
- **No retries.** A single failed run resets the counter to zero.
- **No assertion-weakening.** Do not loosen timeouts in the test file.
- **No timeout-lengthening.** If a timing-sensitive fact is flaky, stop and report rather
  than bumping the budget.
- **No ci.yml `--filter` changes** to exclude leadership facts from the multinode step.
- **Stop and extend this report** if 5× cannot be reached — document the new failure mode and
  keep the gate.

### If 5×-green cannot be reached

Update `FOLLOWUPS.md` with:
- Date of attempt
- WolverineFx version pinned at time of attempt
- Which facts failed and the failure text
- Number of green runs before first failure
- Decision: keep gate; re-evaluate at next WolverineFx version bump

---

## 4. Task → FOLLOWUPS Coverage

| Task | FOLLOWUPS items covered |
|---|---|
| **T2.1** | ISagaStoreDiagnostics not implemented |
| **T3.1** | IListenerStore still NullListenerStore |
| **T4.1** | Demo has no MongoDbUnitOfWork example |
| **T4.2** | Demo saga cascade events have no consumer |
| **T4.3** | Unkeyed IMongoDatabase registration |
| **T4.4** | INodeAgentPersistence.ClearAllAsync scope |
| **T4.5** | Multinode leadership compliance re-eval (+ un-gate runbook above) |
| **T4.6** | Node-number reuse; pre-1.0 index migration; lease fencing token; saga-specific indexes; HasLeadershipLock external-delete edge (document-as-acceptable) |

Items **not** mapped to T4.x: ISagaStoreDiagnostics → T2.1; IListenerStore → T3.1.
All other items in `FOLLOWUPS.md` (the "Untested-but-inspected paths" section at the bottom)
are test-coverage gaps unrelated to D4 scope.
