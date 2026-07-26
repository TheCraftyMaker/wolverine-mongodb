# Identity-Mapping Design (Task F3 — DESIGN GATE)

> **Binding design.** This document resolves **LD1** (identity-mapping mechanism) and **LD4** (saga
> types on the storage-action paths) from `2026-07-07-review-findings-remediation.md` into contracts
> that **F6** and **F7** implement without further debate. Where this document and the plan's
> illustrative snippets differ, **this document wins** — the deltas are listed explicitly in
> §10 so the implementing sessions can see exactly what changed and why.
>
> **Input:** `2026-07-07-identity-mapping-discovery.md` (F1). Every F1 fact is taken as given and is
> not re-derived here. F1 left two items open for this gate — the `ConcurrentDictionary.GetOrAdd`
> double-invocation edge (F1 §2.2, correction 2) and the `LookupClassMap`-freezes-first ordering
> subtlety (F1 §2.3) — both are resolved below (§4.3, §4.4).
>
> **New empirical work done at this gate:** F1 established the driver's class-map semantics by
> reading `mongo-csharp-driver` v3.9.0 source. Because the entire design hinges on one behavior F1
> asserted but did not execute — *does `MapIdMember` on a non-`Id`-named member actually put that
> value in the `_id` element?* — this gate ran an isolated MongoDB.Driver 3.9.0 probe. It found the
> answer is **yes, but only after `Freeze()`**, which no F1 source excerpt showed. §2 records the
> full transcript. Had the design been written from source-reading alone, §2.2's element-name
> normalization would have been an unstated assumption underneath every downstream task.

**Status:** decisions final. **Gates:** F6 (saga identity), F7 (entity identity + LD4 guards), and
F18's demo shapes.

---

## 1. Decision summary

| # | Decision | Resolution |
|---|---|---|
| **LD1** | Identity-mapping mechanism | **Option B′ — ensure-or-fail class map, minimal-mutation.** Option B, refined so the helper *never touches the BSON registry for a type the driver already maps correctly* (§3). |
| **D1a** | Helper contract | `MongoIdentityMapping.EnsureIdMember(Type, MemberInfo)` + `EnsureIdMember(Type)` + `ResolveIdMember(Type)` (§3.2). |
| **D1b** | Invocation points | Codegen time (all 4 saga frame ctors, all 3 entity frame ctors, `DetermineStorageActionFrame`) **and** runtime (via new collection-accessor helpers in `MongoSagaOperations`/`MongoEntityOperations`). The runtime leg is **required**, not belt-and-braces — in `TypeLoadMode.Static` the frame constructors never run (§4.1–§4.2). |
| **D1c** | Thread safety | Lock-free memo read → `lock` → double-check → align. Not `ConcurrentDictionary.GetOrAdd` (§4.3). |
| **D2** | CLAUDE.md reconciliation | `CLAUDE.md:156` and `:159` rewritten, one new decision bullet added — exact text in §7. |
| **D3** | `UpdateSagaFrame`'s `?? "Id"` fallback | Deleted. All identity resolution funnels through `MongoIdentityMapping.ResolveIdMember`, which throws the same `ArgumentException` message `DetermineSagaIdType` throws today; `DetermineSagaIdType` is refactored to delegate to it (§5). |
| **LD4** | `Delete<TSaga>` / `IStorageAction<TSaga>` from a non-saga handler | **Throw** at codegen. `InvalidOperationException`, exact message text in §6. |
| **D4** | On-disk compatibility | No migration owed. `Id`-keyed documents are byte-identical; non-`Id` types never round-tripped. One nuance for the both-members shape (§8). |
| **D5** | Test matrix | 14 rows across F6/F7, §9. Includes the two negative-path rows, the concurrency row, and the Static-mode row. |

---

## 2. Verified driver semantics (empirical, this gate)

