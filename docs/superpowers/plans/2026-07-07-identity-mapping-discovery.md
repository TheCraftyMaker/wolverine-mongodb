# Identity-Mapping Discovery (Task F1)

> Read-only research for the 2026-07-07 review-findings remediation plan. Confirms or corrects
> the plan's "Verified API Facts" section for identity resolution, pins the MongoDB.Driver 3.9.0
> `BsonClassMap` semantics F3's design (LD1) depends on, and enumerates every local code site that
> must change. No library code was touched.

**Wolverine submodule:** `external/wolverine` at `feba5cd21` (tag `6.16.0`) — matches the plan's
pinned V6.16.0. **MongoDB.Driver:** `3.9.0` (`Directory.Packages.props:8`; confirmed installed at
`~/.nuget/packages/mongodb.bson/3.9.0`). Driver source pulled from
`github.com/mongodb/mongo-csharp-driver` at tag `v3.9.0` for the parts not visible from the
compiled package (class-map internals, conventions).

---

## 1. Wolverine identity resolution (core)

### 1.1 `SagaChain.DetermineSagaIdMember` — full precedence, confirmed exact

`external/wolverine/src/Wolverine/Persistence/Sagas/SagaChain.cs:208-225`:

```csharp
public static MemberInfo? DetermineSagaIdMember(Type messageType, Type sagaType, MethodInfo[]? sagaHandlerMethods)
{
    var expectedSagaIdName = $"{sagaType.Name}Id";                                   // :210
    var specifiedSagaIdMemberName = sagaHandlerMethods?...                            // :214-217 [SagaIdentityFrom]
    var members = messageType.GetFields().OfType<MemberInfo>().Concat(messageType.GetProperties()).ToArray();
    return members.FirstOrDefault(x => x.HasAttribute<SagaIdentityAttribute>())                          // 1. [SagaIdentity]
           ?? members.FirstOrDefault(x => x.Name == (specifiedSagaIdMemberName ?? expectedSagaIdName))   // 2. {SagaTypeName}Id
           ?? members.FirstOrDefault(x => x.Name == expectedSagaIdName.Replace("Saga", "", ...))         // 3. {Name-minus-"Saga"}Id
           ?? members.FirstOrDefault(x => x.Name == SagaIdMemberName) ??                                 // 4. "SagaId"
           members.FirstOrDefault(x => x.Name.EqualsIgnoreCase("Id"));                                   // 5. "Id" (case-insensitive)
}
```

**Confirmed exactly as the plan states** — precedence order, all five tiers, line numbers
(`:210` expectedSagaIdName, `:220-224` the fallback chain). No drift.

`ValidSagaIdTypes` — `SagaChain.cs:29`: `[typeof(Guid), typeof(int), typeof(long), typeof(string)]`.
Used by `PullSagaIdFromMessageFrame.cs:28,31` to decide between a "known" id type and a
strong-typed identifier wrapper. **Confirmed**, not previously cited by line in the plan.

### 1.2 Core call sites: `DetermineSagaIdType` vs `PullSagaIdFromMessageFrame`

`SagaChain.cs:288-293` (`generateCodeForMaybeExisting`):

```csharp
var findSagaId = SagaIdMember == null
    ? (Frame)new PullSagaIdFromEnvelopeFrame(frameProvider.DetermineSagaIdType(SagaType, container))
    : new PullSagaIdFromMessageFrame(MessageType, SagaIdMember);
```

**Confirmed exactly as the plan states**: `DetermineSagaIdType` is consulted only on the
envelope-header-only path (no id-bearing member on the incoming message — `SagaIdMember == null`).
When the message *does* carry an identity member, `PullSagaIdFromMessageFrame` reads that
member's type directly and `DetermineSagaIdType` is never called for that dispatch. Nothing
upstream validates the saga type's own identity member before `DetermineUpdateFrame`/`DetermineInsertFrame`
run elsewhere in `SagaChain` — confirmed no guard exists between resolution and frame construction.

### 1.3 `EntityAttribute`'s id resolution path

`external/wolverine/src/Wolverine/Persistence/EntityAttribute.cs:137-158` (`Modify`):

