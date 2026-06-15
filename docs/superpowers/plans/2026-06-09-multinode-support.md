# Multinode Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Wolverine.MongoDB` safe and verified under `DurabilityMode.Balanced` (multi-node clusters): CAS-guarded outgoing recovery, dead-node ownership release, configurable leader lease, node-record retention, an un-gated and stabilized multinode compliance suite, cross-node integration tests, demo support, and documentation.

**Architecture:** Multinode coordination reuses Wolverine core's `NodeAgentController` (leader election, stale-node ejection, agent balancing) — this plan supplies the store-side primitives it needs: a correct lease-based leader lock, ownership release for dead node numbers, and recovery loops that never double-claim. MongoDB has no native control transport, so Balanced mode requires `opts.UseTcpForControlEndpoint()` (mirroring Wolverine's RavenDb provider). All cross-node claims stay CAS-based (`findAndModify` / filtered `UpdateMany` + re-read), never lock-the-world.

**Tech Stack:** .NET 9/10, MongoDB.Driver 3.x, WolverineFx 6.2.2 (`Wolverine.Transports.Tcp` for control endpoints), xUnit + Shouldly, Testcontainers.MongoDb.

**PREREQUISITE: The Solo Hardening plan (`2026-06-09-solo-hardening.md`) must be fully executed and merged to `main` first.** This plan assumes: owner-filtered `LoadOutgoingAsync`, idempotent dead-letter replay, the `DurabilityMode.Balanced` startup guard (removed in Task 1 below), pinned write concerns, and library tests running in CI.

---

## Git & PR Workflow (one branch + one PR per task)

**Every task is delivered as its own feature branch and its own PR against `main`.** The inline "Commit" step at the end of each task stays as written; it is followed by the push/PR steps below. Each PR must be independently green: the task's own tests pass, plus the full library suite.

**Per-task procedure (one git worktree per task — safe for parallel agents):**

> **Why a worktree, not `git checkout`:** a clone has exactly one working tree, one `HEAD`, and one index, all global to the directory. Two task-agents sharing one checkout clobber each other — a `git checkout` / `reset` / `stash` in one silently reverts the other's uncommitted work, or bleeds edits across branches. Each task therefore gets its **own** working directory backed by the shared `.git`. `.worktrees/` is gitignored.

> **Already isolated?** If you were dispatched as a subagent with worktree isolation (the `Agent` / `Workflow` `isolation: "worktree"` option, or a native `EnterWorktree` / `/worktree` tool), you are **already** in your own worktree — do not nest another. Confirm with `git branch --show-current`, then skip straight to the task's steps.

```bash
# Start: from the main checkout, cut an isolated worktree off the CURRENT main
# (so already-merged prerequisite tasks are included).
rtk git fetch origin
rtk git worktree add .worktrees/<branch-name> -b <branch-name> origin/main
cd .worktrees/<branch-name>          # every later step — incl. the task's Commit step — runs HERE

# ... execute the task's steps in this directory, ending with its Commit step ...

# Finish: push and open the PR from inside the worktree
rtk git push -u origin <branch-name>
rtk gh pr create --base main --head <branch-name> \
  --title "<PR title>" \
  --body "<one-paragraph summary: what was broken/missing, what changed, how it is tested. Reference docs/superpowers/plans/2026-06-09-multinode-support.md Task N.>"
rtk gh pr checks --watch

# After the PR is MERGED by the owner: drop the worktree from the main checkout
cd <repo-root>
rtk git worktree remove .worktrees/<branch-name>
```

- **`--head <branch-name>` is now required:** `gh` would infer the head from the current branch, but stating it explicitly keeps the PR unambiguous no matter which worktree it is invoked from. `gh` reads the shared `.git`, so it behaves identically in every worktree.
- A slashed branch name (e.g. `feat/mongodb-persistence-options`) just nests one extra directory level under `.worktrees/` — that is fine.
- Each worktree has its own `bin` / `obj`, so parallel `dotnet build` / `dotnet test` runs never collide. A branch can be checked out in only one worktree at a time; since every task has a distinct branch, this never conflicts.

Commit messages end with the `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` trailer; PR bodies end with the `🤖 Generated with [Claude Code](https://claude.com/claude-code)` line. **A dependent task starts only after its dependency's PR is merged** — this plan is much more sequential than the hardening plan.

| Task | Branch | PR title | Depends on | Model |
|---|---|---|---|---|
| 1 | `feat/mongodb-persistence-options` | feat: MongoDbPersistenceOptions; allow DurabilityMode.Balanced | Solo plan merged | Sonnet |
| 2 | `feat/configurable-leader-lease` | feat: configurable leader lock lease with renewal margin | **Task 1** | **Fable 5 / Opus** |
| 3 | `fix/cas-outgoing-recovery` | fix: CAS-guarded outgoing recovery prevents cross-node double-claims | Solo plan merged | **Fable 5 / Opus** |
| 4 | `feat/release-dead-node-ownership` | feat: release envelope ownership held by dead node numbers | Solo plan merged | Sonnet |
| 5 | `feat/delete-old-node-records` | feat: implement DeleteOldNodeRecordsAsync | Solo plan merged | Sonnet |
| 6 | `test/ungate-multinode-compliance` | test: un-gate multinode leadership election compliance | **Tasks 1, 2** (and merge 3, 4 first — balancing facts exercise recovery) | **Fable 5** |
| 7 | `test/multinode-end-to-end` | test: cross-node exactly-once scheduling and dead-node rescue | **Tasks 1–4** | **Fable 5** |
| 8 | `ci/multinode-category` | ci: run multinode test category as a separate step | **Task 6** (the category must exist) | Sonnet |
| 9 | `demo/config-driven-durability-mode` | demo: config-driven durability mode with multinode runbook | **Task 1** | Sonnet |
| 10 | `docs/multinode-sweep` | docs: multinode support documentation | **Tasks 1–9 merged** | Sonnet |
| 11 | *(no branch/PR)* | final verification on `main` | **Task 10 merged** | Sonnet |

**Recommended merge order:** 1 → 2, with 3, 4, 5 as parallel PRs alongside; then 6 → 8; 7 and 9 once their dependencies are in; 10 last; 11 on `main`.

**Model guidance.** This plan skews to a stronger tier than the hardening plan because the failure modes are distributed-systems races, not transcription errors (Agent tool `model` parameter: `sonnet`, `opus`, `fable`):

