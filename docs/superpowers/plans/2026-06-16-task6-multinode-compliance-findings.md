# Task 6 Findings — Multinode Leadership-Election Compliance Cannot Reach Five-Green

> Status: **BLOCKED — reporting per the plan's escalation rule**, not shipping a green-at-any-cost suite.
> Plan: `docs/superpowers/plans/2026-06-09-multinode-support.md`, Task 6.
> Branch: `test/ungate-multinode-compliance`.

## Summary

The `#if RUN_MULTINODE` gate was removed and the suite tagged `[Trait("Category","multinode")]`
exactly as the plan specifies. Of the 12 compliance facts, **9–10 pass reliably**. The
acceptance bar — **five consecutive green `dotnet test --filter "Category=multinode"` runs** —
**was not reached** after exhausting all of the plan's stabilization levers, because of two
facts whose pass/fail is governed by a non-deterministic leadership-claim race:

- **`leader_switchover_between_nodes`** — the primary blocker. Fails ~40–88% of runs depending
  on config; never reliably green.
- **`singular_agent_is_only_running_on_one`** — a secondary casualty of the same takeover race;
  surfaces once the leader-switchover path is exercised under load.

A third fact, **`take_over_leader_ship_if_leader_becomes_stale`**, *is* stabilizable (see below)
— it is included here only because it shares the same root cause and was flaky in the plan's
baseline config.

