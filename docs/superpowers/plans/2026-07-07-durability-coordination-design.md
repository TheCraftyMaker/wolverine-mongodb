# Durability & Coordination Design (Task F4 — DESIGN GATE)

> **Binding design gate** for Task F4 of `2026-07-07-review-findings-remediation.md`. This document
> resolves **LD2** (batch inbox atomicity) and **LD3** (dead-node ownership release soundness) plus
> the **F10** (destination-scoped incoming claims) and **F12** (durability-agent shutdown) semantics
> into contracts that F8, F10, F11, and F12 implement **without re-litigating the decisions**.
>
> Inputs: `2026-07-07-durability-contracts-discovery.md` (Task F2 — the verified contract facts) and
> a fact-base revalidation against the pins currently on `main` (see §0). Design-only: no library
> code changed by this task.
>
> **How implementers use this doc.** Each of §1–§4 ends with a **Binding contract** block. Implement
> exactly that. Anything marked *implementer's discretion* is yours to choose; anything else requires
> coming back to this gate (see §7). Where a contract depends on a driver behavior this repo cannot
> verify statically, it is marked **[verify empirically]** with the fallback rule to apply.

---

## 0. Fact-base revalidation — F2 was written against V6.16.0, `main` is now V6.21.0

Task F2's discovery ran against `external/wolverine` at `feba5cd` (**V6.16.0**) with
`WolverineFx 6.16.0` / `MongoDB.Driver 3.9.0`. Current `main` (`5c7a0ec`) pins:

| | F2 (2026-07-07) | `main` today | Effect |
|---|---|---|---|
| `external/wolverine` submodule | `feba5cd` (V6.16.0) | `193eabd` (**V6.21.0**) | Line numbers moved in one file (below) |
| `WolverineFx` / `WolverineFx.ComplianceTests` | 6.16.0 | **6.21.0** (`Directory.Packages.props:6-7`) | — |
| `MongoDB.Driver` | 3.9.0 | **3.10.0** (`Directory.Packages.props:8`) | — |

**Every load-bearing F2 fact still holds.** Re-verified one by one against the V6.21.0 tree; only
citations changed, and only in `DurableReceiver.cs`:

| Fact (F2 §) | F2 citation (V6.16.0) | Re-verified citation (V6.21.0) | Substance |
|---|---|---|---|
| Batch persist path | `DurableReceiver.cs:608-660` | **`:692-727`** | unchanged |
| `StoreIncomingAsync(batch)` call | `:623` | **`:698`** | unchanged |
| `catch (DuplicateIncomingEnvelopeException)` + contract comment | `:631-641` | **`:706-717`** (comment `:708-715`) | comment verbatim identical |
| Per-envelope re-post after duplicate | `:641` | **`:716`** | unchanged |
| Per-envelope store / duplicate handling | `:493`, `:496-500`, `:522-538`, `:530` | **`:493`, `:496-498`, `:522`, `:530`** | unchanged |
| RDBMS batch transaction + rollback | `MessageDatabase.Incoming.cs:174-213`, tx `:190`, rollback `:200` | **identical** | unchanged |
| RDBMS destination-scoped reassign | `MessageDatabase.Incoming.cs:27-30` | **`:29-30`** (`ReceivedAt` predicate) | unchanged |
| `MessageIdentity` enum / property | `DurabilitySettings.cs:40-53` / `:103-107` | **`:41-53`** / **`:115`** | unchanged |
| Registration-before-claim | `StartLocalProcessing.cs:15`, `:26` | **identical** | unchanged |
| Release-before-teardown | `HostService.cs:380` / `:402` | **`:390`** / **`:412`** | ordering unchanged |
| Node-doc delete after agent stop | `NodeAgentController.cs:120`, `:136` | **identical** | unchanged |
| RDBMS `DurabilityAgent.StopAsync` awaits timers | `DurabilityAgent.cs:140-166` | **`:145-172`** (4 × `await …DisposeAsync()` at `:153/:158/:163/:168`) | unchanged |
| RDBMS Main vs Ancillary release | `ReleaseOrphanedMessagesOperation.cs:32,34`; ancillary snapshot | **identical**; ancillary at `DurabilityAgent.cs:237`, snapshot `:96-110`, Main at `:233` | unchanged |

### 0a. One new fact F2 did not record (it changes nothing, but it de-risks F8)

`DurableReceiver` has a **second** catch on the batch path, after the duplicate catch:

```csharp
// external/wolverine/src/Wolverine/Runtime/WorkerQueues/DurableReceiver.cs:718-726
catch (Exception e)
{
    _logger.LogError(e, "Error trying to persist incoming envelopes at {Uri}", Uri);
    SignalInboxUnavailable();                                                            // :721
    // Use finer grained retries on one envelope at a time, and this will also deal with
    // duplicate detection
    foreach (var envelope in envelopes) await _receivingOne.PostAsync(envelope).ConfigureAwait(false);  // :725
}
```

`SignalInboxUnavailable` (`:390-410`) logs a warning and calls
`ListeningAgent.PauseForInboxRecoveryAsync()` — it **pauses the listener**.

Two consequences for F8:

1. **Misclassification is not message loss.** If F8's duplicate classifier fails to recognise a
   duplicate-key failure, the generic catch still re-posts every envelope through the per-envelope
   path — which, after F8, is fed by a fully rolled-back batch, so each envelope is correctly
   classified fresh-or-duplicate. The blast radius of a classifier miss is a spurious
   listener pause + an error log, not a stranded envelope.
2. **Throwing `DuplicateIncomingEnvelopeException` (rather than letting the raw driver exception
   escape) remains worth getting right**, precisely because it is what avoids that pause. This is
   the reason §1's contract keeps a real classifier instead of "abort and rethrow whatever".

### 0b. Local precedent F2 did not surface: the store already runs a transaction