Probe: standalone `net9.0` console app referencing `MongoDB.Driver 3.9.0` (the pinned version,
`Directory.Packages.props:8`). Verbatim output, annotated. `ShipmentSaga { Guid ShipmentId; int
Version; string Status }` models the `{Name-minus-Saga}Id` convention; `BothMembers { Guid
BothMembersId; string Id; string Note }` models the review's poisoned shape; `DerivedSaga :
FakeSagaBase { string DerivedId; string Status }` models a real saga (identity on the subclass,
`Version` inherited from the base).

```
[1]  AutoMap-only ShipmentSaga IdMemberMap = (null)
[2]  pre-freeze:  IdMemberMap=ShipmentId, ElementName='ShipmentId'
[3]  post-freeze: IsFrozen=True, IdMemberMap=ShipmentId, ElementName='_id'
[4]  serialized: { "_id" : {Guid ShipmentId}, "Version" : 1, "Status" : "shipped" }
[6]  AutoMap-only BothMembers IdMemberMap = Id
[7]  serialized: { "_id" : {Guid BothMembersId}, "Id" : "legacy", "Note" : "n" }
[8]  PlainId (registry untouched) serialized: { "_id" : "abc", "Note" : "n" }
[9]  NoIdAtAll serialized: { "Note" : "n" }
[10] double register -> ArgumentException: An item with the same key has already been added. Key: ShipmentSaga
[11] LookupClassMap(PlainId).IsFrozen=True, IdMemberMap=Id, ElementName='_id'
[12] mutate frozen -> InvalidOperationException: Class map for PlainId has been frozen and no further changes are allowed.
[13] round-trip ShipmentId = 11111111-… (match=True)
[14] register after LookupClassMap froze it -> ArgumentException
[15] base class map registered BEFORE probe AutoMap? False
[16] AutoMap-only DerivedSaga IdMemberMap = (null)
[17] base class map registered AFTER probe AutoMap?  False
[18] derived serialized: { "Version" : 3, "_id" : "S-1", "Status" : "ok" }
[19] derived round-trip DerivedId=S-1 Version=3
[20] has _t discriminator? False
```

### 2.1 The corruption mechanism, executed

`[1]`/`[16]` confirm F1 §2.7 by execution: `AutoMap()` leaves `IdMemberMap` **null** for a type whose
only identity-shaped member is `{TypeName}Id` — for root types and subclasses alike. `[9]` confirms
F1 §2.5: such a type serializes with **no `_id` element at all**, so the server assigns an
unrelated `ObjectId`. That is the 1.0.0 silent-corruption path end to end.

### 2.2 `Freeze()` normalizes the id element name to `_id` — the load-bearing fact

`[2]` vs `[3]`: immediately after `MapIdMember(ShipmentId)` the member map's `ElementName` is still
`'ShipmentId'`. **Only on `Freeze()` does the driver rewrite it to `'_id'`.** `[4]` and `[18]` show
the consequence on the wire: the document's key element is `_id`, carrying the resolved member's
value, with **no duplicate `ShipmentId`/`DerivedId` field**.

This is what makes LD1-B correct **without touching a single `Eq("_id", …)` filter**: after
alignment, the read-side filters in `MongoSagaOperations`/`MongoEntityOperations` and the write-side
serialization refer to the same element. F1's conclusion that "the raw `Eq("_id", …)` filters stay"
(F1 §5) is confirmed — and now confirmed *for the right reason*.

> **F6/F7 must not "fix" the element name manually.** Do not call `SetElementName("_id")`. The
> driver does it at freeze, and every class map is frozen before first use (`[3]`, `[11]`).

### 2.3 The both-members shape's post-fix document shape

`[6]`/`[7]`: `AutoMap` picks `Id` (the driver's `NamedIdMemberConvention`); after
`MapIdMember(BothMembersId)` the document becomes `{ "_id": <BothMembersId>, "Id": "legacy", … }`.
So the `Id` member does **not** disappear — it demotes to an ordinary field named `Id`. This is the
basis of §8's compatibility statement for that shape.

### 2.4 The no-op path leaves the document byte-identical

`[8]`: a plain-`Id` type whose class map was never registered by us serializes exactly as it does
today. This is the entire regression argument for existing consumers, and it is the reason §3's
algorithm **declines to register** when the driver's own conventions already agree (§3.1, rule 3).

### 2.5 Exception shapes, exactly

- Double `RegisterClassMap` for one type → **`ArgumentException`**, message `An item with the same
  key has already been added. Key: <TypeName>` (`[10]`). Confirms F1 correction 2 by execution.
- Mutating a frozen map → **`InvalidOperationException`**, `Class map for <FullName> has been frozen
  and no further changes are allowed.` (`[12]`) — the unhelpful message §3's design exists to avoid
  ever surfacing.
- `RegisterClassMap` **after** `LookupClassMap` auto-mapped and froze the type → `ArgumentException`
  (`[14]`). Two consequences: (a) the ordering subtlety of F1 §2.3 is real; (b) any app that
  registers its own class map *after* Wolverine's host build would break if we registered maps
  unnecessarily — §3.1 rule 3 preserves that app's ability entirely.

### 2.6 Derived types (i.e. every real saga) behave identically

`[16]`–`[20]`: the identity member on a subclass maps correctly, the inherited `Version` is
serialized, the round-trip recovers both, and **no `_t` discriminator appears** (nominal type ==
actual type for `IMongoCollection<TSaga>`), so saga documents keep their current shape. `[15]`/`[17]`
also settle a side-effect question: `AutoMap()` on a derived type does **not** register a class map
for the base type, so the probe step in §3.1 has no global side effects.

### 2.7 One thing the probe could *not* settle (and why it doesn't matter)

`[5]` (omitted above) attempted `Builders<ShipmentSaga>.Filter.Eq("_id", guid).Render(…)` and threw
`BsonSerializationException: GuidSerializer cannot serialize a Guid when GuidRepresentation is
Unspecified`. This is an artifact of the probe having no `MongoClient` (which is what configures the
default Guid representation) — **not** a finding about `_id` filters. The proof it is an artifact:
the repo's Guid-keyed saga compliance suite (`guid_saga_storage_compliance.cs`, on upstream
`BasicWorkflow<…, Guid>` whose member is named `Id`, carrying no `[BsonGuidRepresentation]`) is green
in CI today through the identical `Eq("_id", sagaId)` filter path. Filter-value serialization is
orthogonal to *which member* is the id, so the member-name change introduces no new representation
risk.

**Implementation note for F6/F7 test authors:** the library's own documents annotate Guid properties
`[BsonGuidRepresentation(GuidRepresentation.Standard)]` (`IncomingMessage.cs:26`, `NodeDocument.cs:9`,
`LockDocument.cs:9`, `AgentAssignmentDocument.cs:9`). Integration tests running against a real host
need no annotation (proven by the existing Guid compliance suite); a *unit*-level test that
serializes a Guid-keyed POCO with no `MongoClient` in the process will hit the `[5]` error — add the
attribute there, or key that particular unit test off a `string`.

---

## 3. LD1 — resolved: Option B′, ensure-or-fail with minimal mutation

**Chosen: Option B (ensure-or-fail class map), refined.** The refinement is one added rule that
Option B as written in the plan did not have, and it materially reduces blast radius:

> **Never register a class map for a type whose id member the driver's own conventions already
> resolve correctly.**

The plan's snippet built a map, called `AutoMap()`, conditionally called `MapIdMember`, and then
registered it **unconditionally**. For every `Id`-keyed type — i.e. every consumer that works
today — that would replace an implicit auto-map with an explicit registration. Per `[14]`, this
would take away something apps have today: the ability to register their own class map for that type
after Wolverine's host build. The refinement makes the helper a true no-op there: registry
untouched, `[8]`'s document shape preserved, app rights preserved. Mutation happens **only** for
types that are otherwise **broken**.

### 3.1 The algorithm

Given `documentType` and Wolverine's resolved `idMember`:

1. **Memo hit** → return. (Per-process, per-type; alignment is idempotent by construction.)
2. **A class map is already registered for `documentType`** → do **not** mutate it. Read it and
   compare: id member name equal to `idMember.Name` → done; otherwise **throw** the §3.3 conflict
   error. Covers the app-registered case, the `[BsonId]` case, and the F1 §2.3 case where something
   already `LookupClassMap`'d (and thus froze) the type.
3. **No class map registered** → build a *probe* map (`new BsonClassMap(documentType)` + `AutoMap()`)
   and ask what the driver's own conventions produced:
   - probe's id member == `idMember.Name` → **return without registering anything.** (`Id`/`id`/`_id`
     members, and `[BsonId]`-annotated members — F1 §2.6.) Registry untouched.
   - otherwise → `probe.MapIdMember(idMember)` and `RegisterClassMap(probe)`. The probe is a
     brand-new object nobody else holds a reference to, and is unfrozen — so this can never hit
     `[12]`'s frozen-mutation error.
4. **`RegisterClassMap` threw `ArgumentException`** (lost a race with application code registering
   the same type between steps 3 and 4 — see §4.3) → fall back to step 2's compare-or-throw against
   the winner's map. The winner decides; we only assert agreement.
5. Record in the memo. **Failures are not memoized** — a conflicting configuration throws on every
   call rather than throwing once and silently passing afterwards.

### 3.2 The exact helper contract

```csharp
// src/Wolverine.MongoDB/Internals/MongoIdentityMapping.cs
namespace Wolverine.MongoDB.Internals;

