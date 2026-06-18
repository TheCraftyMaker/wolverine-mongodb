# Wolverine Saga API Discovery (Task S2)

> **Source:** Read-only study of `external/wolverine` submodule pinned to V6.2.2.
> All file paths are relative to the submodule root (`external/wolverine/`).
> Every signature is quoted verbatim from the source; line numbers are given for each.

---

## 1. `Saga.cs` — Base Class and Exceptions

**File:** `src/Wolverine/Saga.cs`

```csharp
// Line 7
public abstract class Saga
{
    private bool _isCompleted;

    // Line 16
    public bool IsCompleted() { return _isCompleted; }

    // Line 24
    protected void MarkCompleted() { _isCompleted = true; }

    // Line 39
    public int Version { get; set; }
}

// Line 47 — optimistic concurrency exception
public class SagaConcurrencyException : Exception
{
    // Line 49
    public SagaConcurrencyException(string message) : base(message) { }
}

// Line 60 — optional base for sequenced sagas (not in scope for basic saga support)
public abstract class ResequencerSaga<T> : Saga where T : SequencedMessage { ... }
```

**Key facts:**
- `Version` is `int`, aligning with `JasperFx.IRevisioned.Version`. The XML doc says "typed as int to align with IRevisioned … (JasperFx 2.0 rc split versioning into IRevisioned = int and ILongVersioned = long; sagas use the int revision)."
- `IsCompleted()` is a public method (not a property). Generated code calls it as `saga.IsCompleted()`.
- `MarkCompleted()` is `protected` — only callable from within a `Saga` subclass handler.
- `SagaConcurrencyException` is defined in the same file as `Saga`, namespace `Wolverine`.

---

## 2. `IPersistenceFrameProvider.cs` — Full Interface

**File:** `src/Wolverine/Persistence/IPersistenceFrameProvider.cs`

```csharp
// Line 10
public interface IPersistenceFrameProvider
{
    // Line 12 — called for every chain that matches CanApply
    void ApplyTransactionSupport(IChain chain, IServiceContainer container);

    // Line 13 — entity-type scoped variant (delegates to above in most providers)
    void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType);

    // Line 14 — gate: must return true for SagaChain or transaction won't be applied
    bool CanApply(IChain chain, IServiceContainer container);

    // Line 23 — "Use for Saga creation support as returned value"
    bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService);

    // Line 25 — governs the envelope-header-only identity path (see §4 below)
    Type DetermineSagaIdType(Type sagaType, IServiceContainer container);

    // Line 26 — emits the "load saga from storage" frame
    Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId);

    // Line 27 — emits the "insert new saga" frame (used in start path)
    Frame DetermineInsertFrame(Variable saga, IServiceContainer container);

    // Line 28 — emits commit/flush; position differs for saga vs non-saga chains
    Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container);

    // Line 29 — emits the "update existing saga" frame
    Frame DetermineUpdateFrame(Variable saga, IServiceContainer container);

    // Line 30 — emits the "delete completed saga" frame
    Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container);

    // Line 39 — upsert (not called for saga chains; providers may throw NotSupportedException)
    Frame DetermineStoreFrame(Variable saga, IServiceContainer container);

    // Line 48 — non-saga single-variable delete (not called for saga chains)
    Frame DetermineDeleteFrame(Variable variable, IServiceContainer container);

    // Line 50 — generic entity storage action frame (not called for saga chains)
    Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container);

    // Line 52 — soft-delete null-out frames (not called for saga chains)
    Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity);

    // Lines 73-82 — fetch-specification frame; has a default implementation returning false
    bool TryBuildFetchSpecificationFrame(
        Variable specVariable,
        IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame,
        [NotNullWhen(true)] out Variable? result)
    { frame = null; result = null; return false; }
}
```

**Saga-relevant members** (the ones a MongoDB provider must implement):