`MongoDbMessageStore.MoveToDeadLetterStorageAsync` (`Internals/MongoDbMessageStore.Inbox.cs:147-166`)
already wraps two writes in a replica-set transaction — and does it via
**`session.WithTransactionAsync(...)`** (`:154-155`), not manual
`StartTransaction`/`Commit`/`Abort`:

```csharp
using var session = await _client.StartSessionAsync();                       // :154
await session.WithTransactionAsync(async (s, ct) => { …; return true; });     // :155
```

The in-repo comment (`:149-153`) states why: `WithTransactionAsync` transparently retries
`TransientTransactionError` / `UnknownTransactionCommitResult` and aborts automatically if the body
throws. §1 adopts this same shape for F8 rather than the plan's hand-rolled sketch — same semantics,
one less way to leak an un-aborted transaction, and consistent with the file it lives in.

There is also already an in-repo statement of the **fail-fast-in-transaction** behavior F2 §5
derived from the server docs — `Internals/MongoDbMessageStore.Inbox.cs:53-58`:

> *"a duplicate is detected via a READ rather than a duplicate-key INSERT — a failed insert inside a
> Mongo transaction aborts the whole transaction, stranding the subsequent outgoing-message writes."*

So "first write error aborts the transaction" is not a new assumption introduced by F8; the library
already depends on it elsewhere.

---

## 1. Contract 1 (LD2 / OQ3) — batch `StoreIncomingAsync` is all-or-nothing

**Decision: Option A — session/transaction wrap. Adopted.** (Option B, compensating delete, and a
third option considered, are rejected in §1e.)

### 1a. Why

`DurableReceiver`'s duplicate-retry contract (`DurableReceiver.cs:706-717`, comment `:708-715`) is
only sound if a failed batch persisted **nothing**. Today's unordered
`InsertManyAsync(docs, new InsertManyOptions { IsOrdered = false })`
(`Internals/MongoDbMessageStore.Inbox.cs:28`) commits every non-duplicate document before the
duplicate-key error surfaces, so the retry re-attempts already-persisted fresh envelopes, the
per-envelope path (`:493`) sees *their own* documents, classifies them duplicate (`:496-498`), and
`handleDuplicateIncomingEnvelope` (`:522`, `:530`) completes them at the listener **without
enqueuing**. They then sit in Mongo owned by this node, invisible to orphan recovery (which matches
only `OwnerId == AnyNode`, `Internals/MongoDbMessageStore.Durability.cs:126`). Transaction-wrapping
restores the RDBMS provider's contract (`MessageDatabase.Incoming.cs:174-213`) exactly.

The replica-set requirement transactions imply is **already a hard library constraint**
(`CLAUDE.md`: *"MongoDB must run as a replica set (transactions require it)"*), so Option A adds no
deployment demand.

### 1b. Algorithm

```
StoreIncomingAsync(IReadOnlyList<Envelope> envelopes):
  if envelopes.Count == 0: return
  docs = envelopes.Select(e => new IncomingMessage(e, InboxIdentity(e)))

  using session = await _client.StartSessionAsync()
  try:
      await session.WithTransactionAsync((s, ct) =>
          Incoming.InsertManyAsync(s, docs, new InsertManyOptions { IsOrdered = false }, ct),
          TxOptions)                                  // TxOptions: see 1c — REQUIRED
  catch (Exception e) when (isDuplicateKeyFailure(e)):
      // The transaction is already aborted: WithTransactionAsync aborts when the body throws.
      // Nothing from this batch survives, so a single existence probe now yields the COMPLETE,
      // precise duplicate list (the same reasoning the RDBMS provider uses at
      // MessageDatabase.Incoming.cs:200-220, but in one round trip instead of N).
      dupes = await probeForExistingAsync(envelopes)        // see 1d
      if dupes.Count > 0: throw new DuplicateIncomingEnvelopeException(dupes)
      throw                                                // nothing pre-existed → not our contract
```

`isDuplicateKeyFailure(e)` must be **shape-tolerant**, because F2 §5 could not statically pin which
exception type the driver surfaces for a failed insert inside a session **[verify empirically —
F8 Step 2]**. Recognise a duplicate-key failure from any of:

- `MongoBulkWriteException<IncomingMessage>` → any `WriteErrors[i].Category == ServerErrorCategory.DuplicateKey` (or `Code == 11000`)
- `MongoWriteException` → `WriteError.Category == ServerErrorCategory.DuplicateKey` (or `Code == 11000`)
- `MongoCommandException` → `Code == 11000`
- any of the above reached through `AggregateException`/`InnerException` (the RDBMS provider walks
  inner exceptions for the same reason — `MessageDatabase.Incoming.cs:231-247`)

**Mixed-failure rule (preserves today's semantics at `Inbox.cs:36-42`):** if the exception carries
**any** write error that is *not* duplicate-key, rethrow the original exception — do **not** convert
to `DuplicateIncomingEnvelopeException`. A non-duplicate failure must reach
`DurableReceiver.cs:718` so the listener pauses and the inbox-unavailable path runs. Note that
inside a transaction the server fails fast, so a "mixed" exception is unlikely; the rule exists so
the *classifier*, not luck, decides.

### 1c. Transaction options are **required**, not optional

MongoDB **ignores collection/database-level write concern for operations inside a transaction**; the
commit is governed by the transaction's write concern (from `TransactionOptions`, else the session's
default, else the client's). The store's durability guarantee comes from a *database-handle* pin
(`Internals/MongoDbMessageStore.cs:42-44`: `WriteConcern.WMajority.With(journal: true)` +
`ReadConcern.Majority`) on a handle derived from the **consumer's** `IMongoClient` — a client whose
own default write concern is typically `w:1`. Wrapping the insert in a transaction without explicit
options would therefore **silently downgrade** the inbox write from `w:majority, j:true` to the
consumer's client default. That is a durability regression disguised as a correctness fix.

**Binding:** pass explicit transaction options equal to the store's pin:

```csharp
private static readonly TransactionOptions InboxTransactionOptions = new(
    readConcern: ReadConcern.Majority,
    writeConcern: WriteConcern.WMajority.With(journal: true));
```

Define it once (implementer's discretion where — `MongoDbMessageStore.cs` next to the pin, or
`Inbox.cs` next to its only user) and comment it with *why* (the pin does not survive into a
transaction).

### 1d. `probeForExistingAsync` — one query, complete list

```csharp
var ids = envelopes.Select(InboxIdentity).ToList();                  // document _id values
var present = await Incoming.Find(Builders<IncomingMessage>.Filter.In(x => x.Id, ids))
    .Project(x => x.Id).ToListAsync();                               // _id index, one round trip
var presentSet = present.ToHashSet();
var dupes = envelopes.Where(e => presentSet.Contains(InboxIdentity(e))).ToList();
```

Runs **only on the failure path**. Because the transaction aborted, anything found is a
pre-existing document, i.e. a genuine duplicate — so unlike the plan's sketch (which mapped
`WriteErrors[i].Index` back to envelopes and could only report what the fail-fast server reported),
this list is **complete and precise**, and it does not depend on the driver's exception shape.