/// <summary>
/// Bridges Wolverine's identity-member convention (SagaChain.DetermineSagaIdMember) and the MongoDB
/// driver's independent one (NamedIdMemberConvention / [BsonId]) so that read filters keyed on "_id"
/// and written documents agree for every legal Wolverine identity convention.
/// </summary>
internal static class MongoIdentityMapping
{
    private static readonly ConcurrentDictionary<Type, bool> _aligned = new();
    private static readonly object _gate = new();

    /// <summary>
    /// Wolverine's resolved identity member for <paramref name="documentType"/>. The single
    /// resolution point for sagas and entities alike; throws rather than guessing "Id".
    /// </summary>
    internal static MemberInfo ResolveIdMember(Type documentType)
        => SagaChain.DetermineSagaIdMember(documentType, documentType)
           ?? throw new ArgumentException(
               $"Unable to determine the identity member for {documentType.FullNameInCode()}",
               nameof(documentType));

    /// <summary>Resolve-then-ensure. Used where the caller has no MemberInfo in hand.</summary>
    internal static void EnsureIdMember(Type documentType)
    {
        if (_aligned.ContainsKey(documentType)) return;      // skip the reflection on the hot path
        EnsureIdMember(documentType, ResolveIdMember(documentType));
    }

    /// <summary>
    /// Ensures the MongoDB driver serializes <paramref name="idMember"/> as the document _id.
    /// No-op when the driver's own conventions already agree (member named Id/id/_id, [BsonId], or an
    /// app-registered map with the same id member) — in that case the BSON registry is left untouched.
    /// Registers one additive per-type class map when the type has no map and the conventions
    /// disagree. Throws when an already-registered map names a different id member.
    /// </summary>
    internal static void EnsureIdMember(Type documentType, MemberInfo idMember)
    {
        if (_aligned.ContainsKey(documentType)) return;
        lock (_gate)
        {
            if (_aligned.ContainsKey(documentType)) return;
            align(documentType, idMember);
            _aligned[documentType] = true;
        }
    }

    private static void align(Type documentType, MemberInfo idMember)
    {
        if (BsonClassMap.IsClassMapRegistered(documentType))
        {
            assertRegisteredMapAgrees(documentType, idMember);
            return;
        }

        var probe = new BsonClassMap(documentType);
        probe.AutoMap();
        if (probe.IdMemberMap?.MemberName == idMember.Name)
        {
            return;     // the driver already agrees — do NOT register; leave the app in control
        }

        probe.MapIdMember(idMember);
        try
        {
            BsonClassMap.RegisterClassMap(probe);
        }
        catch (ArgumentException)
        {
            // Lost a race with application code that registered a map for this type between the
            // IsClassMapRegistered check above and this call. The winner's map decides.
            assertRegisteredMapAgrees(documentType, idMember);
        }
    }

    private static void assertRegisteredMapAgrees(Type documentType, MemberInfo idMember)
    {
        var map = BsonClassMap.LookupClassMap(documentType);
        if (map.IdMemberMap?.MemberName == idMember.Name) return;

        throw new InvalidOperationException(
            $"The registered BsonClassMap for {documentType.FullNameInCode()} maps " +
            $"'{map.IdMemberMap?.MemberName ?? "(no id member)"}' as the document _id, but Wolverine " +
            $"resolved '{idMember.Name}' as the identity member for MongoDB persistence. Align them " +
            $"by putting [BsonId] on '{idMember.Name}', or by registering a class map that maps " +
            $"'{idMember.Name}' as the id member.");
    }
}
```

**Binding details.** Signatures, exception types, and the `align` decision order are contractual.
The two message texts are contractual (§3.3, §5) because tests assert on them. `_aligned` may be any
lock-free-read memo; `bool` values are unused — presence is the signal.

### 3.3 The conflict error

Type: **`InvalidOperationException`** (a misconfiguration discovered while wiring persistence, not a
bad argument to a method). Text as in §3.2. Properties it must keep:

- Names the type via `FullNameInCode()`.
- Names **both** members — what the driver has and what Wolverine resolved — so the reader knows
  which end to move.
- `"(no id member)"` when the registered map has none (an app that registered a map with only
  `MapMember` calls).
- Gives the two concrete remedies (`[BsonId]`, or a class map mapping the resolved member).

It deliberately never surfaces `[12]`'s "has been frozen and no further changes are allowed", which
says nothing about identity. The design's invariant — **only ever `MapIdMember` on a brand-new,
unregistered map** — is what guarantees that.

---

## 4. Invocation points

### 4.1 Codegen time is necessary but **not sufficient** — `TypeLoadMode.Static`

The plan assumed frame constructors are enough. They are not, and F6/F7 must not ship on that
assumption:

- Frames are constructed in `HandlerChain.AssembleTypes` (`external/wolverine/src/Wolverine/Runtime/Handlers/HandlerChain.cs:224-271`,
  which calls `DetermineFrames` at `:246`).
- The pre-generated path is `ICodeFile.AttachTypesSynchronously` (`:309-325`): it resolves the
  generated handler type out of `assembly.ExportedTypes`, `QuickBuild`s it, and returns. It **never
  calls `AssembleTypes`** — so no `IPersistenceFrameProvider` factory runs and no frame constructor
  runs.
- `TypeLoadMode.Static` apps ship without Roslyn at all: "*`TypeLoadMode.Static` apps that
  pre-generate all of their code need nothing and ship without Roslyn*"
  (`external/wolverine/docs/guide/codegen.md:121`).

Alignment achieved only in a frame constructor therefore happens in the *`codegen write` build
process* and is absent from the deployed process. A non-`Id`-keyed saga would work in `Dynamic` mode
and silently corrupt in `Static`/AOT mode — a mode-dependent variant of the exact bug under repair.

**Decision: align at both codegen time and runtime.**

- **Codegen time** buys the loud, early failure: a conflicting class map or an unresolvable identity
  member throws during host build, not on the first message.
- **Runtime** buys correctness in every `TypeLoadMode`.

### 4.2 The exact call sites

**Codegen time** (F6 = saga rows, F7 = entity rows):

| Site | File | Call |
|---|---|---|
| `LoadSagaFrame` ctor | `SagaFrames.cs:132` | `EnsureIdMember(sagaType, member)` |
| `InsertSagaFrame` ctor | `SagaFrames.cs:176` | `EnsureIdMember(saga.VariableType, member)` |
| `UpdateSagaFrame` ctor | `SagaFrames.cs:221` | `EnsureIdMember(sagaType, member)` — the member it already resolves (§5) |
| `DeleteSagaFrame` ctor | `SagaFrames.cs:268` | `EnsureIdMember(saga.VariableType, member)` |
| `LoadEntityFrame` ctor | `EntityFrames.cs:154` | `EnsureIdMember(entityType)` |
| `MongoUpsertEntityFrame` ctor | `EntityFrames.cs:207` | `EnsureIdMember(entity.VariableType)` |
| `MongoDeleteEntityByVariableFrame` ctor | `EntityFrames.cs:249` | `EnsureIdMember(entity.VariableType)` |
| `DetermineStorageActionFrame` | `MongoDbPersistenceFrameProvider.cs:147-156` | `EnsureIdMember(entityType)` — **no custom frame exists on this path** (it builds a bare `MethodCall`), so the call goes in the provider method itself |

Saga frames pass the `MemberInfo` overload because they already need the member for codegen
(`UpdateSagaFrame` emits `{saga}.{_idMember}`); entity frames use the `Type` overload since they
don't otherwise need it.

**Runtime** — make it structural rather than a list of easily-forgotten one-liners. Introduce one
private collection accessor per operations class and route every existing `GetCollection<T>` call
through it:

```csharp
// MongoSagaOperations — replaces the 4 inline
// database.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga))) calls
// (SagaFrames.cs:37, :56, :82, :113).
private static IMongoCollection<TSaga> SagaCollection<TSaga>(IMongoDatabase database) where TSaga : class
{
    MongoIdentityMapping.EnsureIdMember(typeof(TSaga));
    return database.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga)));
}

