# Tier 2 — ISagaStoreDiagnostics API + Raven Reference

> **Task D2 of** `docs/superpowers/plans/2026-06-21-persistence-suite-completion.md`
>
> **Status:** Complete (read-only discovery; no library code changes).
> **Date:** 2026-06-21
> **Branch:** `docs/saga-diagnostics-discovery`

## Purpose

Pin the exact `ISagaStoreDiagnostics` contract, the supporting core types, and the
RavenDb implementation + registration that `MongoDbSagaStoreDiagnostics` (Task T2.1)
will mirror.  Everything below is quoted directly from the pinned
`external/wolverine` submodule (V6.9.0).

---

## 1. The Interface

**File:** `external/wolverine/src/Wolverine/Persistence/Sagas/ISagaStoreDiagnostics.cs:22-63`  
**Namespace:** `Wolverine.Persistence.Sagas`

```csharp
public interface ISagaStoreDiagnostics
{
    // :31
    Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct);

    // :48
    Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct);

    // :62
    Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count, CancellationToken ct);
}
```

**Notes:**
- `sagaTypeName` matches the saga type's **full name in code** (same as `Type.FullName`).
  The aggregator routes by both `FullName` and `Name` (short form), so implementations
  must index both.
- `count` may be any non-negative integer; implementations are expected to clamp to a
  sensible upper bound (RavenDb uses `[0, 1000]`; MongoDB will do the same).
- Returns `null` / empty when this storage does not own the requested saga type — never throws.
- One implementation per storage; the runtime wraps all registrations in
  `AggregateSagaStoreDiagnostics` and exposes a single unified view.

---

## 2. Supporting Core Types

### 2.1 `SagaDescriptor`

**File:** `external/wolverine/src/Wolverine/Configuration/Capabilities/SagaDescriptor.cs:19`  
**Namespace:** `Wolverine.Configuration.Capabilities`

```csharp
public class SagaDescriptor
{
    public TypeDescriptor StateType { get; set; }   // the concrete : Saga class
    public string? SagaIdType { get; set; }         // CLR FQN of the saga's id member type
    public List<SagaMessageRole> Messages { get; set; } = new();
    public string? StorageProvider { get; set; }    // tag e.g. "RavenDb", "MongoDb"
}

public record SagaMessageRole(
    TypeDescriptor MessageType,
    SagaRole Role,
    string? SagaIdMember,
    TypeDescriptor[] PublishedTypes);

public enum SagaRole { Start, StartOrHandle, Orchestrate, NotFound }
```

`TypeDescriptor` is from `JasperFx.Descriptors`; `TypeDescriptor.For(typeof(T))` produces one.

### 2.2 `SagaInstanceState`

**Package:** `JasperFx.Descriptors` (the `JasperFx` NuGet package, `JasperFx.Descriptors` namespace)  
**Confirmed via:** reflection on `JasperFx 2.12.0` DLL

```csharp
// namespace JasperFx.Descriptors
public class SagaInstanceState
{
    public SagaInstanceState(
        string SagaTypeName,
        object Identity,
        bool IsCompleted,
        JsonElement State,            // System.Text.Json.JsonElement
        DateTimeOffset? LastModified)
    { ... }

    public string SagaTypeName { get; }
    public object Identity { get; }
    public bool IsCompleted { get; }
    public JsonElement State { get; }        // serialised saga POCO
    public DateTimeOffset? LastModified { get; }
}
```

All existing implementations pass `null` for `LastModified` (RavenDb `:136`, RDBMS `:207`,
Marten `:124`, EF Core `:160`).  MongoDB will do the same — the `State` JSON and
`IsCompleted` flag are what CritterWatch cares about.

`State` is produced by `JsonSerializer.SerializeToElement(saga, sagaType)` (reflection-based
STJ over the runtime saga type).  `IsCompleted` is `saga is Wolverine.Saga s && s.IsCompleted()`.

### 2.3 `SagaDescriptorBuilder.Build`

**File:** `external/wolverine/src/Wolverine/Persistence/Sagas/SagaDescriptorBuilder.cs:33`  
**Signature:**

```csharp
internal static SagaDescriptor Build(HandlerGraph graph, Type sagaType, string? storageProvider)
```

Called by each per-storage `ISagaStoreDiagnostics` implementation with its own
`storageProvider` tag (e.g. `"RavenDb"`, `"MongoDb"`).  It walks `SagaChain`s on
`graph`, classifies each chain's handler methods (Start / Orchestrate / NotFound),
and fills `Messages` + `SagaIdType`.

---

## 3. `RavenDbSagaStoreDiagnostics` — Full Reference

