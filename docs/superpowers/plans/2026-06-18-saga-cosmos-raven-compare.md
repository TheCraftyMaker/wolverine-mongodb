# Task S3: Cosmos & RavenDb Saga Implementation Comparison

> **Plan reference:** `2026-06-18-saga-persistence.md` — Task S3  
> **Scope:** Read-only study of `external/wolverine` (pinned V6.2.2). No production code changed.  
> **Output:** Side-by-side comparison table + MongoDB implementation recommendation with exact frame snippets to adapt.

---

## 1. Files Read

### CosmosDb provider (`Wolverine.CosmosDb`)

| File | What it contributed |
|------|---------------------|
| `Internals/CosmosDbPersistenceFrameProvider.cs` | Full `IPersistenceFrameProvider` — all saga methods + `ApplyTransactionSupport` |
| `Internals/LoadDocumentFrame.cs` | Cosmos load frame (try/catch `CosmosException(NotFound)`) |
| `Internals/TransactionalFrame.cs` | Outbox enlistment; no SagaChain special-casing in GenerateCode |
| `WolverineCosmosDbExtensions.cs` | Registration: `IMessageStore`, `Container`, `InsertFirstPersistenceStrategy`, `ReferenceAssembly` |
| `CosmosDbTests/saga_storage_compliance.cs` | `CosmosDbSagaHost : ISagaHost`, `saga_storage_compliance : StringIdentifiedSagaComplianceSpecs<CosmosDbSagaHost>` |
| `CosmosDbTests/Internal/Generated/…/StringStartHandler*.cs` | Generated start handler (frame order verified) |
| `CosmosDbTests/Internal/Generated/…/StringCompleteThreeHandler*.cs` | Generated update/delete handler (frame order verified) |

### RavenDb provider (`Wolverine.RavenDb`)

| File | What it contributed |
|------|---------------------|
| `Internals/RavenDbPersistenceFrameProvider.cs` | Full `IPersistenceFrameProvider` — all saga methods + `ApplyTransactionSupport` |
| `Internals/LoadDocumentFrame.cs` | RavenDb load frame (null on miss, no try/catch) |
| `Internals/TransactionalFrame.cs` | Session open + outbox enlistment; **`SagaChain` branch sets `UseOptimisticConcurrency = true`** |
| `Internals/RavenDbSagaStoreDiagnostics.cs` | `ISagaStoreDiagnostics` implementation (registered by the extension) |
| `WolverineRavenDbExtensions.cs` | Registration: `IMessageStore`, `InsertFirstPersistenceStrategy`, `AsyncDocumentSessionSource`, `ISagaStoreDiagnostics`, `ReferenceAssembly` |
| `RavenDbTests/saga_storage_compliance.cs` | `RavenDbSagaHost : RavenTestDriver, ISagaHost`, `saga_storage_compliance : StringIdentifiedSagaComplianceSpecs<RavenDbSagaHost>` |
| `RavenDbTests/Internal/Generated/…/StringStartHandler*.cs` | Generated start handler (frame order verified) |
| `RavenDbTests/Internal/Generated/…/StringCompleteThreeHandler*.cs` | Generated update/delete handler (frame order verified) |

### Local OCC precedent (demo)

| File | What it contributed |
|------|---------------------|
| `demo/src/OrderDemo.Infrastructure/Persistence/OrderRepository.cs` | `UpdateAsync`: Version-guarded `ReplaceOneAsync` — the OCC pattern MongoDB saga frames should replicate |

---

## 2. Side-by-Side Comparison Table