// MongoEntityOperations — replaces the 4 inline calls
// (EntityFrames.cs:39, :58, :76, :94).
private static IMongoCollection<T> EntityCollection<T>(IMongoDatabase database) where T : class
{
    MongoIdentityMapping.EnsureIdMember(typeof(T));
    return database.GetCollection<T>(MongoConstants.EntityCollectionName(typeof(T)));
}
```

This makes the invariant hold by construction: **you cannot obtain a collection handle for a
persisted document type without its class map having been aligned first.** A future operation added
to either class inherits the guarantee. `ApplyStorageActionAsync` needs no call of its own — it
delegates to `UpsertAsync`/`DeleteAsync`.

**Cost:** one `ConcurrentDictionary.ContainsKey` per operation, against a network round-trip.
Negligible, and it buys mode-independence.

`MongoEntityOperations.IdOf<T>` (`EntityFrames.cs:133-136`) stays exactly as it is. Once the class
map is aligned, `LookupClassMap(typeof(T)).IdMemberMap` **is** the Wolverine-resolved member — the
read/write disagreement (F1 §3.2) closes without touching `IdOf`. Its `InvalidOperationException` for
"no mapped `_id` member" becomes unreachable in practice, and stays as a backstop.

### 4.3 Thread safety — resolving F1's open flag

F1 correction 2 flagged that the plan's `ConcurrentDictionary<Type,bool>.GetOrAdd(type, factory)`
shape leans on a guarantee `ConcurrentDictionary` does **not** give: the value factory may run more
than once concurrently for the same key (only one result is stored). Two concurrent factory runs
would both see `IsClassMapRegistered == false` and both call `RegisterClassMap` — one wins, the
other throws `[10]`'s bare `ArgumentException`.

**Resolution: don't use `GetOrAdd`.** §3.2 uses lock-free memo read → `lock (_gate)` → double-check →
align → memo write. Properties:

1. **Our own callers can never double-register.** All alignment work for all types is serialized by
   one process-wide gate. Codegen constructs frames on one thread anyway; the runtime path takes the
   lock only on a memo miss (once per type per process).
2. **No contention on the hot path.** The pre-lock `ContainsKey` is lock-free; after the first
   alignment of a type, no operation ever takes the lock.
3. **A race with *application* code is still possible** — an app thread calling `RegisterClassMap<T>`
   or triggering `LookupClassMap(T)` concurrently with our first alignment. Our lock cannot cover
   the driver's registry. Step 4 handles it: catch `ArgumentException`, re-read the winner's map,
   assert agreement or throw §3.3's error. Outcome is identical to the app having won by a wider
   margin.
4. **The driver's own locking** (`BsonSerializer.ConfigLock`, F1 §2.1/§2.2) makes each individual
   `IsClassMapRegistered`/`RegisterClassMap`/`LookupClassMap` call atomic; we rely on that and add
   only the compound-operation serialization it doesn't provide.
5. **Coarse gate over per-type locks** deliberately: alignment is once-per-type-per-process
   configuration work, and one gate is far easier to reason about than a lock-striping scheme.

### 4.4 Ordering — resolving F1's second open flag

F1 §2.3 established that `LookupClassMap` **auto-maps and freezes** any unregistered type, and that
`EnsureIdMember` therefore cannot repair a type that something else already froze wrong. `[11]`/`[14]`
confirm. The design's answer:

- **Best effort at ordering:** codegen-time calls run during host build, before any handler executes,
  so for the normal case we are first.
- **When we are not first, fail loudly, never silently:** §3.1 rule 2 compares and throws §3.3's
  actionable error. There is deliberately **no** attempt to unregister, replace, or thaw a map — the
  driver forbids all three (`[12]`, and F1 §2.2's "class maps can NOT be replaced" source comment).
- One in-library ordering hazard is worth naming for F6/F7: `IdOf<T>` calls `LookupClassMap`, so
  *if* an entity write for a type could ever precede that type's alignment, the auto-map would freeze
  first. §4.2's `EntityCollection<T>` accessor removes the hazard structurally — every write path
  obtains its collection (and therefore aligns) before it can reach `IdOf`. F7 must keep that
  ordering when it edits `UpsertAsync`/`DeleteAsync`.

---

## 5. `UpdateSagaFrame`'s silent `?? "Id"` fallback → one throwing resolution

**Today** (`SagaFrames.cs:227-229`):

```csharp
var idMember = SagaChain.DetermineSagaIdMember(sagaType, sagaType);
_idMember = idMember?.Name ?? "Id";                       // silently invents a member
_idType = idMember?.GetRawMemberType() ?? typeof(string); // silently invents a type
```

`DetermineSagaIdType` throws for the identical null (`MongoDbPersistenceFrameProvider.cs:90-93`), and
F1 §1.2 confirmed the null case is reachable, where it produces a cryptic *generated-code compile
error* referencing a non-existent `saga.Id`.

**Decision:** delete the fallback; resolve through `MongoIdentityMapping.ResolveIdMember` (§3.2), and
make `DetermineSagaIdType` delegate to the same method so there is exactly one message and one code
path:

```csharp
// SagaFrames.cs, UpdateSagaFrame ctor
var idMember = MongoIdentityMapping.ResolveIdMember(sagaType);
MongoIdentityMapping.EnsureIdMember(sagaType, idMember);
_idMember = idMember.Name;
_idType = idMember.GetRawMemberType();