| Member | Called for saga chains? | Notes |
|--------|------------------------|-------|
| `CanApply` | Yes — gate | Must return `true` for `SagaChain` |
| `ApplyTransactionSupport` | Yes — setup | Called first in `DetermineFrames`; adds transaction middleware |
| `CanPersist` | Yes — saga creation | Scope to `entityType.CanBeCastTo<Saga>()` (see §6) |
| `DetermineSagaIdType` | Conditionally | Only when message has no saga-id member (envelope-only path) |
| `DetermineLoadFrame` | Yes | Load the saga by `sagaId` variable |
| `DetermineInsertFrame` | Yes | New saga: insert if not completed |
| `DetermineUpdateFrame` | Yes | Existing saga: update unless completed |
| `DetermineDeleteFrame(sagaId, saga, container)` | Yes | Existing saga: delete if completed |
| `CommitUnitOfWorkFrame` | Yes | Commit + flush; called in two positions (see §5) |
| `DetermineStoreFrame` | **No** | Not called by `SagaChain` — may throw `NotSupportedException` |

---

## 3. `SagaChain.cs` — Constants, Identity Resolution, Frame Ordering

**File:** `src/Wolverine/Persistence/Sagas/SagaChain.cs`

### 3.1 Constants (lines 19–29)

```csharp
public const string Orchestrate      = "Orchestrate";
public const string Orchestrates     = "Orchestrates";
public const string Start            = "Start";
public const string Starts           = "Starts";
public const string StartOrHandle    = "StartOrHandle";
public const string StartsOrHandles  = "StartsOrHandles";
public const string NotFound         = "NotFound";

public const string SagaIdMemberName   = "SagaId";    // fallback member name
public const string SagaIdVariableName = "sagaId";    // codegen variable name

// Line 29 — the four primitive id types
public static readonly Type[] ValidSagaIdTypes =
    [typeof(Guid), typeof(int), typeof(long), typeof(string)];
```

Strong-typed identifier structs (e.g. `OrderId` wrapping `Guid`) are also accepted by `IsValidSagaIdType` (lines 36–44): the check is `type is { IsPrimitive: false, IsEnum: false }`.

### 3.2 Identity Resolution — `DetermineSagaIdMember` (lines 208–225)

Wolverine scans the **message type** (not the saga type) for the saga identity member in this exact precedence:

```
1. [SagaIdentityAttribute]    member with this attribute on a field or property
2. [SagaIdentityFromAttribute] property name specified on any handler parameter
3. {SagaType.Name}Id          e.g. "OrderSagaId" for an OrderSaga, or "{SagaName}Id"
4. {SagaType.Name}Id minus "Saga" word  e.g. "OrderId" (line 222: Replace("Saga",""))
5. SagaId                     literal name "SagaId" (SagaIdMemberName constant)
6. Id                         case-insensitive match (last resort)
```

Both fields and properties are scanned (`GetFields().Concat(GetProperties())`).

The static method has three overloads:
```csharp
// Lines 192–196
public static MemberInfo? DetermineSagaIdMember(Type messageType, Type sagaType, MethodInfo? sagaHandlerMethod = null)

// Lines 208–225 — primary implementation
public static MemberInfo? DetermineSagaIdMember(Type messageType, Type sagaType, MethodInfo[]? sagaHandlerMethods)
```

### 3.3 When `DetermineSagaIdType` Is Called vs. `PullSagaIdFromMessageFrame`

This is the **critical distinction** for S7 (native id types):

```csharp
// SagaChain.cs lines 290–292 (generateCodeForMaybeExisting)
var findSagaId = SagaIdMember == null
    ? (Frame)new PullSagaIdFromEnvelopeFrame(
          frameProvider.DetermineSagaIdType(SagaType, container))  // ← only here
    : new PullSagaIdFromMessageFrame(MessageType, SagaIdMember);   // uses member's type directly
```

**Rule:** `DetermineSagaIdType` is **only called when the message has no saga-id member** (i.e. the saga ID comes purely from the envelope `SagaId` header). When the message has an id member, the member's own runtime type governs the `sagaId` variable — `DetermineSagaIdType` is not consulted.

In `LightweightSagaPersistenceFrameProvider` (line 80–82):
```csharp
public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    => SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType()
       ?? throw new ArgumentException(nameof(sagaType), $"Unable to determine the identity member for {sagaType.FullNameInCode()}");
```
For document-store providers (Cosmos, MongoDB), `DetermineSagaIdType → typeof(string)` is the baseline; S7 should use the lightweight pattern to return the native type.

---

## 4. Identity Attribute Types