**Empty-probe case = intra-batch duplicate.** If the probe returns nothing yet the failure was
duplicate-key, two envelopes *within the batch* shared an identity (possible in
`IdAndDestination` mode only if the same envelope is delivered twice to the same destination in one
batch; in `IdOnly` mode, whenever a batch repeats an envelope id). Rule: fall back to intra-batch
detection — group `envelopes` by `InboxIdentity` and report the members of any group with count > 1;
if that is also empty, rethrow the original exception unchanged. Rationale:
`DuplicateIncomingEnvelopeException` must never be constructed with an empty list, and a genuinely
unexplained failure must surface as itself.

### 1e. Alternatives recorded and rejected

| Option | Why rejected |
|---|---|
| **B — compensating delete** (keep unordered insert; on duplicate, delete the freshly-inserted docs by `_id`, then throw) | A crash between the insert and the compensating delete re-creates the exact stranding bug being fixed, and there is no durable record of what to compensate. Strictly weaker than a server-side abort. Preserving the "complete dupe list" was its only advantage over the plan's Option-A sketch — and §1d recovers that property anyway. |
| **C — eager existence pre-check then insert** (probe `ExistsAsync` for the batch first, insert only the fresh ones) | TOCTOU: a concurrent insert between probe and insert still yields a partially-persisted batch. It also changes the exception contract (no `DuplicateIncomingEnvelopeException` when the race is lost). The store's existing eager-idempotency probe (`Inbox.cs:53-58`) is a *different* problem — avoiding an abort inside a handler's transaction — and is not a precedent for the batch path. |
| **D — leave as is, "the receiver retries anyway"** | The receiver's retry is exactly what breaks; see §1a. |

### 1f. Scope guards

- The **single-envelope** overload (`Inbox.cs:9-20`) is **out of scope** — unchanged, no session, no
  transaction. Its `MongoWriteException` → `DuplicateIncomingEnvelopeException` mapping stays.
- Keep `IsOrdered = false` on the insert **with a comment** that it is inert with respect to error
  handling inside a transaction (the server aborts on the first write error regardless) and is kept
  only for the success path's batching behavior. Dropping it would silently change the
  no-error path.
- `IMessageInbox`/`IMessageStore` signatures: **unchanged**.
- Semver character: **pure bug fix (patch)**. No new exception type, no new public API; the
  externally observable change is "a failed batch leaves nothing behind."

> **Binding contract (F8).** `StoreIncomingAsync(IReadOnlyList<Envelope>)` wraps the batch insert in
> `session.WithTransactionAsync(...)` with **explicit** `TransactionOptions` of
> `w:majority + j:true` / `readConcern:majority`; on a duplicate-key failure (recognised across
> `MongoBulkWriteException<T>` / `MongoWriteException` / `MongoCommandException` / inner exceptions,
> by category or code 11000) it throws `DuplicateIncomingEnvelopeException` whose list is built by a
> **single post-abort `_id` existence probe**, falling back to intra-batch duplicate detection, and
> finally to rethrowing the original exception. Any non-duplicate write error rethrows unchanged.
> The single-envelope overload is untouched. Tests assert **persistence count** (nothing from the
> batch survives) as the load-bearing fact; the dupe list may additionally be asserted complete,
> but a `≥1` assertion is sufficient and preferred for robustness.

---

## 2. Contract 2 (LD3 / OQ4) — two-tick-confirmed dead-node ownership release

**Decision: two-tick confirmation, with the release write keyed on a positive `In(confirmed)`
whitelist and the reads ordered owned-before-live. Adopted.** (Transaction wrap and per-number
recheck rejected in §2f.)

### 2a. The defect, restated precisely

`ReleaseDeadNodeOwnershipAsync` (`Internals/MongoDbMessageStore.Durability.cs:169-194`) reads live
node numbers (`:177-180`), appends `AnyNode` (`:183`), then issues two independent
`UpdateManyAsync(Filter.Nin(x => x.OwnerId, liveNumbers), …)` writes (`:185-188`, `:190-193`). The
`Nin` is a **blacklist over a stale snapshot**: any node number *not* in the snapshot is released,
including a number that came into existence after the read. The doc comment's safety claim
(`:172-173`, *"a live node always has a node document, so its in-flight work is never touched"*) is
therefore false — a node that registers and claims between the read and the writes has its
in-flight envelopes released to `AnyNode`, so two nodes can process the same message.

The RDBMS Main-store analogue is immune because its liveness sub-select and its release write are
one statement (`ReleaseOrphanedMessagesOperation.cs:32,34`). Its **ancillary** analogue is *not*:
`ReleaseOrphanedMessagesForAncillaryOperation` (`DurabilityAgent.cs:237`) consumes an
`activeNodeNumbers` snapshot loaded once per tick by the caller (`DurabilityAgent.cs:96-110`) —
the same read-then-write shape as ours. So the framing "the RDBMS mirror is atomic" is only true of
the Main path; this design should not be sold as catching up to a universally-safe reference
implementation. It is a fix for a shape that upstream also ships in one of its two variants.