| Dimension | CosmosDb | RavenDb | Notes |
|---|---|---|---|
| **`CanApply`** | `if (chain is SagaChain) return true;` first, then `Container` dep-scan | Identical (`IAsyncDocumentSession`) | Both put SagaChain first — this is the required shape |
| **`CanPersist`** | `return true;` unconditional | `return true;` unconditional | Both safe because both implement `DetermineStorageActionFrame`; Mongo must scope to `Saga` (see §5 R9) |
| **`DetermineSagaIdType`** | `return typeof(string);` | `return typeof(string);` | String-only; both ignore the saga's actual `Id` member type; **MongoDB should return the native type** |
| **Load frame** | `ReadItemAsync<T>(sagaId, PartitionKey.None, ct)` wrapped in `try { … } catch (CosmosException e when NotFound) { saga = default; }` | `session.LoadAsync<T>(sagaId, ct)` — returns `null` on miss natively | RavenDb's load is simpler; MongoDB's `Find(…).FirstOrDefaultAsync()` also returns `null` natively, so no try/catch needed |
| **Insert frame** | `container.UpsertItemAsync(saga)` — no session, immediate write | `session.StoreAsync(saga, ct)` — batched in unit-of-work | Cosmos writes are immediate (no session); RavenDb defers to `SaveChangesAsync` |
| **Update frame** | `container.UpsertItemAsync(saga)` — **identical to insert** (last-write-wins upsert) | `session.StoreAsync(saga, ct)` — **identical to insert** (change tracking handles it) | Neither provider uses `Saga.Version`; both are unconditional upserts |
| **Store frame** | Delegates to `DetermineUpdateFrame` (same upsert) | Delegates to `DetermineUpdateFrame` (same StoreAsync) | Same in both |
| **Delete frame** | `container.DeleteItemAsync<TSaga>(sagaId, PartitionKey.None)` | `session.Delete(saga)` (sync; batched in `SaveChangesAsync`) | RavenDb delete is sync; Cosmos delete is async but not in a session |
| **`CommitUnitOfWorkFrame`** | `new FlushOutgoingMessages()` — no session commit (Cosmos writes are already persisted) | `session.SaveChangesAsync(ct)` — commits all pending session changes | **Critical difference**: Cosmos has no MongoDB-style transaction to commit; RavenDb does commit a session unit-of-work |
| **`ApplyTransactionSupport`** | Adds `TransactionalFrame`; adds `FlushOutgoingMessages` postprocessor **only if `chain is not SagaChain`** | Adds `TransactionalFrame`; adds `SaveChangesAsync` + `FlushOutgoingMessages` postprocessors **only if `chain is not SagaChain`** | **Both use the same saga-chain guard** — this is the key structural rule to mirror |
| **`TransactionalFrame` — session** | `Container` resolved from DI (no session opened here) | Opens `IAsyncDocumentSession` with `_store.OpenAsyncSession()` | Mongo: `IClientSessionHandle` is already opened by `TransactionalFrame`; saga frames must use it |
| **`TransactionalFrame` — outbox** | `context.EnlistInOutbox(new CosmosDbEnvelopeTransaction(container, context))` | `context.EnlistInOutbox(new RavenDbEnvelopeTransaction(session, context))` | Both enlist the outbox in the same generated code block |
| **`TransactionalFrame` — SagaChain branch** | **None** — identical `GenerateCode` for all chains | **SagaChain branch:** `session.Advanced.UseOptimisticConcurrency = true;` | RavenDb's OCC is e-tag-based; MongoDB does not need a flag (session is already transactional) |
| **Saga OCC** | **None** — last-write-wins | RavenDb e-tag OCC via `UseOptimisticConcurrency = true` — **but does NOT map to `SagaConcurrencyException`** | Neither maps a concurrency conflict to Wolverine's `SagaConcurrencyException`; MongoDB should |
| **`ISagaStoreDiagnostics`** | **Not registered** | Registered as `RavenDbSagaStoreDiagnostics` | Optional/deferred for MongoDB |
| **Collection / container strategy** | Single `wolverine` container for all document types (CosmosDb constraint) | Single database; RavenDb uses type name as document collection prefix | MongoDB should use **one collection per saga type** (idiomatic; avoids cross-type `_id` collision) |
| **`DetermineStorageActionFrame`** | Implemented via `CosmosDbStorageActionApplier.ApplyAction<T>` | Implemented via `RavenDbStorageActionApplier.ApplyAction<T>` | Mongo does NOT implement this (yet) — scoping `CanPersist` to `Saga` avoids the gap |
| **`ISagaHost` implementation** | `CosmosDbSagaHost` — `LoadState<T>(string)` reads via `Container.ReadItemAsync`; `Guid/int/long` overloads throw `NotSupportedException` | `RavenDbSagaHost` — `LoadState<T>(string)` reads via `session.LoadAsync<T>`; others throw `NotSupportedException` | Both implement string only; MongoDB should implement all four overloads (Option B) |
| **`[CollectionDefinition]`** | `"cosmosdb"` — `CosmosDbCollection : ICollectionFixture<AppFixture>` | `"raven"` | MongoDB will use `"mongodb"` (already defined in `AppFixture.cs`) |
| **Compliance spec** | `: StringIdentifiedSagaComplianceSpecs<CosmosDbSagaHost>` | `: StringIdentifiedSagaComplianceSpecs<RavenDbSagaHost>` with `[CollectionDefinition("raven")]` | Same base class; MongoDB adds Guid/int/long specs too |

