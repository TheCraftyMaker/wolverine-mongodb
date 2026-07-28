# Identity-Mapping Design (Task F3, DESIGN GATE)

> **Binding design.** This document resolves **LD1** (identity-mapping mechanism) and **LD4** (saga
> types on the storage-action paths) from `2026-07-07-review-findings-remediation.md` into contracts
> that **F6** and **F7** implement without further debate. Where this document and the plan's
> illustrative snippets differ, **this document wins**: the deltas are listed explicitly in
> §10 so the implementing sessions can see exactly what changed and why.
>
> **Input:** `2026-07-07-identity-mapping-discovery.md` (F1). Every F1 fact is taken as given and is
> not re-derived here. F1 left two items open for this gate, the `ConcurrentDictionary.GetOrAdd`
> double-invocation edge (F1 §2.2, correction 2) and the `LookupClassMap`-freezes-first ordering
> subtlety (F1 §2.3), both are resolved below (§4.3, §4.4).
>
> **New empirical work done at this gate:** F1 established the driver's class-map semantics by
> reading `mongo-csharp-driver` v3.9.0 source. Because the entire design hinges on one behavior F1
> asserted but did not execute, *does `MapIdMember` on a non-`Id`-named member actually put that
> value in the `_id` element?*, this gate ran an isolated MongoDB.Driver 3.9.0 probe. It found the
> answer is **yes, but only after `Freeze()`**, which no F1 source excerpt showed. §2 records the
> full transcript. Had the design been written from source-reading alone, §2.2's element-name
> normalization would have been an unstated assumption underneath every downstream task.

> ### AMENDED 2026-07-26, base-declared identity members
>
> F6 (`fix/saga-identity-mapping`, local commit `75b5b9d`, deliberately unpushed) implemented this
> design faithfully, got its 11 new facts green, and then **broke 40 previously-green compliance
> facts**. It stopped per the plan's escalation rule instead of improvising.
>
> Cause: a `BsonClassMap` semantic neither F1 nor this document's original §2 probe covered, an
> identity member declared on a **base class**, which is the shape *every* upstream saga compliance
> type has (`BasicWorkflow<TStart,TCompleteThree,TId>.Id`, inherited by
> `String`/`Guid`/`Int`/`LongBasicWorkflow`). The original §2 probed identity-on-the-type-itself and
> identity-on-the-**subclass**, never identity-on-the-**base**.
>
> This amendment adds **§2.8** (the base-declared probe battery, 30 new verbatim facts across two
> rounds plus a ground-truth run against the real `*BasicWorkflow` types) and rewrites **§3.1**,
> **§3.2**, **§3.3**, **§4.2**, **§4.4**, **§7(c)**, **§8**, **§9**, **§10**, **§11**, **§12**. It also
> corrects **§2.6**'s side-effect claim. Two decisions are new (**D6**, **D7** in §1); LD1 Option B′
> and LD4 are unchanged in mechanism.
>
> **§10.1 is the F6-facing diff**: precisely what changes relative to `75b5b9d`, file by file and
> member by member. F6 does not need to re-read the rest of this document.

**Status:** decisions final (amended 2026-07-26). **Gates:** F6 (saga identity), F7 (entity identity
+ LD4 guards), and F18's demo shapes.

---

## 1. Decision summary

| # | Decision | Resolution |
|---|---|---|
| **LD1** | Identity-mapping mechanism | **Option B′, ensure-or-fail class map, minimal-mutation.** Option B, refined so the helper *never touches the BSON registry for a type the driver already maps correctly* (§3). **Unchanged by the amendment**: the mechanism was sound; only the "already maps correctly" *predicate* was wrong. |
| **D1a** | Helper contract | `MongoIdentityMapping.EnsureIdMember(Type, MemberInfo)` + `EnsureIdMember(Type)` + `ResolveIdMember(Type)` (§3.2), plus the private `driverIdMember(Type)` walk added by the amendment. |
| **D6** *(new)* | The "driver already agrees" predicate | **Hierarchy walk**, most-derived-first, AutoMapping a throwaway map per level (or reading an already-registered one); first level that resolves an id member wins. Verified exact against the authoritative `LookupClassMap` answer on 8 shapes and on all four real `*BasicWorkflow` types, with **zero registry mutation** (§2.8, §3.1). Replaces the single unfrozen-probe read, which reports `null` for every inherited id member. |
| **D7** *(new)* | Shapes the mechanism cannot fix | Two **throw** cases, both detected at codegen: **(a)** Wolverine's member is declared on a base class, the driver rejects `MapIdMember` for a foreign `DeclaringType`; **(b)** a *different*, base-declared member already occupies `_id`, mapping ours would produce a type that throws `BsonSerializationException` on its **first write**. Exact messages in §3.4/§3.5. |
| **D1b** | Invocation points | Codegen time (all 4 saga frame ctors, all 3 entity frame ctors, `DetermineStorageActionFrame`) **and** runtime (via new collection-accessor helpers in `MongoSagaOperations`/`MongoEntityOperations`). The runtime leg is **required**, not belt-and-braces, in `TypeLoadMode.Static` the frame constructors never run (§4.1–§4.2). |
| **D1c** | Thread safety | Lock-free memo read → `lock` → double-check → align. Not `ConcurrentDictionary.GetOrAdd` (§4.3). |
| **D2** | CLAUDE.md reconciliation | `CLAUDE.md:156` and `:159` rewritten, one new decision bullet added, exact text in §7. |
| **D3** | `UpdateSagaFrame`'s `?? "Id"` fallback | Deleted. All identity resolution funnels through `MongoIdentityMapping.ResolveIdMember`, which throws the same `ArgumentException` message `DetermineSagaIdType` throws today; `DetermineSagaIdType` is refactored to delegate to it (§5). |
| **LD4** | `Delete<TSaga>` / `IStorageAction<TSaga>` from a non-saga handler | **Throw** at codegen. `InvalidOperationException`, exact message text in §6. |
| **D4** | On-disk compatibility | No migration owed. `Id`-keyed documents are byte-identical, **re-derived for the inherited-`Id` case** and proven by a byte-for-byte hex diff against a never-touched twin (§2.8 R2-D2, §8). Non-`Id` types never round-tripped. |
| **D5** | Test matrix | 24 rows across F6/F7, §9, the amendment adds rows 18–24 (inherited-`Id` regression, both throw cases, the registry-untouched assertion, `[BsonId]`-on-base, and the entity analogues). |

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
only identity-shaped member is `{TypeName}Id`, for root types and subclasses alike. `[9]` confirms
F1 §2.5: such a type serializes with **no `_id` element at all**, so the server assigns an
unrelated `ObjectId`. That is the 1.0.0 silent-corruption path end to end.

### 2.2 `Freeze()` normalizes the id element name to `_id`, the load-bearing fact

`[2]` vs `[3]`: immediately after `MapIdMember(ShipmentId)` the member map's `ElementName` is still
`'ShipmentId'`. **Only on `Freeze()` does the driver rewrite it to `'_id'`.** `[4]` and `[18]` show
the consequence on the wire: the document's key element is `_id`, carrying the resolved member's
value, with **no duplicate `ShipmentId`/`DerivedId` field**.

This is what makes LD1-B correct **without touching a single `Eq("_id", …)` filter**: after
alignment, the read-side filters in `MongoSagaOperations`/`MongoEntityOperations` and the write-side
serialization refer to the same element. F1's conclusion that "the raw `Eq("_id", …)` filters stay"
(F1 §5) is confirmed, and now confirmed *for the right reason*.

> **F6/F7 must not "fix" the element name manually.** Do not call `SetElementName("_id")`. The
> driver does it at freeze, and every class map is frozen before first use (`[3]`, `[11]`).

### 2.3 The both-members shape's post-fix document shape