### 2b. Algorithm

```
private HashSet<int>? _previousDeadOwners;          // per-store; recovery loop is the only caller

ReleaseDeadNodeOwnershipAsync(token):
  // (1) OWNED FIRST — see 2c for why this order is load-bearing.
  owned = distinct(Incoming.OwnerId) ∪ distinct(Outgoing.OwnerId)

  // (2) THEN LIVE.
  live  = { NodeDocs.AssignedNodeNumber } ∪ { AnyNode }

  deadNow   = owned \ live
  confirmed = _previousDeadOwners is null ? ∅ : deadNow ∩ _previousDeadOwners
  _previousDeadOwners = deadNow                    // store BEFORE the early return

  if confirmed is empty: return

  await Incoming.UpdateManyAsync(Filter.In(x => x.OwnerId, confirmed), Set(OwnerId, AnyNode))
  await Outgoing.UpdateManyAsync(Filter.In(x => x.OwnerId, confirmed), Set(OwnerId, AnyNode))
```

Notes that are part of the contract:

- **`Filter.In(confirmed)`, never `Filter.Nin(live)`.** The write must name the numbers it releases.
  A number that appeared after the reads cannot be in `confirmed`, so it cannot be released — this
  is what closes the read-then-write window, independently of the two-tick rule.
- **`_previousDeadOwners` is assigned before the early return.** Otherwise a tick that finds nothing
  dead would leave a stale set in place and the *next* tick could confirm against an observation
  older than one interval.