**`SagaIdentityAttribute`** — `src/Wolverine/Persistence/Sagas/SagaIdentityAttribute.cs`
```csharp
/// Marks a public property on a message type as the saga state identity
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class SagaIdentityAttribute : Attribute;
```

**`SagaIdentityFromAttribute`** — `src/Wolverine/Persistence/Sagas/SagaIdentityFromAttribute.cs`
Used on a **handler method parameter** to specify which message property holds the saga id. Scanned across ALL handler methods (fixed in V6 to avoid declaration-order bugs).

---

## 5. Exception Types

All three saga exceptions are in `src/Wolverine/Persistence/Sagas/UnknownSagaException.cs` (except `SagaConcurrencyException` which is in `Saga.cs`):

### `IndeterminateSagaStateIdException` (line 6)
```csharp
public class IndeterminateSagaStateIdException : Exception
{
    public IndeterminateSagaStateIdException(Envelope envelope) : base(
        $"Could not determine a valid saga state id for Envelope {envelope}") { }
}
```
**When thrown:** During id extraction — `PullSagaIdFromMessageFrame` or `PullSagaIdFromEnvelopeFrame` — when the id is default/empty and cannot be parsed from the envelope header. Covers:
- String id is null/empty (both message and envelope header)
- Guid id is `Guid.Empty` (both message and envelope header)
- int/long id is `0` (both message and envelope header)
- Strong-typed id is `default(T)`

**Compliance facts that exercise this:** `update_with_no_saga_id_to_be_on_the_envelope` (sends `CompleteFour` with no envelope header), `update_with_no_saga_id_to_be_on_the_envelope_or_message` (sends `StringCompleteThree()` with default string).

### `UnknownSagaException` (line 14)
```csharp
public class UnknownSagaException : Exception
{
    public UnknownSagaException(Type sagaStateType, object stateId) : base(
        $"Could not find an expected saga document of type {sagaStateType.FullNameInCode()} for id '{stateId}'. Note: new Sagas will not be available in storage until the first message succeeds.") { }
}
```
**When thrown:** By `AssertSagaStateExistsFrame` (`src/Wolverine/Persistence/Sagas/AssertSagaStateExistsFrame.cs`) — emitted in the "saga does not exist" branch when there are no `Start`/`NotFound` handlers. The load frame returns `null`, the if-else guard hits the null branch, and `AssertSagaStateExistsFrame` throws.