**File:** `external/wolverine/src/Persistence/Wolverine.RavenDb/Internals/RavenDbSagaStoreDiagnostics.cs`  
**Class:** `internal sealed class RavenDbSagaStoreDiagnostics : ISagaStoreDiagnostics` (`:30`)

### 3.1 Constructor (`:37-41`)

```csharp
public RavenDbSagaStoreDiagnostics(IWolverineRuntime runtime, IDocumentStore store)
{
    _runtime = runtime;
    _store = store;
}
```

Fields: `_runtime`, `_store`, `_gate` (object for lock), `_sagaIndex?: Dictionary<string, Type>`.

### 3.2 Lazy saga index (`:161-203`)

```csharp
private Dictionary<string, Type> sagaIndex()
{
    if (_sagaIndex is not null) return _sagaIndex;
    lock (_gate)
    {
        if (_sagaIndex is not null) return _sagaIndex;          // double-checked lock

        var index = new Dictionary<string, Type>(StringComparer.Ordinal);
        var providers = _runtime.Options.CodeGeneration.PersistenceProviders();
        var raven = providers.OfType<RavenDbPersistenceFrameProvider>().FirstOrDefault();
        if (raven is null) { _sagaIndex = index; return index; }

        var container = _runtime.Options.HandlerGraph.Container;
        var sagaTypes = _runtime.Options.HandlerGraph.Chains
            .OfType<SagaChain>()
            .Select(c => c.SagaType)
            .Distinct();

        foreach (var sagaType in sagaTypes)
        {
            bool canPersist;
            try { canPersist = raven.CanPersist(sagaType, container, out _); }
            catch { canPersist = false; }
            if (!canPersist) continue;

            index.TryAdd(sagaType.FullName!, sagaType);   // canonical
            index.TryAdd(sagaType.Name, sagaType);         // short-name (caller-friendly)
        }

        _sagaIndex = index;
        return index;
    }
}
```

**Key pattern:** locates the provider via `PersistenceProviders().OfType<TProvider>()`,
then filters saga types by `provider.CanPersist`.  The index has **two entries per type**
(FullName + Name) so aggregator routing works on either form.

### 3.3 `GetRegisteredSagasAsync` (`:43-48`)

```csharp
public Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
{
    var distinct = sagaIndex().Values.Distinct().ToArray();
    var descriptors = distinct.Select(buildDescriptor).ToArray();
    return Task.FromResult<IReadOnlyList<SagaDescriptor>>(descriptors);
}

private SagaDescriptor buildDescriptor(Type sagaType)
    => SagaDescriptorBuilder.Build(_runtime.Options.HandlerGraph, sagaType, "RavenDb"); // :119-122
```

Synchronous (no async I/O needed for descriptor construction).

### 3.4 `ReadSagaAsync` (`:62-87`)

```csharp
[UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "...")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "...")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "...")]
public async Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
{
    if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType)) return null;

    using var session = _store.OpenAsyncSession();
    var id = identity?.ToString();
    if (id is null) return null;

    // Reflection: session.LoadAsync<TSaga>(string id, CancellationToken ct)
    var loadAsync = typeof(IAsyncDocumentSession)
        .GetMethods()
        .FirstOrDefault(m => m.Name == nameof(IAsyncDocumentSession.LoadAsync)
                             && m.IsGenericMethodDefinition
                             && m.GetParameters().Length == 2
                             && m.GetParameters()[0].ParameterType == typeof(string)
                             && m.GetParameters()[1].ParameterType == typeof(CancellationToken));
    if (loadAsync is null) return null;

    var task = (Task)loadAsync.MakeGenericMethod(sagaType).Invoke(session, [id, ct])!;
    await task.ConfigureAwait(false);
    var saga = task.GetType().GetProperty("Result")!.GetValue(task);
    if (saga is null) return null;

    return buildInstance(sagaType, identity!, saga);
}
```

**RavenDb specificity:** RavenDb always uses `string` ids (`.ToString()` coercion).
MongoDB stores native id types (Guid/int/long/string) — the `identity` passed in must be
coerced to the saga's native id type before issuing the `Find` filter.  See
§4 mapping for the MongoDB approach.

### 3.5 `ListSagaInstancesAsync` (`:89-104`) + `querySagasAsync<TSaga>` (`:106-117`)