---

## 3. Generated Frame Order (Verified from Pre-Generated Code)

### 3a. CosmosDb — Start handler

```csharp
// Enlist outbox (TransactionalFrame)
context.EnlistInOutbox(new CosmosDbEnvelopeTransaction(_container, context));

// New saga POCO (inline)
var saga = new StringBasicWorkflow();

// Handler call
saga.Start(stringStart);

// SetSagaId
context.SetSagaId(stringStart.Id);

// If not completed: upsert (DetermineInsertFrame / DetermineUpdateFrame)
if (!saga.IsCompleted())
    await _container.UpsertItemAsync(saga).ConfigureAwait(false);

// CommitUnitOfWorkFrame → FlushOutgoingMessages
await context.FlushOutgoingMessagesAsync().ConfigureAwait(false);
```

### 3b. CosmosDb — Update/complete handler (StringCompleteThree)

```csharp
// Enlist outbox (TransactionalFrame)
context.EnlistInOutbox(new CosmosDbEnvelopeTransaction(_container, context));

// Saga-id extraction
string sagaId = stringCompleteThree.SagaId ?? context.Envelope.SagaId;
if (string.IsNullOrEmpty(sagaId)) throw new IndeterminateSagaStateIdException(context.Envelope);

// Load (DetermineLoadFrame — try/catch NotFound)
StringBasicWorkflow saga = default;
try { var r = await _container.ReadItemAsync<StringBasicWorkflow>(sagaId, PartitionKey.None, ct); saga = r.Resource; }
catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound) { saga = default; }

if (saga == null) throw new UnknownSagaException(typeof(StringBasicWorkflow), sagaId);
else
{
    context.SetSagaId(sagaId);
    saga.Handle(stringCompleteThree);

    // DetermineDeleteFrame vs DetermineUpdateFrame
    if (saga.IsCompleted())
        await _container.DeleteItemAsync<StringBasicWorkflow>(sagaId, PartitionKey.None).ConfigureAwait(false);
    else
        await _container.UpsertItemAsync(saga).ConfigureAwait(false);

    // CommitUnitOfWorkFrame → FlushOutgoingMessages
    await context.FlushOutgoingMessagesAsync().ConfigureAwait(false);
}
```

### 3c. RavenDb — Start handler

```csharp
// TransactionalFrame: open session + enlist outbox
using var asyncDocumentSession = _documentStore.OpenAsyncSession();
context.EnlistInOutbox(new RavenDbEnvelopeTransaction(asyncDocumentSession, context));
// (for SagaChain also: asyncDocumentSession.Advanced.UseOptimisticConcurrency = true)

// New saga POCO
var saga = new StringBasicWorkflow();

// Handler call
saga.Start(stringStart);

context.SetSagaId(stringStart.Id);

// If not completed: StoreAsync (DetermineInsertFrame)
if (!saga.IsCompleted())
    await asyncDocumentSession.StoreAsync(saga, cancellation).ConfigureAwait(false);

// CommitUnitOfWorkFrame → SaveChangesAsync (commits session)
await asyncDocumentSession.SaveChangesAsync(cancellation).ConfigureAwait(false);
```