- **Fable 5 mandatory** for Tasks 6 and 7: stabilizing the leadership-election compliance suite and the cross-node end-to-end tests is iterative flaky-test diagnosis (lease/heartbeat timing, claim races, unverified Wolverine APIs like `PortFinder` and the control-endpoint registration). The plan provides stabilization levers, not a deterministic recipe — the agent must reason about *why* a fact flakes before turning a knob, and the explicit anti-goal is "do not mark facts as skipped / do not add retries".
- **Fable 5 / Opus** for Task 2 (lease semantics + timing-sensitive tests where an off-by-margin bug looks like a pass) and Task 3 (CAS claim correctness — the test simulates a race, and a subtly wrong filter still passes locally most of the time).
- **Sonnet** for Tasks 1, 4, 5, 8, 9, 10, 11 — fully specified code with deterministic tests.
- **Do not use Haiku** anywhere in this plan.
- **Escalation rule:** same as the hardening plan — two non-obvious verification failures, or a broken plan assumption, means re-dispatch on Fable 5 with the failure context instead of improvising. For Tasks 6/7 specifically, if five-in-a-row stability cannot be reached after exhausting the listed levers, stop and report findings back to the user rather than weakening the assertions.
- **Code review between tasks**: Fable 5/Opus, with extra scrutiny on Tasks 2, 3, 4 — concurrency bugs that pass a green suite are precisely what review must catch here.

---

## File Structure Overview

| File | Change |
|---|---|
| `src/Wolverine.MongoDB/MongoDbPersistenceOptions.cs` | **New** — public options (leader lease duration) |
| `src/Wolverine.MongoDB/WolverineMongoDbExtensions.cs` | `configure` overload for options |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs` | Accept options; replace Balanced throw with control-endpoint-aware warning |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Locking.cs` | Configurable lease + renewal margin |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs` | CAS outgoing claim; dead-node ownership release |
| `src/Wolverine.MongoDB/Internals/MongoDbDurabilityAgent.cs` | Wire dead-node release into the recovery loop (non-Solo only) |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.NodeAgents.cs` | `DeleteOldNodeRecordsAsync` override |
| `src/Wolverine.MongoDB.Tests/leadership_election_compliance.cs` | Un-gate (remove `#if RUN_MULTINODE`) |
| `src/Wolverine.MongoDB.Tests/durability_mode_guard.cs` | Invert: Balanced now starts |
| `src/Wolverine.MongoDB.Tests/outgoing_recovery_contention.cs` | **New** |
| `src/Wolverine.MongoDB.Tests/dead_node_ownership_release.cs` | **New** |
| `src/Wolverine.MongoDB.Tests/leader_lease.cs` | **New** |
| `src/Wolverine.MongoDB.Tests/multinode_end_to_end.cs` | **New** — two in-proc Balanced hosts |
| `demo/src/OrderDemo.Api/Program.cs` + `appsettings*.json` | Config-driven durability mode + control port |
| `.github/workflows/ci.yml` | Run the multinode test category |
| `README.md`, `CLAUDE.md`, `FOLLOWUPS.md`, `CHANGELOG.md`, `demo/README.md`, `demo/CLAUDE.md` | Documentation |

---

### Task 1: Introduce `MongoDbPersistenceOptions` and downgrade the Balanced guard

Multinode work needs Balanced hosts to start. Replace the hard throw (Solo plan Task 6) with a startup log warning that Balanced support is new and requires a control endpoint. Introduce the options type now because later tasks (lease duration) depend on it.

**Files:**
- Create: `src/Wolverine.MongoDB/MongoDbPersistenceOptions.cs`
- Modify: `src/Wolverine.MongoDB/WolverineMongoDbExtensions.cs`
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs`
- Modify: `src/Wolverine.MongoDB.Tests/durability_mode_guard.cs`

- [ ] **Step 1: Amend the guard test** — in `durability_mode_guard.cs`, replace `balanced_mode_fails_fast_at_startup` with:

```csharp
[Fact]
public async Task balanced_mode_starts_with_a_control_endpoint()
{
    await _fixture.ClearAll();
    using var host = await Host.CreateDefaultBuilder()
        .UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Balanced;
            // MongoDB has no native control transport; Balanced requires one.
            opts.UseTcpForControlEndpoint();
            opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
            opts.UseMongoDbPersistence(AppFixture.DatabaseName);
        }).StartAsync();
    host.ShouldNotBeNull();
}
```

Add `using Wolverine.Transports.Tcp;`. Keep `solo_mode_starts_normally` unchanged.

- [ ] **Step 2: Run to verify it fails** (the Solo-plan guard still throws)

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~durability_mode_guard"`
Expected: `balanced_mode_starts_with_a_control_endpoint` FAILS with the `InvalidOperationException` from the guard.

- [ ] **Step 3: Create the options type** — `src/Wolverine.MongoDB/MongoDbPersistenceOptions.cs`:

```csharp
namespace Wolverine.MongoDB;

/// <summary>
/// MongoDB-specific persistence tuning for Wolverine.MongoDB.
/// </summary>
public class MongoDbPersistenceOptions
{
    /// <summary>
    /// How long the leader / scheduled-job lock lease is held before it can be
    /// taken over by another node. Wolverine's leadership health checks renew the
    /// lease well within this window. Lower values speed up leader failover at the
    /// cost of more lock churn; clocks across nodes must be synchronized to well
    /// within this duration. Default: 1 minute.
    /// </summary>
    public TimeSpan LockLeaseDuration { get; set; } = TimeSpan.FromMinutes(1);
}
```

(Note: default changes from the previously hardcoded 5 minutes to 1 minute — 5 minutes makes leader failover unacceptably slow and is the root of the compliance-suite flakiness. CHANGELOG-worthy.)

- [ ] **Step 4: Thread the options through** — in `WolverineMongoDbExtensions.cs` change the signature and store construction:

```csharp
public static WolverineOptions UseMongoDbPersistence(this WolverineOptions options, string databaseName,
    Action<MongoDbPersistenceOptions>? configure = null)
{
    var persistenceOptions = new MongoDbPersistenceOptions();
    configure?.Invoke(persistenceOptions);

    options.Services.AddSingleton<IMessageStore>(sp =>
    {
        var client = sp.GetRequiredService<IMongoClient>();
        var wolverineOptions = sp.GetRequiredService<WolverineOptions>();
        return new MongoDbMessageStore(client, databaseName, wolverineOptions, persistenceOptions);
    });
    // ... rest unchanged
}
```