Per the plan ("if five-in-a-row stability cannot be reached after exhausting the listed levers,
stop and report findings back to the user rather than weakening the assertions") and the task's
HARD RULES (no skips, no retries, no timeout lengthening), this report is the deliverable.

## The failing facts and how they fail

All failures are `System.TimeoutException : Did not assume the leadership in the time allowed`
thrown by `LeadershipElectionCompliance.WaitUntilAssumesLeadershipAsync` — i.e. the **specific**
host the test expects to become leader never does within the (generous, 15–30s) budget.

| Fact | Wait that times out | Budget |
|---|---|---|
| `leader_switchover_between_nodes` | `host2`/`host3` assume leadership after the prior leader leaves | 30s each |
| `take_over_leader_ship_if_leader_becomes_stale` | `host2` assumes leadership after the leader is disabled | 15s |
| `singular_agent_is_only_running_on_one` | singular agent reassigned after leader leaves | 15s |

## Root cause (verified against Wolverine V6.2.2 source)

1. **Leadership is purely "whoever wins the lock first." There is no node-number-ordered
   election.** Every node, every heartbeat, unconditionally calls
   `TryAttainLeadershipLockAsync` (`NodeAgentController.HeartBeat.cs:87` — *"Always call
   TryAttainLeadershipLockAsync"*). `AssignmentGrid.cs:75` sets `IsLeader` purely from "does this
   node hold the `wolverine://leader/` assignment", which the lock winner adds to itself in
   `tryStartLeadershipAsync`. Nothing biases the winner toward a particular node.

2. **The compliance test nonetheless requires the lowest-numbered surviving node to win** — host2
   after host1 leaves, then host3 after host2 leaves. For fast stores this holds *emergently*:
   the earliest-started survivor's heartbeat is phase-leading, and with sub-millisecond,
   low-variance lock ops it reliably commits its claim first.

3. **Both takeover paths free the lock immediately** — `StopAsync` (graceful, used by
   `leader_switchover`) and `DisableAgentsAsync` (used by `take_over`) both call
   `ReleaseLeadershipLockAsync`, which for our store is a single `DeleteOne` that fully removes
   the lock document. So takeover is *release-then-race*, not lease-expiry-bound. After the
   release every survivor races to upsert-claim the now-absent lock document; exactly one wins via
   the unique `_id` (others get `DuplicateKey`).

4. **Our lock ops are `w:majority` + `j:true`** (pinned on the store for correctness — see
   `CLAUDE.md`). This gives higher and more variable latency than the relational/RavenDb stores.
   That latency variance erodes the phase-lead, so the test-expected node wins each transfer only
   ~50–70% of the time. `leader_switchover` makes **two** sequential transfers, compounding the
   probability.

5. **`take_over` is fixable because it has a synchronous driver.** The test calls
   `host2.InvokeMessageAndWaitAsync(new CheckAgentHealth())`, so host2 ejects the stale leader and
   claims leadership on a controlled, synchronous tick. Reducing background-heartbeat interference
   (longer `HealthCheckPollingTime`) lets host2's synchronous claim win reliably — config
   `lease=10s, HealthCheckPollingTime=2s, FirstHealthCheckExecution=500ms` made `take_over` pass
   5/5. **`leader_switchover` has no synchronous driver** — host2 must win a pure background race —
   so the same trick does not help it.

## Observed interleavings

From captured xUnit output of failing runs:

- Three nodes log *"Node {guid} successfully assumed leadership"* over a single
  `leader_switchover` run (original → some survivor → some survivor), and core's split-brain
  healer fires: *"Detected duplicate agent wolverine://leader/ reported running on Node X and
  Node Y — sending StopRemoteAgent to the older copy to heal split-brain residue."* The node
  holding leadership during the assertion window is simply **not** the one the test waited on.
- In the `take_over` failures, the split-brain log showed `Node 1` (the disabled ex-leader, whose
  leader-assignment row lingered) and `Node 4` — i.e. host4 grabbed the freed lock instead of the
  expected host2.

## Levers exhausted (full-category `dotnet test --filter "Category=multinode"`, net9.0, Wolverine V6.2.2)

| Config | `LockLeaseDuration` | `HealthCheckPollingTime` | Result |
|---|---|---|---|
| (a) lease sweep | 2s | 1s (base) | ~100% fail — renewal margin (`lease/4`) too tight → incumbent churn |
| plan default | 5s | 1s (base) | ~50% pass — `leader_switchover` **and** `take_over` flaky |
| (b) less interference | 10s | 2s, `FirstHealth=500ms` | `take_over` fixed; `leader_switchover` ~40% pass |
| (b) larger period | 30s | 3s | 0% — `leader_switchover` worse + breaks `singular_agent` |
| (b) faster polling | 20s | 250ms | 0% — `leader_switchover` worse + breaks `singular_agent` |
| (b) big lease, no churn | 30s | 1s | 0% — wrong winner holds leadership for the full lease; host2 never gets a re-race |

Key learnings:
- **Lever (a) shorten lease (plan's first suggestion) makes it worse**, not better. A 2s lease
  trips the conservative `HasLeadershipLock` 75% margin against the 1s renewal cadence, so the
  *incumbent* false-steps-down → churn → deterministic failure.
- **Lever (b) "shorten the heartbeat knobs" (plan's second suggestion) also makes it worse.**
  Faster polling increases background-claim interference; slower polling delays the first health
  check past the 5s initial-leadership wait (`FirstHealthCheckExecution + HealthCheckPollingTime`,
  per `WolverineRuntime.Agents.cs:213-217`) and breaks `singular_agent`. `HealthCheckPollingTime=1s`
  (the base-class value) is the empirical optimum and still only ~50%.
- A **bigger lease removes the only thing accidentally helping**: under a short lease the wrong
  winner's tight renewal margin made it churn and release, occasionally re-handing host2 a chance.
  Remove churn (big lease) and the wrong winner holds leadership stably for the whole lease →
  host2 never wins → deterministic failure.
- **Full-category runs are *harsher* than isolated runs** for this race: after 11 prior facts the
  process carries more GC/connection/thread load → higher latency variance → the phase-lead is
  eroded more. Isolated `leader_switchover` passed ~60% at `lease=30s/HEALTH=1s`; the same config
  in the full category passed ~12%.

## Lever (c): comparison against the RavenDb provider's subclass

`src/Persistence/LeaderElection/RavenDbTests.LeaderElection/leadership_election_compliance.cs`
applies **no** extra configuration beyond `UseTcpForControlEndpoint()` + persistence registration
(it does not shorten the lease or any timing knob; its lock lease is a hardcoded 5 minutes).
RavenDb's `TryAttainLeadershipLockAsync` is a **non-blocking Compare-Exchange CAS**, semantically
identical to ours, and its leader-election test is **not** marked `[Flaky]` (unlike Cosmos and
Oracle, which *are*, for exactly `add_second_node_see_balanced_nodes` / shared-instance lock
contention). The only material difference is op latency/variance: RavenDb runs an in-process
embedded server (microsecond CAS) so the phase-leading node reliably wins; our `w:majority+j:true`
Mongo writes do not. (Attempting to run the RavenDb test here for direct confirmation failed —
its embedded server cannot start in this environment — so this rests on source inspection.)

Notably, the relational/RavenDb providers were stabilized for **these exact facts** through a
series of *core/library* fixes, not test config: advisory-lock-stacking failover fix (#2618),
split-brain heal (GH-2602), and the self-stale guard (GH-2682). The MongoDB provider, being new,
would need analogous **library-level** hardening — this is not reachable by tuning `configureNode`.

## Cross-framework comparison: MassTransit (requested)

MassTransit (`github.com/MassTransit/MassTransit`, `src/Persistence/MassTransit.MongoDbIntegration`)
was reviewed to see how it handles the same problem. It **does not have leader election
at all** — there is no node registry, no agent balancing, and no "leader" concept (the only
`leader` hits in the tree are unrelated Kafka config). Its `BusOutboxDeliveryService` runs on
*every* instance and coordinates purely via **competing-consumers / per-work-item claims**:

- `GetOutboxes` lists pending outbox states; each is claimed inside a transaction via
  `MongoDbCollectionContext.Lock` — a `FindOneAndUpdateAsync(session, …)` that sets a fresh
  `LockToken`, wrapped in a **retry-on-`WriteConflict`/`TransientTransactionError`** loop
  (`TransactionMongoDbCollectionContext.cs:51`).
- Forward progress is guarded by **optimistic concurrency** on a `Version` field
  (`FindOneAndReplace` filtered by `Version < newVersion`).

Implications for us:

- MassTransit's leaderless model **cannot be retrofitted** to satisfy
  `LeadershipElectionCompliance` — our provider must implement Wolverine's
  `INodeAgentPersistence` leader-election contract, and the suite explicitly asserts
  leader-switchover semantics.
- Its `Lock`-with-retry primitive is robust for "exactly one instance processes item X," but it
  selects the winner by **arrival order**, not node identity — so it would *not* make the
  lowest-numbered node win our leadership race either. (Our hot-path scheduled-message claim
  already uses this same atomic-claim idea, which is why the non-leadership facts are green.)

Conclusion: the determinism the compliance suite needs has to be made **explicit**; no faster
lock and no MassTransit-style claim confers it.

## Hypothesis / recommended path

`leader_switchover_between_nodes` (and the dependent `singular_agent_is_only_running_on_one`)
require deterministic election of the lowest-numbered surviving node. Wolverine core does not
provide that ordering, and our durable Mongo lock cannot satisfy it *emergently* the way a
low-latency store does. Closing the gap requires a **library** change, e.g. one of:

1. **Bias the leadership claim toward the lowest live node number** in
   `MongoDbMessageStore.Locking.TryAttainLeadershipLockAsync` (a node defers attaining if a
   lower-numbered live node exists). Makes election deterministic and matches the test's
   assumption. Risk: an extra node-list read on the lock path, and care needed so a slow lowest
   node cannot stall failover.
2. **Reduce lock-op latency variance** for the lock collection specifically (e.g. `w:majority`
   without `j:true`, or a lighter lock primitive) so the phase-lead wins as it does for RavenDb.
   Risk: changes lock durability semantics — needs a deliberate decision.

Either is **out of Task 6's scope** (test-only) and belongs with the Task 2/3 leader-election work
or a dedicated follow-up. Until then `take_over` is stabilizable but `leader_switchover` is not.

## Suggested code change (option 1 — recommended)

Make leadership election deterministic by node number, in
`src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Locking.cs`. Only the lowest-numbered
**live** node contends for the lock; every other node defers. Core then cleanly makes that node
the leader, and a node that discovers a lower-numbered live peer steps down via the existing
`NodeAgentController.HeartBeat.cs:103-106` path. Field names verified against `NodeDocument.cs`
and `MongoDbMessageStore.cs` on this branch.

Replace the one-liner:

```csharp
public Task<bool> TryAttainLeadershipLockAsync(CancellationToken token)
    => TryAttainAsync(MongoConstants.LeaderLockId, token);
```

with:

```csharp
public async Task<bool> TryAttainLeadershipLockAsync(CancellationToken token)
{
    // Deterministic leadership election. Wolverine core elects "whichever node's heartbeat
    // grabs the lock first" (NodeAgentController.HeartBeat.cs:87). For a durable
    // w:majority+j:true Mongo lock that is a race the LeadershipElectionCompliance suite
    // loses — it expects the LOWEST-numbered surviving node to win. Gate the claim on node
    // number so the winner is deterministic (matching the latency-driven emergent behaviour
    // of the RavenDb/Postgres providers): only the lowest-numbered live node contends; all
    // others defer. Returning false while IsLeader makes a now-too-high node step down.
    var myNumber = _options.Durability.AssignedNodeNumber;
    var staleCutoff = DateTime.UtcNow - _options.Durability.StaleNodeTimeout;

    var lowerLiveNodeExists = await NodeDocs
        .Find(Builders<NodeDocument>.Filter.And(
            Builders<NodeDocument>.Filter.Lt(x => x.AssignedNodeNumber, myNumber),
            Builders<NodeDocument>.Filter.Gte(x => x.LastHealthCheck, staleCutoff)))
        .AnyAsync(token);

    if (lowerLiveNodeExists)
    {
        _leaderLock = null; // not eligible to lead right now; drop any stale cached belief
        return false;
    }

    return await TryAttainAsync(MongoConstants.LeaderLockId, token);
}
```

Notes / things the implementer must get right (these are exactly why this is not a mechanical
change):

- **Scope to the leader lock only.** `TryAttainScheduledJobLockAsync` must keep calling
  `TryAttainAsync` directly — the scheduled-job lock is competing-claim, not leader-elected.
- **`AssignedNodeNumber` must be the real sequential number, not the `DurabilitySettings`
  default.** The default is a random hash (`DurabilitySettings.cs:174`); core overwrites it with
  the sequential number during node registration. Confirm it is assigned before the first
  leadership attempt, or guard the unassigned case. (Single-node `the_only_known_node...` is safe
  regardless — the `< myNumber` filter matches no other node.)
- **Staleness must match core's notion.** Use `_options.Durability.StaleNodeTimeout` (default
  1 min) so "live" here agrees with core's `ejectStaleNodes`. The compliance tests force
  staleness via a 1-hour backdate / graceful delete, so both the disabled-leader and
  shutdown-leader paths resolve correctly.
- **Cost is negligible** — one read of the tiny `wolverine_nodes` collection per leadership
  heartbeat; no new index needed.
- **`take_over` keeps passing** for the same reason it can be stabilized today (its synchronous
  `CheckAgentHealth` driver), and now becomes deterministic rather than interference-sensitive,
  so the test no longer needs the short-lease/large-period tuning — the plan's plain `lease 5s`
  config should hold.

Alternative (option 2, no code given): lower the lock collection's write concern (e.g.
`w:majority` without `j:true`) to shrink latency variance so the phase-lead wins as it does for
RavenDb. Smaller change but it weakens lock durability semantics and is a probabilistic
mitigation, not a guarantee — prefer option 1.

### How to verify the fix

1. **TDD first:** add a focused unit test in `Wolverine.MongoDB.Tests` — build two stores with
   distinct `AssignedNodeNumber`s (the lower one with a fresh heartbeat), assert the
   higher-numbered store's `TryAttainLeadershipLockAsync` returns `false` while the lower one
   returns `true`; then mark the lower node stale and assert the higher one takes over. Watch it
   fail, then implement.
2. **Then the suite:** un-gate `leadership_election_compliance` (this branch already does) and run
   `dotnet test src/Wolverine.MongoDB.Tests --filter "Category=multinode"` **five times in a row**,
   plus the full suite once. Run against the CI version — a `V6.2.2` Wolverine worktree exists at
   `C:\source\external\wolverine-V6.2.2`; pass `-p:WolverineSourcePath=C:\source\external\wolverine-V6.2.2`
   (the local `C:\source\external\wolverine` clone has drifted to 6.5.1).
3. **Guard the merged work:** re-run the Task 2/3/4 tests (`leader_lease`, `outgoing_recovery_contention`,
   `dead_node_ownership_release`) — the change touches the shared leader-lock path.

## Recommended model for the implementation

This is a **distributed-systems concurrency change**, not a transcription task: a subtly wrong
election predicate (off-by-one on `<` vs `<=`, wrong staleness window, missing the unassigned-number
case, or applying the gate to the scheduled lock) **passes locally most of the time and only races
under load / in CI** — precisely the failure class the multinode plan calls out.

- **Use Fable 5 or Opus** (the plan's strongest tier — the same it mandates for Tasks 2, 3, 6, 7).
  **Do not use Sonnet or Haiku** for the implementation: the plan reserves Sonnet for
  "fully-specified code with deterministic tests", which this is not, and forbids Haiku anywhere.
- **Drive it with TDD** (step 1 above) and treat the five-in-a-row `Category=multinode` bar as the
  acceptance gate — no retries, no skips, no timeout lengthening (same HARD RULES as this task).
- **Code review on Fable 5 / Opus with explicit concurrency scrutiny**, per the plan's
  between-tasks review rule ("concurrency bugs that pass a green suite are precisely what review
  must catch here").
- **Escalation:** if five-in-a-row still cannot be reached after the change, stop and extend this
  report rather than weakening assertions — the same escalation rule that produced this document.

## Decision

Reviewed with the maintainer. Decision: **land this analysis; do not change library code now.**
The deterministic-election fix (the "lowest live node wins" guard above) is tracked as a follow-up
in `FOLLOWUPS.md`. `leadership_election_compliance` therefore **stays compile-gated** behind
`RUN_MULTINODE` until that library work lands — un-gating it now would run a flaky category in CI
(which runs `dotnet test` with no category filter), and the HARD RULES forbid shipping a
luck-dependent suite.

## What's in this PR

- `leadership_election_compliance.cs`: **kept gated** behind `#if RUN_MULTINODE`, with the
  explanatory comment rewritten to point here and to record that the body is the intended
  un-gated form (`[Trait("Category","multinode")]`, lease 5s — the plan's Task 6 Step 1 content)
  to drop in once the fix lands.
- This findings document and a `FOLLOWUPS.md` entry tracking the deterministic-election fix.

CI stays green; no library or production behaviour changes. Un-gating + the separate CI category
step (plan Task 8) follow the deterministic-election fix.