**Compliance facts that exercise this:** `unknown_state` (sends `StringCompleteThree{SagaId = "unknown"}` — saga doesn't exist, no Start handler on that message).

### `SagaConcurrencyException` (Saga.cs, line 47)
```csharp
public class SagaConcurrencyException : Exception
{
    public SagaConcurrencyException(string message) : base(message) { }
}
```
**When thrown:** By the persistence provider's `DetermineUpdateFrame` implementation when a version-guarded update finds the stored document has been modified (i.e. `ModifiedCount == 0` after `ReplaceOneAsync` filtered on `(_id, Version)`). **Wolverine core never throws this** — it is the provider's responsibility. Neither Cosmos nor RavenDb throw it; the lightweight SQL provider does (see `DatabaseSagaSchema.cs:99-110`).

**No compliance fact tests this** — must be covered by custom test (S11).

---

## 6. `CanPersist` — Scope Constraint

The plan's `🔎 review` note on R9 is confirmed by reading `LightweightSagaPersistenceFrameProvider.CanPersist` (lines 61–78):

```csharp
public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
{
    if (entityType.CanBeCastTo<Saga>())
    {
        var idType = SagaChain.DetermineSagaIdMember(entityType, entityType)?.GetRawMemberType();
        if (idType == null) { persistenceService = default!; return false; }
        persistenceService = typeof(ISagaStorage<,>).MakeGenericType(idType, entityType);
        return true;
    }
    persistenceService = default!;
    return false;
}
```

For a document-store provider (MongoDB), `CanPersist` must be scoped to `entityType.CanBeCastTo<Saga>()`. Returning `true` unconditionally would advertise generic `[Entity]`/`Insert<T>`/`Update<T>`/`IStorageAction<T>` persistence which calls `DetermineStorageActionFrame` — and that method throws `NotSupportedException` in the current stub. The correct `persistenceService` for MongoDB is `typeof(IMongoDatabase)`.

---

## 7. Frame Invocation Order — Complete Trace

### 7.1 Call Site: `DetermineFrames` (SagaChain.cs, line 242)

```csharp
internal override List<Frame> DetermineFrames(...) {
    applyCustomizations(rules, container);           // 1. middleware customizations
    // Audit member middleware (if any)

    var frameProvider = rules.GetPersistenceProviders(this, container);

    frameProvider.ApplyTransactionSupport(this, container);  // 2. ← FIRST provider call
                                                              //    adds TransactionalFrame to Middleware
                                                              //    and (for non-saga) CommitMongoTransactionFrame to Postprocessors

    NotFoundCalls  = findByNames(NotFound);
    StartingCalls  = findByNames(Start, Starts, StartOrHandle, StartsOrHandles);
    ExistingCalls  = findByNames(Orchestrate, Orchestrates, StartOrHandle, StartsOrHandles,
                                 Handle, Handles, Consume, Consumes);

    // generates frames into 'list'
    generateCodeForMaybeExisting(...)  OR  generateForOnlyStartingSaga(...)

    return Middleware.Concat(constructorFrames).Concat(list).Concat(Postprocessors).ToList();
    //     ^ TransactionalFrame is here             ^ generated saga frames       ^ CommitMongoTransactionFrame (non-saga only)
}
```

**Key:** `ApplyTransactionSupport` is called **before** any saga frame generation. For `SagaChain`, the MongoDB provider must add `TransactionalFrame` to `Middleware` but **must NOT** add `CommitMongoTransactionFrame` to `Postprocessors` (that would double-commit — once via the postprocessor and once via `CommitUnitOfWorkFrame` which is emitted inline).

### 7.2 Path A: Start-only (no `ExistingCalls`)

Called via `generateForOnlyStartingSaga` (lines 313–344):

```
[Middleware: TransactionalFrame (opens session, starts transaction)]
[Constructor frames]
1. CreateNewSagaFrame           — new TSaga() (if handler doesn't return saga)
2. StartingCall(s)              — saga.Start(message)
3. SetSagaIdFromSagaFrame       — sets envelope SagaId from saga.Id (if SagaIdMember != null)
4. cascading message frames     — from handler return values
5. ConditionalSagaInsertFrame:
     if (!saga.IsCompleted()) {
         DetermineInsertFrame   — insert saga doc (sets Version = 1 in S8)
     }
     CommitUnitOfWorkFrame      — ← provider's commit+flush (inside the conditional frame)
[Postprocessors: (empty for SagaChain)]
```

`ConditionalSagaInsertFrame` calls `CommitUnitOfWorkFrame` **unconditionally** (even if insert was skipped because `IsCompleted()`). The source (lines 23–31):
```csharp
if (!saga.IsCompleted()) { _insert.GenerateCode(...); }
_commit.GenerateCode(...);   // always
```

### 7.3 Path B: Start-or-Handle (has `ExistingCalls`)

Called via `generateCodeForMaybeExisting` (lines 287–309):

```
[Middleware: TransactionalFrame]
[Constructor frames]
1. ResolveSagaFrame (wraps both id-extraction and load):
     a. PullSagaIdFromMessageFrame  OR  PullSagaIdFromEnvelopeFrame
         → produces Variable sagaId : {idType} named "sagaId"
     b. DetermineLoadFrame(container, SagaType, sagaId)
         → produces Variable saga : TSaga (nullable)
2. IfElseNullGuardFrame:
     ┌─ [saga == null] — "Saga Does Not Exist" branch:
     │   Path B1: StartingCalls exist:
     │     i.  CreateMissingSagaFrame        — new TSaga()
     │     ii. SetSagaIdFrame                — saga.SagaType = sagaId (sets Id on the new instance)
     │     iii. StartingCall(s)              — saga.Start(message)
     │     iv.  cascading message frames
     │     v.   ConditionalSagaInsertFrame:
     │              if (!saga.IsCompleted()) { DetermineInsertFrame }
     │              CommitUnitOfWorkFrame
     │   Path B2: NotFoundCalls exist:
     │     i.  NotFoundCall(s)              — custom not-found handler
     │     ii. cascading message frames
     │     (no insert/commit — handler decides)
     │   Path B3: Neither:
     │     i.  AssertSagaStateExistsFrame   — throws UnknownSagaException
     └─ [saga != null] — "Saga Exists" branch (DetermineSagaExistsSteps, lines 400–429):
         i.   SetSagaIdFrame               — tags envelope SagaId for cascading messages
         ii.  ExistingCall(s)             — saga.Handle/Orchestrate/etc.(message)
         iii. cascading message frames
         (optional: ShouldProceedGuardFrame wraps ii+iii for ResequencerSaga<T>)
         iv.  SagaStoreOrDeleteFrame:
                  if (saga.IsCompleted()) {
                      DetermineDeleteFrame(sagaId, saga, container)   ← provider's delete
                  } else {
                      DetermineUpdateFrame(saga, container)            ← provider's update
                  }
         v.   CommitUnitOfWorkFrame(saga, container)                  ← provider's commit+flush
[Postprocessors: (empty for SagaChain — because ApplyTransactionSupport must skip the postprocessor for SagaChain)]
```

### 7.4 Summary: Provider Call Order for a `Handle` (existing saga) Message

```
ApplyTransactionSupport(chain, container)   — setup (adds TransactionalFrame to Middleware)
  [at runtime, TransactionalFrame opens session + starts transaction]
DetermineLoadFrame(container, SagaType, sagaId)
  → load saga (Find by _id in session)
  [handler runs]
SagaStoreOrDeleteFrame calls either:
  DetermineUpdateFrame(saga, container)     → version-guarded ReplaceOne (S8) in session
  OR
  DetermineDeleteFrame(sagaId, saga, container) → DeleteOne in session
CommitUnitOfWorkFrame(saga, container)      → commit transaction + flush outgoing
```

---

## 8. `ApplyTransactionSupport` — Saga-Chain Handling Pattern

The canonical pattern (from `CosmosDbPersistenceFrameProvider`):

```csharp
public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
{
    if (!chain.Middleware.OfType<TransactionalFrame>().Any())
    {
        chain.Middleware.Add(new TransactionalFrame(chain));

        // CRITICAL: saga chains get commit from CommitUnitOfWorkFrame (emitted inline).
        // Adding a postprocessor here would double-commit the transaction.
        if (chain is not SagaChain)
            chain.Postprocessors.Add(new CommitMongoTransactionFrame());
    }
}
```

This matches what `SagaChain.DetermineFrames` expects: the postprocessors list is empty for `SagaChain`, so `CommitUnitOfWorkFrame` is the only commit path. The guard `if (!chain.Middleware.OfType<TransactionalFrame>().Any())` is idempotent (prevents double-wrapping if called twice).

---

## 9. `CommitUnitOfWorkFrame` — Role in Saga Chains

`CommitUnitOfWorkFrame` returns a frame that is **inlined** into the generated method body — not added to postprocessors. For the MongoDB provider, it should return a `CommitMongoTransactionFrame` (which commits the session transaction and then flushes outgoing envelopes).

It is called in **two positions** within the saga body:
1. **Start path (Path A + Path B1):** inside `ConditionalSagaInsertFrame` — always executed after the conditional insert (even if skipped due to `IsCompleted()`)
2. **Handle path (Path B, existing saga):** last step of `DetermineSagaExistsSteps`, after `SagaStoreOrDeleteFrame`

For non-saga chains, the commit is in `Postprocessors`, not inlined — this is the key difference.

---

## 10. Compliance Test Oracle

**Location:** `src/Testing/Wolverine.ComplianceTests/Sagas/`

### `ISagaHost` (ISagaHost.cs)
```csharp
public interface ISagaHost
{
    Task<IHost> BuildHostAsync<TSaga>();
    Task<T?> LoadState<T>(Guid id)   where T : Saga;
    Task<T?> LoadState<T>(int id)    where T : Saga;
    Task<T?> LoadState<T>(long id)   where T : Saga;
    Task<T?> LoadState<T>(string id) where T : Saga;
}
```
`LoadState` reads **directly from the store** (not via Wolverine) to independently verify persistence.

### Sample Saga: `BasicWorkflow<TStart, TCompleteThree, TId>` (TestMessages.cs)
```csharp
public abstract class BasicWorkflow<TStart, TCompleteThree, TId> : Saga
    where TCompleteThree : CompleteThree<TId>
    where TStart : Start<TId>
{
    public TId Id { get; set; } = default!;   // saga identity member — maps to _id
    public bool OneCompleted { get; set; }
    public bool TwoCompleted { get; set; }
    public bool ThreeCompleted { get; set; }
    public bool FourCompleted { get; set; }
    public string Name { get; set; } = null!;

    public void Start(TStart starting) { Id = starting.Id; Name = starting.Name; }
    public CompleteTwo Handle(CompleteOne one) { OneCompleted = true; return new CompleteTwo(); }
    public void Handle(CompleteTwo message) { TwoCompleted = true; }
    public void Handle(CompleteFour message) { FourCompleted = true; }
    public void Handle(TCompleteThree three) { ThreeCompleted = true; }
    public void Handle(FinishItAll finish) { MarkCompleted(); }
}
```
Concrete types: `StringBasicWorkflow`, `GuidBasicWorkflow`, `IntBasicWorkflow`, `LongBasicWorkflow`.

### Compliance Facts Mapped to Failure Modes

| Fact | Failure tested | Exception expected |
|------|---------------|--------------------|
| `start_1` | Start path inserts saga | None |
| `start_2` | Wildcard start (`Starts` method) | None |
| `complete` | `MarkCompleted()` → delete; `LoadState` → null | None |
| `handle_a_saga_message_with_cascading_messages_passes_along_the_saga_id_in_header` | Cascading message carries saga ID | None |
| `straight_up_update_with_the_saga_id_on_the_message` | Update via `SagaId` member on message | None |
| `unknown_state` | No saga + no Start → `AssertSagaStateExistsFrame` | `UnknownSagaException` |
| `update_expecting_the_saga_id_to_be_on_the_envelope` | Saga ID from envelope header only | None |
| `update_with_message_that_uses_saga_identity_attributed_property` | `[SagaIdentity]` attribute | None |
| `update_with_no_saga_id_to_be_on_the_envelope` | Envelope header missing, message has no member | `IndeterminateSagaStateIdException` |
| `update_with_no_saga_id_to_be_on_the_envelope_or_message` | Both message and envelope missing id | `IndeterminateSagaStateIdException` |

**No concurrency compliance fact exists.** OCC correctness must be proven by custom test (S11).

---

## 11. Key Insights for Implementation

1. **`DetermineStoreFrame` is never called for saga chains.** `SagaChain` only calls `DetermineInsertFrame`, `DetermineUpdateFrame`, and `DetermineDeleteFrame(sagaId, saga, container)`. The upsert-only S6 baseline uses the same frame for insert and update; S8 must diverge them for OCC.

2. **Insert and update are separate provider calls.** Do not conflate them — `ConditionalSagaInsertFrame` calls `DetermineInsertFrame` (start path); `SagaStoreOrDeleteFrame` calls `DetermineUpdateFrame` and `DetermineDeleteFrame` (handle path). S8's version guard only goes on the update frame; insert sets initial `Version`.

3. **`CanApply` must check `chain is SagaChain` first.** Without it, saga chains are skipped silently (the provider is never selected, frames are never injected).

4. **`CanPersist` must be saga-scoped.** Return `true` only when `entityType.CanBeCastTo<Saga>()`. Returning `true` unconditionally triggers `DetermineStorageActionFrame` for non-saga entities (which should throw `NotSupportedException` in the stub) and causes codegen failures for `[Entity]`/`Insert<T>` usages.

5. **`ApplyTransactionSupport` must skip the postprocessor for `SagaChain`.** The `if (chain is not SagaChain)` guard prevents double-commit. This is the Cosmos pattern and is required for correct atomicity.

6. **`DetermineSagaIdType` only matters for the envelope-only case.** When the message type has a member matching the saga identity resolution rules, `PullSagaIdFromMessageFrame` is used and the member's type governs `sagaId` — `DetermineSagaIdType` is bypassed. The S6 baseline returning `typeof(string)` only affects messages that carry the saga id purely in `Envelope.SagaId`.

7. **Saga atomicity with outbox is automatic.** Because the saga frames run inside `TransactionalFrame`, and `CommitUnitOfWorkFrame` commits the same `IClientSessionHandle` session that the outbox enrolled in, saga state changes + outgoing envelopes are committed together in one MongoDB transaction.