```csharp
public async Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(
    string sagaTypeName, int count, CancellationToken ct)
{
    if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType))
        return Array.Empty<SagaInstanceState>();

    var clamped = count <= 0 ? 0 : Math.Min(count, 1000);  // clamp [0, 1000]
    if (clamped == 0) return Array.Empty<SagaInstanceState>();

    using var session = _store.OpenAsyncSession();
    var helper = typeof(RavenDbSagaStoreDiagnostics)
        .GetMethod(nameof(querySagasAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
        .MakeGenericMethod(sagaType);

    var task = (Task<IReadOnlyList<SagaInstanceState>>)helper.Invoke(this, [session, sagaType, clamped, ct])!;
    return await task.ConfigureAwait(false);
}

// The private generic helper used via reflection above:
private async Task<IReadOnlyList<SagaInstanceState>> querySagasAsync<TSaga>(
    IAsyncDocumentSession session, Type sagaType, int count, CancellationToken ct) where TSaga : class
{
    var sagas = await session.Query<TSaga>().Take(count).ToListAsync(ct).ConfigureAwait(false);
    // ... map each saga to SagaInstanceState via buildInstance
}
```

**Pattern:** reflection-dispatch to a private generic helper `querySagasAsync<TSaga>` so
the collection query is fully typed.  MongoDB will use the same pattern with
`IMongoCollection<TSaga>` instead of `IAsyncDocumentSession`.

### 3.6 `buildInstance` (`:132-142`) — AOT annotations

```csharp
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "...")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "...")]
private static SagaInstanceState buildInstance(Type sagaType, object identity, object saga)
{
    var stateJson = JsonSerializer.SerializeToElement(saga, sagaType);
    var isCompleted = saga is global::Wolverine.Saga sagaBase && sagaBase.IsCompleted();
    return new SagaInstanceState(
        sagaType.FullNameInCode(),
        identity,
        isCompleted,
        stateJson,
        null);                   // LastModified — all providers pass null
}
```

### 3.7 `extractIdentity` — fallback id extraction (`:150-159`)

```csharp
[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "...")]
private static object? extractIdentity(object saga, Type sagaType)
{
    var idMember = (MemberInfo?)sagaType.GetProperty("Id") ?? sagaType.GetField("Id");
    return idMember switch
    {
        PropertyInfo p => p.GetValue(saga),
        FieldInfo f => f.GetValue(saga),
        _ => null
    };
}
```

This is **RavenDb-specific** (used when `session.Advanced.GetDocumentId` fails).
MongoDB stores native Bson ids so identity extraction is via
`BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap?.Getter(saga)` — the same mechanism
`MongoEntityOperations.IdOf<T>` uses for entity frames.

---

## 4. Registration

**File:** `external/wolverine/src/Persistence/Wolverine.RavenDb/WolverineRavenDbExtensions.cs:33-36`

```csharp
options.Services.AddSingleton<ISagaStoreDiagnostics>(s =>
    new RavenDbSagaStoreDiagnostics(
        s.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>(),
        s.GetRequiredService<IDocumentStore>()));
```

**MongoDB registration line to add in `UseMongoDbPersistence`
(`src/Wolverine.MongoDB/WolverineMongoDbExtensions.cs`):**

```csharp
// Mirror WolverineRavenDbExtensions.cs:33-36.
// databaseName is closed over from UseMongoDbPersistence(databaseName, configure?).
options.Services.AddSingleton<ISagaStoreDiagnostics>(s =>
    new MongoDbSagaStoreDiagnostics(
        s.GetRequiredService<IWolverineRuntime>(),
        s.GetRequiredService<IMongoClient>(),
        databaseName));
```

The Mongo implementation takes `IMongoClient` (not `IMongoDatabase`) so it can acquire
the database handle inside the service factory — matching the pattern the rest of the
library uses.  The `databaseName` string is the same one passed to
`client.GetDatabase(databaseName)` throughout the store.

---

## 5. Runtime Aggregation

**File:** `external/wolverine/src/Wolverine/Runtime/WolverineRuntime.cs`

```csharp
// :41
private readonly Lazy<ISagaStoreDiagnostics> _sagaStorage;

// :66-68 (constructor)
_sagaStorage = new Lazy<ISagaStoreDiagnostics>(() =>
    new AggregateSagaStoreDiagnostics(
        container.Services.GetServices<ISagaStoreDiagnostics>()));

// :269
public ISagaStoreDiagnostics SagaStorage => _sagaStorage.Value;
```

**`IWolverineRuntime.SagaStorage`** (`IWolverineRuntime.cs:53`):
```csharp
ISagaStoreDiagnostics SagaStorage { get; }
```

The aggregator (`AggregateSagaStoreDiagnostics`) fans out `GetRegisteredSagasAsync` across
all children and routes `ReadSagaAsync`/`ListSagaInstancesAsync` to the child that owns the
saga type.  Routing is built lazily on first access via `GetRegisteredSagasAsync`, keyed by
both `descriptor.StateType.FullName` and `descriptor.StateType.Name`.