In `MongoDbMessageStore.cs`, add the field and a constructor parameter with a backward-compatible overload (the tests construct the store directly):

```csharp
private readonly MongoDbPersistenceOptions _persistenceOptions;

public MongoDbMessageStore(IMongoClient client, string databaseName, WolverineOptions options)
    : this(client, databaseName, options, new MongoDbPersistenceOptions())
{
}

public MongoDbMessageStore(IMongoClient client, string databaseName, WolverineOptions options,
    MongoDbPersistenceOptions persistenceOptions)
{
    _persistenceOptions = persistenceOptions;
    // ... existing constructor body unchanged
}
```

- [ ] **Step 5: Replace the throw with a warning** — in `MongoDbMessageStore.cs` replace `AssertSupportedDurabilityMode` and its call sites:

```csharp
public void Initialize(IWolverineRuntime runtime) => WarnOnBalancedMode(runtime);

public IAgent StartScheduledJobs(IWolverineRuntime runtime) => BuildAgent(runtime);

public IAgent BuildAgent(IWolverineRuntime runtime)
{
    WarnOnBalancedMode(runtime);
    return new MongoDbDurabilityAgent(runtime, this);
}

private bool _warnedOnBalanced;

private void WarnOnBalancedMode(IWolverineRuntime runtime)
{
    if (runtime.Options.Durability.Mode != DurabilityMode.Balanced || _warnedOnBalanced) return;
    _warnedOnBalanced = true;
    runtime.LoggerFactory.CreateLogger<MongoDbMessageStore>().LogInformation(
        "Wolverine.MongoDB is running in Balanced (multi-node) mode. " +
        "A control endpoint is required (e.g. opts.UseTcpForControlEndpoint()) and node clocks " +
        "must be synchronized to well within the lock lease ({Lease}).",
        _persistenceOptions.LockLeaseDuration);
}
```

(Add `using Microsoft.Extensions.Logging;`.)

- [ ] **Step 6: Run the guard tests + full suite, then commit**

Run: `dotnet test src/Wolverine.MongoDB.Tests` → PASS.

```bash
rtk git add -A src/Wolverine.MongoDB src/Wolverine.MongoDB.Tests
rtk git commit -m "feat: MongoDbPersistenceOptions; allow DurabilityMode.Balanced with startup guidance"
```

---

### Task 2: Configurable, renewal-aware leader lease