- **State lives on `MongoDbMessageStore`** (a private field in the `Durability.cs` partial), not on
  the agent. Rationale: the method is a store method, and F2 §3d found that
  `IMessageStore.StartScheduledJobs` (`Internals/MongoDbMessageStore.cs:80`) and `BuildAgent`
  (`:82-86`) each construct a *new* `MongoDbDurabilityAgent` — so "exactly one agent instance per
  store" is not guaranteed by construction. Per-store state keeps one dead-set per database
  regardless of how many agents exist. F2 also confirmed the second (scheduled-jobs) instance is
  never started in the submodule (`.StartAsync()` has no call site; only `.StopAsync()` from
  `WolverineRuntime.Disposal.cs`), so today there is exactly one live caller and **no locking is
  required**. If a future Wolverine version does start that second instance, the per-store field
  degrades to "two observers sharing one set", which is conservative for the *live-node* race
  (§2c's ordering argument does not depend on the interval length) but weakens the temporal spacing
  of the two observations — record that as the trigger to add an `Interlocked`/`lock` guard, not as
  a reason to move the state onto the agent.
- Distinct reads: `Incoming.Distinct(x => x.OwnerId, FilterDefinition<IncomingMessage>.Empty)` and
  the `Outgoing` equivalent. `ownerId` is the **prefix** of an existing index in both collections
  (`Internals/MongoDbMessageStore.Admin.cs:28` — `{ownerId, receivedAt}`; `:42` —
  `{ownerId, destination}`), so an unfiltered distinct on that field is servable by a
  `DISTINCT_SCAN` (one seek per distinct value) rather than a collection scan. This is not a
  correctness claim; if the multinode timing bar tightens, confirm with `explain()` before
  concluding the reads are the cost.

### 2c. Soundness argument (goes in the method comment, condensed — see §2e)

Let *K* and *K+1* be consecutive ticks, and let *N* ∈ `confirmed` at tick *K+1* (so *N* ∈ `deadNow`
at both *K* and *K+1*). Claim: no live node owns envelopes under *N*.

1. **`deadNow` membership requires prior ownership.** *N* ∈ `deadNow` ⇒ *N* ∈ `owned` ⇒ some
   incoming/outgoing document carried `OwnerId == N` at the moment of the owned-read.
2. **Ownership requires completed registration.** A node learns its number only as the return value
   of `INodeAgentPersistence.PersistAsync`, which allocates it from the monotonic counter
   (`Internals/MongoDbMessageStore.NodeAgents.cs:16-20`) and **then** writes the node document
   (`:24`) before returning (`:26`). F2 §3d proved (against
   `NodeAgentController.StartLocalProcessing.cs:15,26`, `WolverineRuntime.HostService.cs:160`, and
   `NodeAgentController.StartAgentAsync`) that a node's own durability agent — the only thing that
   claims — cannot start until that call has completed and the leader has assigned the agent from
   the *persisted* node table. Therefore: any number observed in `owned` belonged to a node whose
   node document **existed before** that observation.
3. **Read order makes the liveness check strictly later than the ownership evidence.** Because the
   owned-read precedes the live-read *within a tick*, `N` ∈ `owned` ∧ `N` ∉ `live` means: the node
   document that provably existed at time *t_owned* was **absent** at *t_live > t_owned*. It was
   deleted — and node-document deletion happens in `NodeAgentController.cs:136`, i.e. only after
   `stopAllAgentsAsync()` (`:120`) and (in a normal shutdown) after
   `ReleaseAllOwnershipAsync` at `WolverineRuntime.HostService.cs:390`. A node that is merely
   *starting* can never produce this pattern. **This alone would fix the plan's originally-cited
   race** (register-then-claim between read and write) even with a single tick.
4. **Monotonic, never-reused numbers make the two-tick confirmation total.** Per the documented
   T4.6 decision (`CLAUDE.md`: *"the node-number counter … is a pure monotonic increment that never
   reuses a freed slot"*, `NodeAgents.cs:16-20`), a number issued after tick *K* is strictly greater
   than every previously issued number, so it **cannot** appear in tick *K*'s `deadNow`. Hence
   `confirmed` at *K+1* contains only numbers that were already allocated, already owning, and
   already document-less at tick *K* — dead for the **entire interval** between the two ticks, not
   merely at one instant. Re-registration of the "same" node also allocates a fresh number
   (`PersistAsync` increments unconditionally), so a number cannot come back to life.

Steps 3 and 4 are independent, which is deliberate: the fix does not rest on a single invariant.
Even if a future change broke monotonicity (T4.6's documented extension point is "track the lowest
free slot"), step 3's ordering argument would still exclude a *starting* node — and the `In(confirmed)`
whitelist would still exclude any number unobserved at both reads.

**Residual exposure (documented, not fixed here).** If a live node's document is deleted while the
node is still running and claiming — e.g. a heartbeat reaper removing a node that is partitioned or
GC-paused rather than dead — two-tick confirmation will (after one extra interval) release its
in-flight envelopes, allowing double processing. This is the **same** semantic the RDBMS
single-statement release has (it releases the moment the node row is gone), so it is not a
regression and not in F11's scope. Fully closing it needs a fencing/epoch token on ownership —
already tracked as the T4.6 "lease fencing token" follow-up. Say so in `FOLLOWUPS.md` only if it is
not already covered there; do not chase it in F11.

### 2d. Cost: one recovery interval of extra rescue latency

A crashed node's envelopes are now released on the **second** tick that observes them, so worst-case
rescue latency grows by one `DurabilitySettings.ScheduledJobPollingTime`
(`Internals/MongoDbDurabilityAgent.cs:50`, `:76`). Consequences:

- **Multinode bar (F11).** `Category=multinode` must be green **5× consecutively per TFM**. If a
  fact times out, widen the **observation window only**, with written justification in the PR — never
  weaken an assertion, never shorten the confirmation to one tick.
- **Graceful shutdown is unaffected**: `ReleaseAllOwnershipAsync` (`HostService.cs:390`;
  `Internals/MongoDbMessageStore.Admin.cs:119-124`) releases ownership directly, without going
  through the dead-node path. The added latency applies to *crashes* only.

### 2e. Required wording changes

**Method comment** (`Internals/MongoDbMessageStore.Durability.cs:169-174`) — the current safety claim
is the falsified invariant and must go. Replacement (adapt prose freely; the four numbered facts and
the "In, not Nin" point are mandatory):

```csharp
/// <summary>
/// Releases incoming/outgoing ownership held by node numbers that are confirmed dead: a number
/// must be observed as owned-but-unregistered on TWO consecutive recovery ticks before its
/// envelopes are released, and the release write names those numbers positively
/// (Filter.In(confirmed)) rather than excluding a possibly-stale live snapshot (Filter.Nin(live)).
///
/// Why this is sound (do not weaken to a single tick):
///  1. A number only enters the candidate set if some document already carries it as OwnerId.
///  2. A node cannot own anything before INodeAgentPersistence.PersistAsync has written its node
///     document (PersistAsync allocates the number and writes the doc before returning; the leader
///     can only assign this store's durability agent from the persisted node table).
///  3. The owned-set read happens BEFORE the live-set read, so "owned but not live" means the node
///     document existed at the earlier instant and was gone at the later one — i.e. deleted on
///     shutdown (NodeAgentController), never merely mid-registration.
///  4. Node numbers are monotonic and never reused (see the T4.6 decision in CLAUDE.md), so a
///     number issued after the previous tick cannot appear in the previous tick's dead set.
///     Together with (3), a confirmed number was dead for the whole interval between ticks.
///
/// Cost: a crashed node's envelopes are rescued one recovery interval later than before.
/// Residual (out of scope, see FOLLOWUPS): if a *live* node's document is deleted by a reaper
/// while it is still claiming, its work is released after two ticks — the same semantic the RDBMS
/// single-statement release has; closing it requires an ownership fencing token.
/// </summary>
```

**`CLAUDE.md`** — replace the current bullet (`CLAUDE.md:147`):

> - **Dead-node ownership release (Balanced mode only):** each recovery tick calls
>   `ReleaseDeadNodeOwnershipAsync`, which releases incoming/outgoing ownership held by node numbers
>   with no live node document. Runs before orphan recovery so released envelopes can be re-claimed
>   in the same tick.

with:

> - **Dead-node ownership release is two-tick-confirmed (Balanced mode only):** each recovery tick
>   calls `ReleaseDeadNodeOwnershipAsync`, which computes the node numbers that are *owned* in
>   `wolverine_incoming_envelopes`/`wolverine_outgoing_envelopes` but have no live `wolverine_nodes`
>   document, and releases only the numbers that were **also** dead on the previous tick
>   (`Filter.In(confirmed)`, never `Filter.Nin(liveSnapshot)`). Reading the owned set *before* the
>   live set, plus monotonic never-reused node numbers (see the node-number-reuse decision below),
>   means a number confirmed dead was dead for the whole interval — a node that registers and claims
>   between the read and the write can never be released. Cost: a crashed node's envelopes are
>   rescued one recovery interval later. Still runs before orphan recovery so released envelopes are
>   re-claimable in the same tick. **A future change to reuse freed node numbers would invalidate
>   this argument** — see the soundness comment on the method.

The cross-reference in the last sentence is deliberate (plan risk **R6**): the T4.6 node-number
bullet and this bullet must point at each other so a future "reuse freed slots" change trips over
the dependency.

### 2f. Alternatives recorded and rejected

| Option | Why rejected |
|---|---|
| **Wrap read + writes in a Mongo transaction** | Does **not** fix the race. A transaction gives snapshot isolation, and a snapshot read of `wolverine_nodes` does not conflict with another session's *insert* of a new node document — there is no write-write conflict to detect, so the transaction commits happily with the same stale liveness view. Transactions here would add cost and a new abort/retry failure mode while leaving the defect intact. |
| **Per-number liveness recheck before each release** (re-read `NodeDocs` for each candidate immediately before the update) | Shrinks the window; does not close it. The recheck is still a separate round trip from the write, so a registration can land in between. Also multiplies round trips per tick. |
| **`findAndModify` per document with a liveness guard** | MongoDB cannot express "and this owner has no node document" as a single-document filter — there is no cross-collection predicate. Would require a lookup-aggregation pipeline update; far more machinery for the same guarantee two-tick provides for free. |
| **Do nothing, rely on the 75 % lease margin** | The lease governs *leadership*, not envelope ownership. Unrelated mechanism. |

### 2g. Existing-test amendment (not a weakened assertion)

`src/Wolverine.MongoDB.Tests/dead_node_ownership_release.cs:45-52` asserts release after a **single**
`ReleaseDeadNodeOwnershipAsync` call. That expectation encodes the defect. F11 must **amend it to
two calls** (first call: still owned; second call: released) — either by editing the existing fact or
by folding it into the new `dead_node_release.cs` and deleting it. This is a deliberate contract
change, must be called out explicitly in F11's PR body, and is **not** an instance of the plan's
"no weakened assertions" prohibition: the new assertion is strictly stronger (it pins *when* release
happens, in both directions).

Semver character: **bug fix (patch)**, with a documented behavior-timing change (one extra interval).

> **Binding contract (F11).** `ReleaseDeadNodeOwnershipAsync` reads the distinct owner set **first**,
> the live node numbers **second**, computes `deadNow = owned \ (live ∪ {AnyNode})`, releases only
> `deadNow ∩ _previousDeadOwners` via `Filter.In(x => x.OwnerId, confirmed)`, and assigns
> `_previousDeadOwners = deadNow` on **every** path including the early return. State is a private
> field on `MongoDbMessageStore`; no locking (single live caller, per F2 §3d). The method comment
> carries the four-step soundness argument and the residual-exposure note; `CLAUDE.md:147` is
> rewritten per §2e. `Filter.Nin` must not appear in the release write.

---

## 3. Contract 3 (F10) — incoming claims key on the document `_id`

### 3a. Decision

Both the claim and the post-claim re-read switch from the `EnvelopeId` (raw envelope Guid) field to
the document `_id` produced by `InboxIdentity(envelope)`
(`Internals/MongoDbMessageStore.cs:46-48`, `:72`). In `MessageIdentity.IdAndDestination` the `_id` is
`$"{e.Id}|{destination}"`, so keying on it is **destination-scoped by construction**; in the default
`IdOnly` the `_id` **is** `e.Id.ToString()`, so the filter values are byte-identical to today and
`IdOnly` consumers observe no change whatsoever.

### 3b. The two call sites

**Claim** — `Internals/MongoDbMessageStore.cs:119-136`:

```csharp
var ids = incoming.Select(InboxIdentity).ToList();          // was: incoming.Select(x => x.Id)  (:126)

return Incoming.UpdateManyAsync(
    Builders<IncomingMessage>.Filter.And(
        Builders<IncomingMessage>.Filter.In(x => x.Id, ids),                          // was: In(x => x.EnvelopeId, ids)  (:133)
        Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, MongoConstants.AnyNode)),
    Builders<IncomingMessage>.Update.Set(x => x.OwnerId, ownerId));
```

The CAS guard (`OwnerId == AnyNode`) and the existing comment (`:128-130`) stay as they are.

**Re-read** — `Internals/MongoDbMessageStore.Durability.cs:153-161`:

```csharp
var byId = envelopes.ToDictionary(InboxIdentity);            // injective; see below
var claimedIds = await Incoming.Distinct(x => x.Id,
    Builders<IncomingMessage>.Filter.And(
        Builders<IncomingMessage>.Filter.In(x => x.Id, byId.Keys),
        Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, nodeNumber)),
    cancellationToken: token).ToListAsync(token);

var claimed = claimedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
```

`ToDictionary(InboxIdentity)` is safe: the page comes from distinct documents
(`LoadPageOfGloballyOwnedIncomingAsync`, `Internals/MongoDbMessageStore.cs:103-117`), so their `_id`s
are unique. This is a second, quieter benefit of the change — the old mapping keyed on `e.Id`
(`Durability.cs:161`), which is precisely the value that is **not** unique per destination in
`IdAndDestination` mode, so the old code could not have mapped winners back unambiguously even if
the filter had been right. Keep the `Distinct` shape (minimal diff; `_id` is indexed).

### 3c. Scope guards and adjacent code that must NOT change

- **No `IMessageStore`/`IMessageInbox` signature changes.** `ReassignIncomingAsync(int, IReadOnlyList<Envelope>)`
  keeps its shape; the identity translation happens inside.
- **The `envelopeId` index stays** (`Internals/MongoDbMessageStore.Admin.cs:29-31`). It is still used
  by the scheduled-message surface (`Internals/MongoDbMessageStore.ScheduledMessages.cs:13`, `:55`).
  Its comment (`Admin.cs:29`: *"Reassignment CAS and reschedule filter by EnvelopeId"*) becomes half
  wrong when the CAS moves to `_id` — **update the comment** to name only the reschedule/query use.
- **`IScheduledMessages.RescheduleAsync(Guid envelopeId, …)`** (`ScheduledMessages.cs:51-57`) stays
  `EnvelopeId`-keyed. Its upstream signature takes a bare `Guid`, so "all documents for this
  envelope id" follows from the contract's identity unit, not from a filter mistake. Do not
  "fix" it in F10.
- **`LoadPageOfGloballyOwnedIncomingAsync`'s sort** (`MongoDbMessageStore.cs:112`, ascending
  `EnvelopeId`) is a stable-paging concern, not identity — leave it.

Semver character: **pure bug fix (patch)**; no observable change in the default `IdOnly` mode.

> **Binding contract (F10).** `ReassignIncomingAsync` filters `Filter.In(x => x.Id, incoming.Select(InboxIdentity))`
> AND `OwnerId == AnyNode`; `RecoverOrphanedIncomingAsync`'s post-claim re-read filters the same
> `_id` set AND `OwnerId == nodeNumber`, mapping winners back through an `InboxIdentity`-keyed
> dictionary. `IdOnly` behavior is byte-identical (`InboxIdentity(e) == e.Id.ToString()`). No
> interface signatures change; the `envelopeId` index is retained with a corrected comment;
> `RescheduleAsync` is untouched.

---

## 4. Contract 4 (F12) — `StopAsync` awaits its loops, bounded, then disposes

### 4a. Decision

`MongoDbDurabilityAgent.StopAsync` (`Internals/MongoDbDurabilityAgent.cs:130-140`) becomes
`async Task`: cancel → **await both loop tasks with a bounded timeout** → dispose both
`CancellationTokenSource`s → `Status = AgentStatus.Stopped`. Today it fires cancellation, calls
`SafeDispose()` on two still-running `Task`s (which swallows the `InvalidOperationException` that
`Task.Dispose()` throws for a non-completed task) and returns `Task.CompletedTask` — so the loops
are still running when the caller believes the agent stopped, and neither CTS is ever disposed.

### 4b. Algorithm

```csharp
internal TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(5);   // internal-only knob, see 4d

public async Task StopAsync(CancellationToken cancellationToken)
{
    if (Interlocked.Exchange(ref _stopping, 1) == 1) return;     // idempotent; see 4c

    try
    {
        await _cancellation.CancelAsync();

        var loops = new[] { _recoveryTask, _scheduledJob }.Where(t => t is not null)!;
        if (loops.Any())
        {
            try
            {
                await Task.WhenAll(loops).WaitAsync(StopTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "MongoDB durability loops did not observe cancellation within {Timeout}; " +
                    "continuing shutdown. In-flight recovery writes may still be in progress.",
                    StopTimeout);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loops await PeriodicTimer/Task.Delay on the linked token, so they
                // complete in the Canceled state. Also covers an aborting caller's token.
            }
        }
    }
    finally
    {
        // Dispose the linked source before the sources it links, and only after the loops have
        // stopped touching _combined.Token / _combined.IsCancellationRequested.
        _combined.Dispose();
        _cancellation.Dispose();
        Status = AgentStatus.Stopped;
    }
}
```

Contract points:

- **Order is load-bearing.** Cancel, then await, then dispose. Disposing `_combined` while a loop
  still reads `_combined.Token`/`IsCancellationRequested`
  (`Internals/MongoDbDurabilityAgent.cs:52`, `:69`, `:78`, `:89`) can throw
  `ObjectDisposedException` inside the loop; the current code has exactly that hazard.
  `_combined` (the linked source, `:33`) is disposed before `_cancellation` (`:20`).
- **Expected terminal state is `Canceled`, not `RanToCompletion`.** Both loops await
  `Task.Delay(recoveryStart, _combined.Token)` (`:49`, `:75`) and
  `timer.WaitForNextTickAsync(_combined.Token)` (`:69`, `:89`), and the `catch` filters are
  `when (!_combined.IsCancellationRequested)` (`:64`, `:84`) — so cancellation propagates out of the
  loop body. `Task.WhenAll` therefore throws `OperationCanceledException`/`TaskCanceledException` on
  the normal path; swallowing it is the expected case, not an error. **Tests must assert
  `task.IsCompleted`, not `IsCompletedSuccessfully`.**
- **`SafeDispose()` on the tasks goes away.** It was never doing anything useful; a completed
  `Task` needs no disposal in modern .NET.
- **The caller's token is passed to `WaitAsync`** so an aborting host is not held for the full bound;
  `NodeAgentController.stopAllAgentsAsync` passes `CancellationToken.None`
  (`external/wolverine/src/Wolverine/Runtime/Agents/NodeAgentController.cs:146-160`), so in practice
  the 5 s bound governs and tests are deterministic.
- **`Status = AgentStatus.Stopped` is set in `finally`** — the timeout path must not leave the agent
  reporting `Running`.

### 4c. Idempotency

The plan's F5 inventory requires a second `StopAsync` not to throw. With CTS disposal now happening,
a naive second call would hit `ObjectDisposedException` on `CancelAsync()`. Bind: an
`Interlocked.Exchange`-guarded `_stopping` flag; the second call returns immediately (the agent is
already `Stopped`). Also relevant in practice — `WolverineRuntime.Disposal` stops the
`DurableScheduledJobs` agent instance in addition to the agent-family path (F2 §3d), so double-stop
of *some* agent instance is a real shape.

### 4d. The timeout knob and test observability

- `StopTimeout` is **`internal`**, default 5 s, settable only so the "does not hang" fact can use a
  short bound deterministically. It must **not** become public API, and must not be surfaced on
  `MongoDbPersistenceOptions` — this is not a tuning knob for consumers.
- Expose the loop tasks for assertion as `internal Task? RecoveryTask => _recoveryTask;` /
  `internal Task? ScheduledJobTask => _scheduledJob;` (names at the implementer's discretion).
  `src/Wolverine.MongoDB/Wolverine.MongoDB.csproj:14` already grants
  `InternalsVisibleTo Wolverine.MongoDB.Tests`, so **no csproj change is needed** — contrary to the
  F5 inventory's "may be needed" note.

### 4e. The residual upstream window — document, do not chase

Even a perfectly-awaited `StopAsync` cannot close the shutdown-ordering window, because the ordering
is upstream's:

- `WolverineRuntime.HostService.StopAsync` releases ownership at
  `external/wolverine/src/Wolverine/Runtime/WolverineRuntime.HostService.cs:390`
  (`ReleaseAllOwnershipAsync`) and only *then* tears down agents at `:412`.
- Teardown → `NodeAgentController.StopAsync` (`NodeAgentController.cs:118`) → `stopAllAgentsAsync()`
  (`:120`) → node-document delete (`:136`).

So the release happens **before** the durability agent is asked to stop: a recovery tick that is
already in flight at `:390` can re-claim envelopes *after* ownership was released, and the node
document is deleted moments later. Awaiting the loops in `StopAsync` shrinks the tail (no *new*
claims are issued after `StopAsync` returns, which is what makes the subsequent node-document delete
safe) but cannot reorder `:390` before `:412`. **Contract:** state this in a comment on `StopAsync`,
naming the upstream file:line pair, and stop there. F12 must not attempt to compensate (no
"re-release after stop", no `DrainAsync` hooks) — that would be a change to upstream's shutdown
contract, made from a provider.

Semver character: **bug fix (patch)**; no API change (both new members are `internal`).

> **Binding contract (F12).** `StopAsync` is `async Task`, guarded idempotent via an interlocked
> flag: cancel `_cancellation`; `await Task.WhenAll(loops).WaitAsync(StopTimeout, cancellationToken)`
> inside a try/catch swallowing `TimeoutException` (log **Warning**) and `OperationCanceledException`
> (expected — the loops end Canceled); in `finally` dispose `_combined` then `_cancellation` and set
> `Status = AgentStatus.Stopped`. `StopTimeout` defaults to 5 s and is `internal`-only. Loop tasks
> are exposed as `internal` properties for assertions (`InternalsVisibleTo` already present). The
> `SafeDispose()` calls are removed. The upstream release-before-teardown window
> (`HostService.cs:390` vs `:412`) is documented in a comment and otherwise left alone.

---

## 5. Cross-task interactions and sequencing

| Interaction | Ruling |
|---|---|
| **F8 ∥ F10 ∥ F11 ∥ F12 file disjointness** | F8 → `Inbox.cs`; F10 → `MongoDbMessageStore.cs` + `Durability.cs` (claim/re-read only); F11 → `Durability.cs` (`ReleaseDeadNodeOwnershipAsync` only) + `CLAUDE.md`; F12 → `MongoDbDurabilityAgent.cs`. F10 and F11 both touch `Durability.cs` but **non-overlapping methods** (`:122-167` vs `:169-194`); whichever merges second rebases with a trivial conflict at most. They may run in parallel as the plan says. |
| **F8 → F17 item 1 (DLQ replay batching)** | Already covered by plan risk **R8**: after F8 a batch containing one already-present envelope persists nothing, so F17's proposed `StoreIncomingAsync(list)` in `ReplayDeadLettersAsync` needs the per-letter fallback on `DuplicateIncomingEnvelopeException`. Note additionally that the **current** per-letter loop (`Internals/MongoDbMessageStore.Durability.cs:43-52`) already catches that exception per letter — the fallback F17 adds must preserve exactly that convergence behavior (re-insert may fail; deleting the DLQ document is what converges the replay). |
| **F11 → F17 items 2-4** | Independent (`NodeAgents.cs`), except that F17's "cache the collection properties" item touches `NodeDocs` (`Internals/MongoDbMessageStore.NodeAgents.cs:8`), which F11's release reads. Behavior-neutral; no ordering constraint beyond the plan's. |
| **F10 → F16 item 4 (`AnyNode` sentinel)** | F10 leaves `MongoConstants.AnyNode` usage in place; F16 may normalise spellings afterwards. No conflict. |
| **F12 → multinode suites** | Every Balanced test's teardown now genuinely waits for loop completion. If a multinode fact slows measurably, that is the fix working (previously the process raced ahead of its own recovery loops); do not shorten `StopTimeout` to compensate. |
| **F8/F10/F11 all sit on the Balanced hot path** (plan risk **R9**) | Each carries the 5×-consecutive multinode bar and the plan's escalation rule verbatim: two non-obvious verification failures, or a fact that contradicts this document, means **stop and report** — do not improvise a different mechanism. |

---

## 6. Adjacent observations (out of scope for F8/F10/F11/F12)

Recorded so they are not silently absorbed into an in-flight task. None is a licence to expand
scope.

1. **The DLQ transaction has the same write-concern gap §1c describes.**
   `MoveToDeadLetterStorageAsync` (`Internals/MongoDbMessageStore.Inbox.cs:154-165`) calls
   `WithTransactionAsync` with **no** `TransactionOptions`, so its commit is governed by the
   consumer's client default, not the store's `w:majority, j:true` pin. **Recommendation for F8:**
   because it is the same file, the same one-line change, and the same durability contract, pass the
   shared `InboxTransactionOptions` there too — and say so explicitly in the PR body as a
   deliberate, named extra. If F8's implementer prefers a hard scope line, file it in `FOLLOWUPS.md`
   instead; what must not happen is noticing it and doing neither.
2. **`ScheduledMessages` identity is `EnvelopeId`-keyed by upstream contract** (§3c) — deliberately
   left alone.
3. **`Distinct` cost per recovery tick** (§2b) — index-prefix servable; measure before optimising.
   Natural home is F17 if it ever matters.
4. **Ownership fencing/epoch** (§2c residual) — already a T4.6 follow-up; nothing here changes it.

---

## 7. What is now settled (and what still needs this gate)

**Settled — implement without re-debating:** OQ3 → **LD2 Option A** (transaction wrap), with the
post-abort probe of §1d and the mandatory transaction options of §1c. OQ4 → **LD3 two-tick
confirmation**, with owned-before-live read order and an `In(confirmed)` write. F10 → claim and
re-read by document `_id`. F12 → bounded await, then dispose, idempotent, with the upstream window
documented.

**Come back to this gate (do not improvise) if:**

- the driver's in-transaction failure shape defeats the §1b classifier even after the
  **[verify empirically]** step (F8) — bring the observed exception type, code, and message;
- the multinode suite cannot reach 5× green with the extra release interval (F11) after widening
  observation windows only;
- awaiting the loops in `StopAsync` deadlocks against a Wolverine teardown path (F12) — that would
  contradict §4e's reading of the upstream ordering and needs re-verification, not a `Task.Run`
  workaround;
- any citation in §0 or §2c fails to reproduce (a submodule bump between now and implementation is
  the likeliest cause — re-verify, then note the drift in the task's PR as §0 does here).

**Semver roll-up for F20/OQ6:** all four tasks are **patch-level bug fixes**. Unlike the identity
lane (F6/F7, which turn silent misbehavior into thrown exceptions), nothing in this lane adds a new
failure mode for previously-working consumers: F8 changes what survives a failed batch, F10 is a
no-op in the default `IdOnly` mode, F11 delays a rescue by one interval, F12 makes a shutdown wait.