### 3d. RavenDb — Update/complete handler

```csharp
// TransactionalFrame: open session
using var asyncDocumentSession = _documentStore.OpenAsyncSession();
// For SagaChain: asyncDocumentSession.Advanced.UseOptimisticConcurrency = true;
context.EnlistInOutbox(...);

string sagaId = context.Envelope.SagaId ?? stringCompleteThree.SagaId;
if (string.IsNullOrEmpty(sagaId)) throw new IndeterminateSagaStateIdException(...);

// Load (DetermineLoadFrame — null on miss)
var saga = await asyncDocumentSession.LoadAsync<StringBasicWorkflow>(sagaId, cancellation);

if (saga == null) throw new UnknownSagaException(...);
else
{
    context.SetSagaId(sagaId);
    saga.Handle(stringCompleteThree);

    // DetermineDeleteFrame vs DetermineUpdateFrame
    if (saga.IsCompleted())
        asyncDocumentSession.Delete(saga);                                      // sync
    else
        await asyncDocumentSession.StoreAsync(saga, cancellation);              // batched

    // CommitUnitOfWorkFrame → SaveChangesAsync
    await asyncDocumentSession.SaveChangesAsync(cancellation).ConfigureAwait(false);
}
```

---

## 4. Key Structural Rules Extracted

These are the rules that **must** be carried over verbatim to the MongoDB provider:

### Rule 1 — `CanApply` checks `SagaChain` first

```csharp
public bool CanApply(IChain chain, IServiceContainer container)
{
    if (chain is SagaChain) return true;
    // ... existing dependency checks ...
}
```

Without this, saga chains are never picked up by `MongoDbPersistenceFrameProvider`.

### Rule 2 — `ApplyTransactionSupport` skips the commit postprocessor for `SagaChain`

```csharp
public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
{
    if (!chain.Middleware.OfType<TransactionalFrame>().Any())
    {
        chain.Middleware.Add(new TransactionalFrame(chain));

        if (chain is not SagaChain)   // ← the critical guard
            chain.Postprocessors.Add(new CommitMongoTransactionFrame());
    }
}
```

For saga chains, `CommitUnitOfWorkFrame` (not a postprocessor) provides the commit.
Without the guard the transaction commits **twice** — once from the postprocessor and once from `CommitUnitOfWorkFrame`. Verified present in both Cosmos and RavenDb.

### Rule 3 — `CommitUnitOfWorkFrame` is the saga-chain's commit+flush

```csharp
public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    => new CommitMongoTransactionFrame();
```

Wolverine core calls this at the end of the saga chain and appends it after the update/delete frame. For MongoDB, `CommitMongoTransactionFrame` must commit the session **and** flush outgoing messages (already its behaviour).

### Rule 4 — `CanPersist` must be saga-scoped for MongoDB

```csharp
public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
{
    persistenceService = typeof(IMongoDatabase);
    return entityType.CanBeCastTo<Saga>();  // NOT unconditional
}
```

Cosmos and RavenDb return `true` unconditionally because they implement `DetermineStorageActionFrame`. MongoDB does not — an unconditional `true` would advertise generic `[Entity]`/`Insert<T>`/`IStorageAction<T>` support that then throws at codegen.

---

## 5. MongoDB Should Go Beyond Cosmos/RavenDb

The plan's recommendation is confirmed by this comparison:

### 5a. Native saga id type (beyond both providers)

Both Cosmos and RavenDb hard-code `DetermineSagaIdType → typeof(string)` and only implement `ISagaHost.LoadState<T>(string)`. This means they only work with string-identified sagas.