Use `LockLeaseDuration` instead of the hardcoded 5 minutes, and make `HasLeadershipLock` conservative: report leadership only while comfortably inside the lease (75% of the lease), so a paused/skewed node stops acting as leader before another node can legitimately take over.

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Locking.cs`
- Test: Create `src/Wolverine.MongoDB.Tests/leader_lease.cs`

- [ ] **Step 1: Write the failing tests** — create `src/Wolverine.MongoDB.Tests/leader_lease.cs`:

```csharp
using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class leader_lease
{
    private readonly AppFixture _fixture;
    public leader_lease(AppFixture fixture) => _fixture = fixture;

    // Each WolverineOptions instance is born with its own UniqueNodeId, so two
    // stores built this way naturally represent two distinct nodes.
    private MongoDbMessageStore BuildStore(TimeSpan lease)
        => new(_fixture.Client, AppFixture.DatabaseName, new WolverineOptions(),
            new MongoDbPersistenceOptions { LockLeaseDuration = lease });

    [Fact]
    public async Task second_node_takes_over_after_the_lease_expires()
    {
        await _fixture.ClearAll();
        var nodeA = BuildStore(TimeSpan.FromSeconds(2));
        var nodeB = BuildStore(TimeSpan.FromSeconds(2));

        (await nodeA.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeTrue();
        (await nodeB.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeFalse(
            "the lease is live, a second node must not steal it");

        await Task.Delay(TimeSpan.FromSeconds(3));

        (await nodeB.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeTrue(
            "an expired lease must be claimable by another node");
        nodeA.HasLeadershipLock().ShouldBeFalse(
            "the deposed node must not still believe it is leader");
    }

    [Fact]
    public async Task has_leadership_lock_goes_false_before_the_lease_fully_expires()
    {
        await _fixture.ClearAll();
        var node = BuildStore(TimeSpan.FromSeconds(2));

        (await node.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeTrue();
        node.HasLeadershipLock().ShouldBeTrue();

        // At 75% of a 2s lease (1.5s), the cached claim must already be reported
        // as lost so the node stops acting as leader before takeover is possible.
        await Task.Delay(TimeSpan.FromMilliseconds(1700));
        node.HasLeadershipLock().ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failures**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~leader_lease"`
Expected: both FAIL (lease is hardcoded to 5 minutes; `HasLeadershipLock` trusts the full lease).

- [ ] **Step 3: Implement** — in `MongoDbMessageStore.Locking.cs`:

Replace `now.AddMinutes(5)` in `TryAttainAsync` with:

```csharp
.Set(x => x.ExpiresAt, now.Add(_persistenceOptions.LockLeaseDuration))
```

Replace `HasLeadershipLock` (use this exact version):

```csharp
public bool HasLeadershipLock()
{
    if (_leaderLock is null || _leaderLock.NodeId != NodeId) return false;

    // Report leadership only while comfortably inside the lease (75%): a paused or
    // clock-skewed node must stop acting as leader BEFORE another node can take over.
    var margin = TimeSpan.FromTicks(_persistenceOptions.LockLeaseDuration.Ticks / 4);
    return _leaderLock.ExpiresAt - margin > DateTime.UtcNow;
}
```

Also fix the failed-takeover cache bug in `TryAttainAsync`: when the duplicate-key catch returns `false` for the leader lock, the stale `_leaderLock` from a previous tenure must be cleared:

```csharp
catch (MongoCommandException e) when (e.Code == 11000)
{
    if (lockId == MongoConstants.LeaderLockId) _leaderLock = null;
    return false;
}
catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
{
    if (lockId == MongoConstants.LeaderLockId) _leaderLock = null;
    return false;
}
```

- [ ] **Step 4: Run the lease tests + full suite, then commit**

Run: `dotnet test src/Wolverine.MongoDB.Tests` → PASS.

```bash
rtk git add src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Locking.cs src/Wolverine.MongoDB.Tests/leader_lease.cs
rtk git commit -m "feat: configurable leader lock lease with renewal safety margin"
```

---

### Task 3: CAS-guarded outgoing recovery (no double-claims across nodes)

After the Solo plan, `LoadOutgoingAsync` only returns owner-0 envelopes — but two nodes can still both load the same orphans and both `UpdateMany` them to themselves; the second write wins and both nodes enqueue → duplicate sends. Mirror the incoming-recovery pattern: claim with an `OwnerId == AnyNode` guard, then re-read which ids this node actually owns and enqueue only those.

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs` (`RecoverOrphanedOutgoingAsync`)
- Test: Create `src/Wolverine.MongoDB.Tests/outgoing_recovery_contention.cs`

- [ ] **Step 1: Write the failing test** — create `src/Wolverine.MongoDB.Tests/outgoing_recovery_contention.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Transports;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class outgoing_recovery_contention
{
    private readonly AppFixture _fixture;
    public outgoing_recovery_contention(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task recovery_does_not_reassign_envelopes_claimed_by_a_competitor_mid_flight()
    {
        await _fixture.ClearAll();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.PublishAllMessages().ToLocalQueue("contended-out").UseDurableInbox();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var store = _fixture.BuildMessageStore();
        var destination = runtime.Endpoints
            .GetOrBuildSendingAgent(new Uri($"{TransportConstants.Local}://contended-out")).Destination;

        // Two orphaned envelopes at the same destination.
        var mine = ObjectMother.Envelope();
        mine.Destination = destination;
        mine.DeliverBy = DateTimeOffset.UtcNow.AddMinutes(5);
        await store.Outbox.StoreOutgoingAsync(mine, MongoConstants.AnyNode);

        var stolen = ObjectMother.Envelope();
        stolen.Destination = destination;
        stolen.DeliverBy = DateTimeOffset.UtcNow.AddMinutes(5);
        await store.Outbox.StoreOutgoingAsync(stolen, MongoConstants.AnyNode);

        // Simulate a competing node winning the race for one envelope between
        // this node's load and claim: flip its owner directly.
        const int competitorNode = 999;
        var outgoing = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<OutgoingMessage>(MongoConstants.OutgoingCollection);
        await outgoing.UpdateOneAsync(
            Builders<OutgoingMessage>.Filter.Eq(x => x.Id, stolen.Id),
            Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, competitorNode));

        await store.RecoverOrphanedOutgoingAsync(runtime, CancellationToken.None);

        // The competitor's envelope must remain untouched; ours must be claimed.
        var stolenDoc = await outgoing.Find(Builders<OutgoingMessage>.Filter.Eq(x => x.Id, stolen.Id)).SingleAsync();
        stolenDoc.OwnerId.ShouldBe(competitorNode,
            "an envelope owned by a live competitor must never be re-claimed");

        var mineDoc = await outgoing.Find(Builders<OutgoingMessage>.Filter.Eq(x => x.Id, mine.Id)).SingleAsync();
        mineDoc.OwnerId.ShouldBe(runtime.DurabilitySettings.AssignedNodeNumber);
    }
}
```

Note: this is deterministic only if the claim happens *after* the load. With the current implementation (load → `DiscardAndReassignOutgoingAsync` unguarded `UpdateMany` on ids), the competitor's ownership IS overwritten — the test fails. That is exactly the bug.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~outgoing_recovery_contention"`
Expected: FAIL — `stolenDoc.OwnerId` is this node's number, not 999.

Caveat: because `LoadOutgoingAsync` (post Solo plan) already filters owner-0 at load time, the load in `RecoverOrphanedOutgoingAsync` happens before the test's manual flip only if the flip is applied after `StoreOutgoingAsync` but before recovery — which it is. The unguarded `UpdateMany` in `DiscardAndReassignOutgoingAsync` then overwrites. If the test unexpectedly passes, verify by inspecting `DiscardAndReassignOutgoingAsync` — the filter must currently be `In(ids)` only.

- [ ] **Step 3: Implement the CAS claim** — in `MongoDbMessageStore.Durability.cs`, rewrite the body of `RecoverOrphanedOutgoingAsync`'s per-destination block:

```csharp
foreach (var destinationStr in destinations)
{
    if (destinationStr is null)
    {
        continue;
    }

    var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(new Uri(destinationStr));
    if (sendingAgent.Latched)
    {
        continue;
    }

    var outgoing = await LoadOutgoingAsync(sendingAgent.Destination);
    var expired = outgoing.Where(x => x.IsExpired()).ToArray();
    var good = outgoing.Where(x => !x.IsExpired()).ToArray();

    if (expired.Length > 0)
    {
        await DeleteOutgoingAsync(expired);
    }

    if (good.Length == 0)
    {
        continue;
    }

    // CAS claim: only flip envelopes still globally owned. A competing node that
    // claimed one of these ids between our load and this write keeps it.
    var nodeNumber = runtime.DurabilitySettings.AssignedNodeNumber;
    var ids = good.Select(e => e.Id).ToList();
    await Outgoing.UpdateManyAsync(
        Builders<OutgoingMessage>.Filter.And(
            Builders<OutgoingMessage>.Filter.In(x => x.Id, ids),
            Builders<OutgoingMessage>.Filter.Eq(x => x.OwnerId, MongoConstants.AnyNode)),
        Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, nodeNumber),
        cancellationToken: token);

    // Re-read which ids this node actually won and enqueue exactly those.
    var claimedIds = (await Outgoing.Distinct(x => x.Id,
            Builders<OutgoingMessage>.Filter.And(
                Builders<OutgoingMessage>.Filter.In(x => x.Id, ids),
                Builders<OutgoingMessage>.Filter.Eq(x => x.OwnerId, nodeNumber)),
            cancellationToken: token).ToListAsync(token))
        .ToHashSet();

    foreach (var envelope in good.Where(e => claimedIds.Contains(e.Id)))
    {
        await sendingAgent.EnqueueOutgoingAsync(envelope);
    }
}
```

(`DiscardAndReassignOutgoingAsync` itself keeps its current unguarded semantics — it is an `IMessageOutbox` interface member with broader contracts — but the recovery loop no longer routes through it.)

- [ ] **Step 4: Run the contention test + existing recovery tests, then commit**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~outgoing_recovery|FullyQualifiedName~outbox_recovery"`
Expected: PASS.

```bash
rtk git add src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs src/Wolverine.MongoDB.Tests/outgoing_recovery_contention.cs
rtk git commit -m "fix: CAS-guarded outgoing recovery prevents cross-node double-claims"
```

---

### Task 4: Release ownership held by dead node numbers

In Balanced mode there is no startup `ReleaseAllOwnershipAsync()`. Core's leader ejects stale nodes (which calls `DeleteAsync` → release), but envelopes can still be stranded under a node number with no registered node (e.g. crash between scheduled-claim and enqueue — the exact window documented in `PublishDueScheduledMessagesAsync`). Mirror the RDBMS `ReleaseOrphanedMessagesOperation`: any node may release ownership for node numbers that have no live node document.

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs` (new method)
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbDurabilityAgent.cs` (wire into recovery loop)
- Test: Create `src/Wolverine.MongoDB.Tests/dead_node_ownership_release.cs`

- [ ] **Step 1: Write the failing test** — create `src/Wolverine.MongoDB.Tests/dead_node_ownership_release.cs`:

```csharp
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class dead_node_ownership_release
{
    private readonly AppFixture _fixture;
    public dead_node_ownership_release(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task releases_incoming_and_outgoing_owned_by_unregistered_node_numbers()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        // A live node with number 1.
        var liveNode = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            ControlUri = new Uri("tcp://localhost:5678")
        };
        var liveNumber = await store.Nodes.PersistAsync(liveNode, CancellationToken.None);

        // Incoming owned by the live node — must be untouched.
        var ownedByLive = ObjectMother.Envelope();
        ownedByLive.Destination = new Uri("local://dead-node-test");
        ownedByLive.OwnerId = liveNumber;
        await store.Inbox.StoreIncomingAsync(ownedByLive);

        // Incoming + outgoing owned by node 999, which has no node document (it crashed).
        var orphanIncoming = ObjectMother.Envelope();
        orphanIncoming.Destination = new Uri("local://dead-node-test");
        orphanIncoming.OwnerId = 999;
        await store.Inbox.StoreIncomingAsync(orphanIncoming);

        var orphanOutgoing = ObjectMother.Envelope();
        orphanOutgoing.Destination = new Uri("local://dead-node-out");
        await store.Outbox.StoreOutgoingAsync(orphanOutgoing, 999);

        await store.ReleaseDeadNodeOwnershipAsync(CancellationToken.None);

        var incoming = await store.Admin.AllIncomingAsync();
        incoming.Single(x => x.Id == ownedByLive.Id).OwnerId.ShouldBe(liveNumber);
        incoming.Single(x => x.Id == orphanIncoming.Id).OwnerId.ShouldBe(0,
            "envelopes owned by a node number with no live node must be released");

        (await store.Admin.AllOutgoingAsync()).Single().OwnerId.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run to verify it fails to compile** (`ReleaseDeadNodeOwnershipAsync` doesn't exist)

Run: `dotnet build src/Wolverine.MongoDB.Tests`
Expected: CS1061.

- [ ] **Step 3: Implement** — add to `MongoDbMessageStore.Durability.cs`:

```csharp
/// <summary>
/// Releases incoming/outgoing ownership held by node numbers that have no
/// registered node document (crashed nodes). Mirrors the RDBMS
/// ReleaseOrphanedMessagesOperation. Safe to run on any node, any time:
/// a live node always has a node document, so its in-flight work is never touched.
/// </summary>
internal async Task ReleaseDeadNodeOwnershipAsync(CancellationToken token)
{
    var liveNumbers = await NodeDocs
        .Find(FilterDefinition<NodeDocument>.Empty)
        .Project(x => x.AssignedNodeNumber)
        .ToListAsync(token);

    // AnyNode (0) is by definition not "owned".
    liveNumbers.Add(MongoConstants.AnyNode);

    await Incoming.UpdateManyAsync(
        Builders<IncomingMessage>.Filter.Nin(x => x.OwnerId, liveNumbers),
        Builders<IncomingMessage>.Update.Set(x => x.OwnerId, MongoConstants.AnyNode),
        cancellationToken: token);

    await Outgoing.UpdateManyAsync(
        Builders<OutgoingMessage>.Filter.Nin(x => x.OwnerId, liveNumbers),
        Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, MongoConstants.AnyNode),
        cancellationToken: token);
}
```

- [ ] **Step 4: Wire into the recovery loop** — in `MongoDbDurabilityAgent.StartTimers()`, inside the recovery task's `try` block, add as the FIRST call (release before recover, so the same tick can pick up what it released), guarded to non-Solo modes:

```csharp
if (_settings.Mode != DurabilityMode.Solo)
{
    await _parent.ReleaseDeadNodeOwnershipAsync(_combined.Token);
}
await _parent.RecoverOrphanedIncomingAsync(_runtime, _combined.Token);
await _parent.RecoverOrphanedOutgoingAsync(_runtime, _combined.Token);
await _parent.ReplayDeadLettersAsync(_combined.Token);
```

(`DurabilitySettings.Mode` — verify the property name on `DurabilitySettings` in the Wolverine clone; it is `Mode` per `DurabilitySettings.cs`.)

- [ ] **Step 5: Run the test + full suite, then commit**

Run: `dotnet test src/Wolverine.MongoDB.Tests` → PASS.

```bash
rtk git add src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs src/Wolverine.MongoDB/Internals/MongoDbDurabilityAgent.cs src/Wolverine.MongoDB.Tests/dead_node_ownership_release.cs
rtk git commit -m "feat: release envelope ownership held by dead node numbers in Balanced mode"
```

---

### Task 5: Implement `DeleteOldNodeRecordsAsync`

The interface default is a no-op; the leader calls it to trim node-event history. (The 14-day TTL from the Solo plan is the backstop; this honors the explicit retain count.)

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.NodeAgents.cs`
- Test: `src/Wolverine.MongoDB.Tests/node_heartbeat.cs` (add test; it already exercises node persistence)

- [ ] **Step 1: Write the failing test** — add to `node_heartbeat.cs`:

```csharp
[Fact]
public async Task delete_old_node_records_keeps_only_the_newest_n()
{
    await _fixture.ClearAll();
    var store = _fixture.BuildMessageStore();

    var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
    for (var i = 0; i < 10; i++)
    {
        await store.Nodes.LogRecordsAsync(new NodeRecord
        {
            NodeNumber = 1,
            RecordType = NodeRecordType.NodeStarted,
            Timestamp = baseTime.AddSeconds(i),
            Description = $"record {i}",
            ServiceName = "test"
        });
    }

    await store.Nodes.DeleteOldNodeRecordsAsync(3);

    var remaining = await store.Nodes.FetchRecentRecordsAsync(100);
    remaining.Count.ShouldBe(3);
    remaining.Select(x => x.Description).ShouldBe(new[] { "record 7", "record 8", "record 9" });
}
```

(`NodeRecord.Timestamp` is settable; `LogRecordsAsync` takes `params NodeRecord[]`. Add `using Wolverine.Runtime.Agents;` if missing.)

- [ ] **Step 2: Run to verify it fails** (default no-op → 10 remain)

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~delete_old_node_records"`
Expected: FAIL — count is 10.

- [ ] **Step 3: Implement** — add to `MongoDbMessageStore.NodeAgents.cs`:

```csharp
public async Task DeleteOldNodeRecordsAsync(int retainCount)
{
    // Find the timestamp of the oldest record we want to KEEP, then delete
    // everything strictly older.
    var newest = await RecordDocs.Find(FilterDefinition<NodeRecordDocument>.Empty)
        .Sort(Builders<NodeRecordDocument>.Sort.Descending(x => x.Timestamp))
        .Limit(retainCount)
        .ToListAsync();

    if (newest.Count < retainCount)
    {
        return; // fewer records than the retain count: nothing to trim
    }

    var cutoff = newest[^1].Timestamp;
    await RecordDocs.DeleteManyAsync(
        Builders<NodeRecordDocument>.Filter.Lt(x => x.Timestamp, cutoff));
}
```

- [ ] **Step 4: Run + commit**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~node"` → PASS.

```bash
rtk git add src/Wolverine.MongoDB/Internals/MongoDbMessageStore.NodeAgents.cs src/Wolverine.MongoDB.Tests/node_heartbeat.cs
rtk git commit -m "feat: implement DeleteOldNodeRecordsAsync node-record trimming"
```

---

### Task 6: Un-gate and stabilize the multinode compliance suite

The suite is currently compile-gated behind `#if RUN_MULTINODE` because the balancing facts raced the hardcoded 5-minute lease. With the lease now configurable (Task 2) and defaulting to 1 minute, configure a short lease for tests and run the suite for real.

**Files:**
- Modify: `src/Wolverine.MongoDB.Tests/leadership_election_compliance.cs`

- [ ] **Step 1: Remove the compile gate and shorten the lease** — replace the file contents with:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests;
using Wolverine.Transports.Tcp;
using Xunit.Abstractions;

namespace Wolverine.MongoDB.Tests;

// Multi-node leadership election + agent balancing compliance, mirroring how the
// Postgres/SqlServer/RavenDb providers subclass LeadershipElectionCompliance.
// The lock lease is shortened so takeover/balancing assertions converge quickly.
[Trait("Category", "multinode")]
[Collection("mongodb")]
public class leadership_election_compliance : LeadershipElectionCompliance
{
    private readonly AppFixture _fixture;

    public leadership_election_compliance(AppFixture fixture, ITestOutputHelper output) : base(output)
    {
        _fixture = fixture;
    }

    protected override void configureNode(WolverineOptions opts)
    {
        // MongoDB has no native control transport (unlike Cosmos); use TCP for the
        // inter-node control endpoint required by Balanced mode, like RavenDb does.
        opts.UseTcpForControlEndpoint();

        opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
        opts.UseMongoDbPersistence(AppFixture.DatabaseName,
            mongo => mongo.LockLeaseDuration = TimeSpan.FromSeconds(5));
    }

    protected override Task beforeBuildingHost()
    {
        return _fixture.ClearAll();
    }
}
```

- [ ] **Step 2: Run the suite, iterate until stable**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "Category=multinode"`
Expected: PASS. Run it **five times in a row** (`for ($i=0; $i -lt 5; $i++) { dotnet test src/Wolverine.MongoDB.Tests --filter "Category=multinode" }` in PowerShell); all five must pass.

Stabilization levers if facts still flake, in order: (a) shorten `LockLeaseDuration` further (≥ 2s); (b) check whether the base class exposes heartbeat/assignment-check timing knobs on `WolverineOptions.Durability` (e.g. `FirstHealthCheckExecution`, `HealthCheckPollingTime`) and shorten them in `configureNode`; (c) compare against the RavenDb provider's subclass in the Wolverine clone (`src/Persistence/Wolverine.RavenDb*/`) for any extra configuration it applies. Do NOT mark facts as skipped — a flaky fact here means a real coordination bug or a timing knob not yet wired.

- [ ] **Step 3: Run the FULL suite** (the multinode suite shares the fixture database; ensure no cross-contamination)

Run: `dotnet test src/Wolverine.MongoDB.Tests`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
rtk git add src/Wolverine.MongoDB.Tests/leadership_election_compliance.cs
rtk git commit -m "test: un-gate multinode leadership election compliance with short test lease"
```

---

### Task 7: Cross-node end-to-end integration tests

Compliance covers election/balancing; these tests cover the *message* guarantees across nodes: scheduled exactly-once claim, and dead-node envelope rescue end to end.

**Files:**
- Test: Create `src/Wolverine.MongoDB.Tests/multinode_end_to_end.cs`

- [ ] **Step 1: Write the tests** — create `src/Wolverine.MongoDB.Tests/multinode_end_to_end.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Transports.Tcp;

namespace Wolverine.MongoDB.Tests;

public record MultinodeCounterMessage(Guid Id);

public static class MultinodeCounterHandler
{
    public static readonly List<Guid> Handled = new();
    private static readonly Lock _lock = new();

    public static void Handle(MultinodeCounterMessage message)
    {
        lock (_lock)
        {
            Handled.Add(message.Id);
        }
    }
}

[Trait("Category", "multinode")]
[Collection("mongodb")]
public class multinode_end_to_end
{
    private readonly AppFixture _fixture;
    public multinode_end_to_end(AppFixture fixture) => _fixture = fixture;

    private Task<IHost> StartNode(int controlPort) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(500);
                opts.Durability.ScheduledJobFirstExecution = TimeSpan.Zero;
                opts.ListenAtPort(controlPort, x => x.UseForControlEndpoint());
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName,
                    mongo => mongo.LockLeaseDuration = TimeSpan.FromSeconds(5));
                opts.Discovery.IncludeType(typeof(MultinodeCounterHandler));
                opts.LocalQueueFor<MultinodeCounterMessage>().UseDurableInbox();
            }).StartAsync();

    [Fact]
    public async Task scheduled_message_executes_exactly_once_across_two_nodes()
    {
        await _fixture.ClearAll();
        MultinodeCounterHandler.Handled.Clear();

        using var nodeA = await StartNode(PortFinder.GetAvailablePort());
        using var nodeB = await StartNode(PortFinder.GetAvailablePort());

        var id = Guid.NewGuid();
        var bus = nodeA.Services.GetRequiredService<IMessageBus>();
        await bus.ScheduleAsync(new MultinodeCounterMessage(id), DateTimeOffset.UtcNow.AddSeconds(1));

        // Give both nodes' scheduled pollers ample time to compete for the claim.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline && MultinodeCounterHandler.Handled.Count == 0)
        {
            await Task.Delay(250);
        }
        await Task.Delay(2000); // window for an (incorrect) duplicate execution to appear

        MultinodeCounterHandler.Handled.Count(x => x == id).ShouldBe(1,
            "the Scheduled->Incoming CAS claim must make cross-node execution exactly-once");
    }

    [Fact]
    public async Task envelopes_owned_by_a_dead_node_are_rescued_by_a_survivor()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        // Simulate a crashed node: an incoming envelope owned by node number 999
        // with NO corresponding node document.
        var stranded = ObjectMother.Envelope();
        stranded.OwnerId = 999;
        await store.Inbox.StoreIncomingAsync(stranded);

        using var survivor = await StartNode(PortFinder.GetAvailablePort());

        // The survivor's recovery loop must release dead-node ownership.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var all = await store.Admin.AllIncomingAsync();
            if (all.Single(x => x.Id == stranded.Id).OwnerId != 999) break;
            await Task.Delay(250);
        }

        (await store.Admin.AllIncomingAsync()).Single(x => x.Id == stranded.Id)
            .OwnerId.ShouldNotBe(999, "a survivor node must release ownership held by a dead node number");
    }
}
```

Implementation notes for the engineer:
- `PortFinder.GetAvailablePort()` comes from `Wolverine.ComplianceTests` (used by Wolverine's own multinode tests). If it does not exist in this Wolverine version, grep the clone: `rtk grep "GetAvailablePort" C:\source\external\wolverine\src\Testing` and use whatever helper their leadership tests use; worst case, bind a `TcpListener` on port 0 to find a free port.
- `opts.ListenAtPort(port, x => x.UseForControlEndpoint())` — verify the exact control-endpoint API against the Wolverine clone (`UseTcpForControlEndpoint()` picks its own port; the explicit-port form is preferred here for clarity, but if only the parameterless form exists, use it and drop the PortFinder).
- `opts.LocalQueueFor<T>()` — verify name (`PublishMessage<T>().ToLocalQueue(...)` is the fallback).
- The static `Handled` list is shared across both in-proc nodes — that is the point: it observes total executions process-wide.

- [ ] **Step 2: Run, iterate, and stabilize** (same 5-in-a-row bar as Task 6)

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~multinode_end_to_end"`
Expected: PASS, five consecutive runs.

