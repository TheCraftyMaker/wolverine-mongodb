# Durability Contracts Discovery (Task F2)

> Read-only discovery for Task F2 of `2026-07-07-review-findings-remediation.md`. Every fact below
> was read directly from the pinned `external/wolverine` submodule (commit `feba5cd`, tag `V6.16.0`)
> or local `main` in this worktree. This doc feeds Task F4's design gate (LD2 + LD3) and the F8/F10/
> F11/F12 implementation tasks. No library code was changed.

---

## 1. `DurableReceiver` batch-store contract + RDBMS transaction

### 1a. `DurableReceiver.ProcessReceivedMessagesAsync` — catch path and per-envelope retry

`external/wolverine/src/Wolverine/Runtime/WorkerQueues/DurableReceiver.cs`

The batch path (`:608-660`, catch block at `:631-641`):

```csharp
// :608
public async ValueTask ProcessReceivedMessagesAsync(DateTimeOffset now, IListener listener, Envelope[] envelopes)
{
    ...
    if (ShouldPersistBeforeProcessing)
    {
        try
        {
            assignAncillaryStoreIfNeeded(envelopes);
            await _inbox.StoreIncomingAsync(envelopes).ConfigureAwait(false);   // :623
            foreach (var envelope in envelopes) envelope.WasPersistedInInbox = true;
            batchSucceeded = true;
        }
        catch (DuplicateIncomingEnvelopeException)                            // :631
        {
            // The batch contained at least one duplicate. We cannot trust which
            // envelopes were actually persisted (some drivers autocommit per
            // statement on multi-statement batches), so we re-attempt every
            // envelope through the per-envelope path. The single-envelope
            // StoreIncomingAsync correctly distinguishes fresh inserts from
            // duplicates: fresh ones get persisted and pipelined, duplicates
            // throw and are completed at the listener via
            // handleDuplicateIncomingEnvelope. Do NOT pause the listener.
            foreach (var envelope in envelopes) await _receivingOne.PostAsync(envelope).ConfigureAwait(false);  // :641
        }
```

The per-envelope path (`:483-507`) that every retried envelope goes through:

```csharp
// :491-506
envelope.OwnerId = _settings.AssignedNodeNumber;
assignAncillaryStoreIfNeeded(envelope);
await _inbox.StoreIncomingAsync(envelope).ConfigureAwait(false);   // :493
envelope.WasPersistedInInbox = true;
...
catch (DuplicateIncomingEnvelopeException e)
{
    await handleDuplicateIncomingEnvelope(envelope, e).ConfigureAwait(false);   // :498
    return;
}
```

`handleDuplicateIncomingEnvelope` (`:522-538`): logs the duplicate, calls
`envelope.Listener.CompleteAsync(envelope)` (`:530`), and **returns** — it never reaches
`EnqueueAsync` (`:511`, only hit when `envelope.Status == EnvelopeStatus.Incoming`, which a
duplicate-detected envelope never sets).

**Confirmed exactly as the plan states.** The contract: `DurableReceiver` retries **every**
envelope in a failed batch through the single-envelope path, and the single-envelope path
distinguishes fresh-vs-duplicate correctly *only if* nothing from the failed batch was already
persisted. If the batch partially persisted N fresh envelopes before hitting a duplicate, the
retry re-attempts those N envelopes' `StoreIncomingAsync(envelope)` calls, which now see *their own*
already-persisted document and misclassify them as duplicates — `handleDuplicateIncomingEnvelope`
completes them at the listener without ever calling `EnqueueAsync`. This is the exact stranding bug
F8 fixes: envelopes are marked complete at the transport layer but never handed to a local queue for
processing, and they sit in Mongo `OwnerId = <this node>`, invisible to orphan recovery (which only
matches `OwnerId == AnyNode`, `MongoDbMessageStore.Durability.cs:126`) until this node fails.

### 1b. RDBMS `StoreIncomingAsync(IReadOnlyList<Envelope>)` — explicit transaction + rollback

`external/wolverine/src/Persistence/Wolverine.RDBMS/MessageDatabase.Incoming.cs:174-213`:

```csharp
// :174
public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
{
    if (envelopes.Count == 0) return;
    await using var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, this);
    await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
    try
    {
        // Wrap the multi-statement batch in an explicit transaction so the
        // semantics are uniform across drivers: SqlClient/MySqlConnector/
        // Microsoft.Data.Sqlite autocommit per statement otherwise, which
        // would partially persist the batch on a duplicate-key failure and
        // leave the inbox in a state that is indistinguishable from
        // "envelope was already there". Npgsql already does this implicitly,
        // but being explicit costs nothing and removes a per-driver footgun.
        await using var tx = await conn.BeginTransactionAsync(_cancellation);   // :190
        try
        {
            cmd.Connection = conn;
            cmd.Transaction = tx;
            await cmd.ExecuteNonQueryAsync(_cancellation);
            await tx.CommitAsync(_cancellation);
        }
        catch (Exception e) when (IsDuplicateEnvelopeException(e))             // :198
        {
            await tx.RollbackAsync(_cancellation);                             // :200

            // Now that the batch is guaranteed rolled back, identify exactly
            // which envelopes were already present via id-existence. Callers
            // can retry the rest per-envelope.
            var duplicates = new List<Envelope>();
            foreach (var envelope in envelopes)
            {
                if (await ExistsAsync(envelope, _cancellation).ConfigureAwait(false))
                {
                    duplicates.Add(envelope);
                }
            }
            ...
        }
    }
}
```

**Confirmed exactly as the plan states** (line numbers match: comment at `:183-189`, rollback at
`:200`). Note the RDBMS provider gets a **complete, precise** duplicate list post-rollback (it can
afford an extra `ExistsAsync` round trip per envelope because rollback already guarantees nothing
persisted); this is the property LD2/F8's transaction-wrap approach cannot fully replicate if Mongo
aborts multi-document transactions fail-fast on the first write error (see §5) — the reported dupe
list from `MongoBulkWriteException` may cover only the first duplicate encountered, not all of
them. The `DurableReceiver` contract in §1a tolerates this because it retries *every* envelope in
the batch regardless of which ones the exception's dupe list actually names.

---

## 2. `MessageIdentity` modes and local `_inboxIdentity`/`EnvelopeId` usage

### 2a. `MessageIdentity` enum + property

`external/wolverine/src/Wolverine/DurabilitySettings.cs`:

```csharp
// :40-53
public enum MessageIdentity
{
    /// The default, "classic" behavior where Wolverine only identifies a received message by the unique message id
    IdOnly,

    /// Make Wolverine identify message identity uniqueness by a combination of the message id and destination
    /// (received_at). Use this if you are having a single Wolverine process receive the same message from
    /// multiple external listeners. This may be necessary for some "Modular Monolith" approaches
    IdAndDestination
}
```

```csharp
// :103-107
/// Direct Wolverine on how it judges message identity. "Classic" default is IdOnly. Switch to IdAndDestination
/// for Modular Monolith usage where you may be receiving the same message and processing separately in different
/// external transport listening endpoints
public MessageIdentity MessageIdentity { get; set; } = MessageIdentity.IdOnly;
```

**Confirmed exactly as the plan states** (`:103-107` is the property + doc comment; the enum
itself is `:40-53`, not `:103-107` — the plan's citation of `:103-107` refers to the settings
property, which is correct).

### 2b. Local `_inboxIdentity` construction

`src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs:46-48`:

```csharp
_inboxIdentity = options.Durability.MessageIdentity == MessageIdentity.IdOnly
    ? e => e.Id.ToString()
    : e => $"{e.Id}|{e.Destination?.ToString().Replace(":/", "").TrimEnd('/')}";
```

In `IdOnly` mode, `InboxIdentity(envelope) == envelope.Id.ToString()` — byte-identical to the raw
envelope Guid string. In `IdAndDestination` mode, the document `_id` becomes `"{guid}|{destination}"`,
so **the same envelope Guid delivered to two different destinations produces two distinct
documents** with distinct `_id`s but the same `EnvelopeId`.

`IncomingMessage.cs:25-26` confirms the two fields are independent:

```csharp
[BsonId] public string Id { get; set; } = string.Empty;                 // :25 — the driver's key, InboxIdentity(envelope)
[BsonElement("envelopeId")] [BsonGuidRepresentation(...)]                // :26
public Guid EnvelopeId { get; set; }                                     // the raw Wolverine envelope id — NOT unique across destinations in IdAndDestination mode
```

### 2c. Claim (`ReassignIncomingAsync`) — unscoped by destination

`src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs:119-136`:

```csharp
// :119
public Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
{
    if (incoming.Count == 0) return Task.CompletedTask;
    var ids = incoming.Select(x => x.Id).ToList();                      // :126 — raw envelope Guids, NOT InboxIdentity(e)

    return Incoming.UpdateManyAsync(                                    // :131-135
        Builders<IncomingMessage>.Filter.And(
            Builders<IncomingMessage>.Filter.In(x => x.EnvelopeId, ids),  // :133 — matches ALL destinations sharing this Guid
            Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, MongoConstants.AnyNode)),
        Builders<IncomingMessage>.Update.Set(x => x.OwnerId, ownerId));
}
```

