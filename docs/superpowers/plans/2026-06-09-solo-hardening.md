# Solo Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the four correctness bugs found in the deep review (duplicate sends, unbounded inbox growth, silent dead-letter loss, replay poison-loop), pin durability guarantees (write concern, fail-fast on unsupported modes), remove the process-global BSON serializer, broaden transaction detection, add a session-bound write helper, tune indexes, wire the library test suite + fresh-package demo into CI, and update all documentation.

**Architecture:** All changes stay inside the existing partial-class `MongoDbMessageStore` layout. Every bug fix lands TDD-style with a regression test in `src/Wolverine.MongoDB.Tests` (Testcontainers MongoDB replica set; requires Docker and the Wolverine clone at `C:\source\external\wolverine` or `WOLVERINE_SOURCE`). CI changes make the same suite run on GitHub Actions against a pinned Wolverine clone, and rewire the demo job to consume the freshly packed nupkg instead of the previously published one.

**Tech Stack:** .NET 9/10, MongoDB.Driver 3.x, WolverineFx 6.2.2, xUnit + Shouldly, Testcontainers.MongoDb, GitHub Actions.

**Prerequisite to verify before starting:** `dotnet test src/Wolverine.MongoDB.Tests` is green locally (it uses the Wolverine source clone). Run it once before Task 1 to get a clean baseline.

---

## Git & PR Workflow (one branch + one PR per task)

**Every task is delivered as its own feature branch and its own PR against `main`.** The inline "Commit" step at the end of each task stays as written; it is followed by the push/PR steps below. Each PR must be independently green: the task's own tests pass, plus the full library suite.

**Per-task procedure:**

```bash
# Start: branch from the CURRENT main (so already-merged prerequisite tasks are included)
rtk git fetch origin
rtk git checkout -b <branch-name> origin/main

# ... execute the task's steps, ending with its Commit step ...

# Finish: push and open the PR
rtk git push -u origin <branch-name>
rtk gh pr create --base main --title "<PR title>" --body "<one-paragraph summary: what was broken/missing, what changed, how it is tested. Reference docs/superpowers/plans/2026-06-09-solo-hardening.md Task N.>"
rtk gh pr checks --watch
```

Commit messages end with the standard `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` trailer; PR bodies end with the `🤖 Generated with [Claude Code](https://claude.com/claude-code)` line.

**A task with a dependency must not start until the dependency's PR is MERGED into `main`** (CI green on the PR is not enough — the next branch forks from `main`). PRs are merged by the repository owner after review.

| Task | Branch | PR title | Depends on | Model |
|---|---|---|---|---|
| 1 | `fix/outgoing-owner-filter` | fix: LoadOutgoingAsync only returns globally-owned envelopes, batch-limited | — | Sonnet |
| 2 | `fix/handled-keep-until` | fix: handled inbox markers carry KeepUntil so the TTL index expires them | — | Sonnet |
| 3 | `fix/dlq-expiration-opt-in` | fix: dead letters only expire when DeadLetterQueueExpirationEnabled is set | — | Sonnet |
| 4 | `fix/dead-letter-replay-idempotent` | fix: dead-letter replay converges after crashes | — | Sonnet |
| 5 | `ci/library-tests-and-fresh-nupkg-demo` | ci: run compliance suite against pinned Wolverine; demo consumes fresh nupkg | — | **Fable 5 / Opus** |
| 6 | `feat/fail-fast-balanced-mode` | feat: fail fast on DurabilityMode.Balanced | — | Sonnet |
| 7 | `refactor/per-property-bson-dates` | refactor: per-property BSON DateTime representation | — | Sonnet |
| 8 | `fix/broaden-transaction-detection` | fix: apply transactions for IMongoCollection/IMongoClient/IClientSessionHandle handlers | — | **Fable 5 / Opus** |
| 9 | `feat/mongodb-unit-of-work` | feat: MongoDbUnitOfWork session-bound write helper | **Task 8 merged** | **Fable 5 / Opus** |
| 10 | `fix/pin-majority-write-concern` | fix: pin majority/journaled write + majority read concern on the store | — | Sonnet |
| 11 | `perf/index-tuning-aggregations` | perf: compound/TTL index tuning and aggregation summaries | — | Sonnet |
| 12 | `docs/solo-hardening-sweep` | docs: hardening pass documentation sweep | **Tasks 1–11 merged** | Sonnet |
| 13 | *(no branch/PR)* | final verification on `main` | **Task 12 merged** | Sonnet |

**Model guidance.** When dispatching a subagent per task (Agent tool `model` parameter: `sonnet`, `opus`, `fable`):

- **Sonnet (4.6)** is sufficient where the plan already contains the exact code and the test is the oracle — Tasks 1–4, 6, 7, 10, 11, and the doc/verification tasks. These are deliberately specified to implementation level; the work is transcription + running tests.
- **Fable 5 (or Opus 4.8)** for the tasks whose steps contain open-ended judgment: Task 5 (iterating on GitHub Actions failures — Wolverine clone resolution, Testcontainers-on-CI, nupkg rewiring — is debugging, not transcription), Task 8 (must verify `IChain.HandlerCalls()`/`ServiceDependencies` semantics against the Wolverine clone and debug generated code), Task 9 (code-generation frame wiring and new public API surface).
- **Do not use Haiku** for anything in this plan — even the "trivial" tasks touch durability semantics where a plausible-but-wrong shortcut (e.g. an unconditional TTL field) is exactly the class of bug this plan exists to remove.
- **Escalation rule:** if a Sonnet-assigned task fails its verification step twice for non-obvious reasons, or discovers that a plan assumption doesn't hold (API missing, test can't fail/pass as predicted), stop and re-dispatch that task on Fable 5 with the failure context rather than letting the Sonnet agent improvise around the plan.
- **Code review between tasks** (the subagent-driven-development flow): run reviews on Fable 5/Opus — review quality is the safety net for the cheaper implementation runs.

**Recommended merge order:** **Task 5 first** — once the CI PR is merged, every subsequent PR gets full library-test coverage on GitHub Actions (Tasks 1–4 are still safe to do before it, since they are test-driven locally). Then 1–4, 6, 7, 10, 11 in any order (they touch disjoint files and can be open as parallel PRs), then 8 → 9, then 12, then 13. If parallel PRs produce trivial merge conflicts in shared test files (`inbox.cs`, `admin_smoke.cs`, `ci.yml`), rebase on `main` and resolve — each task's changes are additive.

**Plan documents:** commit the two plan files themselves as a preliminary docs-only PR (branch `docs/implementation-plans`, title "docs: add solo-hardening and multinode implementation plans") so every task PR can reference them by path.

---

## File Structure Overview