// MongoDbPersistenceFrameProvider.cs — DetermineSagaIdType becomes a delegation
public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    => MongoIdentityMapping.ResolveIdMember(sagaType).GetRawMemberType();
```

**Exception type: `ArgumentException`, message text unchanged** —
`$"Unable to determine the identity member for {documentType.FullNameInCode()}"`. `ArgumentException`
is kept (over the semantically tidier `InvalidOperationException`) for parity with Wolverine's own
`LightweightSagaPersistenceFrameProvider.cs:80-83`, which this provider mirrors (F1 §1.4), and
because it is the type already thrown on `main` — no consumer-visible type change.

**One accepted cosmetic delta:** the `paramName` moves from `nameof(sagaType)` to
`nameof(documentType)`, so `ArgumentException.Message`'s auto-appended tail reads `(Parameter
'documentType')` instead of `(Parameter 'sagaType')`. Acceptable — it is a diagnostic string, no test
asserts it today. **F6/F7 tests must assert on the message *prefix* or use `Contains`, never on
`ParamName`.**

---

## 6. LD4 — resolved: throw for `Delete<TSaga>` / `IStorageAction<TSaga>`

**Chosen: throw at codegen.** Reasons, in order of weight:

1. **Routing would corrupt OCC.** The saga write frames stamp and version-guard `Saga.Version`
   (`CLAUDE.md:161`). A `Delete<TSaga>`/`IStorageAction<TSaga>` arriving from a plain handler has no
   `oldVersion` to guard on and no `SagaChain` around it, so routing to the saga frames means
   unguarded writes into saga collections — trading a visible bug for an invisible one.
2. **No sibling provider supports it** (F1 §1.5).
3. **`Saga` lifecycle is `SagaChain`'s.** Completion is `MarkCompleted()`; the storage-action surface
   is for plain documents.
4. **Codegen time is the right time** — host build, before a single message flows.

Today both paths silently target the un-prefixed entity collection (e.g. `orderfulfillmentsaga`
rather than `wolverine_saga_orderfulfillmentsaga`), so the write appears to succeed and affects
nothing the saga machinery ever reads (F1 §3.3).

### 6.1 Exact contracts

Both sites throw **`InvalidOperationException`**. Two distinct messages, each naming the offending
return-value form:

```csharp
// MongoDbPersistenceFrameProvider.cs — DetermineDeleteFrame(Variable, IServiceContainer) (:136-137)
public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
{
    if (variable.VariableType.CanBeCastTo<Saga>())
    {
        throw new InvalidOperationException(
            $"Cannot use Delete<{variable.VariableType.FullNameInCode()}> from a non-saga handler: " +
            "saga types are managed by Wolverine saga chains, which own the saga's identity, " +
            "version guard, and collection. Complete the saga from a saga handler with " +
            "MarkCompleted() instead.");
    }

    return new MongoDeleteEntityByVariableFrame(variable);
}