- [ ] **Step 3: Commit**

```bash
rtk git add src/Wolverine.MongoDB.Tests/multinode_end_to_end.cs
rtk git commit -m "test: cross-node exactly-once scheduling and dead-node rescue end to end"
```

---

### Task 8: CI runs the multinode category

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Split the test runs** so a multinode flake is immediately distinguishable from a core regression — in the `library` job, replace the single test step with:

```yaml
      - name: Run library tests (single-node)
        env:
          WOLVERINE_SOURCE: ${{ github.workspace }}/external/wolverine
        run: >
          dotnet test src/Wolverine.MongoDB.Tests/Wolverine.MongoDB.Tests.csproj
          -c Release -p:UseWolverineSource=true
          --filter "Category!=multinode"
          --logger "GitHubActions"

      - name: Run library tests (multinode)
        env:
          WOLVERINE_SOURCE: ${{ github.workspace }}/external/wolverine
        run: >
          dotnet test src/Wolverine.MongoDB.Tests/Wolverine.MongoDB.Tests.csproj
          -c Release -p:UseWolverineSource=true
          --filter "Category=multinode"
          --logger "GitHubActions"
```

- [ ] **Step 2: Commit, open the PR, and confirm both test steps green on the PR checks**

```bash
rtk git add .github/workflows/ci.yml
rtk git commit -m "ci: run multinode test category as a separate step"
```