| File | Change |
|---|---|
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs` | Pin write/read concern; fail-fast check |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Outbox.cs` | Owner filter + batch limit in `LoadOutgoingAsync` |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Inbox.cs` | DLQ expiration gating |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs` | Replay fault tolerance; fix misleading comment |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Admin.cs` | Index tuning |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.DeadLetters.cs` | Aggregation-based summarize |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.ScheduledMessages.cs` | Aggregation-based summarize |
| `src/Wolverine.MongoDB/Internals/IncomingMessage.cs` | Map `KeepUntil`; per-property date representation |
| `src/Wolverine.MongoDB/Internals/OutgoingMessage.cs` | Per-property date representation |
| `src/Wolverine.MongoDB/Internals/DeadLetterMessage.cs` | Nullable `ExpirationTime`; per-property date representation |
| `src/Wolverine.MongoDB/Internals/NodeDocument.cs`, `NodeRecordDocument.cs` | Per-property date representation |
| `src/Wolverine.MongoDB/Internals/MongoSerializerRegistration.cs` | **Delete** |
| `src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs` | Broaden `CanApply` |
| `src/Wolverine.MongoDB/Internals/TransactionalFrame.cs` | Emit `MongoDbUnitOfWork` variable |
| `src/Wolverine.MongoDB/MongoDbUnitOfWork.cs` | **New** — session-bound write helper (public API) |
| `src/Wolverine.MongoDB/WolverineMongoDbExtensions.cs` | Remove serializer call |
| `src/Wolverine.MongoDB.Tests/*` | New + amended tests per task |
| `.github/workflows/ci.yml` | Library tests + fresh-nupkg demo job |
| `README.md`, `CLAUDE.md`, `FOLLOWUPS.md`, `CHANGELOG.md`, `demo/README.md`, `demo/CLAUDE.md` | Documentation sweep |

---

### Task 1: `LoadOutgoingAsync` must only return globally-owned envelopes, batch-limited

Every RDBMS provider implements this as `WHERE owner_id = 0 AND destination = @destination LIMIT RecoveryBatchSize`. The Mongo version filters by destination only, which makes `RecoverOrphanedOutgoingAsync` steal in-flight envelopes (duplicate sends) and load unbounded result sets.

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Outbox.cs:8-14`
- Modify: `src/Wolverine.MongoDB.Tests/outbox_recovery.cs` (assertions use `LoadOutgoingAsync` post-reassignment, which the fix breaks by design)
- Test: `src/Wolverine.MongoDB.Tests/outbox.cs` (add test)

- [ ] **Step 1: Write the failing test** — add to `src/Wolverine.MongoDB.Tests/outbox.cs`:

```csharp
[Fact]
public async Task load_outgoing_only_returns_globally_owned_envelopes()
{
    await _fixture.ClearAll();
    var store = _fixture.BuildMessageStore();
    var destination = new Uri("local://load-outgoing-owner-filter");

    var orphaned = ObjectMother.Envelope();
    orphaned.Destination = destination;
    await store.Outbox.StoreOutgoingAsync(orphaned, MongoConstants.AnyNode);

    var ownedByLiveNode = ObjectMother.Envelope();
    ownedByLiveNode.Destination = destination;
    await store.Outbox.StoreOutgoingAsync(ownedByLiveNode, 5);

    var loaded = await store.Outbox.LoadOutgoingAsync(destination);

    // Only the owner_id == 0 envelope may be returned; envelopes owned by a live
    // node are in flight and must never be handed to recovery.
    loaded.Count.ShouldBe(1);
    loaded.Single().Id.ShouldBe(orphaned.Id);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~load_outgoing_only_returns_globally_owned"`
Expected: FAIL — `loaded.Count` is 2.

- [ ] **Step 3: Implement the filter + limit** — replace `LoadOutgoingAsync` in `MongoDbMessageStore.Outbox.cs`:

```csharp
public async Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
{
    // Mirrors the RDBMS providers: only globally-owned (owner 0) envelopes are
    // recovery candidates, capped at RecoveryBatchSize. Envelopes owned by a live
    // node are in flight and must not be re-handed to a sending agent.
    var b = Builders<OutgoingMessage>.Filter;
    var docs = await Outgoing
        .Find(b.And(
            b.Eq(x => x.OwnerId, MongoConstants.AnyNode),
            b.Eq(x => x.Destination, destination.ToString())))
        .Limit(_options.Durability.RecoveryBatchSize)
        .ToListAsync();
    return docs.Select(x => x.Read()).ToList();
}
```

- [ ] **Step 4: Amend `outbox_recovery.cs`** — both existing tests assert recovery results through `LoadOutgoingAsync`, which now (correctly) returns nothing once the envelope is owned. Switch the post-recovery assertions to `Admin.AllOutgoingAsync()`:

In `recovers_orphaned_outgoing_by_reassigning_to_this_node`, replace

```csharp
var after = await store.Outbox.LoadOutgoingAsync(destination);
var nodeNumber = runtime.DurabilitySettings.AssignedNodeNumber;
after.Single().OwnerId.ShouldBe(nodeNumber);
nodeNumber.ShouldNotBe(0);
```

with

```csharp
var after = await store.Admin.AllOutgoingAsync();
var nodeNumber = runtime.DurabilitySettings.AssignedNodeNumber;
after.Single().OwnerId.ShouldBe(nodeNumber);
nodeNumber.ShouldNotBe(0);

// And the recovery feed itself must now be empty: the envelope is owned.
(await store.Outbox.LoadOutgoingAsync(destination)).ShouldBeEmpty();
```

In `recovery_discards_expired_outgoing`, replace the final assertion with:

```csharp
(await store.Admin.AllOutgoingAsync()).Count.ShouldBe(0);
```

- [ ] **Step 5: Run the full outbox test set**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~outbox"`
Expected: PASS (new test + both amended recovery tests).

- [ ] **Step 6: Commit**

```bash
rtk git add src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Outbox.cs src/Wolverine.MongoDB.Tests/outbox.cs src/Wolverine.MongoDB.Tests/outbox_recovery.cs
rtk git commit -m "fix: LoadOutgoingAsync only returns globally-owned envelopes, batch-limited"
```

---

### Task 2: Handled markers must carry `KeepUntil` so the TTL index can expire them

`IncomingMessage`'s constructor drops `envelope.KeepUntil` (`DateTimeOffset?`). Handled markers stored through `StoreIncomingAsync` (Wolverine core's lazy path) and `MongoDbEnvelopeTransaction.PersistIncomingAsync` (eager path) therefore never expire → unbounded `wolverine_incoming_envelopes` growth.

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/IncomingMessage.cs:11-22` (ctor) and `:35-59` (`Read()`)
- Test: `src/Wolverine.MongoDB.Tests/inbox.cs` (add test)
- Modify: `src/Wolverine.MongoDB.Tests/eager_idempotency_transaction.cs` (add end-of-test assertion)

- [ ] **Step 1: Write the failing test** — add to `src/Wolverine.MongoDB.Tests/inbox.cs`:

```csharp
[Fact]
public async Task handled_marker_stored_via_store_incoming_carries_keep_until()
{
    await _fixture.ClearAll();
    var store = _fixture.BuildMessageStore();

    var original = ObjectMother.Envelope();
    original.Destination = new Uri("local://keep-until");

    // This is exactly what Wolverine core's MessageContext does on the lazy
    // handled path (MessageContext.cs:123-126): ForPersistedHandled sets KeepUntil,
    // then the envelope goes through plain StoreIncomingAsync.
    var handled = Envelope.ForPersistedHandled(
        original, DateTimeOffset.UtcNow, new WolverineOptions().Durability);
    await store.Inbox.StoreIncomingAsync(handled);

    var doc = await store.Incoming
        .Find(Builders<IncomingMessage>.Filter.Eq(x => x.EnvelopeId, original.Id))
        .SingleAsync();

    doc.Status.ShouldBe(EnvelopeStatus.Handled);
    doc.KeepUntil.ShouldNotBeNull(
        "a handled marker without KeepUntil is never removed by the TTL index");
}
```

(Requires `using MongoDB.Driver;` and `using Wolverine.MongoDB.Internals;` — already present in `inbox.cs`.)

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~handled_marker_stored_via_store_incoming_carries_keep_until"`
Expected: FAIL — `doc.KeepUntil` is null.

- [ ] **Step 3: Map `KeepUntil` in the constructor and `Read()`** — in `IncomingMessage.cs`, add to the envelope constructor (after the `ReceivedAt` assignment):

```csharp
KeepUntil = envelope.KeepUntil;
```

and in `Read()`, after `envelope.ScheduledTime = ExecutionTime;` add:

```csharp
envelope.KeepUntil = KeepUntil;
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~handled_marker_stored_via_store_incoming_carries_keep_until"`
Expected: PASS

- [ ] **Step 5: Guard the eager path too** — in `eager_idempotency_transaction.cs`, after the existing `stored.ShouldNotBeNull();` at the end of the test, add:

```csharp
// Regression guard: the handled marker persisted by the eager idempotency check
// must carry KeepUntil, otherwise it is never expired by the TTL index.
var incomingCollection = client
    .GetDatabase(AppFixture.DatabaseName)
    .GetCollection<IncomingMessage>(MongoConstants.IncomingCollection);
var marker = await incomingCollection
    .Find(Builders<IncomingMessage>.Filter.Eq(x => x.EnvelopeId, incoming.Id))
    .SingleAsync();
marker.KeepUntil.ShouldNotBeNull();
```

- [ ] **Step 6: Run the full inbox + eager suite, then commit**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~inbox|FullyQualifiedName~eager_idempotency"`
Expected: PASS

```bash
rtk git add src/Wolverine.MongoDB/Internals/IncomingMessage.cs src/Wolverine.MongoDB.Tests/inbox.cs src/Wolverine.MongoDB.Tests/eager_idempotency_transaction.cs
rtk git commit -m "fix: handled inbox markers carry KeepUntil so the TTL index expires them"
```

---

### Task 3: Dead-letter expiration must honor `DeadLetterQueueExpirationEnabled`

Wolverine's `DeadLetterQueueExpirationEnabled` defaults to **false** — RDBMS providers keep dead letters forever unless it is enabled. The Mongo store unconditionally stamps `ExpirationTime` and TTL-deletes every dead letter after 10 days: silent data loss under default settings.

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/DeadLetterMessage.cs:61` (nullable + ignore-if-null)
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Inbox.cs:139-140`
- Test: Create `src/Wolverine.MongoDB.Tests/dead_letter_expiration.cs`

- [ ] **Step 1: Write the failing tests** — create `src/Wolverine.MongoDB.Tests/dead_letter_expiration.cs`:

```csharp
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class dead_letter_expiration
{
    private readonly AppFixture _fixture;
    public dead_letter_expiration(AppFixture fixture) => _fixture = fixture;

    private IMongoCollection<DeadLetterMessage> Dlq
        => _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<DeadLetterMessage>(MongoConstants.DeadLetterCollection);

    [Fact]
    public async Task expiration_disabled_by_default_leaves_dead_letters_unexpirable()
    {
        await _fixture.ClearAll();
        // Default WolverineOptions: DeadLetterQueueExpirationEnabled == false
        var store = _fixture.BuildMessageStore();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("local://dlq-default");
        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("boom"));

        var doc = await Dlq.Find(Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, envelope.Id)).SingleAsync();
        doc.ExpirationTime.ShouldBeNull(
            "with expiration disabled (Wolverine's default) the TTL index must never remove a dead letter");
    }

    [Fact]
    public async Task expiration_enabled_stamps_expiration_time()
    {
        await _fixture.ClearAll();
        var options = new WolverineOptions();
        options.Durability.DeadLetterQueueExpirationEnabled = true;
        options.Durability.DeadLetterQueueExpiration = TimeSpan.FromDays(3);
        var store = new MongoDbMessageStore(_fixture.Client, AppFixture.DatabaseName, options);

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("local://dlq-enabled");
        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("boom"));

        var doc = await Dlq.Find(Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, envelope.Id)).SingleAsync();
        doc.ExpirationTime.ShouldNotBeNull();
        doc.ExpirationTime!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(2));
    }
}
```

- [ ] **Step 2: Run to verify the first test fails**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~dead_letter_expiration"`
Expected: `expiration_disabled_by_default...` FAILS (ExpirationTime is stamped today); the compile may also fail on `ShouldBeNull()` against a non-nullable `DateTimeOffset` — that is the signal for Step 3's type change.

- [ ] **Step 3: Implement** — in `DeadLetterMessage.cs` change the property to nullable and skip nulls so the TTL index ignores unexpirable docs:

```csharp
[BsonElement("expirationTime")]
[BsonIgnoreIfNull]
public DateTimeOffset? ExpirationTime { get; set; }
```

In `MongoDbMessageStore.Inbox.cs` (`MoveToDeadLetterStorageAsync`), replace

```csharp
dlq.ExpirationTime = envelope.DeliverBy ??
                     DateTimeOffset.UtcNow.Add(_options.Durability.DeadLetterQueueExpiration);
```

with

```csharp
// Wolverine semantics: dead letters are retained forever unless the application
// explicitly opts into expiration. The TTL index skips documents without the field.
if (_options.Durability.DeadLetterQueueExpirationEnabled)
{
    dlq.ExpirationTime = envelope.DeliverBy ??
                         DateTimeOffset.UtcNow.Add(_options.Durability.DeadLetterQueueExpiration);
}
```

- [ ] **Step 4: Run the new tests and the existing DLQ suites**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~dead_letter"`
Expected: PASS (including `dead_letters`, `dead_letter_replay`, `dead_letter_admin_compliance`).

- [ ] **Step 5: Commit**

```bash
rtk git add src/Wolverine.MongoDB/Internals/DeadLetterMessage.cs src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Inbox.cs src/Wolverine.MongoDB.Tests/dead_letter_expiration.cs
rtk git commit -m "fix: dead letters only expire when DeadLetterQueueExpirationEnabled is set"
```

---

### Task 4: Make `ReplayDeadLettersAsync` idempotent and per-document fault-tolerant

A crash between `StoreIncomingAsync` and the DLQ delete leaves a state where every subsequent tick throws `DuplicateIncomingEnvelopeException`, aborts the whole batch, and never converges. Body-less poison dead letters (from `ForUnserializableEnvelope`) would also throw on deserialize and block the loop forever.

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs:19-35`
- Test: `src/Wolverine.MongoDB.Tests/dead_letter_replay.cs` (add tests)

- [ ] **Step 1: Write the failing tests** — add to `src/Wolverine.MongoDB.Tests/dead_letter_replay.cs`:

```csharp
[Fact]
public async Task replay_converges_when_incoming_doc_already_exists()
{
    await _fixture.ClearAll();
    var store = _fixture.BuildMessageStore();

    // Simulate the crash window: the envelope was already re-inserted into incoming
    // by a previous (crashed) replay pass, but the DLQ doc was not yet deleted.
    var stranded = ObjectMother.Envelope();
    stranded.Destination = new Uri("local://replay-crash");
    await store.Inbox.StoreIncomingAsync(stranded);
    await store.Inbox.MoveToDeadLetterStorageAsync(stranded, new InvalidOperationException("boom"));
    await store.DeadLetters.ReplayAsync(
        new DeadLetterEnvelopeQuery { MessageIds = [stranded.Id] }, CancellationToken.None);
    // Pre-insert the incoming doc to recreate the post-crash state.
    stranded.Status = EnvelopeStatus.Incoming;
    stranded.OwnerId = 0;
    await store.Inbox.StoreIncomingAsync(stranded);

    // A second replayable letter behind the poisoned one must also be processed.
    var second = ObjectMother.Envelope();
    second.Destination = new Uri("local://replay-crash");
    await store.Inbox.StoreIncomingAsync(second);
    await store.Inbox.MoveToDeadLetterStorageAsync(second, new InvalidOperationException("boom2"));
    await store.DeadLetters.ReplayAsync(
        new DeadLetterEnvelopeQuery { MessageIds = [second.Id] }, CancellationToken.None);

    await store.ReplayDeadLettersAsync(CancellationToken.None);

    // Both DLQ docs are gone, both envelopes are back in incoming, no exception escaped.
    (await store.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(), CancellationToken.None))
        .TotalCount.ShouldBe(0);
    var counts = await store.Admin.FetchCountsAsync();
    counts.Incoming.ShouldBe(2);
}

[Fact]
public async Task replay_skips_and_unflags_bodyless_poison_dead_letters()
{
    await _fixture.ClearAll();
    var store = _fixture.BuildMessageStore();

    var dlqCollection = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
        .GetCollection<DeadLetterMessage>(MongoConstants.DeadLetterCollection);
    await dlqCollection.InsertOneAsync(new DeadLetterMessage
    {
        Id = Guid.NewGuid(),
        MessageType = "poison",
        Replayable = true,
        Body = []
    });

    await store.ReplayDeadLettersAsync(CancellationToken.None);

    // The body-less letter cannot be replayed: it stays in the DLQ but is unflagged
    // so the loop does not retry it on every tick.
    var doc = await dlqCollection.Find(FilterDefinition<DeadLetterMessage>.Empty).SingleAsync();
    doc.Replayable.ShouldBeFalse();
}
```

(Required usings in `dead_letter_replay.cs`: `MongoDB.Driver`, `Wolverine.MongoDB.Internals`, `Wolverine.Persistence.Durability.DeadLetterManagement`, `Wolverine.ComplianceTests` — add any missing.)

- [ ] **Step 2: Run to verify both fail**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~dead_letter_replay"`
Expected: first new test FAILS with `DuplicateIncomingEnvelopeException`; second FAILS on deserialize.

- [ ] **Step 3: Rewrite `ReplayDeadLettersAsync`** in `MongoDbMessageStore.Durability.cs`:

```csharp
internal async Task ReplayDeadLettersAsync(CancellationToken token)
{
    var replayable = await DeadLetterDocs
        .Find(Builders<DeadLetterMessage>.Filter.Eq(x => x.Replayable, true))
        .ToListAsync(token);

    foreach (var doc in replayable)
    {
        if (doc.Body is not { Length: > 0 })
        {
            // A poison dead letter without a serialized body cannot be replayed.
            // Unflag it (instead of failing every tick) and leave it queryable.
            await DeadLetterDocs.UpdateOneAsync(
                Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, doc.Id),
                Builders<DeadLetterMessage>.Update.Set(x => x.Replayable, false),
                cancellationToken: token);
            continue;
        }

        var envelope = EnvelopeSerializer.Deserialize(doc.Body);
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = MongoConstants.AnyNode;

        try
        {
            await StoreIncomingAsync(envelope);
        }
        catch (DuplicateIncomingEnvelopeException)
        {
            // A previous pass (or a competing node) already re-inserted this envelope
            // and crashed before deleting the DLQ doc. Fall through: removing the
            // DLQ doc below is what converges the replay.
        }

        await DeadLetterDocs.DeleteOneAsync(
            Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, doc.Id), token);
    }
}
```

Add `using Wolverine.Persistence.Durability;` to the file if not already present (for `DuplicateIncomingEnvelopeException`).

- [ ] **Step 4: Run the replay suite**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~dead_letter_replay"`
Expected: PASS (new tests + the pre-existing replay test).

- [ ] **Step 5: Commit**

```bash
rtk git add src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs src/Wolverine.MongoDB.Tests/dead_letter_replay.cs
rtk git commit -m "fix: dead-letter replay converges after crashes and skips body-less poison letters"
```

---

### Task 5: CI — run the library suite against a pinned Wolverine clone; run the demo against the freshly packed nupkg

Today CI runs zero library tests and the demo tests exercise the *previously published* package. After this task, every PR runs the full compliance suite and the demo consumes the nupkg packed from the same commit.

**Files:**
- Modify: `.github/workflows/ci.yml` (full rewrite below)

- [ ] **Step 1: Verify the Wolverine tag exists** (the pin must match `WolverineFx 6.2.2` from `Directory.Packages.props`):

Run: `git ls-remote --tags https://github.com/JasperFx/wolverine 6.2.2`
Expected: one ref line. If empty, list nearby tags with `git ls-remote --tags https://github.com/JasperFx/wolverine "6.2.*"` and pin the closest tag ≥ 6.2.2 instead (record the choice in the commit message).

- [ ] **Step 2: Replace `.github/workflows/ci.yml`** with:

```yaml
name: CI
on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

env:
  WOLVERINE_TAG: "6.2.2"   # keep in sync with WolverineFx in Directory.Packages.props
  CI_PACKAGE_VERSION: "0.0.0-ci"

jobs:
  # ---------- Library build + compliance tests + pack ----------
  library:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6

      - name: Checkout Wolverine source (compliance test harness)
        uses: actions/checkout@v6
        with:
          repository: JasperFx/wolverine
          ref: ${{ env.WOLVERINE_TAG }}
          path: external/wolverine

      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: |
            9.0.x
            10.0.x

      - name: Run library tests (project-refs the Wolverine clone)
        env:
          WOLVERINE_SOURCE: ${{ github.workspace }}/external/wolverine
        run: >
          dotnet test src/Wolverine.MongoDB.Tests/Wolverine.MongoDB.Tests.csproj
          -c Release -p:UseWolverineSource=true
          --logger "GitHubActions"

      - name: Pack (declares the WolverineFx package dependency)
        run: >
          dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj
          -c Release -o ./artifacts
          -p:UseWolverineSource=false -p:Version=${{ env.CI_PACKAGE_VERSION }}

      - name: Upload package artifact
        uses: actions/upload-artifact@v4
        with:
          name: nupkg
          path: ./artifacts/*.nupkg

  # ---------- Demo against the freshly packed nupkg ----------
  demo:
    runs-on: ubuntu-latest
    needs: library
    steps:
      - uses: actions/checkout@v6

      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: '9.0.x'

      - name: Download freshly packed nupkg
        uses: actions/download-artifact@v4
        with:
          name: nupkg
          path: ./artifacts

      - name: Point the demo at the CI package
        run: |
          dotnet nuget add source "$GITHUB_WORKSPACE/artifacts" --name local-ci --configfile demo/nuget.config
          sed -i 's|<PackageVersion Include="Wolverine.MongoDB" Version="[^"]*"|<PackageVersion Include="Wolverine.MongoDB" Version="${{ env.CI_PACKAGE_VERSION }}"|' demo/Directory.Packages.props

      - name: Build demo solution
        working-directory: demo
        run: dotnet build OrderDemo.slnx -c Release

      - name: Run demo integration tests
        working-directory: demo
        run: dotnet test tests/OrderDemo.IntegrationTests/OrderDemo.IntegrationTests.csproj -c Release --logger "console;verbosity=normal"
```

Notes for the implementer: `Directory.Build.props:25` picks up the `WOLVERINE_SOURCE` env var for `WolverineSourcePath`, so no csproj change is needed. Testcontainers works on `ubuntu-latest` out of the box (the demo job already proves it).

- [ ] **Step 3: Validate locally what can be validated** — the sed expression and nuget source addition:

Run (PowerShell, from repo root):
```powershell
dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -o ./artifacts -p:UseWolverineSource=false -p:Version=0.0.0-ci
```
Expected: `Wolverine.MongoDB.0.0.0-ci.nupkg` in `./artifacts`.

- [ ] **Step 4: Commit, open the PR, and verify on GitHub**

```bash
rtk git add .github/workflows/ci.yml
rtk git commit -m "ci: run library compliance suite against pinned Wolverine clone; demo consumes fresh nupkg"
```

Then follow the per-task PR procedure from the workflow section (`rtk git push -u ...`, `rtk gh pr create ...`) and watch the checks with `rtk gh pr checks --watch` until both jobs are green. Iterate here until they are — this task is not done until CI is green on the PR.

---

### Task 6: Fail fast on `DurabilityMode.Balanced`; fix the misleading recovery comment

Wolverine defaults to `Balanced`. This store is only safe in `Solo` (and trivially in `MediatorOnly`/`Serverless`). A consumer who forgets `opts.Durability.Mode = DurabilityMode.Solo` currently gets a subtly broken cluster. Throw at startup instead. (The Multinode plan removes this gate later.)

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs:58` (`Initialize`) and `:64-66` (`StartScheduledJobs`/`BuildAgent`)
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs:64-70` (comment fix)
- Test: Create `src/Wolverine.MongoDB.Tests/durability_mode_guard.cs`

- [ ] **Step 1: Write the failing test** — create `src/Wolverine.MongoDB.Tests/durability_mode_guard.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class durability_mode_guard
{
    private readonly AppFixture _fixture;
    public durability_mode_guard(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task balanced_mode_fails_fast_at_startup()
    {
        await _fixture.ClearAll();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // Balanced is Wolverine's DEFAULT mode — not setting Solo must fail loudly,
                    // not run a subtly broken cluster.
                    opts.Durability.Mode = DurabilityMode.Balanced;
                    opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                    opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                }).StartAsync();
        });

        ex.Message.ShouldContain("DurabilityMode.Solo");
    }

    [Fact]
    public async Task solo_mode_starts_normally()
    {
        await _fixture.ClearAll();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync();
        host.ShouldNotBeNull();
    }
}
```

- [ ] **Step 2: Run to verify the first test fails** (host currently starts fine in Balanced)

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~durability_mode_guard"`
Expected: `balanced_mode_fails_fast_at_startup` FAILS (no exception thrown).

- [ ] **Step 3: Implement the guard** — in `MongoDbMessageStore.cs`, replace the empty `Initialize` and route the agent factories through the same check:

```csharp
public void Initialize(IWolverineRuntime runtime) => AssertSupportedDurabilityMode(runtime);

public IAgent StartScheduledJobs(IWolverineRuntime runtime)
{
    AssertSupportedDurabilityMode(runtime);
    return BuildAgent(runtime);
}

public IAgent BuildAgent(IWolverineRuntime runtime)
{
    AssertSupportedDurabilityMode(runtime);
    return new MongoDbDurabilityAgent(runtime, this);
}

private static void AssertSupportedDurabilityMode(IWolverineRuntime runtime)
{
    if (runtime.Options.Durability.Mode == DurabilityMode.Balanced)
    {
        throw new InvalidOperationException(
            "Wolverine.MongoDB currently supports single-node durability only. " +
            "Set opts.Durability.Mode = DurabilityMode.Solo. " +
            "Multi-node (Balanced) support is tracked in FOLLOWUPS.md.");
    }
}
```

The check lives in *both* `Initialize` and the agent factories because `Initialize` timing differs across Wolverine versions; the agent factories are guaranteed to run before any durability work starts. If the test still fails after this change, the runtime is not calling either path before `StartAsync` returns — in that case also add the same `AssertSupportedDurabilityMode` call at the top of `MongoDbDurabilityAgent.StartAsync`.

- [ ] **Step 4: Fix the misleading comment** — in `MongoDbMessageStore.Durability.cs` (`PublishDueScheduledMessagesAsync`), replace the sentence

> `A crash after the flip but before enqueue leaves the doc Incoming owned by this node, which the incoming orphan-recovery loop re-picks — it is never silently stranded.`

with

```
A crash after the flip but before enqueue leaves the doc Incoming owned by this
node's number. The orphan-recovery loop only matches OwnerId == AnyNode, so the
doc is NOT re-picked while owned; it is rescued at the next Solo-mode startup,
which releases all ownership (NodeAgentController.StartLocally). Balanced-mode
recovery of this window is part of the multinode plan.
```

- [ ] **Step 5: Sweep test hosts for missing Solo mode** — every `UseWolverine` host in the test project must set Solo or it now fails:

Run: `rtk grep "UseWolverine" src/Wolverine.MongoDB.Tests --files-with-matches` then check each hit contains `DurabilityMode.Solo`. Add `opts.Durability.Mode = DurabilityMode.Solo;` where missing (known candidates: `end_to_end.cs`, `reassign_incoming.cs`, `scheduled_messages.cs`, `node_heartbeat.cs`, `inbox_identity.cs`, `admin_smoke.cs`, compliance subclasses).

- [ ] **Step 6: Run the FULL library suite** (the guard can break any host-based test)

Run: `dotnet test src/Wolverine.MongoDB.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
rtk git add -A src/Wolverine.MongoDB src/Wolverine.MongoDB.Tests
rtk git commit -m "feat: fail fast on DurabilityMode.Balanced until multinode support lands"
```

---

### Task 7: Replace the process-global `DateTimeOffset` serializer with per-property representations

A library must not mutate the process-wide BSON registry (it changes how the *host app's* `DateTimeOffset` fields serialize). `[BsonRepresentation(BsonType.DateTime)]` on our own document properties produces the identical on-disk format (UTC BSON Date), so no data migration is needed. The existing `datetime_serialization.cs` tests are the regression guard.

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/IncomingMessage.cs`, `OutgoingMessage.cs`, `DeadLetterMessage.cs`, `NodeDocument.cs`, `NodeRecordDocument.cs`
- Delete: `src/Wolverine.MongoDB/Internals/MongoSerializerRegistration.cs`
- Modify: `src/Wolverine.MongoDB/WolverineMongoDbExtensions.cs:45` (remove `MongoSerializerRegistration.Register();` and its comment)

- [ ] **Step 1: Add `[BsonRepresentation(BsonType.DateTime)]` to every `DateTimeOffset`/`DateTimeOffset?` document property.** Complete list (do not miss one — a missed property silently reverts to the `[ticks, offset]` array format and breaks TTL/range queries on existing data):

| File | Properties |
|---|---|
| `IncomingMessage.cs` | `ExecutionTime`, `KeepUntil` |
| `OutgoingMessage.cs` | `DeliverBy` |
| `DeadLetterMessage.cs` | `SentAt`, `ScheduledTime`, `ExpirationTime` |
| `NodeDocument.cs` | `Started` |
| `NodeRecordDocument.cs` | `Timestamp` |

Example (pattern is identical for each):

```csharp
[BsonElement("keepUntil")]
[BsonRepresentation(BsonType.DateTime)]
public DateTimeOffset? KeepUntil { get; set; }
```

(`LockDocument.ExpiresAt` and `NodeDocument.LastHealthCheck` are `DateTime` — BSON Date by default, no change.)

- [ ] **Step 2: Delete the global registration** — delete `src/Wolverine.MongoDB/Internals/MongoSerializerRegistration.cs`, and in `WolverineMongoDbExtensions.UseMongoDbPersistence` remove the `MongoSerializerRegistration.Register();` line and the two comment lines above it.

- [ ] **Step 3: Run the serialization regression tests + full suite** — these tests assert raw BSON types (`doc["keepUntil"].BsonType == BsonType.DateTime`), which is exactly the contract this task must preserve:

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~datetime_serialization"`
Expected: PASS. Then run the full suite: `dotnet test src/Wolverine.MongoDB.Tests` — Expected: PASS.

- [ ] **Step 4: Commit**

```bash
rtk git add -A src/Wolverine.MongoDB src/Wolverine.MongoDB.Tests
rtk git commit -m "refactor: per-property BSON DateTime representation instead of process-global serializer"
```

---

### Task 8: Broaden `CanApply` so collection-, client-, and session-typed handlers get transactions

Today only handlers whose dependency tree contains `IMongoDatabase` get the transactional frame. A handler injecting `IMongoCollection<T>` silently runs **without** a transaction; a handler declaring only `IClientSessionHandle` fails code generation.

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs:34-40`
- Test: Create `src/Wolverine.MongoDB.Tests/transaction_frame_application.cs`

- [ ] **Step 1: Write the failing test** — create `src/Wolverine.MongoDB.Tests/transaction_frame_application.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;

namespace Wolverine.MongoDB.Tests;

public class FrameTestDoc
{
    public string Id { get; set; } = string.Empty;
}

public record CollectionOnlyCommand(Guid Id);
public record SessionOnlyCommand(Guid Id);

public static class CollectionOnlyHandler
{
    // Depends on IMongoCollection<T> (registered in DI below), NOT IMongoDatabase.
    // Must still receive the transactional frame + session.
    public static Task Handle(CollectionOnlyCommand cmd, IMongoCollection<FrameTestDoc> docs,
        IClientSessionHandle session, CancellationToken ct)
        => docs.InsertOneAsync(session, new FrameTestDoc { Id = cmd.Id.ToString() }, cancellationToken: ct);
}

public static class SessionOnlyHandler
{
    // Declares ONLY the session. Without the frame this fails codegen
    // (no variable can supply IClientSessionHandle).
    public static Task Handle(SessionOnlyCommand cmd, IClientSessionHandle session)
        => Task.CompletedTask;
}

[Collection("mongodb")]
public class transaction_frame_application
{
    private readonly AppFixture _fixture;
    public transaction_frame_application(AppFixture fixture) => _fixture = fixture;

    private async Task<IHost> BuildHost()
    {
        await _fixture.ClearAll();
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(sp =>
                    sp.GetRequiredService<IMongoDatabase>().GetCollection<FrameTestDoc>("frame_test_docs"));
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.IncludeType(typeof(CollectionOnlyHandler));
                opts.Discovery.IncludeType(typeof(SessionOnlyHandler));
            }).StartAsync();
    }

    [Fact]
    public async Task handler_with_collection_dependency_gets_a_transaction_and_writes()
    {
        using var host = await BuildHost();
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var id = Guid.NewGuid();

        await bus.InvokeAsync(new CollectionOnlyCommand(id));

        var collection = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<FrameTestDoc>("frame_test_docs");
        var doc = await collection.Find(x => x.Id == id.ToString()).FirstOrDefaultAsync();
        doc.ShouldNotBeNull();
    }

    [Fact]
    public async Task handler_with_only_session_parameter_compiles_and_runs()
    {
        using var host = await BuildHost();
        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.InvokeAsync(new SessionOnlyCommand(Guid.NewGuid()));
    }
}
```

- [ ] **Step 2: Run to verify failures**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~transaction_frame_application"`
Expected: the collection test FAILS (no session variable → codegen error, since the frame is not applied and nothing supplies `IClientSessionHandle`); the session-only test FAILS the same way.

- [ ] **Step 3: Implement `CanApply`** in `MongoDbPersistenceFrameProvider.cs`:

```csharp
public bool CanApply(IChain chain, IServiceContainer container)
{
    // Handlers that declare the session (or the unit-of-work helper, Task 9) as a
    // method parameter explicitly opt into the transaction.
    if (chain.HandlerCalls().Any(call => call.Method.GetParameters().Any(p =>
            p.ParameterType == typeof(IClientSessionHandle))))
    {
        return true;
    }

    var serviceDependencies = chain
        .ServiceDependencies(container, new[] { typeof(IMongoDatabase), typeof(IMongoClient) })
        .ToArray();

    return serviceDependencies.Any(x =>
        x == typeof(IMongoDatabase)
        || x == typeof(IMongoClient)
        || (x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IMongoCollection<>)));
}
```

Add `using Wolverine.Runtime.Handlers;` if `HandlerCalls()` requires it. If `IChain` in this Wolverine version does not expose `HandlerCalls()`, use `chain.As<HandlerChain>().Handlers` for handler chains and fall back to the dependency scan for non-handler chains — verify against the Wolverine clone (`C:\source\external\wolverine\src\Wolverine\Configuration\IChain.cs`) before improvising.

- [ ] **Step 4: Run the new tests + full suite, then commit**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~transaction_frame_application"` → PASS, then `dotnet test src/Wolverine.MongoDB.Tests` → PASS.

```bash
rtk git add src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs src/Wolverine.MongoDB.Tests/transaction_frame_application.cs
rtk git commit -m "fix: apply transactions to handlers using IMongoCollection<T>, IMongoClient, or IClientSessionHandle"
```

---

### Task 9: `MongoDbUnitOfWork` — session-bound write helper

The sharpest consumer edge: forgetting to pass the session compiles, runs, and silently breaks atomicity. `MongoDbUnitOfWork` binds the session to every write so it *cannot* be forgotten. Handlers take it as a parameter instead of (or alongside) `IClientSessionHandle`.

**Files:**
- Create: `src/Wolverine.MongoDB/MongoDbUnitOfWork.cs`
- Modify: `src/Wolverine.MongoDB/Internals/TransactionalFrame.cs` (emit the variable)
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs` (`CanApply` recognizes it)
- Test: Create `src/Wolverine.MongoDB.Tests/unit_of_work.cs`

- [ ] **Step 1: Write the failing test** — create `src/Wolverine.MongoDB.Tests/unit_of_work.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;

namespace Wolverine.MongoDB.Tests;

public class UowDoc
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public record UowWriteCommand(Guid Id, string Name);
public record UowFailingCommand(Guid Id);

public static class UowWriteHandler
{
    public static Task Handle(UowWriteCommand cmd, MongoDbUnitOfWork uow, CancellationToken ct)
        => uow.Collection<UowDoc>("uow_docs")
            .InsertOneAsync(new UowDoc { Id = cmd.Id.ToString(), Name = cmd.Name }, ct);
}

public static class UowFailingHandler
{
    public static async Task Handle(UowFailingCommand cmd, MongoDbUnitOfWork uow, CancellationToken ct)
    {
        await uow.Collection<UowDoc>("uow_docs")
            .InsertOneAsync(new UowDoc { Id = cmd.Id.ToString(), Name = "doomed" }, ct);
        throw new InvalidOperationException("fail after write");
    }
}

[Collection("mongodb")]
public class unit_of_work
{
    private readonly AppFixture _fixture;
    public unit_of_work(AppFixture fixture) => _fixture = fixture;

    private async Task<IHost> BuildHost()
    {
        await _fixture.ClearAll();
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.IncludeType(typeof(UowWriteHandler));
                opts.Discovery.IncludeType(typeof(UowFailingHandler));
            }).StartAsync();
    }

    private IMongoCollection<UowDoc> Docs => _fixture.Client
        .GetDatabase(AppFixture.DatabaseName).GetCollection<UowDoc>("uow_docs");

    [Fact]
    public async Task writes_through_unit_of_work_commit_with_the_handler()
    {
        using var host = await BuildHost();
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var id = Guid.NewGuid();

        await bus.InvokeAsync(new UowWriteCommand(id, "hello"));

        (await Docs.Find(x => x.Id == id.ToString()).FirstOrDefaultAsync()).ShouldNotBeNull();
    }

    [Fact]
    public async Task writes_through_unit_of_work_roll_back_when_the_handler_throws()
    {
        using var host = await BuildHost();
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var id = Guid.NewGuid();

        await Should.ThrowAsync<InvalidOperationException>(
            () => bus.InvokeAsync(new UowFailingCommand(id)));

        // The write went through the session-bound collection, so the abort
        // must have rolled it back.
        (await Docs.Find(x => x.Id == id.ToString()).FirstOrDefaultAsync()).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify it fails to compile** (no `MongoDbUnitOfWork` type yet)

Run: `dotnet build src/Wolverine.MongoDB.Tests`
Expected: compile error `CS0246: MongoDbUnitOfWork`.

- [ ] **Step 3: Create the public API** — `src/Wolverine.MongoDB/MongoDbUnitOfWork.cs`:

```csharp
using MongoDB.Driver;

namespace Wolverine.MongoDB;

/// <summary>
/// Session-bound access to the application's MongoDB database inside a
/// Wolverine-managed outbox transaction. Every write performed through
/// <see cref="Collection{T}"/> automatically participates in the handler's
/// transaction — it is impossible to forget the session.
/// Handlers receive this as a parameter (the Wolverine.MongoDB code generation
/// constructs it from the open <see cref="IClientSessionHandle"/>).
/// </summary>
public class MongoDbUnitOfWork
{
    public MongoDbUnitOfWork(IClientSessionHandle session, IMongoDatabase database)
    {
        Session = session;
        Database = database;
    }

    /// <summary>The open session/transaction for this handler invocation.</summary>
    public IClientSessionHandle Session { get; }

    /// <summary>The raw database. Writes performed directly on this handle do NOT enlist.</summary>
    public IMongoDatabase Database { get; }

    public SessionBoundCollection<T> Collection<T>(string name)
        => new(Database.GetCollection<T>(name), Session);
}

/// <summary>
/// A write surface over <see cref="IMongoCollection{T}"/> that passes the
/// transaction session to every operation. Intentionally NOT an
/// IMongoCollection&lt;T&gt; implementation: only session-safe operations are exposed.
/// </summary>
public class SessionBoundCollection<T>
{
    private readonly IMongoCollection<T> _collection;
    private readonly IClientSessionHandle _session;

    public SessionBoundCollection(IMongoCollection<T> collection, IClientSessionHandle session)
    {
        _collection = collection;
        _session = session;
    }

    public Task InsertOneAsync(T document, CancellationToken ct = default)
        => _collection.InsertOneAsync(_session, document, cancellationToken: ct);

    public Task InsertManyAsync(IEnumerable<T> documents, CancellationToken ct = default)
        => _collection.InsertManyAsync(_session, documents, cancellationToken: ct);

    public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<T> filter, T replacement,
        ReplaceOptions? options = null, CancellationToken ct = default)
        => _collection.ReplaceOneAsync(_session, filter, replacement, options, ct);

    public Task<UpdateResult> UpdateOneAsync(FilterDefinition<T> filter, UpdateDefinition<T> update,
        UpdateOptions? options = null, CancellationToken ct = default)
        => _collection.UpdateOneAsync(_session, filter, update, options, ct);

    public Task<UpdateResult> UpdateManyAsync(FilterDefinition<T> filter, UpdateDefinition<T> update,
        UpdateOptions? options = null, CancellationToken ct = default)
        => _collection.UpdateManyAsync(_session, filter, update, options, ct);

    public Task<DeleteResult> DeleteOneAsync(FilterDefinition<T> filter, CancellationToken ct = default)
        => _collection.DeleteOneAsync(_session, filter, cancellationToken: ct);

    public Task<DeleteResult> DeleteManyAsync(FilterDefinition<T> filter, CancellationToken ct = default)
        => _collection.DeleteManyAsync(_session, filter, cancellationToken: ct);

    public Task<T> FindOneAndUpdateAsync(FilterDefinition<T> filter, UpdateDefinition<T> update,
        FindOneAndUpdateOptions<T>? options = null, CancellationToken ct = default)
        => _collection.FindOneAndUpdateAsync(_session, filter, update, options, ct);

    /// <summary>Transaction-consistent reads (sees this transaction's own writes).</summary>
    public IFindFluent<T, T> Find(FilterDefinition<T> filter)
        => _collection.Find(_session, filter);

    public IFindFluent<T, T> Find(System.Linq.Expressions.Expression<Func<T, bool>> filter)
        => _collection.Find(_session, filter);
}
```

- [ ] **Step 4: Emit the variable from the frame** — in `TransactionalFrame.cs`:

In the constructor, after `Session = ...`, add:

```csharp
UnitOfWork = new Variable(typeof(MongoDbUnitOfWork), "mongoUnitOfWork", this);
```

Add the property next to `Session`:

```csharp
public Variable UnitOfWork { get; }
```

In `FindVariables`, after the `_client` resolution, add:

```csharp
_database = chain.FindVariable(typeof(IMongoDatabase));
yield return _database;
```

(with a new field `private Variable? _database;`).

In `GenerateCode`, immediately after the `StartTransaction()` line, add:

```csharp
writer.Write(
    $"var {UnitOfWork.Usage} = new {typeof(MongoDbUnitOfWork).FullNameInCode()}({Session.Usage}, {_database!.Usage});");
```

Add `using Wolverine.MongoDB;` (or fully qualify) for the type reference.

- [ ] **Step 5: Teach `CanApply` about the new parameter type** — in the `HandlerCalls` check from Task 8, extend the predicate:

```csharp
p.ParameterType == typeof(IClientSessionHandle) || p.ParameterType == typeof(MongoDbUnitOfWork)
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~unit_of_work"`
Expected: PASS (both commit and rollback tests). Then full suite: `dotnet test src/Wolverine.MongoDB.Tests` → PASS.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Wolverine.MongoDB/MongoDbUnitOfWork.cs src/Wolverine.MongoDB/Internals/TransactionalFrame.cs src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs src/Wolverine.MongoDB.Tests/unit_of_work.cs
rtk git commit -m "feat: MongoDbUnitOfWork session-bound write helper for handlers"
```

---

### Task 10: Pin majority write/read concerns on the store's database handle

If the consumer's `MongoClient` is configured `w:1`, the "durable" inbox write can be acknowledged before replication and lost on failover. Pin `w:majority` + journaled writes and majority reads on the store's own handle (the app-facing `IMongoDatabase` registration stays untouched — domain write concerns are the app's choice).

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs:28` (ctor)
- Test: Create `src/Wolverine.MongoDB.Tests/durability_concerns.cs`

- [ ] **Step 1: Write the failing test** — create `src/Wolverine.MongoDB.Tests/durability_concerns.cs`:

```csharp
using MongoDB.Driver;
using Shouldly;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class durability_concerns
{
    private readonly AppFixture _fixture;
    public durability_concerns(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public void store_collections_use_majority_journaled_write_concern_and_majority_reads()
    {
        var store = _fixture.BuildMessageStore();

        var expectedWrite = WriteConcern.WMajority.With(journal: true);
        store.Incoming.Settings.WriteConcern.ShouldBe(expectedWrite);
        store.Outgoing.Settings.WriteConcern.ShouldBe(expectedWrite);
        store.DeadLetterDocs.Settings.WriteConcern.ShouldBe(expectedWrite);

        store.Incoming.Settings.ReadConcern.ShouldBe(ReadConcern.Majority);
        store.Outgoing.Settings.ReadConcern.ShouldBe(ReadConcern.Majority);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Wolverine.MongoDB.Tests --filter "FullyQualifiedName~durability_concerns"`
Expected: FAIL — collections inherit the client default (`w:1` acknowledged or unset).

- [ ] **Step 3: Implement** — in the `MongoDbMessageStore` constructor, replace

```csharp
_database = client.GetDatabase(databaseName);
```

with

```csharp
// The message store's writes ARE the durability guarantee: pin majority +
// journaled acknowledgement and majority reads regardless of how the consumer
// configured their MongoClient. The app-facing IMongoDatabase registered by
// UseMongoDbPersistence is intentionally NOT pinned — domain write concerns
// belong to the application.
_database = client.GetDatabase(databaseName)
    .WithWriteConcern(WriteConcern.WMajority.With(journal: true))
    .WithReadConcern(ReadConcern.Majority);
```

All collection handles (`Incoming`, `Outgoing`, `DeadLetterDocs`, and the lazy node/lock collection properties) derive from `_database`, so they inherit the settings.

- [ ] **Step 4: Run the new test + full suite** (TTL/transactions on the single-node replica-set container must still pass)

Run: `dotnet test src/Wolverine.MongoDB.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs src/Wolverine.MongoDB.Tests/durability_concerns.cs
rtk git commit -m "fix: pin majority/journaled write concern and majority read concern on the message store"
```

---

### Task 11: Index tuning + server-side aggregation for summaries

Compound `(Status, ExecutionTime)` for the scheduled poll, `EnvelopeId` for reassignment/reschedule filters, `(OwnerId, Destination)` serving the fixed `LoadOutgoingAsync`, TTL on node records; convert the two load-everything summaries to `$group` pipelines.

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Admin.cs:18-49` (`EnsureIndexesAsync`)
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.DeadLetters.cs:34-52` (`SummarizeAllAsync`)
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.ScheduledMessages.cs:59-67` (`SummarizeAsync`)
- Test: existing suites (`document_mapping`, `scheduled_*`, `dead_letter_*`) are the behavioral guard; add one index-shape test to `admin_smoke.cs`

- [ ] **Step 1: Update `EnsureIndexesAsync`** in `MongoDbMessageStore.Admin.cs`:

```csharp
private async Task EnsureIndexesAsync()
{
    await Incoming.Indexes.CreateManyAsync(new[]
    {
        new CreateIndexModel<IncomingMessage>(
            Builders<IncomingMessage>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.OwnerId)),
        // Scheduled poll filters Status == Scheduled && ExecutionTime <= now.
        new CreateIndexModel<IncomingMessage>(
            Builders<IncomingMessage>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.ExecutionTime)),
        new CreateIndexModel<IncomingMessage>(
            Builders<IncomingMessage>.IndexKeys.Ascending(x => x.OwnerId).Ascending(x => x.ReceivedAt)),
        // Reassignment CAS and reschedule filter by EnvelopeId.
        new CreateIndexModel<IncomingMessage>(
            Builders<IncomingMessage>.IndexKeys.Ascending(x => x.EnvelopeId)),
        new CreateIndexModel<IncomingMessage>(
            Builders<IncomingMessage>.IndexKeys.Ascending(x => x.KeepUntil),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero })
    });

    await Outgoing.Indexes.CreateManyAsync(new[]
    {
        // Serves LoadOutgoingAsync (OwnerId == 0 && Destination == X) and the
        // distinct-destinations recovery scan.
        new CreateIndexModel<OutgoingMessage>(
            Builders<OutgoingMessage>.IndexKeys.Ascending(x => x.OwnerId).Ascending(x => x.Destination)),
        new CreateIndexModel<OutgoingMessage>(Builders<OutgoingMessage>.IndexKeys.Ascending(x => x.Destination)),
        new CreateIndexModel<OutgoingMessage>(Builders<OutgoingMessage>.IndexKeys.Ascending(x => x.DeliverBy))
    });

    await DeadLetterDocs.Indexes.CreateManyAsync(new[]
    {
        new CreateIndexModel<DeadLetterMessage>(Builders<DeadLetterMessage>.IndexKeys.Ascending(x => x.SentAt)),
        new CreateIndexModel<DeadLetterMessage>(Builders<DeadLetterMessage>.IndexKeys.Ascending(x => x.MessageType)),
        new CreateIndexModel<DeadLetterMessage>(Builders<DeadLetterMessage>.IndexKeys.Ascending(x => x.ExceptionType)),
        new CreateIndexModel<DeadLetterMessage>(Builders<DeadLetterMessage>.IndexKeys.Ascending(x => x.Replayable)),
        new CreateIndexModel<DeadLetterMessage>(
            Builders<DeadLetterMessage>.IndexKeys.Ascending(x => x.ExpirationTime),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero })
    });

    // Node-event records: retain two weeks, then let TTL discard them
    // (FOLLOWUPS: previously unbounded growth).
    await RecordDocs.Indexes.CreateManyAsync(new[]
    {
        new CreateIndexModel<NodeRecordDocument>(
            Builders<NodeRecordDocument>.IndexKeys.Ascending(x => x.Timestamp),
            new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(14) })
    });
}
```

Note: `RecordDocs` is a private property in `MongoDbMessageStore.NodeAgents.cs` — it is visible here because both files are the same partial class. The old single-field `ExecutionTime` and `OwnerId` (outgoing) index definitions are removed; pre-existing deployments keep the stale indexes harmlessly (mention in CHANGELOG, no migration for a beta).

- [ ] **Step 2: Add an index-shape test** to `src/Wolverine.MongoDB.Tests/admin_smoke.cs`:

```csharp
[Fact]
public async Task migrate_creates_the_expected_indexes()
{
    var store = _fixture.BuildMessageStore();
    await store.Admin.RebuildAsync();

    var incomingIndexes = await (await store.Incoming.Indexes.ListAsync()).ToListAsync();
    var names = incomingIndexes.Select(i => i["name"].AsString).ToList();

    names.ShouldContain("status_1_executionTime_1");
    names.ShouldContain("envelopeId_1");
    names.ShouldContain("keepUntil_1");
}
```

(Driver-generated index names follow `field_direction` convention; if the assertion fails on naming, print `names` and adjust to the actual generated names — the point is presence, not naming.)

- [ ] **Step 3: Convert `SummarizeAllAsync`** in `MongoDbMessageStore.DeadLetters.cs` to a `$group` pipeline:

```csharp
public async Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range, CancellationToken token)
{
    var b = Builders<DeadLetterMessage>.Filter;
    var filter = b.Empty;
    if (range.From.HasValue) filter &= b.Gte(x => x.SentAt, range.From.Value);
    if (range.To.HasValue) filter &= b.Lte(x => x.SentAt, range.To.Value);

    var grouped = await DeadLetterDocs.Aggregate()
        .Match(filter)
        .Group(x => new { x.ReceivedAt, x.MessageType, x.ExceptionType },
            g => new { g.Key.ReceivedAt, g.Key.MessageType, g.Key.ExceptionType, Count = g.Count() })
        .ToListAsync(token);

    return grouped
        .Select(g => new DeadLetterQueueCount(
            serviceName,
            g.ReceivedAt.IsNotEmpty() ? new Uri(g.ReceivedAt!) : Uri,
            g.MessageType ?? "",
            g.ExceptionType ?? "",
            Uri,
            g.Count))
        .ToList();
}
```

- [ ] **Step 4: Convert `IScheduledMessages.SummarizeAsync`** in `MongoDbMessageStore.ScheduledMessages.cs`:

```csharp
async Task<IReadOnlyList<ScheduledMessageCount>> IScheduledMessages.SummarizeAsync(string serviceName, CancellationToken token)
{
    var grouped = await Incoming.Aggregate()
        .Match(Builders<IncomingMessage>.Filter.Eq(x => x.Status, EnvelopeStatus.Scheduled))
        .Group(x => x.MessageType, g => new { MessageType = g.Key, Count = g.Count() })
        .ToListAsync(token);

    return grouped
        .Select(g => new ScheduledMessageCount(serviceName, g.MessageType, Uri, g.Count))
        .ToList();
}
```

- [ ] **Step 5: Run the full suite** (compliance suites for DLQ + scheduled messages cover both summaries)

Run: `dotnet test src/Wolverine.MongoDB.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Admin.cs src/Wolverine.MongoDB/Internals/MongoDbMessageStore.DeadLetters.cs src/Wolverine.MongoDB/Internals/MongoDbMessageStore.ScheduledMessages.cs src/Wolverine.MongoDB.Tests/admin_smoke.cs
rtk git commit -m "perf: compound/TTL index tuning and server-side aggregation summaries"
```

---

### Task 12: Documentation sweep

**Files:**
- Modify: `README.md`, `CLAUDE.md`, `FOLLOWUPS.md`, `CHANGELOG.md`, `demo/README.md`, `demo/CLAUDE.md`

- [ ] **Step 1: `README.md`** — add/update these sections (adapt to the existing structure):
  - **Durability mode**: state that `opts.Durability.Mode = DurabilityMode.Solo` is required and that startup now fails fast on `Balanced` (multinode is on the roadmap).
  - **Dead-letter retention**: dead letters are kept forever by default; opting into `DeadLetterQueueExpirationEnabled` activates TTL-based expiry.
  - **Write durability**: document that the message store pins `w:majority` (journaled) + majority reads internally, independent of the consumer's `MongoClient` configuration, and that the app-facing `IMongoDatabase` is not modified.
  - **Session-bound writes**: replace the "thread the `IClientSessionHandle` manually" guidance as primary pattern with `MongoDbUnitOfWork`, including this snippet:

```csharp
public static async Task<OrderPlaced> Handle(PlaceOrder cmd, MongoDbUnitOfWork mongo, CancellationToken ct)
{
    // Every write through the unit of work participates in the outbox transaction —
    // the session cannot be forgotten.
    await mongo.Collection<Order>("orders").InsertOneAsync(new Order(cmd.OrderId), ct);
    return new OrderPlaced(cmd.OrderId);
}
```

  Keep the raw `IClientSessionHandle` pattern documented as the advanced/repository-friendly alternative.

- [ ] **Step 2: `CLAUDE.md`** — update the "Key Design Decisions" and "Important Constraints" sections:
  - Add: dead-letter TTL only active when `DeadLetterQueueExpirationEnabled`; handled markers expire via `KeepUntil` TTL; store pins majority write/read concerns; `Balanced` mode throws at startup; `MongoDbUnitOfWork` is the recommended handler write surface; `LoadOutgoingAsync` is owner-scoped and batch-limited.
  - Update the CI description: library compliance tests now run in CI against a pinned Wolverine clone (`WOLVERINE_TAG` in `ci.yml`); the demo job consumes the freshly packed nupkg (version `0.0.0-ci`).
  - Remove the now-false statements: "CI skips library tests for now" and the `[ModuleInitializer]` serializer mention.

- [ ] **Step 3: `FOLLOWUPS.md`** — prune completed items and add new ones:
  - **Remove** (done in this plan): "Missing `EnvelopeId` index", "Unbounded `wolverine_node_records`" (TTL added; the `DeleteOldNodeRecordsAsync` override remains a multinode-plan item), "Process-wide `DateTimeOffset` serializer", "Session-bound write helper".
  - **Keep**: `ClearAllAsync` scope, node-number reuse, unqualified `IMongoDatabase` registration, `HasLeadershipLock` external-delete edge.
  - **Update** the multinode entry to point at `docs/superpowers/plans/2026-06-09-multinode-support.md`.
  - **Add**: "Old single-field `executionTime`/outgoing `ownerId` indexes are not dropped on existing deployments — add an index migration if this ever matters pre-1.0."

- [ ] **Step 4: `CHANGELOG.md`** — add an `## [Unreleased]` section listing every change from Tasks 1–11 under `### Fixed` / `### Added` / `### Changed`, explicitly calling out the two behavior changes: (a) dead letters no longer expire by default, (b) startup now throws on `DurabilityMode.Balanced`.

- [ ] **Step 5: `demo/README.md` + `demo/CLAUDE.md`** — add a short "Session-bound writes" note: the demo's repository pattern (explicit `IClientSessionHandle` threading) remains valid and is shown in all handlers; link to the library README's `MongoDbUnitOfWork` section as the lighter-weight alternative for handlers that talk to collections directly. Update the demo CLAUDE.md "Dependencies" note to mention CI consumes the freshly packed library.

- [ ] **Step 6: Verify docs claims against code** — re-read each edited file once; every statement must be true *after* Tasks 1–11 (e.g. don't claim index migration happens automatically).

- [ ] **Step 7: Commit**

```bash
rtk git add README.md CLAUDE.md FOLLOWUPS.md CHANGELOG.md demo/README.md demo/CLAUDE.md
rtk git commit -m "docs: hardening pass — durability mode, DLQ retention, write concerns, MongoDbUnitOfWork"
```

---

### Task 13: Final verification (on `main`, after the Task 12 PR merges — no branch, no PR)

- [ ] **Step 1: Full local run against merged main**

```bash
rtk git checkout main && rtk git pull
```

Run: `dotnet test src/Wolverine.MongoDB.Tests` → PASS
Run: `dotnet build src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false` → succeeds (package-ref build still compiles)

- [ ] **Step 2: Confirm the post-merge CI run on main is green** (both `library` and `demo` jobs)

```bash
rtk gh run list --branch main --limit 3
```

- [ ] **Step 3: Self-review merged main against this plan**

Run: `rtk git log --oneline -15` — one merged PR per task (1–12). Then confirm every file in the File Structure Overview was touched: `rtk git diff <commit-before-task-1> main --stat`. Anything missing means a task PR was not merged — stop and resolve before starting the multinode plan.