MongoDB should return the saga's **actual** identity-member type:

```csharp
public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
{
    // mirrors LightweightSagaPersistenceFrameProvider.cs:80-82
    return SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType()
           ?? typeof(string);
}
```

This enables `GuidIdentifiedSagaComplianceSpecs` and `IntIdentifiedSagaComplianceSpecs` in addition to `StringIdentifiedSagaComplianceSpecs`.

**Note (🔎 review):** `DetermineSagaIdType` only governs the *envelope-header-only* identity path (`SagaChain.cs:290-292`). When the message itself carries a saga-id member, `PullSagaIdFromMessageFrame` uses that member's type directly. The load/delete frames must key `_id` off whatever typed `sagaId` variable they receive, regardless of what `DetermineSagaIdType` returns.

### 5b. `Saga.Version` optimistic concurrency (beyond both providers)

Neither Cosmos nor RavenDb maps concurrency conflicts to `SagaConcurrencyException`:

- **Cosmos:** last-write-wins upsert; no conflict detection.
- **RavenDb:** sets `UseOptimisticConcurrency = true` (e-tag OCC) in `TransactionalFrame`, so `SaveChangesAsync` may throw RavenDb's own `ConcurrencyException` — but **this is never caught and re-thrown as `SagaConcurrencyException`**. The compliance suite passes because it does not exercise concurrent updates.

MongoDB should implement proper `Saga.Version`-guarded OCC, matching Wolverine's lightweight SQL providers (`DatabaseSagaSchema.cs:99-110`) and the demo's `OrderRepository.UpdateAsync`:

```csharp
// demo precedent: OrderRepository.UpdateAsync
var result = await _orders.ReplaceOneAsync(
    session,
    Builders<Order>.Filter.And(
        Builders<Order>.Filter.Eq(o => o.Id, order.Id),
        Builders<Order>.Filter.Eq(o => o.Version, order.Version - 1)),  // filter on OLD version
    order,
    cancellationToken: ct);

if (result.ModifiedCount == 0)
    throw new InvalidOperationException("Optimistic concurrency conflict …");
```

For sagas, use `SagaConcurrencyException` (from `Saga.cs`) and split the insert/update frames:

```csharp
// INSERT (DetermineInsertFrame): set initial Version = 1, then InsertOneAsync
saga.Version = 1;
await collection.InsertOneAsync(session, saga, cancellationToken: ct);

// UPDATE (DetermineUpdateFrame): capture oldVersion, increment, ReplaceOneAsync on (_id, oldVersion)
var oldVersion = saga.Version;
saga.Version = oldVersion + 1;
var result = await collection.ReplaceOneAsync(
    session,
    Builders<TSaga>.Filter.And(
        Builders<TSaga>.Filter.Eq("_id", saga.Id),
        Builders<TSaga>.Filter.Eq(s => s.Version, oldVersion)),
    saga,
    new ReplaceOptions { IsUpsert = false },
    cancellationToken: ct);

if (result.ModifiedCount == 0)
    throw new SagaConcurrencyException(saga.GetType(), saga.Id);
```

### 5c. Per-saga-type collections (beyond both providers' single-container model)

Cosmos uses one container for all documents (forced by the CosmosDB model); RavenDb uses a single session with type-prefix conventions. MongoDB should use **one collection per saga type** — idiomatic, no cross-type `_id` collisions, independently scalable:

```csharp
// MongoConstants.cs helper
public static string SagaCollectionName(Type sagaType)
    => $"wolverine_saga_{sagaType.Name.ToLowerInvariant()}";
```

**Cleanup requirement (🔎 review):** `MongoDbMessageStore.Admin.ClearAllAsync`/`RebuildAsync` currently only drops the five known Wolverine system collections. Per-saga-type collections are not in that list, so they leak between compliance test facts on the shared fixture DB. The fix (decided in S4) is either drop all `wolverine_saga_*`-prefixed collections in `ClearAllAsync`, or use per-test databases for saga compliance.