Then follow the per-task PR procedure from the workflow section (`rtk git push -u ...`, `rtk gh pr create ...`, `rtk gh pr checks --watch`). Iterate until green. If the multinode step flakes on CI but not locally, revisit Task 6's stabilization levers before merging — do not add retries.

---

### Task 9: Demo — config-driven durability mode + multinode runbook

The demo stays Solo by default (correct for a single-instance reference app) but becomes cluster-capable via configuration, with a documented two-instance local run.

**Files:**
- Modify: `demo/src/OrderDemo.Api/Program.cs`
- Modify: `demo/src/OrderDemo.Api/appsettings.json`
- Create: `demo/docker-compose.multinode.yml` *(documentation-only convenience: second API instance is run via `dotnet run`, compose stays infra-only)* — **skip creating this file**; the runbook below uses two `dotnet run` invocations instead.
- Modify: `demo/README.md`, `demo/CLAUDE.md`

- [ ] **Step 1: Make the mode configurable** — in `demo/src/OrderDemo.Api/Program.cs`, replace

```csharp
// Single-node durability for this demo (no multi-node agent coordination needed)
opts.Durability.Mode = DurabilityMode.Solo;
```

with

```csharp
// Single-node durability by default. Set Wolverine:DurabilityMode=Balanced (plus a
// control port) to run multiple instances against the same MongoDB + RabbitMQ —
// see README "Running multiple instances".
var durabilityMode = builder.Configuration["Wolverine:DurabilityMode"] ?? "Solo";
opts.Durability.Mode = Enum.Parse<DurabilityMode>(durabilityMode);

if (opts.Durability.Mode == DurabilityMode.Balanced)
{
    // MongoDB has no native control transport; nodes coordinate over TCP.
    opts.UseTcpForControlEndpoint();
}
```