**Confirmed exactly as the plan states** (`:131-135`). In `IdAndDestination` mode, claiming a page
of envelopes for destination A's recovery loop will also flip `OwnerId` on any *other* destination's
document that happens to share the same `EnvelopeId` — a document this node never loaded, was never
asked to recover, and has no listener circuit prepared to enqueue it into. It becomes "claimed" (no
longer `OwnerId == AnyNode`, so no other node's recovery will pick it up) but is never enqueued —
a second, distinct stranding bug from the same root cause (unscoped `EnvelopeId` filter) as F8's.

### 2d. Re-read (`RecoverOrphanedIncomingAsync`) — unscoped by destination

`src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs:122-167`, re-read at `:153-158`:

```csharp
var ids = envelopes.Select(x => x.Id).ToList();                          // :153 — raw envelope Guids (the page just loaded for ONE destination)
var claimedIds = await Incoming.Distinct(x => x.EnvelopeId,              // :154
    Builders<IncomingMessage>.Filter.And(
        Builders<IncomingMessage>.Filter.In(x => x.EnvelopeId, ids),      // :156
        Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, nodeNumber)), // :157
    cancellationToken: token).ToListAsync(token);

var claimedSet = claimedIds.ToHashSet();
var claimed = envelopes.Where(e => claimedSet.Contains(e.Id)).ToList();   // :161 — maps back by e.Id (envelope Guid), not by document _id
```

**Confirmed exactly as the plan states** (`:154-158`). `LoadPageOfGloballyOwnedIncomingAsync`
(`:103-117` in `MongoDbMessageStore.cs`) *does* scope the initial load correctly by
`ReceivedAt == listenerAddress.ToString()` (`:108`), so the `envelopes` list passed into the claim +
re-read is destination-correct going in. The bug is entirely in the claim/re-read filter shape:
because both key off `EnvelopeId` (not the destination-scoped document `_id`), a same-Guid sibling
document at a different destination gets pulled into the `UpdateManyAsync` write and, if this node
wins the CAS race for it too, into the `claimedIds` distinct-projection — the `envelopes.Where(...
Contains(e.Id))` re-mapping at `:161` then further conflates them since `e.Id` (envelope Guid) is
exactly the field that is *not* unique per destination in this mode.

**F10's fix direction is sound**: switching both filters to key on the document `_id`
(`InboxIdentity(e)`, i.e. `Filter.In(x => x.Id, envelopes.Select(InboxIdentity))`) scopes both the
claim and the re-read to exactly the documents `LoadPageOfGloballyOwnedIncomingAsync` loaded, and in
`IdOnly` mode `InboxIdentity(e) == e.Id.ToString()` so the filter values are unchanged from today
(no `IdOnly` regression).

### 2e. RDBMS comparison — already destination-scoped

`external/wolverine/src/Persistence/Wolverine.RDBMS/MessageDatabase.Incoming.cs:17-35`:

```csharp
// :17
public Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
{
    ...
    foreach (var envelope in incoming)
    {
        builder.Append($"update {...}.{DatabaseConstants.IncomingTable} set owner_id = ");
        builder.AppendParameter(ownerId);
        builder.Append($" where {DatabaseConstants.Id} = ");
        builder.AppendParameter(envelope.Id);
        builder.Append($" and {DatabaseConstants.ReceivedAt} = ");        // :29 — destination scope
        builder.AppendParameter(envelope.Destination!.ToString());        // :30
        builder.Append(";");
    }
    ...
}
```

**Confirmed exactly as the plan states** (`:27-30`). The RDBMS provider's composite primary key is
effectively `(id, received_at)` and every incoming-table statement in this file (`ReassignIncomingAsync`
`:27-30`, `MoveToDeadLetterStorageAsync` `:66-69`, both `MarkIncomingEnvelopeAsHandledAsync`
overloads `:88-90`, `:104-109`) scopes by both columns — there is no RDBMS analogue of the Mongo
bug because the RDBMS schema never conflates two destinations' rows under one identity in the first
place.

---

## 3. Dead-node release ordering + registration-before-claim proof

### 3a. RDBMS `ReleaseOrphanedMessagesOperation` — single command, two statements

`external/wolverine/src/Persistence/Wolverine.RDBMS/Durability/ReleaseOrphanedMessagesOperation.cs:24-35`:

```csharp
public void ConfigureCommand(DbCommandBuilder builder)
{
    ...
    builder.Append(
        $"update {incomingTable} set {OwnerId} = 0 where {OwnerId} != 0 and {OwnerId} not in (select {NodeNumber} from {nodesTable});");   // :31-32
    builder.Append(
        $"update {outgoingTable} set {OwnerId} = 0 where {OwnerId} != 0 and {OwnerId} not in (select {NodeNumber} from {nodesTable});");   // :33-34
}
```

**Correction to the plan's characterization:** this is not literally *one* SQL statement — it is
two `update` statements (incoming, then outgoing), each carrying its own `not in (select ...)`
subquery against the live `nodes` table. But each individual `update` is evaluated by the RDBMS
engine as a single atomic operation: the subquery and the row-matching happen inside one statement's
execution, so there is no read-then-write gap *within* a single `update` — a node that registers
mid-statement either is or isn't in the sub-select's result set at the instant the engine evaluates
it, and no envelope can be released based on a *snapshot* of node membership taken earlier and then
compared later. This is the key structural difference from the Mongo implementation (§3b): the RDBMS
version's "liveness check" and "release write" are the same database operation, not two round trips
separated by network latency. Confirmed via `DurabilityAgent.buildOperationBatch`
(`external/wolverine/src/Persistence/Wolverine.RDBMS/DurabilityAgent.cs:225-227`): this operation is
only added `if (_settings.Mode != DurabilityMode.Solo)` and `_database.Settings.Role ==
MessageStoreRole.Main`; for non-Main (ancillary) stores a *different* operation,
`ReleaseOrphanedMessagesForAncillaryOperation`, is used instead, driven by an `activeNodeNumbers`
list loaded once per tick by the *caller* (`DurabilityAgent.cs:194-197`) — that ancillary variant
**does** have the same snapshot-then-write structure as the Mongo implementation (a list of live
numbers computed once, then used in a later write), so it is not immune to the same race in
principle. This nuance is worth flagging to F4/F11: the plan's LD3 two-tick design should apply
regardless of Main/Ancillary role, since the Mongo provider has only one message store shape (no
Main/Ancillary split in the local file structure) and always takes the "ancillary-shaped" snapshot
path.

### 3b. Local `ReleaseDeadNodeOwnershipAsync` — two round trips, stale snapshot

`src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs:169-194`:

```csharp
// :169-174 (doc comment)
/// Releases incoming/outgoing ownership held by node numbers that have no
/// registered node document (crashed nodes). Mirrors the RDBMS
/// ReleaseOrphanedMessagesOperation. Safe to run on any node, any time:
/// a live node always has a node document, so its in-flight work is never touched.
internal async Task ReleaseDeadNodeOwnershipAsync(CancellationToken token)
{
    var liveNumbers = await NodeDocs                                    // :177-180 — READ #1
        .Find(FilterDefinition<NodeDocument>.Empty)
        .Project(x => x.AssignedNodeNumber)
        .ToListAsync(token);

    liveNumbers.Add(MongoConstants.AnyNode);                            // :183

    await Incoming.UpdateManyAsync(                                     // :185-188 — WRITE #1 (separate round trip)
        Builders<IncomingMessage>.Filter.Nin(x => x.OwnerId, liveNumbers),
        Builders<IncomingMessage>.Update.Set(x => x.OwnerId, MongoConstants.AnyNode),
        cancellationToken: token);

    await Outgoing.UpdateManyAsync(                                     // :190-193 — WRITE #2 (separate round trip)
        Builders<OutgoingMessage>.Filter.Nin(x => x.OwnerId, liveNumbers),
        Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, MongoConstants.AnyNode),
        cancellationToken: token);
}
```

**Confirmed exactly as the plan states** (`:175-194`). The safety claim in the doc comment
(`:172-173`, "a live node always has a node document, so its in-flight work is never touched") is
false in the interleaving where: (1) a brand-new node N registers its `NodeDocument` **after** the
`liveNumbers` read at `:177-180` completes but **before** either `UpdateManyAsync` at `:185`/`:190`
runs, and (2) that same node N has already been assigned envelopes with `OwnerId == N` by a prior
tick or a concurrent claim in the same tick (possible once N's number is registered but before this
particular recovery tick's read observed it). N's number is `Nin` the stale `liveNumbers` list, so
its freshly-owned envelopes get released to `AnyNode` — exactly the race the plan describes. Two
round trips (read, then two separate writes) is strictly worse than the RDBMS Main-store shape
(§3a) and structurally identical to the RDBMS ancillary-store shape.

### 3c. Node numbers are monotonic and never reused

`src/Wolverine.MongoDB/Internals/MongoDbMessageStore.NodeAgents.cs:1-30` (read in this worktree):

```csharp
private static long _nodeNumberCounter = 0;   // approximate location per T4.6's documented decision
```

Confirmed via the existing documented T4.6 decision in `CLAUDE.md` (already load-bearing prior
review fact, not re-derived here): "the node-number counter... is a pure monotonic increment that
never reuses a freed slot." This library-side fact (not from the Wolverine submodule) is what makes
LD3's two-tick soundness argument valid: **a node number that appears in tick K's "dead" set cannot
belong to any node that registers after tick K**, because registration always allocates a strictly
higher number than any previously-issued one. If two-tick confirmation requires the same number to
appear as "dead" in two consecutive ticks before release, and a live node's number can only newly
appear (never reappear after being freed and reissued), then a number confirmed dead across two
ticks was dead for the *entire* interval between them — no live node could have silently reclaimed it
mid-interval, because reclaiming would require either (a) the number being reissued (impossible,
monotonic) or (b) the original node coming back with the same number (impossible — `PersistAsync`
always allocates a new one per registration, per NodeAgentController flow in §3d).

### 3d. PROOF: node registers before its durability agent issues any claim

Traced the full Balanced-mode startup sequence in
`external/wolverine/src/Wolverine/Runtime/WolverineRuntime.HostService.cs:144-152`:

```csharp
case DurabilityMode.Balanced:
    await loadAgentRestrictionsAsync();
    await startMessagingTransportsAsync();
    startInMemoryScheduledJobs();
    await startNodeAgentWorkflowAsync();          // :150 — node registration happens inside this call
    _idleAgentCleanupLoop = Task.Run(executeIdleSendingAgentCleanup, Cancellation);
    break;
```

`startNodeAgentWorkflowAsync` (`WolverineRuntime.Agents.cs:207-219`) calls
`NodeController.StartLocalAgentProcessingAsync(Options)`. That method
(`NodeAgentController.StartLocalProcessing.cs:7-27`) is where registration actually happens:

```csharp
public async Task<AgentCommands> StartLocalAgentProcessingAsync(WolverineOptions options)
{
    var current = WolverineNode.For(options);
    foreach (var controller in _agentFamilies.Values.OfType<IStaticAgentFamily>())
        current.Capabilities.AddRange(await controller.SupportedAgentsAsync());

    current.AssignedNodeNumber = await _persistence.PersistAsync(current, _cancellation.Token);   // :15 — REGISTRATION
    await _observer.NodeStarted();
    _runtime.Options.Durability.AssignedNodeNumber = current.AssignedNodeNumber;
    ...
    HasStartedLocalAgentWorkflowForBalancedMode = true;
    return AgentCommands.Empty;    // :26 — no agents are started as a side effect of this call
}
```

Critically, this method returns `AgentCommands.Empty` — **it does not itself start any per-store
durability agent.** Per-store agents (e.g. `MongoDbDurabilityAgent`, whose `StartAsync` is what
kicks off `ReleaseDeadNodeOwnershipAsync`/`RecoverOrphanedIncomingAsync`/etc. via `StartTimers()`,
`MongoDbDurabilityAgent.cs:36-41`) are started only via `NodeAgentController.StartAgentAsync(uri)`
(`NodeAgentController.cs:172-...`), which in Balanced mode is invoked either (a) locally, from
`runtime.Agents.StartLocallyAsync` (`WolverineRuntime.Agents.cs:26-36`) in response to a `StartAgent`
command (`StartAgent.cs:11-16`, `ExecuteAsync` → `runtime.Agents.StartLocallyAsync(AgentUri)`), or
(b) never, if this node is never assigned that agent. `StartAgent` is a message dispatched by the
cluster's leader after evaluating `AssignmentGrid` (`WolverineRuntime.Agents.cs:123-132`,
`ApplyRestrictionsAsync` → `Storage.Nodes.LoadNodeAgentStateAsync` reads the **persisted** node
table → `NodeController.EvaluateAssignmentsAsync(nodes, assignments)`). A node cannot appear in
`LoadNodeAgentStateAsync`'s result — and therefore cannot be assigned any agent, including its own
store's durability agent — until its `NodeDocument` exists, which is exactly the `PersistAsync` call
at `StartLocalProcessing.cs:15`.

**Conclusion: PROVEN.** `PersistAsync` (registration) happens-before any `StartAgent` command can
target this node, which happens-before `NodeController.StartAgentAsync` for the store's own agent
URI, which happens-before `MongoDbDurabilityAgent.StartAsync`/`StartTimers()`, which is the earliest
point `ReleaseDeadNodeOwnershipAsync` or any recovery/claim method can run. There is no code path in
`external/wolverine`'s Balanced-mode startup where a node's own durability agent starts before that
node's `PersistAsync` call completes. This confirms the LD3 soundness precondition holds.

**One discovery worth flagging to F4, out of the plan's original scope but directly relevant to the
two-tick algorithm's storage location:** `startDurableScheduledJobs()`
(`WolverineRuntime.Agents.cs:188-191`) calls `_stores.Value.StartScheduledJobProcessing(this)`
**before** `startNodeAgentWorkflowAsync()` in the Balanced-mode sequence (`HostService.cs:132` vs
`:150`). For MongoDB, `IMessageStore.StartScheduledJobs` (`MongoDbMessageStore.cs:80`) delegates to
the *same* `BuildAgent(runtime)` factory as the agent-family path
(`MongoDbMessageStore.cs:82-86`) — but each call constructs a **new** `MongoDbDurabilityAgent`
instance. The object returned from `StartScheduledJobProcessing` is stored in
`WolverineRuntime.DurableScheduledJobs` (`WolverineRuntime.Agents.cs:193`) but — per an exhaustive
grep of `external/wolverine/src` — **no code path ever calls `.StartAsync()` on it**; the only call
site found is `.StopAsync()` during `WolverineRuntime.Disposal.cs:22-24`. This strongly suggests
`DurableScheduledJobs` is currently a construct-but-never-start no-op in the reference
implementation for stores that (like Mongo) implement scheduled-job polling via the *same* agent
object as recovery, rather than a genuinely separate scheduled-jobs-only agent — i.e. it does not
appear to be a second, earlier-starting instance that would race the registration-before-claim
proof above. This is worth a one-line note in F4's design doc (not a blocker): the two-tick dead-set
state (LD3) should be stored per-`MongoDbDurabilityAgent`-instance or per-store, not assuming
exactly one instance ever exists, since the plumbing technically constructs (if never starts) a
second one.

### 3e. Shutdown ordering — release before teardown

`external/wolverine/src/Wolverine/Runtime/WolverineRuntime.HostService.cs:362-403`:

```csharp
if (_stores.IsValueCreated && StopMode == StopMode.Normal)
{
    try { await _stores.Value.DrainAsync(); } catch (OperationCanceledException) { }

    try
    {
        // Release any ownership on the way out. Do this *after* draining endpoints
        // so in-flight messages complete before their ownership is released.
        await _stores.Value.ReleaseAllOwnershipAsync(DurabilitySettings.AssignedNodeNumber);   // :380
    }
    catch (ObjectDisposedException) { }
    catch (OperationCanceledException) { }
}

if (StopMode == StopMode.Normal)
{
    // Step 2: Now teardown agents — safe after endpoints drained and ownership released
    await teardownAgentsAsync();   // :402
}
```

**Confirmed exactly as the plan states** (`:380` release, `:402` teardown — release strictly
precedes teardown). `teardownAgentsAsync()` (`WolverineRuntime.Agents.cs:262-271`) calls
`NodeController.StopAsync(bus)`
(`NodeAgentController.cs:118-144`), which itself: (1) `stopAllAgentsAsync()` (`:120`, iterates
every locally-running `IAgent` — including this node's `MongoDbDurabilityAgent` — and calls
`entry.Value.StopAsync(CancellationToken.None)`, `:146-160`), (2) releases the leadership lock if
held (`:126-134`), (3) **deletes the node document** via `_persistence.DeleteAsync(...)` (`:136`).

**Confirmed exactly as the plan states**: "`NodeAgentController.cs:136` deletes the node document
immediately after the agent's instant-return stop" — `stopAllAgentsAsync()` at `:120` runs first and
awaits each agent's `StopAsync`, but `MongoDbDurabilityAgent.StopAsync` (`MongoDbDurabilityAgent.cs
:130-140`) returns `Task.CompletedTask` synchronously after firing cancellation and
`SafeDispose`-ing both background tasks (§4 below) — so "await"-ing it doesn't actually wait for the
recovery/scheduled-job loop bodies to finish their current iteration. By the time `_persistence
.DeleteAsync` runs at `:136`, the loop bodies may still be mid-flight against a database handle
whose owning `MongoDbMessageStore` may already be disposed (`HasDisposed` is set by
`MongoDbMessageStore.DisposeAsync`, `MongoDbMessageStore.cs:138-142`, called separately from
`_stores.Value` teardown, not shown to be ordered relative to this).

---

## 4. RDBMS `DurabilityAgent.StopAsync` awaits its loops; `SafeDispose` semantics

### 4a. RDBMS `DurabilityAgent.StopAsync` — actually awaits

`external/wolverine/src/Persistence/Wolverine.RDBMS/DurabilityAgent.cs:141-158`:

```csharp
public async Task StopAsync(CancellationToken cancellationToken)
{
    _runningBlock.Complete();
    _metrics.SafeDispose();

    if (_scheduledJobTimer != null) await _scheduledJobTimer.DisposeAsync();   // :146-149
    if (_recoveryTimer != null) await _recoveryTimer.DisposeAsync();           // :151-154
    if (_expirationTimer != null) await _expirationTimer.DisposeAsync();       // :156-159
    if (_handledCleanupTimer != null) await _handledCleanupTimer.DisposeAsync();  // :161-164

    Status = AgentStatus.Stopped;
}
```

**Confirmed as the plan states** (line range in this checkout is `:141-166` — the plan's cited
`:140-166` is off by one line but names the same method body). Every timer here is a
`System.Threading.Timer` wrapped so its `DisposeAsync()` genuinely waits for any in-flight callback
to finish before returning (the .NET `Timer.DisposeAsync` contract: it signals a `WaitHandle`/
completes a `Task` only once no callback is executing and none will execute again) — this is a real
await, not a fire-and-forget dispose. The RDBMS agent's recovery/scheduled-job/expiration/cleanup
work all runs as `Timer` callbacks posted onto `_runningBlock` (a `Block<IAgentCommand>`,
constructed in the ctor `:47-67`); `_runningBlock.Complete()` at `:143` additionally signals the
block to drain in-flight work.

### 4b. Local `MongoDbDurabilityAgent.StopAsync` — fire-and-forget

Already quoted in full in §3e / earlier in this doc (`MongoDbDurabilityAgent.cs:130-140`):

```csharp
public Task StopAsync(CancellationToken cancellationToken)
{
    _cancellation.Cancel();
    _recoveryTask?.SafeDispose();
    _scheduledJob?.SafeDispose();
    Status = AgentStatus.Stopped;
    return Task.CompletedTask;
}
```

`_recoveryTask`/`_scheduledJob` are plain `Task.Run(...)` background loops (`StartTimers()`,
`:47-91`), not `System.Threading.Timer`s — there is no driver-provided "wait for in-flight callback"
primitive here. `Task.Dispose()` on a `Task` that is not yet in a terminal state throws
`InvalidOperationException` ("A task may only be disposed if it is in a completion state"); `SafeDispose`
(JasperFx.Core — an external NuGet dependency, no local source in this repo or submodule; behavior
here is the CLAUDE.md/plan's already-established fact, re-confirmed only at the call site) is a
try/catch wrapper that swallows exactly that exception. So `_recoveryTask?.SafeDispose()` neither
awaits completion nor observes whether the task actually stopped — it merely attempts (and
discards the result of attempting) to dispose a `Task` object that is, in the overwhelmingly likely
case, still running (cancellation was only just requested one line above, and the loop's
`PeriodicTimer.WaitForNextTickAsync` / in-flight `await` chain needs at least one scheduler turn to
observe `_combined.IsCancellationRequested` and unwind). `StopAsync` then returns
`Task.CompletedTask` immediately: **the method's `Task` return value completing does not mean the
recovery loop has stopped running.**

**Confirmed exactly as the plan states**, with the added precision that the two loops
(`_recoveryTask`, `_scheduledJob`) are raw `Task.Run` continuations racing a `CancellationToken`,
not driver-awaited `Timer`s — the F12 fix (awaiting both tasks with a bounded timeout before
returning, per the plan's F4-deferred contract) has a real, uncancelled `await` primitive to bind to
(`await Task.WhenAny(Task.WhenAll(_recoveryTask, _scheduledJob), Task.Delay(timeout))`-shaped), unlike
the RDBMS agent's `Timer.DisposeAsync`, so the exact implementation shape will necessarily differ
even though the awaited-vs-fire-and-forget *contract* converges.

---

## 5. MongoDB in-transaction `InsertMany(IsOrdered:false)` failure mode

No local source for `MongoDB.Driver` is available in this repo or the pinned submodule (compiled
NuGet package only, `MongoDB.Driver 3.9.0` per `Directory.Packages.props:8`) — this section states
the **documented MongoDB server/driver contract**, which F8 must still confirm empirically against
the exact .NET exception type/shape (the plan already anticipates this: "code to the ACTUAL
in-transaction exception shape... if it differs from the shape above, follow the driver, not the
snippet", `2026-07-07-review-findings-remediation.md:350`).

**Server-level contract (MongoDB multi-document transactions):** all writes inside a transaction are
part of one atomic unit. Per MongoDB's transaction semantics, once any operation inside a
transaction errors, the transaction is left in an aborted state — the server does not continue
processing further statements in that transaction, and no partial writes from that transaction are
visible to any other session (transactions provide snapshot isolation; nothing commits until
`commitTransaction` succeeds). This is qualitatively different from an **unordered** bulk write
*outside* a transaction, where the server is explicitly permitted to continue attempting the
remaining documents after one fails and report a `BulkWriteError` array covering every failed
document.

**Practical consequence for `InsertMany(session, docs, new InsertManyOptions { IsOrdered = false })`
inside a `session.StartTransaction()`/`CommitTransactionAsync()` block:** the `IsOrdered = false`
option only controls client/server *request-batching* behavior (whether the server may reorder or
parallelize the individual inserts within the one bulk command) — it does not, and cannot, grant
transactional writes permission to continue past the first write conflict, because doing so would
require partially committing a transaction, which MongoDB does not support. The practical effect
F4/F8 must design around: **the driver will very likely surface only the first encountered
duplicate-key error** (or a smaller subset than the true full set of duplicates in the batch),
wrapped in some exception shape the .NET driver throws for a failed bulk write inside a session —
still very plausibly `MongoBulkWriteException<T>` (the same type thrown outside transactions) since
that's the driver's standard vehicle for reporting `WriteError`s from an `insertMany`-shaped
command, but this repo has no local evidence pinning the exact type/shape when a session is
attached, and the actual error may instead surface as a `MongoCommandException` if the server aborts
before producing a structured bulk-write response. **This is exactly the caveat LD2 already
anticipates** ("the reported duplicate list may be partial") and it is confirmed here at the
protocol/semantics level, not merely asserted; F8's red-first test must assert only *persistence
count* (nothing from the batch survived) and *an exception was thrown*, never a complete duplicate
list, and F8's implementation should catch broadly enough (e.g. any exception whose
`ServerErrorCategory`/error code indicates duplicate key, across whatever concrete exception type(s)
the empirical test observes) rather than hardcoding one type.

---

## Corrections / additions to the plan's Verified API Facts

1. **§3a nuance:** `ReleaseOrphanedMessagesOperation` (RDBMS Main-store path) is genuinely
   single-round-trip-atomic per statement, but the plan's phrase "single-statement semantics"
   undersells that it's actually *two* statements (incoming, outgoing) each independently atomic —
   not one combined statement. There is also a **second, ancillary-store RDBMS operation**
   (`ReleaseOrphanedMessagesForAncillaryOperation`) that takes a pre-loaded `activeNodeNumbers`
   snapshot exactly like the Mongo implementation does — meaning the RDBMS provider is not
   universally immune to this race either; only its *Main*-store path is. Worth a one-line mention
   in F4's design doc so the "RDBMS mirror is atomic" framing doesn't imply every RDBMS deployment
   shape avoids this class of bug.
2. **§3d discovery (not a plan error, an addition):** a `DurableScheduledJobs` composite agent is
   constructed earlier in Balanced-mode startup than node registration, but appears to never be
   started (`.StartAsync()` has no call site in the submodule) — so it does not threaten the
   registration-before-claim proof, but F4 should note it as a "confirmed not a second live claimer"
   footnote rather than silently assuming only one `MongoDbDurabilityAgent` instance is ever
   constructed.
3. **§5:** the plan's caveat about partial dupe-list reporting inside a transaction is confirmed at
   the level of documented MongoDB transaction semantics (no partial commits are possible), but the
   *exact* .NET exception type/shape the driver throws in that path is not locally verifiable
   (no driver source in this repo) and must be pinned empirically by F8's red-first test before the
   catch-clause type is finalized.

All other Verified API Facts in the plan (§1, §2, §4, and the remainder of §3) are confirmed
byte-for-byte against the submodule and local source at the cited file:line locations.