// MongoDbPersistenceFrameProvider.cs — DetermineStorageActionFrame (:147-156)
public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
{
    if (entityType.CanBeCastTo<Saga>())
    {
        throw new InvalidOperationException(
            $"Cannot use IStorageAction<{entityType.FullNameInCode()}> from a non-saga handler: " +
            "saga types are managed by Wolverine saga chains, which own the saga's identity, " +
            "version guard, and collection. Return the saga from a saga handler, or complete it " +
            "with MarkCompleted(), instead.");
    }
    // … unchanged
}
```

**Binding:** the exception type, both prefixes (`Cannot use Delete<…> from a non-saga handler:` /
`Cannot use IStorageAction<…> from a non-saga handler:`), and the guard placement (**before** any
frame or `MethodCall` construction). Tests assert on the prefix.

### 6.2 Sites deliberately left unguarded

| Method | Why no guard |
|---|---|
| `DetermineDeleteFrame(Variable sagaId, Variable saga, …)` (`:132-133`) | The two-variable overload **is** the saga path — `SagaChain` calls it for completion. Guarding it would break every saga. |
| `DetermineInsertFrame` / `DetermineUpdateFrame` / `DetermineStoreFrame` (`:108-131`) | Already branch on `CanBeCastTo<Saga>()` and route sagas to the correct version-guarded frames — correct as written. |
| `DetermineLoadFrame` (`:95-98`) | Already branches; loads carry no write semantics. |
| `CanPersist` (`:70-80`) | Stays unconditional `true`. `[Entity]` parameter loads select the provider through it (`CLAUDE.md`, T1.1); narrowing it would break entity loads. The saga/entity distinction stays in the frame factories. |

---

## 7. Reconciliation with the "no process-global serializer mutation" stance

Two existing `CLAUDE.md` bullets become inaccurate once F6/F7 land, and one new bullet is needed.
**Exact text** (F6 lands the first two edits plus the saga half of the new bullet; F7 extends the new
bullet with its entity/LD4 half — or F6 may land the whole bullet and F7 verify it, at F6's
discretion):

**(a) Replace `CLAUDE.md:156`:**

> - **DateTimeOffset stored as UTC BSON Date:** every `DateTimeOffset`/`DateTimeOffset?` property on document types is annotated with `[BsonRepresentation(BsonType.DateTime)]`. No process-global serializer is registered — the library does not mutate the host app's BSON registry.

with:

> - **DateTimeOffset stored as UTC BSON Date:** every `DateTimeOffset`/`DateTimeOffset?` property on document types is annotated with `[BsonRepresentation(BsonType.DateTime)]`. **No serializer, convention, or convention pack is ever registered process-globally** — the library does not change how the host app serializes any type. The one registry interaction it does make is narrow and additive: a per-type `BsonClassMap` id-member alignment for saga/entity types Wolverine persists whose identity member the driver would not otherwise map to `_id` (see "Identity-member alignment" below).

**(b) Replace `CLAUDE.md:159`:**

> - **Direct document storage:** the saga POCO is stored as a MongoDB document. The identity member maps to `_id` via the driver's default Id-member convention. No envelope wrapper.

with:

> - **Direct document storage:** the saga POCO is stored as a MongoDB document, no envelope wrapper. Wolverine's resolved identity member is what maps to `_id` — via the driver's own convention when the member is named `Id`/`id`/`_id` or carries `[BsonId]`, and via the library's per-type class-map alignment otherwise (see "Identity-member alignment").

**(c) Add this bullet** (in the Key Design Decisions list, adjacent to the two above):

> - **Identity-member alignment (`MongoIdentityMapping`, F6/F7):** Wolverine resolves a document's identity member by *its* convention (`SagaChain.DetermineSagaIdMember`: `[SagaIdentity]` → `{TypeName}Id` → `{Name-minus-Saga}Id` → `SagaId` → `Id`); the MongoDB driver resolves `_id` by *its own* (`NamedIdMemberConvention`: only `Id`/`id`/`_id`, plus `[BsonId]`). Before 1.0.1 nothing reconciled the two, so a saga or entity keyed on e.g. `ShipmentId` was written with a **server-generated `ObjectId` `_id`** and could never be loaded back — silent data corruption. `MongoIdentityMapping.EnsureIdMember` bridges them: for a type whose class map is not yet registered **and** whose driver-resolved id member disagrees with Wolverine's, it registers one additive per-type `BsonClassMap` (`AutoMap()` + `MapIdMember(resolvedMember)`); the driver's `Freeze()` then normalizes that member's element name to `_id`, so the frames' `Eq("_id", …)` filters and the written documents agree. It is **a no-op that leaves the BSON registry untouched** whenever the driver already agrees (every `Id`-keyed type — i.e. every consumer that worked before 1.0.1 — and every `[BsonId]`-annotated type), and it **throws a precise `InvalidOperationException`** when a class map is already registered naming a different id member (the app owns its own maps; we only assert agreement). Called at codegen time from every saga/entity frame constructor **and** at runtime from the `MongoSagaOperations`/`MongoEntityOperations` collection accessors — the runtime leg is required because `TypeLoadMode.Static` never constructs frames (`HandlerChain.cs:309-325` attaches pre-generated types without calling `AssembleTypes`). This is **not** the process-global serializer/convention mutation the library forswears: no serializer, no convention, no convention pack, and no behavior change for any type Wolverine does not persist.

**(d) Also add**, in the same list, the LD4 decision:

> - **Saga types are rejected on the generic storage-action paths (LD4, F7):** a non-saga handler returning `Delete<TSaga>` or `IStorageAction<TSaga>` throws `InvalidOperationException` at codegen. Nothing upstream guards this (`Delete.cs:22-26`, `IStorageAction.cs:23-27`, `Storage.cs:63-74` are all gated only by `CanPersist`, which this provider hardcodes `true`), and before 1.0.1 those returns silently targeted the un-prefixed **entity** collection (`orderfulfillmentsaga`) instead of `wolverine_saga_orderfulfillmentsaga`, with no `Saga.Version` guard. Routing them to the saga frames was rejected: without a `SagaChain` there is no captured `oldVersion`, so it would trade a visible bug for silent OCC corruption. Sagas are completed with `MarkCompleted()` from a saga handler. No sibling provider supports this path for sagas either.

**README.md:** no change is required (it documents the public API surface, and nothing public
changes). F6/F7 **may** add a short "Identity conventions" subsection stating that any Wolverine
identity convention works and that `[BsonId]` is the escape hatch for a pre-registered class map;
optional, not gating.

---

## 8. On-disk compatibility statement

**No migration is owed and none is provided.** The fix changes only how future reads and writes key
`_id`; it never rewrites an existing document. Case by case:

| Shape | Before 1.0.1 | After | Compatibility |
|---|---|---|---|
| Identity member named `Id`/`id`/`_id` (every saga in every compliance suite; upstream `Todo`; the demo's `OrderNote`, `Order`, `OrderFulfillmentSaga`) | Driver maps `Id` → `_id`; works | Helper is a **no-op, registry untouched** (§3.1 rule 3) | **Byte-identical.** `[8]` shows the unchanged document. This is the whole regression story for working consumers. |
| Identity member carries `[BsonId]` (any name) | Driver maps it → `_id`; works | Helper is a **no-op** (F1 §2.6, `[BsonId]` applies during `AutoMap`) | **Byte-identical.** |
| Non-`Id` member, no `Id` member at all (e.g. `ShipmentSaga.ShipmentId`) | **Never worked.** No `_id` written; server assigns an `ObjectId` (`[9]`); every load returns null and every insert creates a new unfindable document | `_id` = the identity value (`[4]`) | **No migration owed** — no functioning data can exist. Any documents on disk are unreachable orphans that were never readable by the library or the app. Dropping the collection is optional cleanup, not a migration step. |
| **Both** `{TypeName}Id` **and** `Id` (the review's poisoned entity shape) | Writes keyed `Id` → `_id`; reads filtered on `{TypeName}Id`'s value. Worked **only** while the app kept the two members equal | `_id` = `{TypeName}Id`; `Id` demotes to an ordinary field named `Id` (`[7]`) | **Values equal (the accidentally-working case): existing documents stay loadable** — `_id` holds the same value before and after; re-writes converge to the new shape, which additionally carries an `Id` field. **Values unequal: was already broken**, same as the row above. |
| Saga documents generally | — | — | `Version` and every other member serialize unchanged; **no `_t` discriminator is introduced** (`[18]`/`[20]`), so saga documents keep their shape. |

**Index impact: none.** Alignment changes which member's value lands in `_id`, not the `_id` index
itself, and saga/entity collections have no secondary indexes (`CLAUDE.md`, T4.6).

**Semver character (for the F6/F7 PR descriptions, per the plan's post-1.0 note):** a **patch**-level
bug fix for the primary behavior (unloadable documents become loadable), with a **minor**-flavored
edge: two previously-silent misconfigurations now throw at codegen (an unresolvable identity member,
§5; a conflicting registered class map, §3.3) and one previously-silent misuse now throws (LD4, §6).
No working configuration starts throwing. F20/OQ6 decides the actual version.

---

## 9. Test matrix

Style follows the repo's existing custom suites (`saga_atomicity.cs`, `storage_action_compliance.cs`)
on `AppFixture`. No upstream compliance spec covers any non-`Id` identity member (F1 §4), so these
are the only oracle. Rows 1–5 and 12–14 drive **real generated frames** through an `IHost` and verify
with **direct Mongo reads** of the native-typed `_id`; rows 6–11 are cheaper unit-level tests of the
helper and the codegen guards.

| # | Shape / scenario | Precedence tier | Id type | Task | File |
|---|---|---|---|---|---|
| 1 | Saga, `[SagaIdentity]`-attributed member | 1 | `string` | F6 | `saga_identity_conventions.cs` |
| 2 | Saga, `{TypeName}Id` (e.g. `ShipmentTrackerSaga.ShipmentTrackerSagaId`) | 2 | `Guid` | F6 | ″ |
| 3 | Saga, `{Name-minus-Saga}Id` (e.g. `ShipmentSaga.ShipmentId`) | 3 | `Guid` | F6 | ″ |
| 4 | Saga, `SagaId` | 4 | `int` | F6 | ″ |
| 5 | Saga, plain `Id` — regression | 5 | Guid/string/int/long | F6 | **existing** `{string,guid,int,long}_saga_storage_compliance.cs` — must stay green **unchanged** |
| 6 | Helper: driver already agrees (`Id`-named) → **registry untouched** (`IsClassMapRegistered` still `false` after the call) | — | — | F6 | `identity_mapping_helper.cs` (new, unit) |
| 7 | Helper: `[BsonId]` on a non-`Id`-named member → no-op, no registration | — | — | F6 | ″ |
| 8 | Helper: conflicting **pre-registered/frozen** map → `InvalidOperationException`, message contains both member names (§3.3) | — | — | F6 | ″ |
| 9 | Helper: unresolvable identity member (POCO with no id-shaped member) → `ArgumentException`, message prefix per §5 | — | — | F6 | ″ |
| 10 | Helper: N threads call `EnsureIdMember` concurrently for one fresh type → exactly one registration, **no** `ArgumentException` escapes (§4.3) | — | — | F6 | ″ |
| 11 | Helper: idempotent — repeated calls after alignment are no-ops and never throw | — | — | F6 | ″ |
| 12 | Entity with **only** `{TypeName}Id`: `[Entity]` load + `Insert<T>` + `Delete<T>` round-trip; direct read proves `_id` is the identity value | 2 | `Guid` | F7 | `entity_identity_conventions.cs` |
| 13 | Entity with **both** `{TypeName}Id` and `Id` (the poisoned shape): read and write agree on `{TypeName}Id`; direct read proves `_id` == `{TypeName}Id` and a separate `Id` field exists (§2.3, `[7]`) | 2 | `Guid` + `string Id` | F7 | ″ |
| 14 | Entity, plain `Id` — regression | 5 | `string` | F7 | **existing** `storage_action_compliance.cs` (upstream `Todo`) — must stay green **unchanged** |
| 15 | LD4: plain handler returning `Delete<TSaga>` → codegen `InvalidOperationException` with §6.1's `Delete<…>` prefix | — | — | F7 | ″ |
| 16 | LD4: plain handler returning `IStorageAction<TSaga>` (e.g. `Storage.Delete(saga)`) → codegen `InvalidOperationException` with §6.1's `IStorageAction<…>` prefix | — | — | F7 | ″ |
| 17 | Runtime-leg / Static-mode net: invoke a `MongoSagaOperations`/`MongoEntityOperations` entry point for a non-`Id`-keyed type **whose frames were never constructed**; alignment still applies and the round-trip succeeds (§4.1) | 2 or 3 | `Guid` | F6 (saga) / F7 (entity) | respective conventions file |

Each saga/entity row also asserts the **lifecycle**, not just the first write: start → update →
complete for sagas (proving load, insert, version-guarded update, and delete all key the same
member), and load → upsert → delete for entities.

### 9.1 Test-authoring constraints (all rows)

These follow from the driver's semantics and will silently produce false greens if ignored:

1. **Class maps are process-global and cannot be unregistered or replaced** (F1 §2.2, `[12]`,
   `[14]`). Give **every** row its own dedicated POCO type; never reuse a document type across two
   test scenarios, and never assume a fresh registry inside a test.
2. **`MongoIdentityMapping`'s memo is process-global too.** A type aligned by one test stays aligned
   for the whole run. Rows 6–11 each need a type touched by nothing else. If a test needs to observe
   the pre-alignment state, it must be the only test that ever mentions that type.
3. **xUnit parallelism.** Because of (1) and (2), rows 6–11 must not race each other over a shared
   type; distinct types per row makes them parallel-safe without collection attributes.
4. **Row 8 must pre-register its conflicting map itself** (`BsonClassMap.RegisterClassMap` mapping
   `Id`, on a type whose Wolverine-resolved member is `FooId`) — and that type is then permanently
   poisoned for the process, which is fine because no other row uses it.
5. **Row 10** should assert both "no exception" and "exactly one id-member mapping" — e.g. N threads
   through a `Barrier`, then a single `LookupClassMap` assertion.
6. **Guid representation:** see §2.7 — integration rows need no annotation; a unit row serializing a
   Guid-keyed POCO without a `MongoClient` in the process needs
   `[BsonGuidRepresentation(GuidRepresentation.Standard)]` or should use a `string` id.
7. **Direct-read assertions must resolve collection names through `MongoConstants`**
   (`SagaCollectionName`/`EntityCollectionName`), never string literals — the existing house rule
   (`MongoConstants.cs:31-36`).
8. **Codegen-guard rows (8, 9, 15, 16)** assert on the message **prefix** (`Contains`/`StartsWith`),
   never on `ArgumentException.ParamName` (§5) and never on the full formatted string.
9. **Stale generated code:** after touching `Internals`, delete `src/Wolverine.MongoDB.Tests/Internal/Generated`
   before running locally (known repo gotcha).
10. **Dump the generated source** for at least one convention saga and one convention entity
    (`HandlerChain.SourceCode` via reflection, per the repo's existing convention) to confirm the
    frames still emit the same session-bound operations and that only the id member's name changed.

### 9.2 Upstream-contribution note (OQ5)

Rows 1–5 and 12–14 are exactly the coverage `Wolverine.ComplianceTests` lacks (F1 §4). Write them in
compliance style — a POCO per convention, lifecycle-driven, no Mongo-specific assertions in the
*arrange* half — so they can be lifted upstream later. Still **defer** the contribution; F6's PR adds
the `FOLLOWUPS.md` note.

---

## 10. Deltas from the plan (what F6/F7 should do differently)

Explicit, so implementing sessions don't treat these as scope creep or as mistakes:

| # | Plan said | This design says | Why |
|---|---|---|---|
| 1 | `EnsureIdMember` registers the class map unconditionally once built | **Don't register when the driver's own conventions already agree** (§3.1 rule 3) | Keeps every working consumer's registry untouched and preserves their right to register their own map later (`[14]`). Zero-risk for the `Id`-keyed majority. |
| 2 | `ConcurrentDictionary.GetOrAdd(type, factory)` | Lock-free memo read → `lock` → double-check → align (§4.3) | Resolves F1 correction 2: `GetOrAdd`'s factory may run twice, and `RegisterClassMap` is not idempotent (`[10]`). |
| 3 | Invocation at "every frame constructor — codegen time" | Frame constructors **plus** runtime collection accessors (§4.1, §4.2) | `TypeLoadMode.Static` never constructs frames (`HandlerChain.cs:309-325`), so codegen-only alignment is silently absent in pre-generated/AOT deployments. **This is the most important delta.** |
| 4 | F6 snippet threw `InvalidOperationException` for an unresolvable identity member | **`ArgumentException`**, message identical to today's `DetermineSagaIdType` (§5) | The plan's own decision text asks for "the same message as `DetermineSagaIdType`"; matching the type too keeps parity with `LightweightSagaPersistenceFrameProvider` and changes nothing consumer-visible. |
| 5 | F6 scope: `MongoIdentityMapping` + 4 saga frame ctors | Also: `MongoSagaOperations`'s 4 inline `GetCollection` calls → one `SagaCollection<TSaga>` accessor; `DetermineSagaIdType` delegates to `ResolveIdMember` | Follows from deltas 3 and 4. Same files, no new files. |
| 6 | F7 scope: 3 entity frame ctors + 2 provider guards | Also: `MongoEntityOperations`'s 4 inline `GetCollection` calls → one `EntityCollection<T>` accessor; `EnsureIdMember` in `DetermineStorageActionFrame` itself (no frame exists on that path) | Same reasons; `MethodCall`-based path has no constructor to hook. |
| 7 | Test matrix: 5 shapes | 17 rows incl. all four non-`Id` precedence tiers, helper unit rows, and the Static-mode row (§9) | Tiers 2 and 4 were unlisted but are legal conventions; the helper's negative and concurrency paths need direct coverage since integration tests can't reach them. |

Everything else in the plan's F6/F7 sections stands: red-first ordering, the compliance suites as the
regression oracle, both TFMs, one branch/PR per task, CHANGELOG `### Fixed` entries.