Add `using Wolverine.Transports.Tcp;` at the top of `Program.cs`. (`WolverineFx` ships the TCP transport in the core package; if the compiler disagrees, check the demo's package set and add the correct using/namespace from the library's own `leadership_election_compliance.cs`.)

- [ ] **Step 2: Add the default to `demo/src/OrderDemo.Api/appsettings.json`** — add a sibling section to `MongoDB`/`RabbitMQ`:

```json
"Wolverine": {
  "DurabilityMode": "Solo"
}
```

- [ ] **Step 3: Add the runbook to `demo/README.md`** — new section:

```markdown
## Running multiple instances (multinode)

The demo runs single-node (`Solo`) by default. To see Wolverine's multi-node
coordination (leader election, agent balancing, cross-node recovery) against
MongoDB, run two instances in `Balanced` mode:

​```bash
docker compose up -d   # shared MongoDB replica set + RabbitMQ

# Terminal 1
ASPNETCORE_URLS=http://localhost:5000 Wolverine__DurabilityMode=Balanced dotnet run --project src/OrderDemo.Api

# Terminal 2
ASPNETCORE_URLS=http://localhost:5001 Wolverine__DurabilityMode=Balanced dotnet run --project src/OrderDemo.Api
​```

Both instances register in `wolverine_nodes`; one acquires the leader lock in
`wolverine_locks`. Place orders against either port — events flow through the
shared outbox and exactly one instance projects each event. Kill one instance and
watch the survivor take over its work (node ejection + ownership release).

Requirements: synchronized clocks across instances (the leader lease tolerates
skew well under its duration) and reachable TCP control endpoints between nodes.
```