```csharp
var idType = provider.DetermineSagaIdType(parameter.ParameterType, container);   // :153
if (!tryFindIdentityVariable(chain, parameter, idType, out var identity))
    throw new InvalidEntityLoadUsageException(this, parameter);
```

**Confirmed exactly**: line 153, and the comment at `:152` ("I know it's goofy that this refers
to the saga, but it should work fine here too") is in the actual source — `EntityAttribute` reuses
the saga-named resolver for plain entities too. This is *why* the Mongo provider's
`DetermineSagaIdType` implementation is on the hot path for `[Entity]` loads, not just sagas.

### 1.4 How the *other* providers reconcile Wolverine identity with their storage

This is the one place the plan's "Verified API Facts" summarized rather than quoted, and it turns
out the two families diverge in mechanism — worth stating precisely for F3:

- **Marten** (`Wolverine.Marten/Persistence/Sagas/MartenPersistenceFrameProvider.cs:30-36`):
  ```csharp
  public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
  {
      var store = container.GetInstance<IDocumentStore>();
      var documentType = store.Options.FindOrResolveDocumentType(sagaType);
      return documentType.IdType;
  }
  ```
  Marten does **not** call `SagaChain.DetermineSagaIdMember` at all here — it asks its own
  document-store metadata (which Marten computed independently, via its own id-member
  conventions) what type it thinks the id is. Reconciliation between Wolverine's naming
  convention and Marten's is implicit: both walk similar conventions independently and happen to
  agree for the common cases; Marten's `IDocumentSession` storage then keys off whatever member
  Marten itself resolved, not off Wolverine's answer.
- **EF Core** (`Wolverine.EntityFrameworkCore/Codegen/EFCorePersistenceFrameProvider.cs:104-119`):
  same shape — asks the `DbContext`'s own `IEntityType.FindPrimaryKey()` for the configured EF
  Core primary key, independent of `SagaChain.DetermineSagaIdMember`.
- **Lightweight (in-memory/`ISagaStorage<,>`)** (`Wolverine/Persistence/Sagas/LightweightSagaPersistenceFrameProvider.cs:80-83`):
  ```csharp
  public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
      => SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType()
         ?? throw new ArgumentException(nameof(sagaType), $"Unable to determine the identity member for {sagaType.FullNameInCode()}");
  ```
  This is the **exact pattern the Mongo provider already copies** (`MongoDbPersistenceFrameProvider.cs:90-93`,
  confirmed identical modulo the exception type — Lightweight throws `ArgumentException`, Mongo
  throws `ArgumentException` too, matching). Both call `SagaChain.DetermineSagaIdMember` directly
  and trust its answer completely — there is no independent storage-side metadata to reconcile
  against, because in-memory/generic storage has no schema of its own.
- **Correction to record for F3:** the Mongo provider's situation is *not* like Marten/EF Core
  (which have their own independently-derived storage metadata to compare against and can ignore
  Wolverine's answer). It is exactly like Lightweight: `SagaChain.DetermineSagaIdMember`'s answer
  *is* the only source of truth for which member is the identity, and the defect is that nothing
  downstream tells the MongoDB **driver** (a third, independent id-resolution mechanism —
  `NamedIdMemberConvention`, see §2) to agree. Lightweight has no analogous problem because its
  storage is a plain `Dictionary<TId, TSaga>` keyed directly off the resolved member's value, with
  no serializer-level naming convention in between. This sharpens LD1: the fix is specifically
  bridging *two* independently-arrived-at answers (Wolverine's `DetermineSagaIdMember`, the
  driver's `NamedIdMemberConvention`/`[BsonId]`), not replacing either.

### 1.5 No upstream `Saga` guard on storage-action paths — confirmed, with exact line ranges

- `Delete<T>.BuildFrame` — `Wolverine/Persistence/Delete.cs:19-31` (full file is 34 lines). Gated
  only by `rules.TryFindPersistenceFrameProvider(container, typeof(T), out var provider)`
  (`:22`) → `provider.DetermineDeleteFrame(value, container)` (`:26`). No `CanBeCastTo<Saga>()`
  check anywhere in the file.
- `IStorageAction<T>.BuildFrame` — `Wolverine/Persistence/IStorageAction.cs:21-31`. Same shape:
  `TryFindPersistenceFrameProvider` → `DetermineStorageActionFrame`. No saga check.
- `Storage.TryApply` — `Wolverine/Persistence/Storage.cs:61-78` (plan cited `:63-74`; the full
  method body confirmed at `:61-78`, the `IStorageAction<>`-closure check is `:63-64`, the throw
  is `:74`). Same gating — `effect.VariableType.Closes(typeof(IStorageAction<>))` then
  `TryFindPersistenceFrameProvider`, no saga check.
- The gate all three funnel through — `TryFindPersistenceFrameProvider`
  (`Wolverine/Persistence/Sagas/GenerationRulesExtensions.cs:61-78`) — filters candidates purely
  by `provider.CanPersist(entityType, container, out _)` (`:68`). **`CanPersist` is the only gate**,
  and the Mongo provider hardcodes it to unconditional `true`
  (`MongoDbPersistenceFrameProvider.cs:70-80`, comment at `:74-78` explains why — required for
  `[Entity]` loads). This is exactly the confirmed defect: a plain (non-saga) handler returning
  `Delete<TSaga>` or `IStorageAction<TSaga>` sails through every upstream gate uncontested and
  reaches the Mongo provider's **entity** frame factories.

**Conclusion: the plan's "no upstream Saga guard" claim is confirmed with no corrections.**

---

## 2. MongoDB.Driver 3.9.0 `BsonClassMap` semantics

Source: `mongo-csharp-driver` tag `v3.9.0`,
`src/MongoDB.Bson/Serialization/BsonClassMap.cs` (1775 lines) and
`.../Conventions/{NamedIdMemberConvention,DefaultConventionPack}.cs`.

### 2.1 `IsClassMapRegistered` (`BsonClassMap.cs:303-319`)

```csharp
public static bool IsClassMapRegistered(Type type)
{
    BsonSerializer.ConfigLock.EnterReadLock();
    try { return __classMaps.ContainsKey(type); }
    finally { BsonSerializer.ConfigLock.ExitReadLock(); }
}
```

Simple, thread-safe (`ReaderWriterLockSlim`-style `ConfigLock`) dictionary-containment check
against the process-global `__classMaps` (`:35`,
`private readonly static Dictionary<Type, BsonClassMap> __classMaps`).

### 2.2 `RegisterClassMap` idempotency/thread-safety — **NOT idempotent; this corrects an implicit assumption in the plan's pseudocode**

`BsonClassMap.cs:400-418`:

```csharp
public static void RegisterClassMap(BsonClassMap classMap)
{
    BsonSerializer.ConfigLock.EnterWriteLock();
    try
    {
        // note: class maps can NOT be replaced (because derived classes refer to existing instance)
        __classMaps.Add(classMap.ClassType, classMap);   // Dictionary<>.Add — THROWS on duplicate key
        BsonSerializer.RegisterDiscriminator(classMap.ClassType, classMap.Discriminator);
    }
    finally { BsonSerializer.ConfigLock.ExitWriteLock(); }
}
```

`RegisterClassMap` uses `Dictionary<Type, BsonClassMap>.Add`, which throws
`ArgumentException` ("An item with the same key has already been added") if a map for that type
is already registered — **it is not safe to call twice for the same type**, and there is no
try/check-and-add overload on the base `RegisterClassMap(BsonClassMap)` signature (the
`TryRegisterClassMap<TClass>` family, `:425-522`, exists specifically because of this and does its
own `ContainsKey` check under the write lock before adding). The write lock does make a single
`RegisterClassMap` call atomic against concurrent `RegisterClassMap`/`LookupClassMap` calls — the
*call* is thread-safe, but calling it twice for the same type is a programming error, not a race
that resolves gracefully.

**Implication for F3/F6 (`MongoIdentityMapping.EnsureIdMember`):** the plan's pseudocode already
guards this correctly by construction, but the *reason* it's correct is worth stating explicitly
in the design doc: (a) the outer `ConcurrentDictionary<Type,bool> _ensured .GetOrAdd(documentType, _ => {...})`
ensures the factory closure — which contains the `IsClassMapRegistered` check and the eventual
`RegisterClassMap` call — runs **at most once per type** even under concurrent first-use, because
`GetOrAdd`'s value factory is guaranteed to be invoked at most once per key for the winning racer
(other concurrent callers for the same key block on the result, they do not re-run the factory
redundantly in a way that would double-register — note this is true for the *effective* value
returned, though .NET's `ConcurrentDictionary.GetOrAdd` documentation only guarantees the factory
may run more than once with only one result being stored; **this must be re-verified in F3/F6 against
the actual `ConcurrentDictionary.GetOrAdd` overload semantics** — if the factory can execute
concurrently more than once, a second concurrent execution's `RegisterClassMap` call could still
race with the first and throw, since the type-not-yet-registered check happened before either
call took the class map lock). (b) Only when
`IsClassMapRegistered` is `false` does the helper construct a **new, unregistered** `BsonClassMap`
and call `MapIdMember` — mutating an object nobody else has a reference to yet — before calling
`RegisterClassMap` on it once. It never calls `MapIdMember` on an *existing* (possibly frozen) map, which is the
scenario that would hit §2.4's `ThrowFrozenException`.

**Correction/flag for F3:** verify (or make defensive) the `ConcurrentDictionary.GetOrAdd`
double-invocation edge case above — the safest fix is for `EnsureIdMember`'s factory to also
catch the specific `ArgumentException` from a losing-race `RegisterClassMap` call and treat it as
"someone else already registered it, go re-check via `IsClassMapRegistered`" rather than letting
it propagate as an opaque `ArgumentException`. This is a minor hardening note, not a blocker.

### 2.3 `LookupClassMap` auto-map + freeze behavior (`BsonClassMap.cs:326-371`)

```csharp
public static BsonClassMap LookupClassMap(Type classType)
{
    // fast path under read lock: if already registered AND frozen, return it
    ...
    if (__classMaps.TryGetValue(classType, out var classMap) && classMap.IsFrozen) return classMap;
    ...
    // slow path: speculatively build a new map OUTSIDE any lock, then...
    var newClassMap = (BsonClassMap)Activator.CreateInstance(classMapType);
    newClassMap.AutoMap();
    // ...under the WRITE lock, register it only if nobody else did first, then freeze whichever won
    EnterWriteLock();
    if (!__classMaps.TryGetValue(classType, out var classMap)) { RegisterClassMap(newClassMap); classMap = newClassMap; }
    return classMap.Freeze();
}
```

**Confirmed as the plan implies**: `LookupClassMap` will auto-map (bare `AutoMap()`, no custom
initializer) and **freeze** a class map for any type that doesn't have one registered yet, and the
returned map is always frozen (freezing is idempotent — `Freeze()` on an already-frozen map is a
no-op, confirmed by the unconditional `return classMap.Freeze()` on both the fast and slow path).
**Consequence for F3:** once *anything* calls `LookupClassMap(T)` for a type with no explicit
`[BsonId]`/manual registration, that type's class map is permanently frozen with whatever
`NamedIdMemberConvention` picked (or no id member at all, if none of `Id`/`id`/`_id` exist).
`MongoIdentityMapping.EnsureIdMember` **must run before any `LookupClassMap` call for that type**
— if the entity/saga type has already been auto-mapped-and-frozen via `LookupClassMap` (e.g. by
`MongoEntityOperations.IdOf`/`EntityFrames.cs:134` running for an *earlier* frame in the same
codegen pass, or by application code), `EnsureIdMember`'s "no registered map → build+MapIdMember"
branch is unreachable (the map already exists) and its "registered map → compare id member" branch
correctly falls into the throwing path if the auto-picked member disagrees with Wolverine's
resolved member — which is actually the *desired* outcome (a clear error) rather than a silent
`ThrowFrozenException` about frozen state. **No corrective action needed, but this ordering
subtlety belongs in the F3 design doc's invocation-site section**: `EnsureIdMember` must be called
from every frame constructor (as planned) and there is no safe way to "fix" a class map after
`LookupClassMap` has already frozen it wrong — the only recourse at that point is the clear
exception, never a silent re-map.

### 2.4 Freezing semantics / mutation after freeze

`BsonClassMap.cs:1473-1477` (`ThrowFrozenException`):

```csharp
private void ThrowFrozenException()
{
    throw new InvalidOperationException($"Class map for {_classType.FullName} has been frozen and no further changes are allowed.");
}
```

Every mutating method (`MapIdMember`, `SetIdMember`, `MapMember`, etc.) checks `IsFrozen` and
calls this before making any change. **Confirmed**: attempting `MapIdMember` on an already-frozen
map (registered by anyone, including `LookupClassMap`'s auto-map) throws this generic frozen-state
exception, not a helpful "id member mismatch" message. This is exactly why `EnsureIdMember`'s
design (only ever calling `MapIdMember` on a brand-new, not-yet-registered map — see §2.2) is the
only safe shape; it must never attempt to mutate a map obtained via `LookupClassMap` or already in
`__classMaps`.

### 2.5 Insert behavor for a type with NO mapped id member

`BsonClassMapSerializer<TClass>.GetDocumentId` (`BsonClassMapSerializer.cs:316-335`):

```csharp
public bool GetDocumentId(object document, out object id, out Type idNominalType, out IIdGenerator idGenerator)
{
    var idMemberMap = _classMap.IdMemberMap;
    if (idMemberMap != null) { id = ...; idNominalType = ...; idGenerator = ...; return true; }
    id = null; idNominalType = null; idGenerator = null;
    return false;
}
```

And `Serialize` (`:573-581`) only writes an `_id` element when `idMemberMap != null`. **Confirmed
the plan's claim**: for a type with no mapped id member, `GetDocumentId` returns `false` and the
serialized document carries **no `_id` field at all** — the driver never invents a client-side id.
On `InsertOneAsync`/`InsertManyAsync`, the MongoDB **server** then applies its own default
behavior for a missing `_id`: it generates and inserts an `ObjectId` for `_id` server-side. This
is a server-level document-storage default, not a driver behavior — the driver simply doesn't
attempt to populate one and passes the field-less document through.

### 2.6 `[BsonId]` interaction

`BsonIdAttribute.Apply` (`Attributes/BsonIdAttribute.cs`, full file quoted — `Apply` is the last
method):

```csharp
public void Apply(BsonMemberMap memberMap)
{
    memberMap.SetOrder(_order);
    if (_idGenerator != null) memberMap.SetIdGenerator(...);
    memberMap.ClassMap.SetIdMember(memberMap);
}
```

`[BsonId]` is an `IBsonMemberMapAttribute`, applied per-member during `AutoMap`'s attribute-convention
pass (the same pass that reads `[BsonElement]`, `[BsonIgnore]`, etc.) — it calls
`ClassMap.SetIdMember(memberMap)` directly and **unconditionally**, regardless of the member's
name. This runs independently of, and takes priority over, `NamedIdMemberConvention` (which only
looks for members literally named `Id`/`id`/`_id` — confirmed default names,
`DefaultConventionPack.cs:40`: `new NamedIdMemberConvention(new[] { "Id", "id", "_id" })`).
**Confirmed**: `[BsonId]` is the escape hatch an application can already use today, on its own
POCOs, to satisfy `EnsureIdMember`'s "already registered with the same id member" branch without
the helper ever needing to call `RegisterClassMap` itself — if an app pre-registers (or
auto-maps via an earlier incidental `LookupClassMap` call) a type with `[BsonId]` on the
Wolverine-resolved member, `EnsureIdMember` is a pure no-op fast path.

### 2.7 `NamedIdMemberConvention` — exact matching rule (relevant to non-`Id` entity/saga members)

`NamedIdMemberConvention.Apply` (`Conventions/NamedIdMemberConvention.cs:93-109`) walks the
configured names in order (`["Id", "id", "_id"]` by default) and maps the **first** member whose
name case-sensitively equals one of them and passes `IsValidIdMember` (roughly: a real
get-accessor, and no conflicting non-`_id` `[BsonElement]` name). **This means**: a saga/entity
whose only identity-shaped member is `{TypeName}Id` (e.g. `ShipmentId` on `ShipmentSaga`) will
**not** be auto-picked as `_id` by this convention at all — `AutoMap()` leaves `IdMemberMap` null
for such a type unless `[BsonId]` is present or a class map is registered with an explicit
`MapIdMember` call. This is the precise mechanism behind the review's "silent data corruption":
without intervention, `ShipmentId`-keyed sagas get a **server-generated `ObjectId` `_id`**
(§2.5) that has no relationship to the Wolverine-resolved `ShipmentId` value the frames filter on
— so a `Filter.Eq("_id", shipmentIdValue)` load never matches, and every insert creates a new,
unfindable document (the review's exact "load returns null / duplicate saga docs" failure mode).

---

## 3. Local defect sites — every raw `"_id"` filter and `IdMemberMap` site, exact line numbers

Confirmed by direct read of `src/Wolverine.MongoDB/Internals/{SagaFrames,EntityFrames,MongoDbSagaStoreDiagnostics}.cs`
and `MongoDbPersistenceFrameProvider.cs` on current `main` (via this worktree, unmodified). **Zero
corrections to the plan's line citations** — every one below matches exactly.

### 3.1 Saga frames (`SagaFrames.cs`)

| Site | Line | Detail |
|---|---|---|
| `MongoSagaOperations.LoadSagaAsync` | `:39` | `Builders<TSaga>.Filter.Eq("_id", sagaId)` |
| `MongoSagaOperations.InsertSagaAsync` | `:58` | bare `InsertOneAsync(session, saga, ...)` — relies entirely on the driver's class map for what gets written as `_id` |
| `MongoSagaOperations.UpdateSagaAsync` | `:84` | `Builders<TSaga>.Filter.Eq("_id", sagaId)` (anded with the version filter at `:85`) |
| `MongoSagaOperations.DeleteSagaAsync` | `:115` | `Builders<TSaga>.Filter.Eq("_id", sagaId)` |
| `UpdateSagaFrame` ctor silent fallback | `:227-229` | `var idMember = SagaChain.DetermineSagaIdMember(sagaType, sagaType); _idMember = idMember?.Name ?? "Id"; _idType = idMember?.GetRawMemberType() ?? typeof(string);` — silently substitutes `"Id"`/`string` instead of throwing, where `DetermineSagaIdType` (`MongoDbPersistenceFrameProvider.cs:90-93`) throws for the identical null case |

**No `BsonClassMap.RegisterClassMap`/`MapIdMember` call anywhere in this file** (or anywhere in
`src/Wolverine.MongoDB` — confirmed by repo-wide grep, §3.4).

### 3.2 Entity frames (`EntityFrames.cs`)

| Site | Line | Detail |
|---|---|---|
| `MongoEntityOperations.LoadAsync` (session overload) | `:41` | `Builders<T>.Filter.Eq("_id", id)` — filters on the **Wolverine-resolved** `id` variable (from `EntityAttribute`'s `provider.DetermineSagaIdType` path) |
| `MongoEntityOperations.LoadAsync` (session-less overload) | `:60` | same, session-less read path |
| `MongoEntityOperations.UpsertAsync` | `:79` | `Builders<T>.Filter.Eq("_id", IdOf(entity))` — filters on the **driver-resolved** id via `IdOf` |
| `MongoEntityOperations.DeleteAsync` | `:97` | same, `IdOf(entity)` |
| `MongoEntityOperations.IdOf<T>` | `:133-136` | `BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap?.Getter(entity) ?? throw ...` — the driver-resolved extraction that disagrees with the Wolverine-resolved load path above whenever the two conventions pick different members |
| `MongoConstants.EntityCollectionName` | `MongoConstants.cs:37-38` | `entityType.Name.ToLowerInvariant()`, un-prefixed (vs. saga's `wolverine_saga_` prefix, `MongoConstants.cs:25,27-28`) |

**This is the read/write identity disagreement** the plan describes: `LoadEntityFrame` (line 190,
calling `LoadAsync`) keys off whatever member `EntityAttribute`/`DetermineSagaIdType` resolved
(Wolverine's convention), while `MongoUpsertEntityFrame`/`MongoDeleteEntityByVariableFrame` key off
whatever the MongoDB driver's class map resolved (`IdOf`, driver's `NamedIdMemberConvention`) — for
an `{TypeName}Id`-only entity these are two different values pointing at two different documents.

### 3.3 Provider saga/entity branching (`MongoDbPersistenceFrameProvider.cs`)

| Method | Line | Branches on `CanBeCastTo<Saga>()`? |
|---|---|---|
| `DetermineInsertFrame` | `:108-111` | **Yes** |
| `DetermineUpdateFrame` | `:116-119` | **Yes** |
| `DetermineStoreFrame` | `:128-131` | **Yes** (delegates to `DetermineUpdateFrame` for sagas) |
| `DetermineDeleteFrame(Variable, ...)` (single-variable, `Delete<T>`) | `:136-137` | **No** — always `MongoDeleteEntityByVariableFrame` |
| `DetermineStorageActionFrame` (`IStorageAction<T>`) | `:147-156` | **No** — always builds a `MongoEntityOperations.ApplyStorageActionAsync<T>` call |

**Confirmed exactly** — this is the LD4 gap: a plain handler returning `Delete<TSaga>` or
`Storage.Delete(someSaga)`/`IStorageAction<TSaga>` reaches `MongoDeleteEntityByVariableFrame`/
`ApplyStorageActionAsync<TSaga>`, which target the **un-prefixed** entity collection name
(`MongoConstants.EntityCollectionName(typeof(TSaga))`, e.g. `orderfulfillmentsaga`) — a different
collection than the saga frames' `wolverine_saga_orderfulfillmentsaga` — and perform an
unguarded upsert/delete with no `Saga.Version` awareness.

### 3.4 Diagnostics (`MongoDbSagaStoreDiagnostics.cs`)

- `ReadSagaAsync` filter — `:101-104` (not `:101` alone as one plan line cited, but the same
  statement spans these four lines): `Builders<TSaga>.Filter.Eq("_id", identity)` with the boxed
  identity used as-is (no coercion), per the class doc at `:23` ("the identity is used as-is") and
  the inline comment at `:98-100`.
- `LookupClassMap` site — `:148`: `BsonClassMap.LookupClassMap(sagaType).IdMemberMap?.Getter(saga) ?? string.Empty`.

### 3.5 Confirmed: zero `RegisterClassMap` calls in the library

```
$ grep -rn "RegisterClassMap\|LookupClassMap\|IsClassMapRegistered" src/Wolverine.MongoDB
src/Wolverine.MongoDB/Internals/EntityFrames.cs:134:        => BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap?.Getter(entity)
src/Wolverine.MongoDB/Internals/MongoDbSagaStoreDiagnostics.cs:148:            var id = BsonClassMap.LookupClassMap(sagaType).IdMemberMap?.Getter(saga) ?? string.Empty;
```

Exactly two read-only `LookupClassMap` call sites, zero `RegisterClassMap`, zero
`IsClassMapRegistered`. **Confirmed, no corrections.**

---

## 4. Upstream compliance coverage — no non-`Id` identity member anywhere

- `StorageActionCompliance.cs:272-274`: `public class Todo { ... public string Id { get; set; } = null!; }` — the
  storage-action suite's only entity, `Id`-membered.
- `Sagas/TestMessages.cs:46-50`: `BasicWorkflow<TStart, TCompleteThree, TId> : Saga { public TId Id { get; set; } }`
  — every upstream saga compliance spec (`String/Guid/Int/Long IdentifiedSagaComplianceSpecs`) is
  built on this generic type, and its identity member is **always named `Id`** regardless of the
  closed `TId` type parameter.
- Local compliance subclasses (`string_saga_storage_compliance.cs`, `guid_saga_storage_compliance.cs`,
  and int/long siblings) all extend the upstream `*IdentifiedSagaComplianceSpecs<MongoDbSagaHost>`
  base classes — they inherit `BasicWorkflow`'s facts verbatim and add no saga type of their own.
- A repo-wide search for other saga/entity POCOs across `Wolverine.ComplianceTests` (`CoreTests`
  hits for `public string Id` — `using_storage_return_types_and_entity_attributes.cs:76`,
  `saga_id_member_determination.cs:79`, `async_method_name_saga.cs:319`,
  `auditing_determination.cs:130`, `Bug_2521_saga_identity_from_ordering.cs:85` — all `Id`-named)
  turned up no counter-example.

**Confirmed: no upstream saga or storage-action compliance fact exercises a non-`Id` identity
member.** This is exactly why the defect shipped in 1.0.0 undetected — the compliance suites this
provider runs against structurally cannot catch it; new custom tests (F6/F7's
`saga_identity_conventions.cs`/`entity_identity_conventions.cs`) are the only oracle.

---

## 5. Summary: every code site that must change (feeds F3/F6/F7 directly)

| # | File:Line | Change needed |
|---|---|---|
| 1 | `SagaFrames.cs:227-229` | `UpdateSagaFrame` ctor: replace `?? "Id"` / `?? typeof(string)` silent fallback with a throw matching `DetermineSagaIdType`'s message |
| 2 | `SagaFrames.cs` (all 4 frame ctors: `LoadSagaFrame`, `InsertSagaFrame`, `UpdateSagaFrame`, `DeleteSagaFrame`) | Call `MongoIdentityMapping.EnsureIdMember(sagaType, idMember)` at construction, once per type |
| 3 | `EntityFrames.cs` (`LoadEntityFrame`, `MongoUpsertEntityFrame`, `MongoDeleteEntityByVariableFrame` ctors) | Same `EnsureIdMember` call, with the Wolverine-resolved member (from the provider's `DetermineSagaIdType`) so the driver-resolved `IdOf` (line `:133-136`) agrees by construction |
| 4 | `MongoDbPersistenceFrameProvider.cs:136-137` (`DetermineDeleteFrame(Variable, ...)`) | Add `CanBeCastTo<Saga>()` guard → throw (LD4) |
| 5 | `MongoDbPersistenceFrameProvider.cs:147-156` (`DetermineStorageActionFrame`) | Same LD4 guard |
| 6 | **New** `Internals/MongoIdentityMapping.cs` | The ensure-or-fail helper itself (per LD1 Option B) |

No other files need to change for the identity-mapping fix; `MongoConstants.cs`,
`MongoDbSagaStoreDiagnostics.cs`, and the read-side `Eq("_id", ...)` filters throughout
`SagaFrames.cs`/`EntityFrames.cs` are all **correct as written** once the class map agrees — they
don't need to change, only the class-map alignment needs to be introduced upstream of them.

---

## Corrections to the plan's Verified API Facts

Two items, both refinements rather than reversals — nothing in the plan's Phase 0/1 assumptions is
wrong:

1. **§1.4 (new content):** the plan's Verified Facts section didn't characterize *how* Marten/EF
   Core reconcile identity (it only asserted the Mongo/Lightweight pattern for context). Marten and
   EF Core each ask their **own** independently-derived storage metadata for the id type — they do
   not call `SagaChain.DetermineSagaIdMember` for this purpose at all — whereas Mongo (like
   Lightweight) trusts `DetermineSagaIdMember`'s answer completely and has no independent metadata
   to fall back on. This sharpens why the fix is a *bridge* between two independent naming
   resolvers (Wolverine's convention, the driver's `NamedIdMemberConvention`), not a "look up what
   Marten does" port.
2. **§2.2:** `RegisterClassMap` is **not idempotent** (throws on a duplicate key) — the plan's F3/F6
   pseudocode already avoids calling it twice via the `ConcurrentDictionary.GetOrAdd` guard, but F3
   should explicitly verify (or defensively handle via catching `ArgumentException`) the edge case
   where `GetOrAdd`'s factory could theoretically execute concurrently more than once for the same
   key before .NET commits one winner, since two concurrent `RegisterClassMap` calls for the same
   type is exactly the scenario the driver method itself does not defend against.

Everything else — precedence order, line numbers for every defect site, the "no upstream Saga
guard" claim, the "no non-`Id` compliance coverage" claim, the zero-`RegisterClassMap` claim — is
confirmed exactly as written in the plan with no corrections.