---

## 11. Rejected alternatives

| Option | Verdict |
|---|---|
| **LD1-A — fail fast only** (throw unless the driver already maps the resolved member; require `[BsonId]`) | Rejected. It converts silent corruption into a clear error — a real improvement — but leaves three of Wolverine's five identity conventions unusable without a Mongo-specific annotation on the app's own POCOs, which no sibling provider demands. LD1-B′ subsumes it: every case where A would throw, B′ either fixes silently (unregistered type) or throws the *same* clear error (already-registered conflict). Kept documented as the downscope if the registry interaction is ever judged unacceptable (plan R2). |
| **LD1-C — key frames off the mapped element name instead of `_id`** | Rejected up front by the plan, confirmed here. Documents would carry a server `ObjectId` `_id` **plus** a secondary identity field needing its own unique index, diverging from every sibling provider and breaking 1.0.0's on-disk shape for `Id`-keyed documents — i.e. it would break the consumers who currently work, to help those who don't. |
| **`BsonClassMap.TryRegisterClassMap`** instead of `RegisterClassMap` + catch | Rejected. In 3.9.0 it exists **only** in generic form (`TryRegisterClassMap<TClass>(BsonClassMap<TClass>/Action<>/Func<>)` — verified against `MongoDB.Bson.xml`), so a `Type`-keyed helper would need `MakeGenericMethod` plus a reflectively-constructed `BsonClassMap<T>`. And it buys nothing: a `false` return still requires `LookupClassMap` + compare to know *why*, which is exactly what the catch branch does. More reflection, same logic. |
| **`GetRegisteredClassMaps()`** instead of `LookupClassMap` in the already-registered branch | Rejected on balance. It would avoid freezing the app's map during our check, but it's an O(n) scan of a global list and a less obvious API, while the freeze is benign: the map's *content* is unchanged, and the driver freezes it on first use anyway (`[11]`). Recorded here so a future reader knows it was considered. |
| **Registering a custom `IConvention`/convention pack** (e.g. a Wolverine-aware `IdMemberConvention`) | Rejected. Convention packs apply process-globally to types matching a filter — precisely the "mutate the host app's BSON registry" behavior `CLAUDE.md:156` forswears, and it would affect types Wolverine never persists. The per-type class map is the narrowest instrument that does the job. |
| **Emitting the alignment call into the generated code** (instead of the runtime accessors) | Rejected. It would put a per-invocation call in every generated handler body, couple the emitted source to an internal helper, and still need the codegen-time call for the early failure. The collection accessors achieve the same coverage inside the library, invisibly to generated code. |
| **LD4 — route `Delete<TSaga>`/`IStorageAction<TSaga>` to the saga frames** | Rejected, §6 reasons 1–4: no `SagaChain`, therefore no captured `oldVersion`, therefore unguarded writes into saga collections — silent OCC corruption in place of a visible bug. |
| **Mutating/unregistering a conflicting frozen class map** | Impossible, not merely rejected: the driver forbids replacement ("class maps can NOT be replaced", F1 §2.2) and mutation after freeze (`[12]`). Hence "assert agreement or throw" is the only available contract. |

---

## 12. Handed to F6/F7 (implementation-time confirmations, not open questions)

Nothing here changes a decision; each is a one-line check that the design's premises still hold in
the implementing session:

1. **Confirm** `SagaFrames.cs:227-229`'s fallback is still present and that the four frame ctors are
   at the §4.2 line numbers (drift-check only).
2. **Confirm** row 5 / row 14 (the existing compliance suites) pass **unchanged** — no edits to
   compliance files. If any needs an edit, stop and report: that means the design moved a working
   consumer's data, which §8 says it must not.
3. **Verify by dumped generated source** (§9.1 rule 10) that the only difference for an `Id`-keyed
   saga is *nothing at all*, and for a convention saga is the emitted member name.
4. If a Guid-keyed convention row hits `GuidSerializer … Unspecified`, apply §2.7's note — do **not**
   register a global Guid serializer.
5. F7 must keep the `EntityCollection<T>`-before-`IdOf<T>` ordering (§4.4) when it edits
   `UpsertAsync`/`DeleteAsync`.