---

## 6. Per-Provider Test Pattern

**File:** `external/wolverine/src/Persistence/RavenDbTests/raven_saga_store_diagnostics_tests.cs`

```
[Collection("raven")]
public class raven_saga_store_diagnostics_tests : RavenTestDriver, IAsyncLifetime
```

- Builds a host with `DisableConventionalDiscovery().IncludeType<RavenDiagSaga>()` so the
  saga catalog is stable (not polluted by every saga in the test project).
- Wires `opts.Services.AddSingleton(_store); opts.UseRavenDbPersistence();`.
- Asserts via `_host.GetRuntime().SagaStorage` (the aggregated view).
- **Five facts:**
  1. `registered_saga_types_includes_raven_owned_saga` — `StorageProvider == "RavenDb"`,
     `StartRavenDiagSaga` appears in `Messages` with `SagaRole.Start`.
  2. `read_saga_returns_state_for_existing_instance` — after invoking the start message,
     `ReadSagaAsync(FullName, id)` returns non-null; `IsCompleted == false`;
     `State.GetProperty("Note").GetString() == "alpha"`.
  3. `read_saga_returns_null_for_missing_instance` — missing id → `null`.
  4. `list_saga_instances_returns_recent_sagas` — after two start invocations,
     list returns `≥ 2`; all have `SagaTypeName == typeof(RavenDiagSaga).FullName`.
  5. `unknown_saga_type_returns_null_and_empty` — unknown name → null read + empty list.
- **No unified compliance spec** — `StorageActionCompliance` exists for entity persistence
  but there is no analogue for diagnostics.  Cosmos does NOT register `ISagaStoreDiagnostics`
  (confirmed: no `AddSingleton<ISagaStoreDiagnostics>` in `CosmosDbExtensions`).

The MongoDB test (`src/Wolverine.MongoDB.Tests/saga_store_diagnostics.cs`, Task T2.2)
will mirror these five facts using `[Collection("mongodb")]` on `AppFixture`, reusing a
compliance saga type rather than a new in-test saga.

---

## 7. RavenDb → MongoDB Method Mapping

| Raven concept | MongoDB equivalent |
|---|---|
| `IDocumentStore` (constructor arg) | `IMongoClient` + `string databaseName` |
| `_store.OpenAsyncSession()` | `_client.GetDatabase(databaseName)` (no session needed for reads) |
| `session.LoadAsync<TSaga>(id.ToString(), ct)` via reflection | `collection.Find(Builders<TSaga>.Filter.Eq("_id", id)).FirstOrDefaultAsync(ct)` via reflection |
| `session.Query<TSaga>().Take(n).ToListAsync(ct)` via `querySagasAsync<TSaga>` | `collection.Find(FilterDefinition<TSaga>.Empty).Limit(n).ToListAsync(ct)` via analogous private generic helper |
| `session.Advanced.GetDocumentId(saga)` → fallback `extractIdentity` | `BsonClassMap.LookupClassMap(typeof(TSaga)).IdMemberMap?.Getter(saga)` |
| Raven always coerces to `string` id | MongoDB: pass `identity` directly (native type). The `_id` filter must match the saga's stored id type exactly. Coercion: if `identity` is already the right type, use as-is; otherwise convert (`Guid.Parse`, `int.Parse`, etc.) |
| Collection name: Raven uses its own document-type convention | `MongoConstants.SagaCollectionName(sagaType)` → `"wolverine_saga_<lowercased-type-name>"` (e.g. `"wolverine_saga_orderfulfillmentsaga"`) |
| Provider tag passed to `SagaDescriptorBuilder.Build` | `"MongoDb"` |
| `providers.OfType<RavenDbPersistenceFrameProvider>()` | `providers.OfType<MongoDbPersistenceFrameProvider>()` |

### 7.1 `ReadSagaAsync` — MongoDB implementation sketch

```csharp
public async Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
{
    if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType)) return null;
    if (identity is null) return null;

    // Reflection-dispatch to typed helper (avoids casting Task<TSaga>).
    var helper = typeof(MongoDbSagaStoreDiagnostics)
        .GetMethod(nameof(readSagaAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
        .MakeGenericMethod(sagaType);
    return await ((Task<SagaInstanceState?>)helper.Invoke(this, [sagaType, identity, ct])!).ConfigureAwait(false);
}

[UnconditionalSuppressMessage("Trimming", "IL2060", ...)]
[UnconditionalSuppressMessage("AOT", "IL3050", ...)]
private async Task<SagaInstanceState?> readSagaAsync<TSaga>(Type sagaType, object identity, CancellationToken ct)
    where TSaga : class
{
    var db = _client.GetDatabase(_databaseName);
    var collection = db.GetCollection<TSaga>(MongoConstants.SagaCollectionName(sagaType));
    var saga = await collection
        .Find(Builders<TSaga>.Filter.Eq("_id", identity))
        .FirstOrDefaultAsync(ct)
        .ConfigureAwait(false);
    if (saga is null) return null;
    return buildInstance(sagaType, identity, saga);
}
```

