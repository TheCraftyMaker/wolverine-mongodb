# Task S4 — MongoDB Saga Document Model, Identity & Concurrency Design

> **Plan reference:** `2026-06-18-saga-persistence.md` — Task S4 (Design Gate)  
> **Synthesizes:** S1 (repo analysis), S2 (Wolverine API), S3 (Cosmos/RavenDb comparison)  
> **Date:** 2026-06-18  
> **Status:** Complete — all seven design decisions recorded; Lead Open Design Decision resolved.  
> **Gates:** S6, S7, S8, S9, S10, S11 — do not start S6 without reading §3–§8 of this document.

> **Revision (2026-06-18, Opus 4.8 — re-run of the design gate against the `external/wolverine`
> source).** The first pass (merged as PR #90) was produced on the wrong model. This revision
> verified the frame contracts directly against the submodule and corrected three defects that
> would compile and pass *some* compliance facts yet break real sagas:
> 1. **`DetermineInsertFrame` / `DetermineUpdateFrame` receive only the `saga` variable — not
>    `sagaId`** (`IPersistenceFrameProvider.cs:27,29`; only `DetermineLoadFrame` and
>    `DetermineDeleteFrame` get `sagaId`). The previous insert/update snippets filtered on a
>    `sagaId` variable that is out of scope there. The `_id` value must be read from the saga
>    document. Cosmos avoids this because `UpsertItemAsync(saga)` derives the id internally
>    (`CosmosDbPersistenceFrameProvider.cs:140-163`); MongoDB's `ReplaceOneAsync` needs an
>    explicit filter, so this is a genuine MongoDB-specific divergence.
> 2. **The id value must not be hard-coded as `saga.Id`.** Wolverine resolves the saga **state's**
>    identity member by precedence (`SagaChain.DetermineSagaIdMember(sagaType, sagaType)`,
>    `SagaChain.cs:208-224`), which need not be named `Id`. The frame resolves the member name.
> 3. **Normative `_id`-mapping constraint added (Decision 1).** The MongoDB driver only auto-maps
>    a member named `Id`/`id`/`_id` to BSON `_id`. A saga whose identity member is named otherwise
>    (Wolverine allows `SagaId`, `{Saga}Id`, …) would get an auto-generated `ObjectId` `_id`, and
>    `Find(Eq("_id", sagaId))` would silently never match. The constraint (identity member named
>    `Id`, or annotated `[BsonId]`) is what makes "no `[BsonId]` required" true for the compliance
>    and demo sagas — it must be stated, not assumed. (The S5 demo already satisfies it: state
>    member `Id`, with `[SagaIdentity]` on the message's `OrderId`.)

> **Independent review (2026-06-18, second smart-model pass, read-only against source).** An
> independent reviewer re-verified all of the above against `external/wolverine` and the local
> tree — every frame-contract, signature, and line citation **VERIFIED**, no blocking issues. It
> folded in four precision corrections (search "codex" / "🔎 codex"):
> 1. **Double-emit failure mode** (Decision 5) — the duplicate commit does **not** throw; it is a
>    no-op (commit is guarded by `if (IsInTransaction)`, `TransactionalFrame.cs:136-139`) but
>    `FlushOutgoingMessagesAsync` runs **twice** (`:141-145`, outside the guard). The guard is
>    still required; the bug is a double flush, not an exception.
> 2. **Guid `_id` is not "ceremony-free"** (Decisions 1 & 6) — the demo's Guid `_id` round-trip
>    relies on a **process-global** `GuidSerializer` (`InfrastructureBootstrap.cs:46`) plus
>    per-property `[BsonGuidRepresentation]` (`OrderSummary.cs`). MongoDB.Driver 3.x has no default
>    Guid representation. Since Decision 6 forbids the **library** registering a global serializer,
>    Guid-keyed sagas (S7/S10) need an explicit representation strategy — see Decision 6 and R14.
> 3. **`ModifiedCount == 0` OCC caveat** (Decision 4) — safe **only because `Version` always
>    changes**; noted to prevent a future spurious-conflict regression.
> 4. **`SagaCollectionName` is authoritative here** (Decision 2) — S5's snake_case example
>    (`wolverine_saga_order_fulfillment_saga`) is illustrative; this gate's `ToLowerInvariant()`
>    form wins.

---

## Lead Open Design Decision: **Option B — Native Id + Optimistic Concurrency**

**Chosen: Option B.** `Wolverine.MongoDB` targets full native-id + `Saga.Version` OCC support —
going deliberately beyond Cosmos/Raven parity.

**Rationale (confirmed by S1–S3):**
- Cosmos/Raven are both string-only and neither maps concurrency conflicts to `SagaConcurrencyException`.
  This is not the right model to clone — it limits id types unnecessarily and leaves a correctness
  gap (concurrent saga updates silently overwrite each other).
- MongoDB handles Guid, int, long, and string `_id` natively without any BSON ceremony — the demo
  already stores `Order.Id : Guid` as `_id` without special attributes (verified S1 §8).
- Wolverine's own **Marten**, **EF Core**, and **lightweight SQL** providers all use `Saga.Version`
  for OCC and throw `SagaConcurrencyException` (confirmed against
  `DatabaseSagaSchema.cs:99-110` in S3 §5b). The "Cosmos parity" option aligns with the two weakest
  providers; MongoDB should align with the stronger ones.
- The decomposition (S6 = Cosmos-parity baseline → S7 = native id → S8 = OCC) means Option A
  is a **self-contained milestone** within Option B. If S7 or S8 hit a wall, S6 ships alone
  with zero wasted effort.

**Option A fallback (explicit downscope path):**  
If S7 or S8 must be dropped: `DetermineSagaIdType → typeof(string)` and upsert-only storage
are sufficient to ship string-identified sagas. The S6 branch already implements this as the
`StoreSagaFrame(upsert: true)` baseline. S9 (string compliance) will be green. S10, S11-OCC,
and the Guid/int/long overloads of `MongoDbSagaHost.LoadState<T>` are simply omitted. Mark
those tasks as deferred in `FOLLOWUPS.md`.

---

## Decision 1: Document Shape

**Decision: store the saga POCO directly, no envelope wrapper.**

Mirror Cosmos and RavenDb: the collection document IS the `Saga` subclass. No intermediate
`SagaDocument<T>` wrapper is introduced.

**Saga-state identity member → BSON `_id` (normative constraint):**

The saga **state's** identity member is the one MongoDB stores as `_id` and that every saga
frame keys on. Two distinct identity resolutions are in play — do not conflate them:

- **Message-side identity** (where the saga id is *read from* on an incoming message): resolved
  by `SagaChain.DetermineSagaIdMember(messageType, sagaType)` with precedence `[SagaIdentity]` →
  `[SagaIdentityFrom]` → `{SagaType.Name}Id` → `{SagaType.Name minus "Saga"}Id` → `SagaId` → `Id`
  (`SagaChain.cs:208-224`). This governs the `sagaId` variable handed to the load/delete frames.
- **State-side identity** (the saga document's own id, stored as `_id`): resolved by the same
  method applied to the saga type itself, `DetermineSagaIdMember(sagaType, sagaType)`. This is the
  member the MongoDB document's `_id` must equal.

**The MongoDB .NET driver's default `IdMemberConvention` maps only a member named `Id`/`id`/`_id`
to BSON `_id`.** If a saga's state-side identity member is named anything else (e.g. `SagaId`,
`OrderFulfillmentId`), the driver does **not** map it to `_id` — it persists that member as an
ordinary field and **auto-generates an `ObjectId` `_id`**. Then `Find(Eq("_id", sagaId))` in the
load frame silently never matches, and the saga appears to "not exist" on every continue message.
This is a silent-corruption failure mode, not a compile error.

**Constraint:** a saga type persisted by `Wolverine.MongoDB` MUST have its state-side identity
member map to BSON `_id`, by one of:
1. **Name it `Id`** (recommended) — the driver's default convention maps it to `_id` with no
   annotation. This is what every compliance type (`public TId Id { get; set; }`) and the S5 demo
   `OrderFulfillmentSaga` (`public Guid Id`, set from `evt.OrderId` by the `Start` handler) do.
   Message routing to a differently-named member (e.g. `OrderId`) is handled independently with
   `[SagaIdentity]` on the message — it does **not** change the state's `_id`.
2. **Annotate the identity member with `[BsonId]`** if it cannot be named `Id`. Place it on the
   user/test saga type only.

For the compliance suite and the demo this constraint holds by construction, so **no `[BsonId]`
is required** for `_id` *mapping* on any planned saga. Document the constraint in S16 (README) so
downstream users naming their identity member something other than `Id` add `[BsonId]`.

> **🔎 codex — `_id` *mapping* vs Guid *representation* are separate concerns.** "No `[BsonId]`
> required" addresses only which member becomes `_id`. It does **not** mean a Guid-keyed saga
> serializes with no configuration: MongoDB.Driver 3.x has **no default Guid representation**, so a
> `Guid` `_id` needs an explicit representation (e.g. `GuidRepresentation.Standard`). The demo
> achieves this with a process-global `GuidSerializer` (`InfrastructureBootstrap.cs:46`) plus
> per-property `[BsonGuidRepresentation]` (`OrderSummary.cs`) — i.e. the Guid round-trip is **not**
> ceremony-free. Because Decision 6 forbids the **library** registering a global serializer, the
> Guid-id strategy for S7/S10 must come from the **saga type or the test/demo host**, not the
> provider — see Decision 6 and **R14**.

**`[BsonRepresentation]` policy:**  
Add representation attributes only on the **test/demo saga type** and only if a compliance fact
proves the default convention insufficient — never inject attributes into `Saga.cs` upstream or
register a global serializer. Consistent with the per-property
`[BsonRepresentation(BsonType.DateTime)]` decision in the existing provider.

**`Saga.Version` BSON:**  
`int Version` serializes as a BSON `int32` by default — no annotation needed.

---

## Decision 2: Collection Strategy

**Decision: one collection per saga type.**

Named `wolverine_saga_{sagatype.Name.ToLowerInvariant()}`.

**Rationale:** Cosmos uses one container for all documents only because CosmosDB forces it.
RavenDb uses a single session with type-prefix conventions. Neither choice is idiomatic for
MongoDB. One collection per saga type is standard MongoDB modeling: no cross-type `_id`
collision risk, independently indexed, independently enumerable for cleanup.

### Naming helper (add to `MongoConstants.cs`)

```csharp
public const string SagaCollectionPrefix = "wolverine_saga_";

public static string SagaCollectionName(Type sagaType)
    => $"{SagaCollectionPrefix}{sagaType.Name.ToLowerInvariant()}";
```

`sagaType.Name.ToLowerInvariant()` produces clean, lowercase identifiers for all compliance
types (`stringbasicworkflow`, `guidbasicworkflow`, `intbasicworkflow`, `longbasicworkflow`) and
demo types (`orderfulfillmentsaga`). Generic saga types (uncommon) would produce names like
`myworkflow\`1` — if this causes issues in S6, sanitize backtick and backtick-digit suffixes:

> **🔎 codex — this naming helper is authoritative.** The S5 demo/test-inventory doc
> (`2026-06-18-saga-demo-and-test-inventory.md`) shows a snake_case example
> (`wolverine_saga_order_fulfillment_saga`) and hedges "pending the S4 naming helper." That example
> is **illustrative only**; the form S6 implements is this gate's `ToLowerInvariant()` (no word
> separators → `wolverine_saga_orderfulfillmentsaga`). Test/demo `LoadState<T>` reads via
> `MongoConstants.SagaCollectionName(typeof(T))`, so the host and provider stay consistent
> automatically — but any hard-coded collection-name string in an S5-derived assertion must use
> the `ToLowerInvariant()` form.

```csharp
private static string SanitizeSagaTypeName(string name)
    => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9_]", "_");
```

**Default:** use `ToLowerInvariant()` first; switch to regex sanitizer only if a compliance
fact hits an invalid collection name at runtime.

### Cleanup: mandatory for compliance correctness

`AppFixture` uses a **fixed** database (`DatabaseName = "wolverine_tests"`) and calls
`store.Admin.RebuildAsync()` before suites. Today `ClearAllAsync`
(`MongoDbMessageStore.Admin.cs:67-78`) only deletes documents from the nine named system
collections. Per-saga collections are not touched — **stale saga documents leak between
compliance test facts**, e.g. `start_1` in run 2 would find a pre-existing saga and silently
use it rather than inserting a new one.

**Decision: enumerate + drop all `wolverine_saga_*` collections in `ClearAllAsync`.**

Dropping is cleaner than `DeleteMany` — it removes the collection and its indexes, avoiding
index accumulation. Pattern:

```csharp
// Append to ClearAllAsync after the existing system-collection deletes:
var cursor = await _database.ListCollectionNamesAsync();
var names = await cursor.ToListAsync();
foreach (var name in names.Where(n => n.StartsWith(MongoConstants.SagaCollectionPrefix)))
    await _database.DropCollectionAsync(name);
```

This is a **required S6 change** — failing to add it will cause compliance tests to leak
between runs on the shared fixture database.

---

## Decision 3: Saga Identity Types

**Decision: Option B — `DetermineSagaIdType` returns the saga's native identity-member type.**

### S6 baseline (string-only, Cosmos parity)

```csharp
public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    => typeof(string);
```

Passes `StringIdentifiedSagaComplianceSpecs`. The `sagaId` variable will be `string` for
the envelope-only identity path.

### S7 generalization (native id type)

```csharp
public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    => SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType()
       ?? typeof(string);
```

Mirrors the resolution in `LightweightSagaPersistenceFrameProvider.cs:80-83`. Enables
`GuidIdentifiedSagaComplianceSpecs`, `IntIdentifiedSagaComplianceSpecs`, and
`LongIdentifiedSagaComplianceSpecs`. **🔎 codex:** one deliberate deviation — lightweight
*throws* `ArgumentException` when no identity member resolves; this provider falls back to
`typeof(string)` instead (graceful, and only reached on the envelope-header-only path). This is a
choice, not an oversight.

### Scope of `DetermineSagaIdType` (S2 confirmed)

**Critical:** `DetermineSagaIdType` is called **only** for the envelope-header-only identity
path (`SagaChain.cs:290-292`). When the message type has a member matching saga identity
resolution (via `[SagaIdentity]`, `{SagaType}Id`, `SagaId`, or `Id`), `PullSagaIdFromMessageFrame`
is used instead and the member's own runtime type governs the `sagaId` variable.

**Implementation consequence for frames:** `LoadSagaFrame` and `DeleteSagaFrame` receive the
`sagaId` variable from Wolverine core; they must key `_id` off **whatever type that variable
has** (Guid, string, int, long) regardless of what `DetermineSagaIdType` returns. Do not
hard-code string filters in the load frame.

### Supported id types

| Type | BSON mapping | Notes |
|------|-------------|-------|
| `string` | BSON String | Default for envelope-only case in S6 |
| `Guid` | BSON Binary / UUID | Demo already uses this; driver handles it |
| `int` | BSON Int32 | |
| `long` | BSON Int64 | |

All four are in `SagaChain.ValidSagaIdTypes`. Strong-typed identifier structs (e.g.
`OrderId : struct`) are accepted by `SagaChain.IsValidSagaIdType` too; support them if
they arise naturally.

---

## Decision 4: Concurrency Model

**Decision: `Saga.Version`-guarded update frame throwing `SagaConcurrencyException`.**

This is Option B's key differentiator over Cosmos/Raven and directly satisfies the user's
explicit "optimistic concurrency" requirement.

### Insert frame (S6 baseline vs. S8 proper)

**S6 baseline (Cosmos-parity upsert):** Use `ReplaceOneAsync` with `IsUpsert = true` for
both insert and update. No version management. Last-write-wins.

**S8 proper insert:** Set initial `Version = 1`, then `InsertOneAsync`. Insert is **not**
version-guarded — there is no pre-existing document to conflict with.

```csharp
// Generated by InsertSagaFrame (S8)
saga.Version = 1;
await db.GetCollection<TSaga>(coll)
    .InsertOneAsync(mongoSession, saga, cancellationToken: ct);
```

### Update frame (S6 baseline vs. S8 OCC)

**S6 baseline:** `ReplaceOneAsync` with `IsUpsert = true` (same as insert).

**S8 OCC update:**
1. Capture `oldVersion = saga.Version` — the version currently stored in MongoDB.
2. Set `saga.Version = oldVersion + 1` — the version to write.
3. `ReplaceOneAsync` with filter `(_id == saga.<IdMember> && Version == oldVersion)`,
   `IsUpsert = false`.
4. Throw `SagaConcurrencyException` if `ModifiedCount == 0`.

> **🔎 codex — `ModifiedCount == 0` is the conflict signal, and it is safe *only because*
> `Version` always changes.** Step 2 always increments `Version` (`oldVersion → oldVersion + 1`),
> so a *matched* document is always *modified* — `ModifiedCount == 0` therefore means "filter
> matched nothing" (the version moved or the doc was deleted) = a genuine conflict. This mirrors
> the demo's `OrderRepository.UpdateAsync` and the lightweight SQL provider (affected-row count).
> Caveat for future maintainers: if a saga update ever writes a byte-identical replacement (no
> field, including `Version`, actually changes), `ReplaceOneAsync` reports `ModifiedCount == 0` on
> a successful match and would throw a **spurious** `SagaConcurrencyException`. The always-changing
> `Version` makes that impossible here; do not remove the version increment. `MatchedCount == 0`
> is the more intent-revealing alternative if a future change ever breaks that invariant.

> **The update frame is handed only the `saga` variable — not `sagaId`** (see §"Resolving the
> `_id` value" in the Implementation Contracts). The `_id` filter value must therefore be read
> from the saga document via its resolved identity member (`saga.<IdMember>`), **not** from a
> `sagaId` variable (which is not in scope in `DetermineUpdateFrame`). `<IdMember>` is the
> state-side identity member resolved at frame-build time.

```csharp
// Generated by UpdateSagaFrame (S8). <IdMember> is resolved at frame-build time
// (e.g. "Id"); the version field name is resolved from the serialized member.
var oldVersion = saga.Version;
saga.Version = oldVersion + 1;
var result = await db.GetCollection<TSaga>(coll)
    .ReplaceOneAsync(
        mongoSession,
        Builders<TSaga>.Filter.And(
            Builders<TSaga>.Filter.Eq("_id", saga.<IdMember>),
            Builders<TSaga>.Filter.Eq(s => s.Version, oldVersion)),
        saga,
        new ReplaceOptions { IsUpsert = false },
        ct);
if (result.ModifiedCount == 0)
    throw new SagaConcurrencyException(
        $"Optimistic concurrency conflict for saga {typeof(TSaga).Name} id '{saga.<IdMember>}': " +
        $"expected version {oldVersion}, but the document was modified by a concurrent process.");
```

> **`SagaConcurrencyException` constructor:** `SagaConcurrencyException(string message)` —
> single string argument (verified from `Saga.cs:47-52`). Build the message string inline.

**OCC frame naming:** Because S6 uses a unified `StoreSagaFrame(upsert: true)` for both insert
and update, S8 must **replace it with separate `InsertSagaFrame` and `UpdateSagaFrame` classes**
(or a `StoreSagaFrame(mode: Insert | Update | Upsert)` discriminated enum). The
`DetermineInsertFrame` / `DetermineUpdateFrame` factory methods in the provider return different
frames after S8. This is the primary S8 implementation delta vs. the S6 baseline.

### Delete frame (OQ5 resolved)

**Decision: completion delete is NOT version-guarded.**

Rationale: Once a saga is completed (all handler branches called `MarkCompleted()`), any
concurrent process that loaded the same saga document is about to fail on the update (OCC
guard) before it can issue a delete. The delete fires only in the `SagaStoreOrDeleteFrame`
path when `saga.IsCompleted()` returns `true`. A version guard on delete would require the
frame to receive both the saga id and the saga instance, know the current version, and then
throw if another process beat it — but since the other process would have already won the
update and presumably re-persisted a non-completed saga, the delete of the stale completed
copy would simply miss (Wolverine lightweight SQL behavior: `DatabaseSagaSchema.cs:113-119`
also does an unguarded delete). Matching this behavior keeps parity and avoids a spurious
failure when two processes load the same already-completed saga.

```csharp
// Generated by DeleteSagaFrame
await db.GetCollection<TSaga>(coll)
    .DeleteOneAsync(
        mongoSession,
        Builders<TSaga>.Filter.Eq("_id", sagaId),
        cancellationToken: ct);
```

### Concurrency exception retry policy (R11 — required before S14)

`SagaConcurrencyException` must have a retry policy wired before S14 (multi-node saga test),
because two nodes racing the same saga will legitimately throw it under OCC. Without a retry,
the losing node fails the message rather than retrying. Recommended approach: wire a Wolverine
retry policy on `SagaConcurrencyException` in the saga host configuration (e.g. two immediate
retries, then dead-letter). **Document this in the S8 PR and wire it in S14's test host.**
Do not rely on the default Wolverine error policy (which will DLQ on first throw without a
retry policy configured).

---

## Decision 5: Frame Ordering and Atomicity

**Decision: mirror the Cosmos/RavenDb pattern exactly — saga writes run inside the
`TransactionalFrame` session; the commit postprocessor is omitted for `SagaChain`; 
`CommitUnitOfWorkFrame` is the saga chain's sole commit+flush.**

### `ApplyTransactionSupport` (S6 change from current)

**Current code** (unconditional postprocessor — verified S1 §1):
```csharp
if (!chain.Middleware.OfType<TransactionalFrame>().Any())
{
    chain.Middleware.Add(new TransactionalFrame(chain));
    chain.Postprocessors.Add(new CommitMongoTransactionFrame());  // ← WRONG for SagaChain
}
```

**After S6 (Flip 3):**
```csharp
if (!chain.Middleware.OfType<TransactionalFrame>().Any())
{
    chain.Middleware.Add(new TransactionalFrame(chain));
    if (chain is not SagaChain)   // saga chains get commit via CommitUnitOfWorkFrame
        chain.Postprocessors.Add(new CommitMongoTransactionFrame());
}
```

Without this guard, `CommitMongoTransactionFrame` fires **twice** for saga chains: once from
`CommitUnitOfWorkFrame` (inlined by `SagaChain.DetermineFrames`) and once from the postprocessor.

> **🔎 codex — what the double-emit actually does (verified against the local frame).** The
> duplicate commit does **not** throw. `CommitMongoTransactionFrame` guards the commit with
> `if (mongoSession.IsInTransaction)` (`TransactionalFrame.cs:136-139`), so the second invocation
> finds the transaction already committed and **skips** it (no-op). However,
> `FlushOutgoingMessagesAsync()` sits **outside** that guard (`TransactionalFrame.cs:141-145`), so
> it runs **twice** — a double flush of outgoing messages. The guard (Flip 3) is still required;
> the precise defect it prevents is a redundant frame + double flush, not a commit exception.

Both Cosmos and RavenDb use the identical guard (verified S2 §8, S3 §4 Rule 2).

### `CommitUnitOfWorkFrame`

```csharp
public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    => new CommitMongoTransactionFrame();
```

`CommitMongoTransactionFrame` already: (1) commits the `IClientSessionHandle` transaction, and
(2) calls `FlushOutgoingMessagesAsync`. Both actions must happen atomically (same order as the
existing non-saga path). No new frame type is needed.

### Generated saga chain order (verified against Cosmos pre-generated code in S3 §3)

**Start path:**
```
TransactionalFrame (opens session, starts transaction, enlists outbox)
  new TSaga()                                    ← CreateNewSagaFrame
  saga.Start(message)                            ← handler
  context.SetSagaId(saga.Id)                     ← SetSagaIdFromSagaFrame
  if (!saga.IsCompleted()) {
      [InsertSagaFrame or upsert StoreSagaFrame]  ← DetermineInsertFrame
  }
  CommitMongoTransactionFrame                     ← CommitUnitOfWorkFrame (inlined)
  (postprocessors: empty for SagaChain)
```

**Handle path (existing saga):**
```
TransactionalFrame
  sagaId = message.SagaId (or envelope header)  ← PullSagaIdFromMessageFrame/Envelope
  [LoadSagaFrame]                                ← DetermineLoadFrame → Find by _id in session
  if (saga == null) throw UnknownSagaException
  else {
      context.SetSagaId(sagaId)
      saga.Handle(message)                       ← handler
      if (saga.IsCompleted()) {
          [DeleteSagaFrame]                       ← DetermineDeleteFrame
      } else {
          [UpdateSagaFrame or upsert StoreSagaFrame] ← DetermineUpdateFrame
      }
      CommitMongoTransactionFrame                 ← CommitUnitOfWorkFrame (inlined)
  }
  (postprocessors: empty for SagaChain)
```

**Atomicity guarantee:** all writes (saga state change + outgoing envelopes) are committed in
the single `mongoSession` transaction. `TransactionalFrame` enlists the outbox at the start
(`context.EnlistInOutbox(new MongoDbEnvelopeTransaction(...))`); `CommitMongoTransactionFrame`
commits the session **after** the saga write, so saga state and outbox commit together or both
roll back.

### Codegen verification requirement (S6 Step 2)

S6 MUST inspect the generated code (via `codeFor<T>()` or `GeneratedCodeOutputPath`) to
confirm the load → handler → write/delete → commit order above before opening the S6 PR.
A frame that compiles and passes some facts but commits in the wrong order will silently
break atomicity. This is a hard requirement — not optional.

---

## Decision 6: No Global Serializer Mutation

**Decision: rely on the driver's default conventions; no process-global BSON registry change.**

Consistent with the existing `[BsonRepresentation(BsonType.DateTime)]` per-property decision
(documented in `CLAUDE.md`):

- The `Id` → `_id` *mapping* (which member is the id) is handled by the driver's default
  `IdMemberConvention`. The demo's `Order.Id : Guid` does map to `_id` with no `[BsonId]`.
  **🔎 codex caveat:** that demo's Guid *value* round-trip is **not** ceremony-free — the demo
  registers a process-global `GuidSerializer(GuidRepresentation.Standard)`
  (`InfrastructureBootstrap.cs:46`) and uses per-property `[BsonGuidRepresentation]`
  (`OrderSummary.cs`). MongoDB.Driver 3.x has no default Guid representation. So `Id`→`_id`
  mapping is convention-free, but Guid *representation* is a separate, required choice (see R14).
- `int Version` serializes as BSON `int32` by default — no annotation.
- `bool IsCompleted()` is a method, not a property — the driver does not serialize it.
  `_isCompleted` is a private field and will also not be serialized by default
  (driver only maps public properties/fields by default).
- `DateTimeOffset` fields on user saga types: if a saga subclass has `DateTimeOffset` fields,
  the developer must annotate them with `[BsonRepresentation(BsonType.DateTime)]` per the
  existing convention. The library does not apply this automatically.
- `[BsonId]` on test/demo saga types: add **only if** a compliance fact fails due to `_id`
  not being mapped correctly. Do not add proactively.

**No `BsonClassMap.RegisterClassMap`, `ConventionRegistry.Register`, or
`BsonSerializer.RegisterSerializer` calls in the provider code.** Such calls mutate the
process-global MongoDB BSON registry and break application code that depends on its own
serialization setup.

**🔎 codex — Guid-id representation strategy for S7/S10 (consequence of this decision).** Because
the library must not register a global Guid serializer, a Guid-keyed saga needs its representation
supplied elsewhere. Order of preference, decided here so S7/S10 don't improvise:
1. **Per-property `[BsonGuidRepresentation(GuidRepresentation.Standard)]` on the saga's `Guid Id`**
   (test/demo saga types only) — local, no global mutation, matches the demo's `OrderSummary`
   precedent. **Preferred** for the compliance/demo Guid sagas.
2. **The test/demo *host* registers the serializer** (app/test code, not the library) if a
   per-property attribute proves insufficient across the compliance types.
3. The library itself does **not** register one (would violate this decision).
If S7's first Guid compliance run fails on Guid *representation* (not `_id` *mapping*), apply
option 1 to the `GuidBasicWorkflow`-side test saga; do not reach for a global registration in the
provider. Record the chosen mechanism in the S7 PR.

---

## Decision 7: Upstream Readiness

**Decision: implement in `Wolverine.MongoDB`'s existing `Internals/` structure; provide an
upstream-contribution checklist in S16.**

### Target for upstream contribution

If contributed upstream to the JasperFx/Wolverine repository:

- **Namespace:** `Wolverine.MongoDb` (Wolverine uses `MongoDb` capitalization, not `MongoDB`)
- **Project:** `src/Persistence/Wolverine.MongoDb/`
- **Tests:** `src/Persistence/MongoDbTests/` with `MongoDbSagaHost`, `string_saga_storage_compliance`,
  `guid_saga_storage_compliance`, `saga_atomicity.cs`
- **Compliance subclasses:** Add `StringIdentifiedSagaComplianceSpecs<MongoDbSagaHost>` and
  `GuidIdentifiedSagaComplianceSpecs<MongoDbSagaHost>` (+ int/long if in scope)

### Deliberate improvements over Cosmos/RavenDb (call out in upstream PR)

1. **Native saga id type** — `DetermineSagaIdType` returns the saga's actual identity-member
   type (Guid/int/long/string) rather than hard-coding `typeof(string)`. Enables Guid/int/long
   compliance specs. Mirrors `LightweightSagaPersistenceFrameProvider`.

2. **`Saga.Version` optimistic concurrency** — version-guarded update frame throws
   `SagaConcurrencyException` on conflict. Cosmos and RavenDb are last-write-wins for sagas;
   this aligns MongoDB with Wolverine's own Marten, EF Core, and lightweight SQL providers.

3. **`CanPersist` scoped to `Saga`** — avoids advertising generic `[Entity]`/`Insert<T>`/
   `IStorageAction<T>` support that Mongo's `DetermineStorageActionFrame` does not implement.
   This is a correctness improvement over the unconditional `return true` in Cosmos/RavenDb.

4. **Per-saga-type collections** — idiomatic MongoDB modeling; avoids cross-type `_id`
   collision; independently enumerable for cleanup.

---

## S6–S8 Implementation Contracts (Normative)

This section is the single source of truth for S6, S7, and S8 implementors.

### New files

| File | Purpose |
|------|---------|
| `src/Wolverine.MongoDB/Internals/SagaFrames.cs` | `LoadSagaFrame`, `StoreSagaFrame` (S6 upsert), `InsertSagaFrame` (S8), `UpdateSagaFrame` (S8), `DeleteSagaFrame` |

### Changed files

| File | What changes |
|------|-------------|
| `MongoDbPersistenceFrameProvider.cs` | Flip 1 (`CanApply`), Flip 2 (`CanPersist`), Flip 3 (`ApplyTransactionSupport`); implement saga factory methods; S7: `DetermineSagaIdType` → native type |
| `MongoConstants.cs` | Add `SagaCollectionPrefix` constant + `SagaCollectionName(Type)` helper |
| `MongoDbMessageStore.Admin.cs` | Extend `ClearAllAsync` to enumerate + drop `wolverine_saga_*` collections |

### `MongoDbPersistenceFrameProvider` saga method contracts

```csharp
// Flip 1 — gate (S6)
public bool CanApply(IChain chain, IServiceContainer container)
{
    if (chain is SagaChain) return true;
    // ... existing IClientSessionHandle / MongoDbUnitOfWork / IMongoDatabase / IMongoClient / IMongoCollection<> checks ...
}

// Flip 2 — saga-scoped (S6; NOT unconditional — see R9)
public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
{
    persistenceService = typeof(IMongoDatabase);
    return entityType.CanBeCastTo<Saga>();
}

// DetermineSagaIdType — S6 baseline
public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    => typeof(string);  // S7 replaces with: SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType() ?? typeof(string)

// Frame factories — S6 all use StoreSagaFrame(upsert); S8 splits InsertSagaFrame / UpdateSagaFrame
public Frame DetermineLoadFrame(IServiceContainer c, Type sagaType, Variable sagaId)
    => new LoadSagaFrame(sagaType, sagaId);

public Frame DetermineInsertFrame(Variable saga, IServiceContainer c)
    => new StoreSagaFrame(saga, SagaWriteMode.Upsert);  // S8: new InsertSagaFrame(saga)

public Frame DetermineUpdateFrame(Variable saga, IServiceContainer c)
    => new StoreSagaFrame(saga, SagaWriteMode.Upsert);  // S8: new UpdateSagaFrame(saga)

public Frame DetermineStoreFrame(Variable saga, IServiceContainer c)
    => DetermineUpdateFrame(saga, c);  // not called for SagaChain; keep as delegation

public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer c)
    => new DeleteSagaFrame(sagaId, saga);

public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer c)
    => new CommitMongoTransactionFrame();

// Flip 3 — saga-chain guard (S6)
public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
{
    if (!chain.Middleware.OfType<TransactionalFrame>().Any())
    {
        chain.Middleware.Add(new TransactionalFrame(chain));
        if (chain is not SagaChain)
            chain.Postprocessors.Add(new CommitMongoTransactionFrame());
    }
}
```

### Which id variable each frame receives (read this before the frame contracts)

The `IPersistenceFrameProvider` factory signatures differ in what they hand each frame
(`IPersistenceFrameProvider.cs:26-30`) — getting this wrong is the easiest way to emit a frame
that compiles but is wrong:

| Factory | Receives | The `_id` value comes from |
|---|---|---|
| `DetermineLoadFrame(container, sagaType, **sagaId**)` | `sagaId` (typed) | the `sagaId` variable directly |
| `DetermineDeleteFrame(**sagaId**, saga, container)` | `sagaId` **and** `saga` | the `sagaId` variable directly |
| `DetermineInsertFrame(**saga**, container)` | `saga` only | the saga document's identity member |
| `DetermineUpdateFrame(**saga**, container)` | `saga` only | the saga document's identity member |

**Consequence:** Load and Delete filter on the `sagaId` variable. **Insert and Update have no
`sagaId` variable** — they must read the id from the saga document. Cosmos sidesteps this
entirely because `UpsertItemAsync(saga)` derives the id from the document
(`CosmosDbPersistenceFrameProvider.cs:140-163`); MongoDB's `ReplaceOneAsync` requires an explicit
`_id` filter, so the insert/update frames must construct one from `saga.<IdMember>`.

**Resolving `<IdMember>`:** at frame construction the frame knows the saga `Type` (from
`saga.VariableType`). Resolve the state-side identity member name once:

```csharp
// In StoreSagaFrame / UpdateSagaFrame constructor:
var idMember = SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.Name ?? "Id";
```

then emit the filter value as `{saga.Usage}.{idMember}`. For every compliance type and the demo
saga `idMember == "Id"`, so the generated code reads `saga.Id` — but the frame must **resolve**
the name, not hard-code it, so a saga whose identity member is `SagaId`/`OrderFulfillmentId`
(with `[BsonId]`, per Decision 1) still works. The serialized filter field stays the literal
`"_id"` because that member maps to `_id` (Decision 1 constraint).

### `LoadSagaFrame` contract

Resolves `IClientSessionHandle` (`mongoSession` created by `TransactionalFrame`), `IMongoDatabase`
(DI-registered, resolved from DI by `TransactionalFrame.FindVariables`), and `CancellationToken`.

Generated code shape:
```csharp
var saga = await db.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga)))
    .Find(mongoSession, Builders<TSaga>.Filter.Eq("_id", sagaId))
    .FirstOrDefaultAsync(cancellationToken);
// saga == null if not found
```

- The `sagaId` variable type may be `string`, `Guid`, `int`, or `long` depending on which
  identity path Wolverine used. The `"_id"` string filter accepts all of these — BSON handles
  the type matching.
- No `try/catch` needed — `Find(…).FirstOrDefaultAsync()` returns `null` on miss (unlike Cosmos
  which needed `try/catch CosmosException(NotFound)`).

### `StoreSagaFrame` (S6 upsert — both insert and update paths)

`<IdMember>` is resolved at frame-build time (see "Which id variable each frame receives" above);
for the compliance/demo sagas it is `Id`, so the emitted filter value is `saga.Id`.

```csharp
await db.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga)))
    .ReplaceOneAsync(
        mongoSession,
        Builders<TSaga>.Filter.Eq("_id", saga.<IdMember>),
        saga,
        new ReplaceOptions { IsUpsert = true },
        cancellationToken);
```

No version management. `saga.<IdMember>` is the value the document serializes as `_id`
(Decision 1 constraint), so the upsert filter and the inserted document agree on `_id`. This
frame is used for both `DetermineInsertFrame` and `DetermineUpdateFrame` in the S6 baseline —
S8 replaces it with the split `InsertSagaFrame` / `UpdateSagaFrame`.

### `InsertSagaFrame` (S8 — proper insert)

```csharp
saga.Version = 1;
await db.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga)))
    .InsertOneAsync(mongoSession, saga, cancellationToken: cancellationToken);
```

### `UpdateSagaFrame` (S8 — OCC update)

The update frame receives only `saga` (no `sagaId`), so the `_id` filter value is read from
`saga.<IdMember>` (resolved at frame-build time), **not** from a `sagaId` variable.

```csharp
var oldVersion = saga.Version;
saga.Version = oldVersion + 1;
var result = await db.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga)))
    .ReplaceOneAsync(
        mongoSession,
        Builders<TSaga>.Filter.And(
            Builders<TSaga>.Filter.Eq("_id", saga.<IdMember>),
            Builders<TSaga>.Filter.Eq(s => s.Version, oldVersion)),
        saga,
        new ReplaceOptions { IsUpsert = false },
        cancellationToken);
if (result.ModifiedCount == 0)
    throw new SagaConcurrencyException(
        $"Optimistic concurrency conflict for {typeof(TSaga).Name} saga with id '{saga.<IdMember>}': " +
        $"expected version {oldVersion}. The document was modified or deleted concurrently.");
```

### `DeleteSagaFrame` (unguarded)

```csharp
await db.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga)))
    .DeleteOneAsync(
        mongoSession,
        Builders<TSaga>.Filter.Eq("_id", sagaId),
        cancellationToken: cancellationToken);
```

### `MongoConstants.cs` additions

```csharp
public const string SagaCollectionPrefix = "wolverine_saga_";

public static string SagaCollectionName(Type sagaType)
    => $"{SagaCollectionPrefix}{sagaType.Name.ToLowerInvariant()}";
```

### `ClearAllAsync` addition (append after existing system-collection clears)

```csharp
var cursor = await _database.ListCollectionNamesAsync();
var names = await cursor.ToListAsync();
foreach (var name in names.Where(n => n.StartsWith(MongoConstants.SagaCollectionPrefix)))
    await _database.DropCollectionAsync(name);
```

---

## Open Questions Resolved by This Document

| OQ | Resolution |
|----|-----------|
| **OQ1** Lead decision A vs B | **B** — native id + OCC. Decomposed: S6 = A-parity baseline; S7/S8 layer B features. |
| **OQ2** int/long ids | **Include** in S7/S10 — cheap once native-id works. Defer to FOLLOWUPS only if fiddly. |
| **OQ3** `ISagaStoreDiagnostics` | **Defer** to FOLLOWUPS. Optional; default not implemented. |
| **OQ4** Release saga-enabled NuGet | **Stay `[Unreleased]`** until explicit user request. |
| **OQ5** Completion delete version-guarded? | **No** — unguarded (matches `DatabaseSagaSchema.cs:113-119`). |

---

## Risk Notes for Implementors

| Risk | Mitigation |
|------|-----------|
| **R2 — double-commit** | Flip 3 (`if chain is not SagaChain`) prevents it. Verify in generated code. |
| **R3 — Guid `_id` *mapping*** | Demo maps `Guid Id`→`_id` with no `[BsonId]`; add `[BsonId]` only if a fact fails on *mapping*. (Guid *representation* is separate — see R14.) |
| **R9 — `CanPersist` over-broad** | Scoped to `entityType.CanBeCastTo<Saga>()` — stated in Flip 2. |
| **R10 — envelope-only start assigns saga id** | Compliance `BasicWorkflow.Start` assigns `Id = starting.Id` — provider does not set `Id` from the envelope. Document that start handlers must assign the saga id. |
| **R11 — `SagaConcurrencyException` retry policy** | Required before S14. Wire in S14 test host; recommended: 2 immediate retries, then dead-letter. |
| **R12 — insert/update frames have no `sagaId` variable** | `DetermineInsertFrame`/`DetermineUpdateFrame` receive only `saga` (`IPersistenceFrameProvider.cs:27,29`). The `_id` filter value must be read from `saga.<IdMember>`, resolved via `SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.Name`. Do **not** reference a `sagaId` variable in those frames. S6 codegen check (`codeFor<T>()`) must confirm the upsert/update filter references the saga's id member. |
| **R13 — state identity member must map to `_id`** | Driver maps only `Id`/`id`/`_id` to BSON `_id`; a differently-named identity member yields an auto `ObjectId` `_id` and load-by-`_id` silently misses. Constraint (Decision 1): name the state identity `Id` or annotate `[BsonId]`. Compliance + demo satisfy it by construction; README (S16) documents it for users. |
| **R14 — Guid id needs an explicit representation** | MongoDB.Driver 3.x has no default Guid representation; the demo relies on a process-global `GuidSerializer` (`InfrastructureBootstrap.cs:46`) + `[BsonGuidRepresentation]` (`OrderSummary.cs`). Decision 6 forbids the **library** registering one, so S7/S10 supply it per-property on the test/demo Guid saga (`[BsonGuidRepresentation(GuidRepresentation.Standard)]`) or in the test host — never in the provider. Distinguish a Guid *representation* failure from an `_id` *mapping* failure (R3/R13) when diagnosing the first S7 Guid run. |

---

*Produced by Task S4 of `2026-06-18-saga-persistence.md`. This document supersedes any
"TBD" or "decide in S4" notes in the plan for the decisions above.*