(On Windows PowerShell the env-var prefix syntax differs — note that in the README: `$env:Wolverine__DurabilityMode='Balanced'; dotnet run ...`.)

- [ ] **Step 4: Update `demo/CLAUDE.md`** — change the "Key Wolverine Configuration" bullet from "`opts.Durability.Mode = DurabilityMode.Solo` — single-node" to "Durability mode is config-driven (`Wolverine:DurabilityMode`, default `Solo`); `Balanced` enables multi-instance coordination with a TCP control endpoint".

- [ ] **Step 5: Verify the demo still builds and its tests pass** (demo tests run Solo, unchanged)

Run (from `demo/`): `dotnet build OrderDemo.slnx -c Release && dotnet test tests/OrderDemo.IntegrationTests/OrderDemo.IntegrationTests.csproj -c Release`
Expected: PASS.

- [ ] **Step 6: Manual smoke (recommended, requires Docker + RabbitMQ up):** follow the runbook with two instances, place one order on each port, confirm both project summaries and `wolverine_nodes` contains two documents. Record the outcome in the PR description.

- [ ] **Step 7: Commit**

```bash
rtk git add demo
rtk git commit -m "demo: config-driven durability mode with a multinode runbook"
```

---

### Task 10: Documentation sweep

**Files:**
- Modify: `README.md`, `CLAUDE.md`, `FOLLOWUPS.md`, `CHANGELOG.md`

- [ ] **Step 1: `README.md`** — replace the "Solo only / fails fast on Balanced" section (from the Solo plan) with a **Multinode** section:
  - Requirements: MongoDB replica set; `opts.UseTcpForControlEndpoint()` (or any control endpoint); synchronized node clocks (well within `LockLeaseDuration`); `UseMongoDbPersistence(db, mongo => mongo.LockLeaseDuration = ...)` for failover-speed tuning (default 1 minute).
  - Semantics: leader election via lease-based lock document; scheduled messages claimed exactly-once via CAS; dead-node envelopes released by survivors' recovery loops; node-event records trimmed (TTL 14 days + leader trim).
  - Known limits (be honest): leadership is lease-based, not fenced — a node paused longer than the lease margin could briefly act on stale leadership for non-store side effects; clock skew approaching the lease duration breaks takeover ordering.

- [ ] **Step 2: `CLAUDE.md`** — update: remove "does not support Multinode" / "Least mature subsystem" wording on `MongoDbMessageStore.Locking.cs`; document `MongoDbPersistenceOptions`; note the multinode test category and how to run it (`dotnet test --filter "Category=multinode"`); update the CI description (separate multinode step).

- [ ] **Step 3: `FOLLOWUPS.md`** — remove the now-done items: "Multi-node agent balancing / cross-node orphan correctness", "`HasLeadershipLock` external-delete edge" (the renewal margin shrinks it — keep a residual note if desired), "node-records `DeleteOldNodeRecordsAsync`". Keep/add: node-number reuse, lease fencing token (epoch) as a future hardening item, `IListenerStore` still `NullListenerStore`.

- [ ] **Step 4: `CHANGELOG.md`** — `## [Unreleased]` (or the next minor version): `### Added` multinode/Balanced support, `MongoDbPersistenceOptions.LockLeaseDuration`, `DeleteOldNodeRecordsAsync`; `### Changed` leader lease default 5 min → 1 min, Balanced startup no longer throws; `### Fixed` cross-node outgoing double-claim.

- [ ] **Step 5: Truth-check** — re-read every edited doc against the code on this branch; every claim must hold.

- [ ] **Step 6: Commit**

```bash
rtk git add README.md CLAUDE.md FOLLOWUPS.md CHANGELOG.md
rtk git commit -m "docs: multinode support — requirements, semantics, tuning, and known limits"
```

---

### Task 11: Final verification (on `main`, after the Task 10 PR merges — no branch, no PR)

- [ ] **Step 1: Full suite on merged main, five consecutive runs of the multinode category**

```bash
rtk git checkout main && rtk git pull
```

Run: `dotnet test src/Wolverine.MongoDB.Tests` → PASS
Run (PowerShell): `for ($i=0; $i -lt 5; $i++) { dotnet test src/Wolverine.MongoDB.Tests --filter "Category=multinode"; if ($LASTEXITCODE -ne 0) { break } }` → all PASS

- [ ] **Step 2: Package build + demo**

Run: `dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false -o ./artifacts` → succeeds
Run (from `demo/`): `dotnet test tests/OrderDemo.IntegrationTests/OrderDemo.IntegrationTests.csproj -c Release` → PASS

- [ ] **Step 3: Confirm main CI green and review the merged history**

```bash
rtk gh run list --branch main --limit 3
rtk git log --oneline -12
```

Confirm CI on main is green including the multinode step, one merged PR per task (1–10), and every file in the File Structure Overview was touched across those merges.