`[6]`/`[7]`: `AutoMap` picks `Id` (the driver's `NamedIdMemberConvention`); after
`MapIdMember(BothMembersId)` the document becomes `{ "_id": <BothMembersId>, "Id": "legacy", … }`.
So the `Id` member does **not** disappear, it demotes to an ordinary field named `Id`. This is the
basis of §8's compatibility statement for that shape.

### 2.4 The no-op path leaves the document byte-identical

`[8]`: a plain-`Id` type whose class map was never registered by us serializes exactly as it does
today. This is the entire regression argument for existing consumers, and it is the reason §3's
algorithm **declines to register** when the driver's own conventions already agree (§3.1 step 4).

### 2.5 Exception shapes, exactly

- Double `RegisterClassMap` for one type → **`ArgumentException`**, message `An item with the same
  key has already been added. Key: <TypeName>` (`[10]`). Confirms F1 correction 2 by execution.
- Mutating a frozen map → **`InvalidOperationException`**, `Class map for <FullName> has been frozen
  and no further changes are allowed.` (`[12]`), the unhelpful message §3's design exists to avoid
  ever surfacing.
- `RegisterClassMap` **after** `LookupClassMap` auto-mapped and froze the type → `ArgumentException`
  (`[14]`). Two consequences: (a) the ordering subtlety of F1 §2.3 is real; (b) any app that
  registers its own class map *after* Wolverine's host build would break if we registered maps
  unnecessarily, §3.1 step 4 preserves that app's ability entirely.

### 2.6 Derived types (i.e. every real saga) behave identically

`[16]`–`[20]`: the identity member on a subclass maps correctly, the inherited `Version` is
serialized, the round-trip recovers both, and **no `_t` discriminator appears** (nominal type ==
actual type for `IMongoCollection<TSaga>`), so saga documents keep their current shape. `[15]`/`[17]`
also settle a side-effect question: `AutoMap()` on a derived type does **not** register a class map
for the base type.

> **CORRECTED BY THE AMENDMENT.** This subsection originally concluded from `[15]`/`[17]` that "the
> probe step in §3.1 has no global side effects." That is true of **`AutoMap()` alone**: and stays
> true of the amended §3.1 predicate, which uses nothing else (proven directly: §2.8 R1-B1, R2-D2,
> and GT-F5 all show the registry unchanged before and after). It is **false** of the obvious repair
> to the inherited-member bug: `probe.Freeze()` **does** register the base type's class map globally
> (§2.8 R1-B3, fact C). §3.1's predicate therefore never freezes a probe. Also note what `[16]`
> did *not* cover: it put the identity member on the **subclass**. The base-declared case, every
> upstream compliance saga, behaves differently and is §2.8's subject.

### 2.7 One thing the probe could *not* settle (and why it doesn't matter)

`[5]` (omitted above) attempted `Builders<ShipmentSaga>.Filter.Eq("_id", guid).Render(…)` and threw
`BsonSerializationException: GuidSerializer cannot serialize a Guid when GuidRepresentation is
Unspecified`. This is an artifact of the probe having no `MongoClient` (which is what configures the
default Guid representation), **not** a finding about `_id` filters. The proof it is an artifact:
the repo's Guid-keyed saga compliance suite (`guid_saga_storage_compliance.cs`, on upstream
`BasicWorkflow<…, Guid>` whose member is named `Id`, carrying no `[BsonGuidRepresentation]`) is green
in CI today through the identical `Eq("_id", sagaId)` filter path. Filter-value serialization is
orthogonal to *which member* is the id, so the member-name change introduces no new representation
risk.

### 2.8 Base-declared identity members (amendment, 2026-07-26)