---

## 6. Frame Snippets to Adapt for MongoDB

The snippets below are the concrete codegen frames MongoDB should produce. They assume:
- `session` = the `IClientSessionHandle` opened by `TransactionalFrame` (already a codegen variable)
- `db` = the `IMongoDatabase` (already a codegen variable)
- `ct` = `CancellationToken` (already a codegen variable)
- `coll` = `MongoConstants.SagaCollectionName(typeof(TSaga))`

### Load frame (mirrors RavenDb simplicity — no try/catch)

```csharp
// Generated by LoadSagaFrame
var sagaTypeName = db.GetCollection<TSaga>(coll);
var saga = await db.GetCollection<TSaga>(coll)
    .Find(session, Builders<TSaga>.Filter.Eq("_id", sagaId))
    .FirstOrDefaultAsync(ct)
    .ConfigureAwait(false);
// saga == null if not found (analogous to RavenDb's session.LoadAsync returning null)
```

### Insert frame (sets initial Version = 1)

```csharp
// Generated by StoreSagaFrame (insert path, S8)
saga.Version = 1;
await db.GetCollection<TSaga>(coll)
    .InsertOneAsync(session, saga, cancellationToken: ct)
    .ConfigureAwait(false);
```

*Baseline (S6 — Cosmos-parity upsert, no Version yet):*

```csharp
await db.GetCollection<TSaga>(coll)
    .ReplaceOneAsync(session,
        Builders<TSaga>.Filter.Eq("_id", sagaId),
        saga,
        new ReplaceOptions { IsUpsert = true },
        ct)
    .ConfigureAwait(false);
```

### Update frame (Version-guarded, S8)

```csharp
// Generated by StoreSagaFrame (update path)
var _oldVersion = saga.Version;
saga.Version = _oldVersion + 1;
var _replaceResult = await db.GetCollection<TSaga>(coll)
    .ReplaceOneAsync(
        session,
        Builders<TSaga>.Filter.And(
            Builders<TSaga>.Filter.Eq("_id", sagaId),
            Builders<TSaga>.Filter.Eq(s => s.Version, _oldVersion)),
        saga,
        new ReplaceOptions { IsUpsert = false },
        ct)
    .ConfigureAwait(false);
if (_replaceResult.ModifiedCount == 0)
    throw new SagaConcurrencyException(typeof(TSaga), sagaId);
```

*Baseline (S6 — upsert, same as insert):* identical to insert upsert above.

### Delete frame

```csharp
// Generated by DeleteSagaFrame
await db.GetCollection<TSaga>(coll)
    .DeleteOneAsync(session,
        Builders<TSaga>.Filter.Eq("_id", sagaId),
        cancellationToken: ct)
    .ConfigureAwait(false);
```

Delete is **not Version-guarded** (matches lightweight SQL `DatabaseSagaSchema.cs:113-119`; decided in S4/S8).

### CommitUnitOfWorkFrame

```csharp
// Generated by CommitMongoTransactionFrame (already exists)
// commits the session transaction AND flushes outgoing messages
await session.CommitTransactionAsync(ct).ConfigureAwait(false);
await context.FlushOutgoingMessagesAsync().ConfigureAwait(false);
```

This is the existing `CommitMongoTransactionFrame` — no new frame needed.

---

## 7. ISagaHost Implementation Pattern

Both providers implement only `LoadState<T>(string id)` and throw `NotSupportedException` for the other three overloads. MongoDB should implement all four (Option B):