> **Note:** `Builders<TSaga>.Filter.Eq("_id", identity)` matches MongoDB's `_id` field.
> The driver handles type coercion (e.g. `identity` as `object` containing a `Guid`).
> This is idiomatic and does not require class-map lookup for the filter — only for
> identity extraction from a saga POCO (the `querySagasAsync` helper, below).

### 7.2 `ListSagaInstancesAsync` — MongoDB implementation sketch

```csharp
[UnconditionalSuppressMessage("Trimming", "IL2060", ...)]
[UnconditionalSuppressMessage("AOT", "IL3050", ...)]
private async Task<IReadOnlyList<SagaInstanceState>> querySagasAsync<TSaga>(
    Type sagaType, int count, CancellationToken ct) where TSaga : class
{
    var db = _client.GetDatabase(_databaseName);
    var collection = db.GetCollection<TSaga>(MongoConstants.SagaCollectionName(sagaType));
    var sagas = await collection
        .Find(FilterDefinition<TSaga>.Empty)
        .Limit(count)
        .ToListAsync(ct)
        .ConfigureAwait(false);

    var list = new List<SagaInstanceState>(sagas.Count);
    foreach (var saga in sagas)
    {
        // Identity extraction via driver class map (avoids convention-specific "Id" lookup).
        var idGetter = BsonClassMap.LookupClassMap(sagaType).IdMemberMap?.Getter;
        var id = idGetter?.Invoke(saga) ?? string.Empty;
        list.Add(buildInstance(sagaType, id, saga));
    }
    return list;
}
```

---

## 8. Key Differences from RavenDb

| Dimension | RavenDb | MongoDB |
|---|---|---|
| Session model | `IAsyncDocumentSession` (disposable per-call) | No session for reads; use `IMongoDatabase` / `IMongoCollection<T>` directly |
| Identity coercion | Always `identity.ToString()` (Raven stores all saga ids as strings) | Native type (Guid/int/long/string) — driver matches directly |
| Identity extraction from POCO | `session.Advanced.GetDocumentId` → fallback property/field reflection | `BsonClassMap.LookupClassMap(sagaType).IdMemberMap?.Getter(saga)` |
| Collection naming | Raven's implicit document-type collection | `MongoConstants.SagaCollectionName(sagaType)` = `"wolverine_saga_<lowercased>"` |
| List query | `session.Query<TSaga>().Take(n).ToListAsync(ct)` | `collection.Find(Empty).Limit(n).ToListAsync(ct)` |
| Provider locator | `providers.OfType<RavenDbPersistenceFrameProvider>()` | `providers.OfType<MongoDbPersistenceFrameProvider>()` |
| AOT annotations needed | `IL2060`, `IL3050`, `IL2075`, `IL2026`, `IL2070` | Same set — apply to `readSagaAsync`, `querySagasAsync`, `buildInstance` |

---

## 9. Files to Create / Modify in T2.1

| File | Change |
|---|---|
| `src/Wolverine.MongoDB/Internals/MongoDbSagaStoreDiagnostics.cs` | **New** — `internal sealed class MongoDbSagaStoreDiagnostics : ISagaStoreDiagnostics` mirroring `RavenDbSagaStoreDiagnostics` |
| `src/Wolverine.MongoDB/WolverineMongoDbExtensions.cs` | Add `options.Services.AddSingleton<ISagaStoreDiagnostics>(s => new MongoDbSagaStoreDiagnostics(...))` inside `UseMongoDbPersistence` |

No other library files need to change for T2.1.

---

## 10. Confirmation: Cosmos Does Not Implement This

Checked `external/wolverine/src/Persistence/Wolverine.CosmosDb/`:
- No `AddSingleton<ISagaStoreDiagnostics>` in `WolverineCosmosExtensions`.
- No `*SagaStoreDiagnostics.cs` file.

MongoDB implementing `ISagaStoreDiagnostics` (matching RavenDb, not Cosmos) is therefore
an **above-Cosmos** capability and a meaningful value-add for the upstream contribution.