Three probe runs. **R1**/**R2** are a standalone `net9.0` console app on `MongoDB.Driver 3.9.0`
(same method as §2), each scenario on its own dedicated POCO because class maps are process-global
and un-undoable. Shapes are modelled three levels deep to match reality:
`SagaLike { int Version }` stands in for `Wolverine.Persistence.Sagas.Saga`
(`external/wolverine/src/Wolverine/Saga.cs:39`). **GT** is the ground-truth run: a throwaway xUnit
probe *inside `Wolverine.MongoDB.Tests`*, against the real `*BasicWorkflow` types and the real
`SagaChain.DetermineSagaIdMember` (probe file and `Internal/Generated` deleted afterwards).

Notation: `_W` twins are measured with the candidate **walk**; `_A` twins with the authoritative
(registering + freezing) `LookupClassMap`. Twins keep the two measurements from contaminating each
other, a necessity, not neatness: reading the authoritative answer permanently freezes the type.

#### R1: is the walk exact? (`unfrozen` = what `75b5b9d` reads; `walk` = candidate; `authoritative` = truth)

```
A1 self Id                    unfrozen=Id      walk=Id   (A1_SelfId_W automap)     authoritative=Id  element='_id'   MATCH
A2 Id on closed generic base  unfrozen=(null)  walk=Id   (A2_Generic_W`1 automap)  authoritative=Id  element='_id'   MATCH
A3 Id on non-generic base     unfrozen=(null)  walk=Id   (A3_Base_W automap)       authoritative=Id  element='_id'   MATCH
A4 [BsonId] self, non-Id name unfrozen=Key     walk=Key  (A4_BsonIdSelf_W automap) authoritative=Key element='_id'   MATCH
A5 [BsonId] on base           unfrozen=(null)  walk=Key  (A5_Base_W automap)       authoritative=Key element='_id'   MATCH
A6 non-Id self member         unfrozen=(null)  walk=(null)                         authoritative=(null)              MATCH
A7 non-Id base member SagaId  unfrozen=(null)  walk=(null)                         authoritative=(null)              MATCH
A8 base Id + derived {Type}Id unfrozen=(null)  walk=Id   (A8_Base_W automap)       authoritative=Id  element='_id'   MATCH
```

**8/8 exact.** The `unfrozen` column is the defect: it reads `(null)` for A2/A3/A5/A8, every shape
whose id member is inherited, so `75b5b9d` concludes "the driver disagrees" and proceeds to mutate.
A2 is the compliance shape. A8 is new and important: the driver resolves the **base's** `Id` while
Wolverine resolves the **derived** `{TypeName}Id` (its tier 2 beats tier 5), a disagreement in which
the driver's id member is declared on a *different type* than the member we would map.

#### R1-B: side effects of each candidate for reading the driver's answer

```
B1 before walk: derived=False base=False        B1 after walk: derived=False base=False
B2 read probe.BaseClassMap on an unfrozen AutoMap'd probe => (null); base registered=False; IdMemberMap=(null)
B3 before Freeze: derived=False base=False      B3 after Freeze: derived=False base=True  frozenIdMember=Id
```

- **B1: the walk registers nothing.** This is what preserves §3.1 step 4's guarantee.
- **B2 rules out `BaseClassMap` traversal**: on an unfrozen probe the property is simply `null`, so
  there is nothing to traverse. (It is populated during `Freeze()`, i.e. only once B3's side effect
  has already happened.)
- **B3 confirms fact C:** `Freeze()` registers the **base** type's map (not the derived type's) and
  does resolve the inherited `Id`. Correct answer, unacceptable price.

#### R1-C / R2-E: the write path, and what happens when it cannot work

```
C1 MapIdMember(base-declared member) -> ArgumentOutOfRangeException:
     The memberInfo argument must be for class C1_Derived, but was for class C1_Base. (Parameter 'memberInfo')

C2 id mapped on the DECLARING non-generic base, then two sibling subclasses serialized:
     SiblingOne: { "Version" : 1, "_id" : "S-1", "One" : "a" }
     SiblingTwo: { "Version" : 2, "_id" : "S-2", "Two" : "b" }
     both have _id? True/True   _t present? False/False   sibling maps registered afterwards? True/True

C3 same, but the declaring type is a CLOSED GENERIC:
     ClosedString: { "Version" : 1, "_id" : "G-1", "S" : "s" }
     C3_Generic<string> registered=True   C3_Generic<int> registered=False
     ClosedInt (its own closed base NOT mapped): { "Version" : 1, "SagaId" : 7, "I" : "i" }
```

- **C1 confirms fact B** with the exact message. Mapping an inherited member on the *document* type's
  map is impossible.
- **C2** shows the declaring-type alternative works, **and that it propagates to every sibling
  subclass** (both siblings got `_id` from the base's mapping, and serializing them registered their
  own maps on top of it). That is the blast radius §11 weighs.
- **C3** shows it also works for a closed-generic base, and that each closed generic is a **distinct
  registry entry**: mapping `<string>` leaves `<int>` untouched (`ClosedInt` still writes `SagaId` as
  an ordinary field with no `_id`).

The A8 shape is worse than "doesn't work", R2-E1 steps the four calls apart to find out *when* it
fails:

```
E1 walk resolves 'Id' declared on E1_Base (document type is E1_Derived)
   step 1 AutoMap ....... ok
   step 2 MapIdMember ... ok (no throw)
   step 3 Register ...... ok (no throw)
   step 4 serialize ..... BsonSerializationException: The property 'E1_DerivedId' of type 'E1_Derived'
                          cannot use element name '_id' because it is already being used by property
                          'Id' of type 'E1_Base'.
```

**Alignment "succeeds" and the type becomes unserializable.** Nothing throws at codegen; the failure
lands on the **first saga insert**, at runtime, inside the outbox transaction. §3.5 therefore
pre-detects it. (A fifth step re-`Freeze()`d the same map and reported a `Version` element conflict,
an artifact of forcing a second freeze on a map whose first freeze threw. No conclusion is drawn
from it.)

Candidate remedies, each tested before being written into an error message:

```
E2 [BsonId] on the derived member instead -> SAME BsonSerializationException (does NOT help)
E3 [BsonElement("legacyId")] on the base Id -> walk resolves (null); serialize ok:
      { "Version" : 1, "legacyId" : "legacy-value", "_id" : "d" }
E4 [BsonIgnore] on the base Id            -> walk resolves (null); serialize ok:
      { "Version" : 1, "_id" : "d" }
```

E2 matters most: the intuitive advice (`[BsonId]`) is **wrong** for this shape, so §3.5's message must
not offer it. E3/E4 work because either annotation disqualifies the base member from being an id
member, which frees `_id`.

#### R2-D2: the inherited-`Id` no-op path is byte-identical

```
D2 before walk: closed=False genericBase=False        D2 walk resolves 'Id' declared on D2_Generic`1
D2 after  walk: closed=False genericBase=False
D2 (walked)      bytes: 2B0000001056657273696F6E0004000000025F696400040000006162630002...
D3 (never walked) bytes: 2B0000001056657273696F6E0004000000025F696400040000006162630002...
identical? True
```

Walk first, registry unchanged at every level, then serialize, and the bytes match a structurally
identical twin the walk never touched. This is §8's load-bearing evidence for the case that actually
broke.

#### GT: ground truth against the real compliance types

Run inside `Wolverine.MongoDB.Tests`, using the real `SagaChain.DetermineSagaIdMember`:

```
GT-F1  StringBasicWorkflow  hierarchy: BasicWorkflow`3 -> Saga
       Wolverine resolves 'Id' declared on BasicWorkflow`3 (self-declared? False)      [same for Guid/Int/Long]

GT-F2  registry BEFORE: self=False genericBase=False Saga=False                        [all four]

GT-F3  StringBasicWorkflow  wolverine='Id'  broken-probe='(null)' [DISAGREES -> would MapIdMember]
                                            walk='Id' declaredOn=BasicWorkflow`3 [agrees -> no-op]
       GuidBasicWorkflow / IntBasicWorkflow / LongBasicWorkflow — identical

GT-F4  the broken predicate's next step, reproduced:
       MapIdMember(w) -> ArgumentOutOfRangeException: The memberInfo argument must be for class
       StringBasicWorkflow, but was for class BasicWorkflow`3. (Parameter 'memberInfo')

GT-F5  registry AFTER the walk ran for all four: self=False genericBase=False Saga=False   [all four]

GT-F6  StringBasicWorkflow: { "Version" : 3, "_id" : "SBW-1", "OneCompleted" : false, … "Name" : "n" }
       GuidBasicWorkflow:   { "Version" : 2, "_id" : UuidStandard:0x11111111…, … }
       IntBasicWorkflow:    { "Version" : 1, "_id" : 42, … }
       LongBasicWorkflow:   { "Version" : 1, "_id" : 43, … }
       each keyed on _id? True/True/True/True
```

GT-F4 is byte-for-byte the 40-regression failure (79 occurrences in F6's run). GT-F3 shows the walk
returns all four to the no-op path; GT-F5 shows it does so without registering anything, including
`Saga` itself.

**Incidental observation (not a decision):** GT-F6 serialized `GuidBasicWorkflow.Id` with no
`[BsonGuidRepresentation]` annotation and no explicit `MongoClient`, whereas §2.7's standalone probe
threw `GuidRepresentation is Unspecified` for the same shape. So Guid representation depends on
process-level driver initialization that the test assembly satisfies and a bare console app does not.
§2.7's guidance is unchanged and, if anything, safer than needed: integration rows need no
annotation.

**Implementation note for F6/F7 test authors:** the library's own documents annotate Guid properties
`[BsonGuidRepresentation(GuidRepresentation.Standard)]` (`IncomingMessage.cs:26`, `NodeDocument.cs:9`,
`LockDocument.cs:9`, `AgentAssignmentDocument.cs:9`). Integration tests running against a real host
need no annotation (proven by the existing Guid compliance suite); a *unit*-level test that
serializes a Guid-keyed POCO with no `MongoClient` in the process will hit the `[5]` error, add the
attribute there, or key that particular unit test off a `string`.

---

## 3. LD1, resolved: Option B′, ensure-or-fail with minimal mutation

**Chosen: Option B (ensure-or-fail class map), refined.** The refinement is one added rule that
Option B as written in the plan did not have, and it materially reduces blast radius:

> **Never register a class map for a type whose id member the driver's own conventions already
> resolve correctly.**

The plan's snippet built a map, called `AutoMap()`, conditionally called `MapIdMember`, and then
registered it **unconditionally**. For every `Id`-keyed type, i.e. every consumer that works
today, that would replace an implicit auto-map with an explicit registration. Per `[14]`, this
would take away something apps have today: the ability to register their own class map for that type
after Wolverine's host build. The refinement makes the helper a true no-op there: registry
untouched, `[8]`'s document shape preserved, app rights preserved. Mutation happens **only** for
types that are otherwise **broken**.

### 3.1 The algorithm (amended)

Given `documentType` and Wolverine's resolved `idMember` (`w` below), and writing `d` for what the
driver resolves via the §3.1a walk:

1. **Memo hit** → return. (Per-process, per-type; alignment is idempotent by construction.)
2. **A class map is already registered for `documentType`** → do **not** mutate it. Read it and
   compare: id member name equal to `w.Name` → done; otherwise **throw** the §3.3 conflict error.
   Covers the app-registered case, the `[BsonId]` case, and the F1 §2.3 case where something already
   `LookupClassMap`'d (and thus froze) the type.
3. **Compute `d`**: the §3.1a hierarchy walk. Registry untouched (§2.8 R1-B1, R2-D2, GT-F5).
4. **`d != null && d.MemberName == w.Name`** → **return without registering anything.** Registry
   untouched. This is the no-op path for every `Id`-named member *whether declared on the type or
   inherited*, and for every `[BsonId]`-annotated member at any level (§2.8 R1 A1–A5, GT-F3).
5. **`w.DeclaringType != documentType`** → **throw §3.4.** The driver rejects `MapIdMember` for a
   member whose `DeclaringType` is not the class map's own type (§2.8 R1-C1, GT-F4), so this
   mechanism cannot align it. Checked **before** step 6 because when both conditions hold, the
   inherited-Wolverine-member diagnosis is the actionable one.
6. **`d != null && d.MemberInfo.DeclaringType != documentType`** → **throw §3.5.** A different,
   base-declared member already occupies `_id`; mapping ours would register successfully and then
   throw `BsonSerializationException` on the type's **first write** (§2.8 R2-E1). Note the contrast
   with `d` declared on `documentType` itself, which is *fine*: `MapIdMember` re-points within one map
   and the previous id member demotes to an ordinary field (§2 `[7]`, the both-members entity shape).
7. **Otherwise** (`w` is self-declared, and nothing base-declared holds `_id`) → build a *probe*
   (`new BsonClassMap(documentType)` + `AutoMap()`), `probe.MapIdMember(w)`, `RegisterClassMap(probe)`.
   The probe is brand-new, unregistered and unfrozen, so this can never hit `[12]`'s frozen-mutation
   error.
8. **`RegisterClassMap` threw `ArgumentException`** (lost a race with application code registering the
   same type between steps 2 and 7, see §4.3) → fall back to step 2's compare-or-throw against the
   winner's map. The winner decides; we only assert agreement.
9. Record in the memo. **Failures are not memoized**: a conflicting configuration throws on every
   call rather than throwing once and silently passing afterwards.

The five reachable outcomes, and the §2.8 evidence for each:

| `w` (Wolverine) | `d` (driver) | Outcome | Evidence |
|---|---|---|---|
| any | same member | no-op, registry untouched | R1 A1–A5, GT-F3, R2-D2 |
| self-declared | `null` | register `documentType` map + `MapIdMember(w)` | F6's 4 green convention tiers |
| self-declared | different, on `documentType` | register + `MapIdMember(w)` re-points | §2 `[7]` |
| **inherited** | anything different | **throw §3.4** | R1-C1, GT-F4 |
| self-declared | **different, base-declared** | **throw §3.5** | R2-E1, R1-A8 |

### 3.1a The predicate: `driverIdMember(Type)` (D6)

The original design read the id member off a single unfrozen `AutoMap()`'d probe of `documentType`.
That reports `null` for **every** inherited id member (§2.8 R1 A2/A3/A5/A8, GT-F3), it maps only
members *declared* on that class, so the no-op path became unreachable for the shape every
compliance saga has. The replacement asks the same question one level at a time:

> Walk from `documentType` up the base chain (exclusive of `object`), most-derived-first. At each
> level, read the id member from the **already-registered** class map if there is one, otherwise from
> a **throwaway `AutoMap()`'d probe** of that level. The first level that resolves an id member wins;
> `null` if none does.

This mirrors what the driver itself does when it freezes a map (a frozen map inherits its base map's
id member when it has none of its own), and it is **verified exact** against the authoritative
`LookupClassMap` answer on all 8 R1 shapes and all four real `*BasicWorkflow` types, while
registering nothing (R1-B1, R2-D2, GT-F5).

Two properties worth stating because they are easy to get wrong:

- **Continue past a level whose map resolves no id member.** A registered base map with no id member
  does not stop the search; the driver keeps inheriting upward, so the walk must too.
- **`LookupClassMap` is used only for levels that are *already* registered**, never for
  `documentType` (step 2 has already returned in that case) and never to create a map. Freezing an
  already-registered base map is the benign case §11 records: its content is unchanged, and the
  driver freezes it at first use anyway.

The asymmetry that makes this predicate safe: predicting "agrees" wrongly means **skipping** alignment
and silently corrupting; predicting "disagrees" wrongly means registering a map that names the same
member the driver would have, same `_id`, no data difference. The walk should therefore only ever
claim agreement when it can point at the member.

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

        // What WILL the driver serialize as _id? Not "what does an unfrozen AutoMap of this one class
        // say" — that reports null for every inherited id member, which is the shape of every saga
        // whose identity member lives on a base class.
        var driverIdMap = driverIdMember(documentType);
        if (driverIdMap?.MemberName == idMember.Name)
        {
            // The driver already agrees. Do NOT register: leave the registry untouched so this type's
            // documents stay byte-identical and the application keeps its own right to register a
            // class map for it later.
            return;
        }

        // The driver refuses to map a member it does not own, and we can only register a map for
        // documentType itself, so an inherited identity member is beyond this mechanism's reach.
        if (idMember.DeclaringType != documentType)
        {
            throw inheritedIdentityMember(documentType, idMember);
        }

        // A different, BASE-declared member already claims _id. Mapping ours would register fine and
        // then throw BsonSerializationException on this type's first write, so refuse now. (A
        // different member declared on documentType itself is fine: MapIdMember re-points within the
        // one map and the previous id member demotes to an ordinary field.)
        if (driverIdMap != null && driverIdMap.MemberInfo.DeclaringType != documentType)
        {
            throw conflictingInheritedId(documentType, idMember, driverIdMap);
        }

        // The probe is brand new and unfrozen, so this can never hit the driver's
        // "class map has been frozen and no further changes are allowed" error.
        var probe = new BsonClassMap(documentType);
        probe.AutoMap();
        probe.MapIdMember(idMember);
        try
        {
            BsonClassMap.RegisterClassMap(probe);
        }
        catch (ArgumentException)
        {
            // Lost a race with application code that registered a map for this type between the
            // IsClassMapRegistered check above and this call. The winner's map decides; we only
            // assert agreement.
            assertRegisteredMapAgrees(documentType, idMember);
        }
    }

    /// <summary>
    /// What the MongoDB driver will actually serialize as <c>_id</c> for <paramref name="documentType"/>,
    /// or <c>null</c> if nothing will. Walks the base chain most-derived-first because
    /// <c>AutoMap()</c> maps only the members a class itself declares, while the driver's frozen map
    /// inherits its base map's id member — so a single unfrozen probe of the document type reports
    /// <c>null</c> for every inherited id member. Registers nothing: already-registered levels are
    /// read as they are, unregistered levels are auto-mapped on a throwaway map. Never called for
    /// <paramref name="documentType"/> when a map is already registered for it — <c>align</c> has
    /// returned by then.
    /// </summary>
    private static BsonMemberMap? driverIdMember(Type documentType)
    {
        for (var type = documentType; type != null && type != typeof(object); type = type.BaseType)
        {
            if (BsonClassMap.IsClassMapRegistered(type))
            {
                // A registered map with no id member does not end the search: the driver keeps
                // inheriting upward, so we do too.
                var registered = BsonClassMap.LookupClassMap(type);
                if (registered.IdMemberMap != null) return registered.IdMemberMap;
                continue;
            }

            var probe = new BsonClassMap(type);
            probe.AutoMap();
            if (probe.IdMemberMap != null) return probe.IdMemberMap;
        }

        return null;
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

**Binding details.** Signatures, exception types, and the `align` decision order are contractual,
including that step 5's inherited-member check precedes step 6's conflict check. The four message
texts are contractual (§3.3, §3.4, §3.5, §5) because tests assert on their prefixes. `_aligned` may be
any lock-free-read memo; `bool` values are unused, presence is the signal. `driverIdMember` returns
the driver's `BsonMemberMap` rather than a `MemberInfo` because step 6 needs both its `MemberName` and
its `MemberInfo.DeclaringType`.

### 3.3 The conflict error

Type: **`InvalidOperationException`** (a misconfiguration discovered while wiring persistence, not a
bad argument to a method). Text as in §3.2. Properties it must keep:

- Names the type via `FullNameInCode()`.
- Names **both** members (what the driver has and what Wolverine resolved) so the reader knows
  which end to move.
- `"(no id member)"` when the registered map has none (an app that registered a map with only
  `MapMember` calls).
- Gives the two concrete remedies (`[BsonId]`, or a class map mapping the resolved member).

It deliberately never surfaces `[12]`'s "has been frozen and no further changes are allowed", which
says nothing about identity. The design's invariant, **only ever `MapIdMember` on a brand-new,
unregistered map**, is what guarantees that.

### 3.4 New: the inherited-identity-member error (D7a)

Fires when Wolverine's resolved identity member is declared on a base class **and** the driver does
not already map it (step 5). The driver makes this unfixable by this mechanism: `MapIdMember` rejects
a member whose `DeclaringType` is not the map's own class (§2.8 R1-C1), and a class map can only be
registered for one type.

Type: **`InvalidOperationException`**.

```csharp
private static InvalidOperationException inheritedIdentityMember(Type documentType, MemberInfo idMember)
    => new(
        $"Wolverine resolved '{idMember.Name}' as the identity member for " +
        $"{documentType.FullNameInCode()}, but that member is declared on the base type " +
        $"{idMember.DeclaringType!.FullNameInCode()}, and the MongoDB driver only lets a class map " +
        $"declare an id member of its own. Either put [BsonId] on " +
        $"{idMember.DeclaringType.FullNameInCode()}.{idMember.Name}, or register a class map for " +
        $"{idMember.DeclaringType.FullNameInCode()} that maps '{idMember.Name}' as the id member " +
        $"(BsonClassMap.RegisterClassMap<{idMember.DeclaringType.Name}>(cm => {{ cm.AutoMap(); " +
        $"cm.MapIdMember(x => x.{idMember.Name}); }})). Either way every saga or entity inheriting " +
        "that member is fixed at once.");
```

Both remedies are verified, not guessed: `[BsonId]` on a base-declared member is resolved by the
driver (§2.8 R1-A5) and an app-registered class map on the declaring type works for non-generic and
closed-generic bases alike (R1-C2, R1-C3). Either one puts the type back on §3.1's no-op path, since
the walk reads registered base maps and honours `[BsonId]` at any level. A third-party base the app
cannot annotate is covered by the second remedy.

**Not reachable for the `Id` convention**, which is the only one the compliance suites use: an
inherited member *named* `Id` is resolved by the driver too, so step 4 returns first. This error is
specific to a non-`Id` Wolverine convention (`[SagaIdentity]`, `{TypeName}Id`, `{Name-minus-Saga}Id`,
`SagaId`) on a base class, e.g. a shared `abstract class TenantSagaBase : Saga { public Guid SagaId }`.

### 3.5 New: the conflicting-inherited-`_id` error (D7b)

Fires when Wolverine's member *is* self-declared but a **different**, base-declared member already
occupies `_id` (step 6), e.g. a saga base class declaring `Id` while the saga itself declares
`{TypeName}Id`, which Wolverine's tier 2 prefers over tier 5 (§2.8 R1-A8).

Type: **`InvalidOperationException`**.

```csharp
private static InvalidOperationException conflictingInheritedId(
    Type documentType, MemberInfo idMember, BsonMemberMap driverIdMap)
    => new(
        $"Wolverine resolved '{idMember.Name}' as the identity member for " +
        $"{documentType.FullNameInCode()}, but the MongoDB driver already maps " +
        $"'{driverIdMap.MemberName}' — inherited from " +
        $"{driverIdMap.MemberInfo.DeclaringType!.FullNameInCode()} — as the document _id, and a " +
        "document cannot have two. Because the conflicting member belongs to a base type, this " +
        $"cannot be resolved by mapping '{idMember.Name}': the driver would accept the mapping and " +
        "then fail on the first write. Either stop the inherited member from being an id member " +
        $"([BsonIgnore] or [BsonElement(\"...\")] on " +
        $"{driverIdMap.MemberInfo.DeclaringType.FullNameInCode()}.{driverIdMap.MemberName}), or " +
        $"rename '{idMember.Name}' so Wolverine resolves the inherited member instead.");
```

The message deliberately does **not** suggest `[BsonId]` on `{idMember.Name}`: §2.8 R2-E2 shows that
produces the identical `BsonSerializationException`. `[BsonIgnore]` and `[BsonElement("…")]` on the
inherited member both work (R2-E3, R2-E4), and renaming puts the type on the no-op path.

Without this guard the failure is far worse than an exception: alignment reports success at codegen
and the type throws `BsonSerializationException: … cannot use element name '_id' because it is
already being used by …` on its **first saga insert**, at runtime, inside the outbox transaction
(R2-E1). Converting a first-write runtime failure into a host-build failure is the whole point.

---

## 4. Invocation points

### 4.1 Codegen time is necessary but **not sufficient**: `TypeLoadMode.Static`

The plan assumed frame constructors are enough. They are not, and F6/F7 must not ship on that
assumption:

- Frames are constructed in `HandlerChain.AssembleTypes` (`external/wolverine/src/Wolverine/Runtime/Handlers/HandlerChain.cs:224-271`,
  which calls `DetermineFrames` at `:246`).
- The pre-generated path is `ICodeFile.AttachTypesSynchronously` (`:309-325`): it resolves the
  generated handler type out of `assembly.ExportedTypes`, `QuickBuild`s it, and returns. It **never
  calls `AssembleTypes`**, so no `IPersistenceFrameProvider` factory runs and no frame constructor
  runs.
- `TypeLoadMode.Static` apps ship without Roslyn at all: "*`TypeLoadMode.Static` apps that
  pre-generate all of their code need nothing and ship without Roslyn*"
  (`external/wolverine/docs/guide/codegen.md:121`).

Alignment achieved only in a frame constructor therefore happens in the *`codegen write` build
process* and is absent from the deployed process. A non-`Id`-keyed saga would work in `Dynamic` mode
and silently corrupt in `Static`/AOT mode, a mode-dependent variant of the exact bug under repair.

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
| `UpdateSagaFrame` ctor | `SagaFrames.cs:221` | `EnsureIdMember(sagaType, member)`, the member it already resolves (§5) |
| `DeleteSagaFrame` ctor | `SagaFrames.cs:268` | `EnsureIdMember(saga.VariableType, member)` |
| `LoadEntityFrame` ctor | `EntityFrames.cs:154` | `EnsureIdMember(entityType)` |
| `MongoUpsertEntityFrame` ctor | `EntityFrames.cs:207` | `EnsureIdMember(entity.VariableType)` |
| `MongoDeleteEntityByVariableFrame` ctor | `EntityFrames.cs:249` | `EnsureIdMember(entity.VariableType)` |
| `DetermineStorageActionFrame` | `MongoDbPersistenceFrameProvider.cs:147-156` | `EnsureIdMember(entityType)`, **no custom frame exists on this path** (it builds a bare `MethodCall`), so the call goes in the provider method itself |

Saga frames pass the `MemberInfo` overload because they already need the member for codegen
(`UpdateSagaFrame` emits `{saga}.{_idMember}`); entity frames use the `Type` overload since they
don't otherwise need it.

**Runtime**: make it structural rather than a list of easily-forgotten one-liners. Introduce one
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
to either class inherits the guarantee. `ApplyStorageActionAsync` needs no call of its own, it
delegates to `UpsertAsync`/`DeleteAsync`.

**Cost:** one `ConcurrentDictionary.ContainsKey` per operation, against a network round-trip.
Negligible, and it buys mode-independence.

`MongoEntityOperations.IdOf<T>` (`EntityFrames.cs:133-136`) stays exactly as it is. Once the class
map is aligned, `LookupClassMap(typeof(T)).IdMemberMap` **is** the Wolverine-resolved member, the
read/write disagreement (F1 §3.2) closes without touching `IdOf`. Its `InvalidOperationException` for
"no mapped `_id` member" becomes unreachable in practice, and stays as a backstop.

> **Amendment, the entity half is unchanged (decision 3).** `IdOf`'s `LookupClassMap` resolves
> **inherited** id members correctly (§2.8 R1 A2/A3/A5, `authoritative` column), which is precisely
> why an inherited-`Id` entity needs no alignment: the driver already writes that member as `_id`
> (step 4 no-op) and the `[Entity]` load filters `_id` with the same member's value, so read and write
> agree with the registry untouched. The two new throws close the two shapes where they would *not*
> agree: an inherited non-`Id` member (§3.4) and a base-declared member squatting on `_id` (§3.5),
> both of which are broken today, silently. §4.2's entity rows and §4.4's ordering argument stand as
> written; F7's only delta is the two extra test rows (§9 rows 22–24). No change to `EntityFrames.cs`
> beyond what the original design already specified, and no change to LD4 (§6).

### 4.3 Thread safety, resolving F1's open flag

F1 correction 2 flagged that the plan's `ConcurrentDictionary<Type,bool>.GetOrAdd(type, factory)`
shape leans on a guarantee `ConcurrentDictionary` does **not** give: the value factory may run more
than once concurrently for the same key (only one result is stored). Two concurrent factory runs
would both see `IsClassMapRegistered == false` and both call `RegisterClassMap`, one wins, the
other throws `[10]`'s bare `ArgumentException`.

**Resolution: don't use `GetOrAdd`.** §3.2 uses lock-free memo read → `lock (_gate)` → double-check →
align → memo write. Properties:

1. **Our own callers can never double-register.** All alignment work for all types is serialized by
   one process-wide gate. Codegen constructs frames on one thread anyway; the runtime path takes the
   lock only on a memo miss (once per type per process).
2. **No contention on the hot path.** The pre-lock `ContainsKey` is lock-free; after the first
   alignment of a type, no operation ever takes the lock.
3. **A race with *application* code is still possible**, an app thread calling `RegisterClassMap<T>`
   or triggering `LookupClassMap(T)` concurrently with our first alignment. Our lock cannot cover
   the driver's registry. Step 8 handles it: catch `ArgumentException`, re-read the winner's map,
   assert agreement or throw §3.3's error. Outcome is identical to the app having won by a wider
   margin.
4. **The driver's own locking** (`BsonSerializer.ConfigLock`, F1 §2.1/§2.2) makes each individual
   `IsClassMapRegistered`/`RegisterClassMap`/`LookupClassMap` call atomic; we rely on that and add
   only the compound-operation serialization it doesn't provide.
5. **Coarse gate over per-type locks** deliberately: alignment is once-per-type-per-process
   configuration work, and one gate is far easier to reason about than a lock-striping scheme.

### 4.4 Ordering, resolving F1's second open flag

F1 §2.3 established that `LookupClassMap` **auto-maps and freezes** any unregistered type, and that
`EnsureIdMember` therefore cannot repair a type that something else already froze wrong. `[11]`/`[14]`
confirm. The design's answer:

- **Best effort at ordering:** codegen-time calls run during host build, before any handler executes,
  so for the normal case we are first.
- **When we are not first, fail loudly, never silently:** §3.1 step 2 compares and throws §3.3's
  actionable error. There is deliberately **no** attempt to unregister, replace, or thaw a map, the
  driver forbids all three (`[12]`, and F1 §2.2's "class maps can NOT be replaced" source comment).
- One in-library ordering hazard is worth naming for F6/F7: `IdOf<T>` calls `LookupClassMap`, so
  *if* an entity write for a type could ever precede that type's alignment, the auto-map would freeze
  first. §4.2's `EntityCollection<T>` accessor removes the hazard structurally, every write path
  obtains its collection (and therefore aligns) before it can reach `IdOf`. F7 must keep that
  ordering when it edits `UpsertAsync`/`DeleteAsync`.

**Amendment, the predicate does not weaken this.** `driverIdMember` (§3.1a) never calls
`LookupClassMap` for `documentType` (step 2 has already returned when a map exists for it) and never
creates a map for any level, so **it cannot itself freeze the document type wrong**: the register
branch stays reachable after the predicate runs. It does call `LookupClassMap` on *already-registered*
base levels, which freezes those, the benign case (their content is unchanged, and the driver freezes
them at first use anyway; §11 records `GetRegisteredClassMaps` as the considered non-freezing
alternative). This is a strictly better ordering position than the original predicate, which read a
map of `documentType` it then discarded.

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

**Exception type: `ArgumentException`, message text unchanged**:
`$"Unable to determine the identity member for {documentType.FullNameInCode()}"`. `ArgumentException`
is kept (over the semantically tidier `InvalidOperationException`) for parity with Wolverine's own
`LightweightSagaPersistenceFrameProvider.cs:80-83`, which this provider mirrors (F1 §1.4), and
because it is the type already thrown on `main`, no consumer-visible type change.

**One accepted cosmetic delta:** the `paramName` moves from `nameof(sagaType)` to
`nameof(documentType)`, so `ArgumentException.Message`'s auto-appended tail reads `(Parameter
'documentType')` instead of `(Parameter 'sagaType')`. Acceptable, it is a diagnostic string, no test
asserts it today. **F6/F7 tests must assert on the message *prefix* or use `Contains`, never on
`ParamName`.**

---

## 6. LD4, resolved: throw for `Delete<TSaga>` / `IStorageAction<TSaga>`

**Chosen: throw at codegen.** Reasons, in order of weight:

1. **Routing would corrupt OCC.** The saga write frames stamp and version-guard `Saga.Version`
   (`CLAUDE.md:161`). A `Delete<TSaga>`/`IStorageAction<TSaga>` arriving from a plain handler has no
   `oldVersion` to guard on and no `SagaChain` around it, so routing to the saga frames means
   unguarded writes into saga collections, trading a visible bug for an invisible one.
2. **No sibling provider supports it** (F1 §1.5).
3. **`Saga` lifecycle is `SagaChain`'s.** Completion is `MarkCompleted()`; the storage-action surface
   is for plain documents.
4. **Codegen time is the right time**: host build, before a single message flows.

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
| `DetermineDeleteFrame(Variable sagaId, Variable saga, …)` (`:132-133`) | The two-variable overload **is** the saga path, `SagaChain` calls it for completion. Guarding it would break every saga. |
| `DetermineInsertFrame` / `DetermineUpdateFrame` / `DetermineStoreFrame` (`:108-131`) | Already branch on `CanBeCastTo<Saga>()` and route sagas to the correct version-guarded frames, correct as written. |
| `DetermineLoadFrame` (`:95-98`) | Already branches; loads carry no write semantics. |
| `CanPersist` (`:70-80`) | Stays unconditional `true`. `[Entity]` parameter loads select the provider through it (`CLAUDE.md`, T1.1); narrowing it would break entity loads. The saga/entity distinction stays in the frame factories. |

---

## 7. Reconciliation with the "no process-global serializer mutation" stance

Two existing `CLAUDE.md` bullets become inaccurate once F6/F7 land, and one new bullet is needed.
**Exact text** (F6 lands the first two edits plus the saga half of the new bullet; F7 extends the new
bullet with its entity/LD4 half, or F6 may land the whole bullet and F7 verify it, at F6's
discretion):

**(a) Replace `CLAUDE.md:156`:**

> - **DateTimeOffset stored as UTC BSON Date:** every `DateTimeOffset`/`DateTimeOffset?` property on document types is annotated with `[BsonRepresentation(BsonType.DateTime)]`. No process-global serializer is registered, the library does not mutate the host app's BSON registry.

with:

> - **DateTimeOffset stored as UTC BSON Date:** every `DateTimeOffset`/`DateTimeOffset?` property on document types is annotated with `[BsonRepresentation(BsonType.DateTime)]`. **No serializer, convention, or convention pack is ever registered process-globally**: the library does not change how the host app serializes any type. The one registry interaction it does make is narrow and additive: a per-type `BsonClassMap` id-member alignment for saga/entity types Wolverine persists whose identity member the driver would not otherwise map to `_id` (see "Identity-member alignment" below).

**(b) Replace `CLAUDE.md:159`:**

> - **Direct document storage:** the saga POCO is stored as a MongoDB document. The identity member maps to `_id` via the driver's default Id-member convention. No envelope wrapper.

with:

> - **Direct document storage:** the saga POCO is stored as a MongoDB document, no envelope wrapper. Wolverine's resolved identity member is what maps to `_id`, via the driver's own convention when the member is named `Id`/`id`/`_id` or carries `[BsonId]`, and via the library's per-type class-map alignment otherwise (see "Identity-member alignment").

**(c) Add this bullet** (in the Key Design Decisions list, adjacent to the two above):

> - **Identity-member alignment (`MongoIdentityMapping`, F6/F7):** Wolverine resolves a document's identity member by *its* convention (`SagaChain.DetermineSagaIdMember`: `[SagaIdentity]` → `{TypeName}Id` → `{Name-minus-Saga}Id` → `SagaId` → `Id`); the MongoDB driver resolves `_id` by *its own* (`NamedIdMemberConvention`: only `Id`/`id`/`_id`, plus `[BsonId]`, inherited members included). Before 1.0.1 nothing reconciled the two, so a saga or entity keyed on e.g. `ShipmentId` was written with a **server-generated `ObjectId` `_id`** and could never be loaded back, silent data corruption. `MongoIdentityMapping.EnsureIdMember` bridges them. It first asks what the driver *will* resolve, by walking the type's base chain most-derived-first and reading each level's registered class map, or auto-mapping a throwaway one (**not** a single unfrozen probe of the document type, `AutoMap` maps only a class's own declared members, so that reports nothing for an identity member declared on a base class, which is the shape of every upstream compliance saga). If the driver already resolves the same member, the helper **does nothing at all and leaves the BSON registry untouched**: that covers every `Id`-keyed type whether the member is declared on the type or inherited (i.e. every consumer that worked before 1.0.1, byte-identical) and every `[BsonId]`-annotated type. Only when the driver disagrees does it register one additive per-type `BsonClassMap` (`AutoMap()` + `MapIdMember(resolvedMember)`); the driver's `Freeze()` then normalizes that member's element name to `_id`, so the frames' `Eq("_id", …)` filters and the written documents agree. Three misconfigurations throw a precise `InvalidOperationException` instead of corrupting or deferring: a class map already registered for the type naming a different id member (the app owns its own maps, we only assert agreement); an identity member declared on a **base** type, which the driver refuses to map from the subclass's map (remedy: `[BsonId]` on it, or register a class map for the declaring type, either fixes every subclass at once); and a **different**, base-declared member already occupying `_id`, which would otherwise register cleanly and then fail on the type's first write. Called at codegen time from every saga/entity frame constructor **and** at runtime from the `MongoSagaOperations`/`MongoEntityOperations` collection accessors, the runtime leg is required because `TypeLoadMode.Static` never constructs frames (`HandlerChain.cs:309-325` attaches pre-generated types without calling `AssembleTypes`). This is **not** the process-global serializer/convention mutation the library forswears: no serializer, no convention, no convention pack, and no behavior change for any type Wolverine does not persist.

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
| Identity member named `Id`/`id`/`_id` **declared on the type itself** (upstream `Todo`; the demo's `OrderNote`, `Order`, `OrderFulfillmentSaga`) | Driver maps `Id` → `_id`; works | Helper is a **no-op, registry untouched** (§3.1 step 4) | **Byte-identical.** `[8]` shows the unchanged document. |
| Identity member named `Id` **inherited from a base type**: *every saga in every compliance suite*: `String`/`Guid`/`Int`/`LongBasicWorkflow` inherit `Id` from `BasicWorkflow<TStart,TCompleteThree,TId>` | Driver maps the inherited `Id` → `_id`; works | Helper is a **no-op, registry untouched**: the amended predicate resolves the inherited member (§2.8 GT-F3), where the original read `null` and mutated | **Byte-identical, proven by hex diff** against a structurally identical twin the predicate never touched (§2.8 R2-D2), with the registry unchanged at every level before and after (R2-D2, GT-F5). **This is the row that actually broke**: 40 facts, and it is now the row with the strongest evidence. |
| Identity member carries `[BsonId]` (any name, **any level**: declared on the type or inherited) | Driver maps it → `_id`; works | Helper is a **no-op** (F1 §2.6; `[BsonId]` on a base member is resolved by the walk, §2.8 R1-A5) | **Byte-identical.** |
| Non-`Id` member, no `Id` member at all (e.g. `ShipmentSaga.ShipmentId`) | **Never worked.** No `_id` written; server assigns an `ObjectId` (`[9]`); every load returns null and every insert creates a new unfindable document | `_id` = the identity value (`[4]`) | **No migration owed**: no functioning data can exist. Any documents on disk are unreachable orphans that were never readable by the library or the app. Dropping the collection is optional cleanup, not a migration step. |
| **Both** `{TypeName}Id` **and** `Id` (the review's poisoned entity shape) | Writes keyed `Id` → `_id`; reads filtered on `{TypeName}Id`'s value. Worked **only** while the app kept the two members equal | `_id` = `{TypeName}Id`; `Id` demotes to an ordinary field named `Id` (`[7]`) | **Values equal (the accidentally-working case): existing documents stay loadable**: `_id` holds the same value before and after; re-writes converge to the new shape, which additionally carries an `Id` field. **Values unequal: was already broken**, same as the row above. |
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
| 5 | Saga, plain `Id`, regression | 5 | Guid/string/int/long | F6 | **existing** `{string,guid,int,long}_saga_storage_compliance.cs`, must stay green **unchanged** |
| 6 | Helper: driver already agrees (`Id`-named) → **registry untouched** (`IsClassMapRegistered` still `false` after the call) | — | — | F6 | `identity_mapping_helper.cs` (new, unit) |
| 7 | Helper: `[BsonId]` on a non-`Id`-named member → no-op, no registration | — | — | F6 | ″ |
| 8 | Helper: conflicting **pre-registered/frozen** map → `InvalidOperationException`, message contains both member names (§3.3) | — | — | F6 | ″ |
| 9 | Helper: unresolvable identity member (POCO with no id-shaped member) → `ArgumentException`, message prefix per §5 | — | — | F6 | ″ |
| 10 | Helper: N threads call `EnsureIdMember` concurrently for one fresh type → exactly one registration, **no** `ArgumentException` escapes (§4.3) | — | — | F6 | ″ |
| 11 | Helper: idempotent, repeated calls after alignment are no-ops and never throw | — | — | F6 | ″ |
| 12 | Entity with **only** `{TypeName}Id`: `[Entity]` load + `Insert<T>` + `Delete<T>` round-trip; direct read proves `_id` is the identity value | 2 | `Guid` | F7 | `entity_identity_conventions.cs` |
| 13 | Entity with **both** `{TypeName}Id` and `Id` (the poisoned shape): read and write agree on `{TypeName}Id`; direct read proves `_id` == `{TypeName}Id` and a separate `Id` field exists (§2.3, `[7]`) | 2 | `Guid` + `string Id` | F7 | ″ |
| 14 | Entity, plain `Id`, regression | 5 | `string` | F7 | **existing** `storage_action_compliance.cs` (upstream `Todo`), must stay green **unchanged** |
| 15 | LD4: plain handler returning `Delete<TSaga>` → codegen `InvalidOperationException` with §6.1's `Delete<…>` prefix | — | — | F7 | ″ |
| 16 | LD4: plain handler returning `IStorageAction<TSaga>` (e.g. `Storage.Delete(saga)`) → codegen `InvalidOperationException` with §6.1's `IStorageAction<…>` prefix | — | — | F7 | ″ |
| 17 | Runtime-leg / Static-mode net: invoke a `MongoSagaOperations`/`MongoEntityOperations` entry point for a non-`Id`-keyed type **whose frames were never constructed**; alignment still applies and the round-trip succeeds (§4.1) | 2 or 3 | `Guid` | F6 (saga) / F7 (entity) | respective conventions file |

#### Rows added by the amendment (18–24)

| # | Shape / scenario | Assertion | Task | File |
|---|---|---|---|---|
| **18** | **`Id` inherited from a closed generic base, the regression** | The four existing `{string,guid,int,long}_saga_storage_compliance.cs` suites (upstream `*BasicWorkflow`, `Id` on `BasicWorkflow<TStart,TCompleteThree,TId>`) stay green **unchanged**, both TFMs. **Zero edited facts**: the bar F6 must restore. This is the oracle; no new integration test is needed for it. | F6 | **existing** compliance suites |
| **19** | Helper: inherited **`Id`** → no-op **and registry untouched at every level** | On a dedicated POCO hierarchy `Derived : Base<string>` where `Base<TId>` declares `Id`: call `EnsureIdMember`, then assert `IsClassMapRegistered` is `false` for the derived type, the closed generic base, **and** the root base. Directly encodes §2.8 R2-D2/GT-F5 as a permanent guard, this is the fact whose absence let `75b5b9d` regress. | F6 | `identity_mapping_helper.cs` |
| **20** | Helper: `[BsonId]` on a **base-declared** member → no-op, no registration | Proves the walk honours `[BsonId]` at any level (§2.8 R1-A5), i.e. the §3.4 remedy actually works. | F6 | ″ |
| **21** | Helper: identity member **inherited and non-`Id`** → `InvalidOperationException` per §3.4 | Dedicated hierarchy, e.g. `abstract class …Base : Saga { public Guid SagaId }` + a concrete subclass. Assert on the message **prefix** and that it names both the member and its declaring type. Registry must be left untouched (nothing registered for either type). | F6 | ″ |
| **22** | Helper: base declares `Id`, type declares `{TypeName}Id` → `InvalidOperationException` per §3.5 | The §2.8 R1-A8/R2-E1 shape. Assert the message prefix, and that it does **not** recommend `[BsonId]` (R2-E2 proves that remedy fails). Registry untouched. | F6 | ″ |
| **23** | Saga, identity member inherited and non-`Id`, through **real codegen** | A saga on the row-21 shape wired into a host → host build (codegen) fails with §3.4's `InvalidOperationException`, not at first message. Proves the frame-constructor call site surfaces it at build time. | F6 | `saga_identity_conventions.cs` |
| **24** | Entity analogues of rows 19/21/22 | (a) entity with an inherited `Id` round-trips through `[Entity]` load + `Insert<T>` + `Delete<T>` with the registry untouched, proving `IdOf`'s `LookupClassMap` and the load filter agree on the inherited member (§4.2 amendment note); (b) entity on the row-21 shape → §3.4 throw at codegen; (c) entity on the row-22 shape → §3.5 throw at codegen. | F7 | `entity_identity_conventions.cs` |

**Not required:** a sibling-subclass row. That row would only be needed if D7 had chosen the
declaring-type mapping option (§11), which propagates a base's id mapping to every sibling subclass
(§2.8 R1-C2). Since both inherited cases throw instead, no sibling type is ever mutated and there is
nothing to assert.

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
   `Id`, on a type whose Wolverine-resolved member is `FooId`), and that type is then permanently
   poisoned for the process, which is fine because no other row uses it.
5. **Row 10** should assert both "no exception" and "exactly one id-member mapping", e.g. N threads
   through a `Barrier`, then a single `LookupClassMap` assertion.
6. **Guid representation:** see §2.7, integration rows need no annotation; a unit row serializing a
   Guid-keyed POCO without a `MongoClient` in the process needs
   `[BsonGuidRepresentation(GuidRepresentation.Standard)]` or should use a `string` id.
7. **Direct-read assertions must resolve collection names through `MongoConstants`**
   (`SagaCollectionName`/`EntityCollectionName`), never string literals, the existing house rule
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
compliance style, a POCO per convention, lifecycle-driven, no Mongo-specific assertions in the
*arrange* half, so they can be lifted upstream later. Still **defer** the contribution; F6's PR adds
the `FOLLOWUPS.md` note.

---

## 10. Deltas from the plan (what F6/F7 should do differently)

Explicit, so implementing sessions don't treat these as scope creep or as mistakes:

| # | Plan said | This design says | Why |
|---|---|---|---|
| 1 | `EnsureIdMember` registers the class map unconditionally once built | **Don't register when the driver's own conventions already agree** (§3.1 step 4) | Keeps every working consumer's registry untouched and preserves their right to register their own map later (`[14]`). Zero-risk for the `Id`-keyed majority. |
| 2 | `ConcurrentDictionary.GetOrAdd(type, factory)` | Lock-free memo read → `lock` → double-check → align (§4.3) | Resolves F1 correction 2: `GetOrAdd`'s factory may run twice, and `RegisterClassMap` is not idempotent (`[10]`). |
| 3 | Invocation at "every frame constructor, codegen time" | Frame constructors **plus** runtime collection accessors (§4.1, §4.2) | `TypeLoadMode.Static` never constructs frames (`HandlerChain.cs:309-325`), so codegen-only alignment is silently absent in pre-generated/AOT deployments. **This is the most important delta.** |
| 4 | F6 snippet threw `InvalidOperationException` for an unresolvable identity member | **`ArgumentException`**, message identical to today's `DetermineSagaIdType` (§5) | The plan's own decision text asks for "the same message as `DetermineSagaIdType`"; matching the type too keeps parity with `LightweightSagaPersistenceFrameProvider` and changes nothing consumer-visible. |
| 5 | F6 scope: `MongoIdentityMapping` + 4 saga frame ctors | Also: `MongoSagaOperations`'s 4 inline `GetCollection` calls → one `SagaCollection<TSaga>` accessor; `DetermineSagaIdType` delegates to `ResolveIdMember` | Follows from deltas 3 and 4. Same files, no new files. |
| 6 | F7 scope: 3 entity frame ctors + 2 provider guards | Also: `MongoEntityOperations`'s 4 inline `GetCollection` calls → one `EntityCollection<T>` accessor; `EnsureIdMember` in `DetermineStorageActionFrame` itself (no frame exists on that path) | Same reasons; `MethodCall`-based path has no constructor to hook. |
| 7 | Test matrix: 5 shapes | 17 rows incl. all four non-`Id` precedence tiers, helper unit rows, and the Static-mode row (§9) | Tiers 2 and 4 were unlisted but are legal conventions; the helper's negative and concurrency paths need direct coverage since integration tests can't reach them. |

Everything else in the plan's F6/F7 sections stands: red-first ordering, the compliance suites as the
regression oracle, both TFMs, one branch/PR per task, CHANGELOG `### Fixed` entries.

### 10.1 The F6-facing diff, what changes relative to commit `75b5b9d`

F6 implemented the original design correctly; the design was wrong about one predicate. **Everything
in `75b5b9d` stays except the items below.** Nothing here touches `EnsureIdMember`, `ResolveIdMember`,
the memo/`lock` structure, `assertRegisteredMapAgrees`, the four saga frame constructors,
`sagaCollection<TSaga>()`, `DetermineSagaIdType`'s delegation, or the `UpdateSagaFrame` fallback
removal, all of those are confirmed correct by F6's own 11 green facts.

| # | File · member | Change | Reference |
|---|---|---|---|
| 1 | `MongoIdentityMapping.align` | **Replace** the `var probe = new BsonClassMap(documentType); probe.AutoMap(); if (probe.IdMemberMap?.MemberName == idMember.Name) return;` block with `var driverIdMap = driverIdMember(documentType); if (driverIdMap?.MemberName == idMember.Name) return;`. The probe is then built **after** the two new guards, immediately before `MapIdMember`. | §3.1 steps 3–7, §3.2 |
| 2 | `MongoIdentityMapping.driverIdMember` | **New** private static method, the base-chain walk, verbatim from §3.2 (including the XML doc, which records *why* a single unfrozen probe is wrong). | §3.1a, §3.2 |
| 3 | `MongoIdentityMapping.inheritedIdentityMember` | **New** private static factory returning the §3.4 `InvalidOperationException`; thrown from `align` when `idMember.DeclaringType != documentType`. | §3.4 |
| 4 | `MongoIdentityMapping.conflictingInheritedId` | **New** private static factory returning the §3.5 `InvalidOperationException`; thrown from `align` when `driverIdMap != null && driverIdMap.MemberInfo.DeclaringType != documentType`. Guard order: after #3. | §3.5 |
| 5 | `MongoIdentityMapping` XML doc (`<summary>`, "Minimal mutation" para) | Amend to state that agreement is decided by the base-chain walk and that inherited `Id` members take the no-op path; keep the "Element naming" paragraph as committed. | §7(c) |
| 6 | `identity_mapping_helper.cs` | **Add** rows 19–22 (inherited-`Id` no-op + registry-untouched-at-every-level; `[BsonId]`-on-base no-op; §3.4 throw; §3.5 throw). Existing rows 6–11 unchanged. | §9 rows 19–22 |
| 7 | `saga_identity_conventions.cs` | **Add** row 23 (inherited non-`Id` saga → codegen throw). Existing 5 facts unchanged. | §9 row 23 |
| 8 | `CLAUDE.md` | Land §7(a)/(b)/(d) as already specified, and §7(c) in its **amended** wording. | §7 |
| 9 | *(nothing)* | `EntityFrames.cs`, `MongoDbPersistenceFrameProvider.cs`, and LD4 are unaffected; F7's only delta is §9 row 24. | §4.2 amendment note |

**Verification bar, unchanged:** 40 regressions → **0**, with **zero edited compliance facts**, on
net9.0 **and** net10.0. Row 18 is the oracle. Expect the four `*BasicWorkflow` suites to pass without
any test-side change, if any compliance file needs editing, stop and report: that means the predicate
is still moving working consumers' data.

---

## 11. Rejected alternatives

| Option | Verdict |
|---|---|
| **LD1-A, fail fast only** (throw unless the driver already maps the resolved member; require `[BsonId]`) | Rejected. It converts silent corruption into a clear error (a real improvement) but leaves three of Wolverine's five identity conventions unusable without a Mongo-specific annotation on the app's own POCOs, which no sibling provider demands. LD1-B′ subsumes it: every case where A would throw, B′ either fixes silently (unregistered type) or throws the *same* clear error (already-registered conflict). Kept documented as the downscope if the registry interaction is ever judged unacceptable (plan R2). |
| **LD1-C, key frames off the mapped element name instead of `_id`** | Rejected up front by the plan, confirmed here. Documents would carry a server `ObjectId` `_id` **plus** a secondary identity field needing its own unique index, diverging from every sibling provider and breaking 1.0.0's on-disk shape for `Id`-keyed documents, i.e. it would break the consumers who currently work, to help those who don't. |
| **`BsonClassMap.TryRegisterClassMap`** instead of `RegisterClassMap` + catch | Rejected. In 3.9.0 it exists **only** in generic form (`TryRegisterClassMap<TClass>(BsonClassMap<TClass>/Action<>/Func<>)`, verified against `MongoDB.Bson.xml`), so a `Type`-keyed helper would need `MakeGenericMethod` plus a reflectively-constructed `BsonClassMap<T>`. And it buys nothing: a `false` return still requires `LookupClassMap` + compare to know *why*, which is exactly what the catch branch does. More reflection, same logic. |
| **`GetRegisteredClassMaps()`** instead of `LookupClassMap` in the already-registered branch | Rejected on balance. It would avoid freezing the app's map during our check, but it's an O(n) scan of a global list and a less obvious API, while the freeze is benign: the map's *content* is unchanged, and the driver freezes it on first use anyway (`[11]`). Recorded here so a future reader knows it was considered. |
| **Registering a custom `IConvention`/convention pack** (e.g. a Wolverine-aware `IdMemberConvention`) | Rejected. Convention packs apply process-globally to types matching a filter, precisely the "mutate the host app's BSON registry" behavior `CLAUDE.md:156` forswears, and it would affect types Wolverine never persists. The per-type class map is the narrowest instrument that does the job. |
| **Emitting the alignment call into the generated code** (instead of the runtime accessors) | Rejected. It would put a per-invocation call in every generated handler body, couple the emitted source to an internal helper, and still need the codegen-time call for the early failure. The collection accessors achieve the same coverage inside the library, invisibly to generated code. |
| **LD4, route `Delete<TSaga>`/`IStorageAction<TSaga>` to the saga frames** | Rejected, §6 reasons 1–4: no `SagaChain`, therefore no captured `oldVersion`, therefore unguarded writes into saga collections, silent OCC corruption in place of a visible bug. |
| **Mutating/unregistering a conflicting frozen class map** | Impossible, not merely rejected: the driver forbids replacement ("class maps can NOT be replaced", F1 §2.2) and mutation after freeze (`[12]`). Hence "assert agreement or throw" is the only available contract. |

### 11.1 Rejected at the amendment (2026-07-26)

| Option | Verdict |
|---|---|
| **Freeze the throwaway probe to read the driver's answer** (`probe.AutoMap(); probe.Freeze(); probe.IdMemberMap`) | Rejected. It *is* authoritative (§2.8 R1-B3 shows a frozen probe resolves the inherited `Id` correctly) but `Freeze()` **registers the base type's class map globally** (R1-B3, fact C). That would silently register maps for `Saga`, `BasicWorkflow<…>`, and every app saga base as a side effect of the predicate, weakening the one guarantee that justifies Option B′ ("registry untouched when the driver already agrees") for *every* hierarchy rather than just the broken ones. The walk gets the same answer for zero side effects (R1: 8/8 exact; GT-F5: registry unchanged). |
| **Traverse `probe.BaseClassMap`** | Rejected, it does not work. On an unfrozen `AutoMap()`'d probe the property is simply `null` (§2.8 R1-B2); it is only populated *during* `Freeze()`, i.e. after the side effect above has already happened. |
| **`LookupClassMap(idMember.DeclaringType)`** to read the base's answer | Rejected. Registers **and freezes** the declaring type's map, fact C's side effect, deliberately, on a type Wolverine may not persist. Also answers a narrower question than the walk (one level, not the chain). |
| **Decide purely from `MemberInfo` reflection**: "is the member named `Id`/`id`/`_id`, or does it carry `[BsonId]`?" | Rejected, though tempting: it is the simplest side-effect-free predicate and would have fixed the 40 regressions. It hard-codes the *default* `NamedIdMemberConvention` names and `[BsonId]`, so it silently mispredicts for an app that registers a custom convention pack or an app-registered class map on a **base** type, and in the "predicts agreement wrongly" direction, which is the fatal one (skip alignment → silent corruption). The walk runs the driver's *actual* registered conventions at each level (`AutoMap` applies them), so it stays correct under app customization. It is also what makes §3.4's second remedy work: an app that registers a class map on the declaring type is recognized as agreement. |
| **Map the id member on the declaring type's class map** (the automatic fix for an inherited non-`Id` identity member, instead of §3.4's throw) | Rejected. It works: §2.8 R1-C2 maps `SagaId` on a non-generic base and both sibling subclasses then serialize with the right `_id`; R1-C3 shows it works for closed generics too, per closed generic. But the mapping applies to **every subclass of that base**, including types Wolverine never persists: a sibling the app stores in its own collection would silently move from a server-assigned `ObjectId` `_id` to that member's value. That directly contradicts the promise in §7 ("no behavior change for any type Wolverine does not persist"), the same blast-radius reasoning that produced Option B′'s minimal-mutation rule in the first place. §3.4's throw names this exact fix as a remedy the **app** can apply deliberately for its own hierarchy, which keeps the choice where the ownership is. |
| **Skip alignment when the identity member is inherited** (smallest blast radius) | Rejected outright: it leaves precisely the silent corruption F6 exists to close, and does so *only* for a subset of shapes, which is worse than either fixing or refusing, the behavior would depend on where a member happens to be declared, with no diagnostic. |
| **Let the §3.5 conflict surface naturally** (register the mapping and let the driver throw) | Rejected. The driver accepts `MapIdMember` and `RegisterClassMap` without complaint and throws `BsonSerializationException` on the type's **first write** (§2.8 R2-E1), at runtime, inside the outbox transaction, on a message that will then retry and fail identically. Pre-detecting at codegen converts that into a host-build failure with an actionable message. |
| **Recommend `[BsonId]` as the §3.5 remedy** | Rejected on evidence: §2.8 R2-E2 shows `[BsonId]` on the derived member produces the **identical** `BsonSerializationException`, because the inherited member still claims `_id`. The message names `[BsonIgnore]`/`[BsonElement("…")]` on the inherited member instead (R2-E3, R2-E4, both verified working). This is the clearest case in the amendment for testing a remedy before writing it into an error string. |

---

## 12. Handed to F6/F7 (implementation-time confirmations, not open questions)

Nothing here changes a decision; each is a one-line check that the design's premises still hold in
the implementing session:

> **Amendment note.** Items 1–3 below were written for a from-scratch F6. F6 has already done them
> (`75b5b9d`): the fallback was replaced, the frames wired, and the generated source dumped and
> compared (identical session-bound operations in identical order; the only delta is the update
> argument, `shipmentSaga.ShipmentId` vs `dumpIdKeyedSaga.Id`). Treat them as done and start from
> **§10.1**. Items 4–5 still apply, plus:
>
> 6. **Re-run the full suite on both TFMs before anything else**: row 18 (the four compliance suites)
>    is the acceptance oracle, and the amendment's whole purpose is returning it to green with zero
>    edited facts.
> 7. **Do not add a `Freeze()` anywhere in `MongoIdentityMapping`**, including as a "make sure it's
>    resolved" convenience: §11.1 records why (it registers base maps globally). The driver freezes
>    every map at first use on its own.

1. **Confirm** `SagaFrames.cs:227-229`'s fallback is still present and that the four frame ctors are
   at the §4.2 line numbers (drift-check only).
2. **Confirm** row 5 / row 14 (the existing compliance suites) pass **unchanged**: no edits to
   compliance files. If any needs an edit, stop and report: that means the design moved a working
   consumer's data, which §8 says it must not.
3. **Verify by dumped generated source** (§9.1 rule 10) that the only difference for an `Id`-keyed
   saga is *nothing at all*, and for a convention saga is the emitted member name.
4. If a Guid-keyed convention row hits `GuidSerializer … Unspecified`, apply §2.7's note, do **not**
   register a global Guid serializer.
5. F7 must keep the `EntityCollection<T>`-before-`IdOf<T>` ordering (§4.4) when it edits
   `UpsertAsync`/`DeleteAsync`.