```csharp
// mirrors CosmosDbSagaHost structure
public class MongoDbSagaHost : ISagaHost
{
    private readonly AppFixture _fixture;

    public MongoDbSagaHost()
    {
        _fixture = new AppFixture();
        _fixture.InitializeAsync().GetAwaiter().GetResult();
    }

    public Task<IHost> BuildHostAsync<TSaga>()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.CodeGeneration.GeneratedCodeOutputPath = …;
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.Discovery.IncludeType<StringBasicWorkflow>();
                opts.Discovery.IncludeAssembly(typeof(StringBasicWorkflow).Assembly);
                opts.Services.AddSingleton(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync();
    }

    // LoadState reads directly from MongoDB (not via Wolverine) to independently verify persistence
    public async Task<T?> LoadState<T>(string id) where T : Saga
    {
        var coll = MongoConstants.SagaCollectionName(typeof(T));
        var collection = _fixture.Client.GetDatabase(AppFixture.DatabaseName).GetCollection<T>(coll);
        return await collection.Find(Builders<T>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
    }

    public async Task<T?> LoadState<T>(Guid id) where T : Saga   // S10
    {
        var coll = MongoConstants.SagaCollectionName(typeof(T));
        var collection = _fixture.Client.GetDatabase(AppFixture.DatabaseName).GetCollection<T>(coll);
        return await collection.Find(Builders<T>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
    }

    public async Task<T?> LoadState<T>(int id) where T : Saga    // S10
    {
        var coll = MongoConstants.SagaCollectionName(typeof(T));
        var collection = _fixture.Client.GetDatabase(AppFixture.DatabaseName).GetCollection<T>(coll);
        return await collection.Find(Builders<T>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
    }

    public async Task<T?> LoadState<T>(long id) where T : Saga   // S10
    {
        var coll = MongoConstants.SagaCollectionName(typeof(T));
        var collection = _fixture.Client.GetDatabase(AppFixture.DatabaseName).GetCollection<T>(coll);
        return await collection.Find(Builders<T>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
    }
}
```

---

## 8. Recommendation

**Mirror Cosmos for the overall frame structure; add native-id + `Saga.Version` OCC like the demo's `OrderRepository`.**

### Mirror from Cosmos (structural rules)

1. **`CanApply` shape:** `if (chain is SagaChain) return true;` first
2. **`ApplyTransactionSupport` saga-guard:** skip commit postprocessor for `SagaChain` — `if (chain is not SagaChain)` gates the `CommitMongoTransactionFrame` postprocessor
3. **`CommitUnitOfWorkFrame` role:** the saga chain's terminal frame (commit + flush) — already `CommitMongoTransactionFrame`
4. **Load → handler → write/delete → commit ordering:** exactly the order both providers generate

### Mirror from RavenDb (simplifications)

1. **Load frame:** `Find(session, …).FirstOrDefaultAsync()` — returns `null` on miss natively; no `try/catch` needed (unlike Cosmos)
2. **Session-based writes:** all frames accept `session` and `db` variables (analogous to RavenDb's `asyncDocumentSession`)

### Go beyond both (MongoDB-specific improvements)

1. **`DetermineSagaIdType` → native type:** `SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType() ?? typeof(string)` — enables Guid/int/long sagas
2. **`Saga.Version` OCC on update:** filter `(_id, oldVersion)`, throw `SagaConcurrencyException` on `ModifiedCount == 0` — mirrors `OrderRepository.UpdateAsync` (local precedent), and Wolverine's own lightweight SQL saga providers
3. **`CanPersist` scoped to `Saga`:** `entityType.CanBeCastTo<Saga>()` — prevents advertising generic document persistence that MongoDB doesn't implement
4. **Per-saga-type collections:** `wolverine_saga_{name}` naming convention — idiomatic MongoDB, no cross-type `_id` collision

### Decomposition strategy (from the plan)

- **S6** delivers Cosmos-parity (string-id, upsert/last-write-wins, no OCC) — passing `StringIdentifiedSagaComplianceSpecs`
- **S7** generalizes to native id types — passing `GuidIdentifiedSagaComplianceSpecs`
- **S8** adds `Saga.Version` OCC — proven by custom S11 concurrency tests

S7 and S8 can each be dropped independently without invalidating S6's contribution, making the Cosmos-parity baseline the guaranteed minimum.
