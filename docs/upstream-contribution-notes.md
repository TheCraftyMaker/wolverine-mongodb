# Upstream Contribution Notes

Notes for contributing `Wolverine.MongoDB` as a first-party persistence provider to the
[Wolverine](https://github.com/JasperFx/wolverine) repository.

## Target location

Upstream organises persistence providers under `src/Persistence/`. MongoDB would sit at:

```
src/Persistence/Wolverine.MongoDb/     ← package (note: upstream convention is MongoDb, not MongoDB)
src/Persistence/MongoDbTests/          ← integration tests
```

**Namespace:** `Wolverine.MongoDb` (matching `Wolverine.CosmosDb` / `Wolverine.RavenDb`).  
**NuGet id:** `WolverineFx.MongoDB` (or `Wolverine.MongoDb` by analogy with other providers).

## What to contribute

### Core library

All of `src/Wolverine.MongoDB/` maps cleanly to the upstream provider shape:

- `WolverineMongoDbExtensions.cs` — `UseMongoDbPersistence` registration
- `MongoDbPersistenceOptions.cs` — `LockLeaseDuration` tuning
- `MongoDbUnitOfWork.cs` — session-bound write helper
- `Internals/MongoDbMessageStore.*.cs` — `IMessageStore` partial implementation
- `Internals/MongoDbPersistenceFrameProvider.cs` — `IPersistenceFrameProvider` with full saga *and* generic entity support (`CanPersist` unconditional `true`; write/load frame factories branch saga-vs-entity)
- `Internals/SagaFrames.cs` — saga codegen frames + `MongoSagaOperations` helpers
- `Internals/EntityFrames.cs` — generic entity codegen frames (`[Entity]`/`Insert`/`Update`/`Store`/`Delete<T>`/`IStorageAction<T>`) + `MongoEntityOperations` helpers
- `Internals/MongoDbSagaStoreDiagnostics.cs` — `ISagaStoreDiagnostics` implementation
- `Internals/TransactionalFrame.cs` — handler-wrapping transaction frame

### Compliance subclasses to add

Add `MongoDbSagaHost : ISagaHost` and four compliance subclasses (mirroring `CosmosDbTests`):

```csharp
public class string_saga_storage_compliance : StringIdentifiedSagaComplianceSpecs<MongoDbSagaHost> { }
public class guid_saga_storage_compliance   : GuidIdentifiedSagaComplianceSpecs<MongoDbSagaHost>   { }
public class int_saga_storage_compliance    : IntIdentifiedSagaComplianceSpecs<MongoDbSagaHost>    { }
public class long_saga_storage_compliance   : LongIdentifiedSagaComplianceSpecs<MongoDbSagaHost>   { }
```

All four pass on net9.0 and net10.0 with the current implementation (27 compliance facts).

Also add `storage_action_compliance : StorageActionCompliance` (`src/Wolverine.MongoDB.Tests/storage_action_compliance.cs`)
— the upstream oracle for generic `[Entity]`/`Insert`/`Update`/`Store`/`Delete<T>`/`IStorageAction<T>`
persistence. Unlike the saga specs, `StorageActionCompliance` is not generic over a host type: it's
configured directly via `configureWolverine`/`Load`/`Persist` overrides, targeting the same
`MongoConstants.EntityCollectionName(typeof(Todo))` (`"todo"`) collection the generated frames write
to. RavenDb, Marten, EF Core, and Polecat all subclass it; Cosmos does not (implemented, but without
compliance-test coverage) — RavenDb remains the closest tested template either way. All facts pass on
net9.0 and net10.0 with the current implementation.

`MongoDbSagaHost` mirrors `CosmosDbSagaHost`: `DurabilityMode.Solo`, `TypeLoadMode.Dynamic`,
`GeneratedCodeOutputPath`, `UseMongoDbPersistence`. `LoadState<T>(id)` reads the saga
directly from the saga's MongoDB collection by `_id` — independent of Wolverine message dispatch.

> **Note on `TypeLoadMode.Dynamic` (not `Auto`):** saga compliance hosts must use
> `TypeLoadMode.Dynamic`. Under `Auto`, the message-keyed generated handler type name is shared
> across String/Guid/int/long workflows — the first-compiled handler is reused in-memory for
> every later host, so a non-string spec would load the string workflow saga. This matches
> Wolverine's own `SqlServerSagaHost`, which also uses `Dynamic`.

The test project also needs:
- A `[ModuleInitializer]` that registers `GuidRepresentation.Standard` process-wide so
  un-annotated Guid `_id` members (`GuidBasicWorkflow.Id`) round-trip under MongoDB.Driver 3.x.
- A Testcontainers MongoDB replica-set fixture (reuse `AppFixture.cs`).

### Message store compliance

Add `MongoDbMessageStoreCompliance : MessageStoreCompliance` (analogous to `CosmosDbMessageStoreCompliance`). This repo's `src/Wolverine.MongoDB.Tests/` contains the full compliance harness.

### Saga store diagnostics

Add `MongoDbSagaStoreDiagnostics` (mirroring `RavenDbSagaStoreDiagnostics`) and register it
unconditionally in `UseMongoDbPersistence`, matching RavenDb's registration — Cosmos does not
implement `ISagaStoreDiagnostics` at all. There is no unified upstream compliance spec for this
interface (each provider has its own integration test); bring `saga_store_diagnostics.cs`
(`src/Wolverine.MongoDB.Tests/`) as the functional coverage, mirroring
`raven_saga_store_diagnostics_tests.cs`.

**Upstream-only simplification available:** this repo's implementation reaches three
Wolverine-internal members (`SagaDescriptorBuilder.Build`, `WolverineOptions.HandlerGraph`,
`HandlerGraph.Container`) via cached, non-throwing reflection, because as an *external* package
it isn't on Wolverine's `[InternalsVisibleTo]` list — unlike RavenDb/Marten/EF Core/RDBMS, which
call these members directly. Each reflective bridge in `MongoDbSagaStoreDiagnostics` carries a
`// TODO(upstream)` marker. When contributed into the Wolverine repo, add `Wolverine.MongoDB` to
`[InternalsVisibleTo]` (alongside `Wolverine.RavenDb`) and collapse each bridge to the direct
one-liner every sibling provider uses.

## Deliberate deltas vs Cosmos/RavenDb

These intentional improvements should be called out in the upstream PR description:

| Feature | Cosmos/RavenDb | MongoDB |
|---------|---------------|---------|
| **Saga id type** | String only | Native — `Guid`, `string`, `int`, `long` |
| **Optimistic concurrency (sagas)** | Last-write-wins | `Saga.Version` guarded; throws `SagaConcurrencyException` on stale write |
| **Id mapping (sagas)** | String stored as document/item key | Native BSON type via driver's Id-member convention; maps to `_id` |
| **Completion delete** | Unguarded ✓ | Unguarded ✓ (matches lightweight SQL provider) |
| **Entity id extraction** | Cosmos: `entity.ToString()` coercion | `BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap` getter — the driver's own Id-member convention, generic across id types |
| **`ISagaStoreDiagnostics`** | Cosmos: not implemented; RavenDb: implemented | Implemented (via reflective bridges pending `[InternalsVisibleTo]`, see above) |

The saga OCC model matches Wolverine's own lightweight SQL provider (`DatabaseSagaSchema`) rather
than Cosmos/Raven — `SagaConcurrencyException` and `Saga.Version` are already part of the
framework; MongoDB is the first document-store provider to use them. Generic entity persistence
(`[Entity]`/`Insert`/`Update`/`Store`/`Delete<T>`) otherwise mirrors Cosmos closely — unconditional
`CanPersist`, upsert-for-all-writes semantics, no entity-level OCC — with the id-extraction
mechanism as the one deliberate delta.

## Cross-node retry policy (Balanced mode)

Under `DurabilityMode.Balanced`, concurrent saga updates produce either:
- A MongoDB `WriteConflict` with the `"TransientTransactionError"` error label (server-side
  transaction abort, most common), or
- A `SagaConcurrencyException` from the version guard (rarer — committed before the loser's
  replace ran).

Both are guaranteed no-commit aborts and safe to retry. The recommended policy:

```csharp
opts.Policies
    .OnException<SagaConcurrencyException>()
    .Or<MongoException>(e => e.HasErrorLabel("TransientTransactionError"))
    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
```

This should be documented in the provider README and ideally registered by default in
`UseMongoDbPersistence` (behind a flag or opt-in).

## Known gaps vs full upstream readiness

- **`ISagaStoreDiagnostics` reflection bridges are external-package-only.** The interface itself
  is implemented and registered (see Saga store diagnostics above); the gap is that three
  Wolverine-internal members are reached via reflection rather than direct calls, because this
  package isn't on `[InternalsVisibleTo]`. Resolved automatically once contributed upstream and
  added to that list — no design change needed, just a mechanical simplification.
- **No process-global BSON serializer registration.** The library does not mutate the host
  app's BSON registry — consistent with Wolverine's philosophy of not globally side-effecting
  a consumer's serialization setup. Saga and entity types that need non-default serialization
  annotate their own members.
- **Testcontainers setup required.** The upstream test project needs the same
  Testcontainers MongoDB replica-set fixture this repo uses (`AppFixture.cs`).
- **Four RDBMS/Marten-only parity capabilities are documented non-goals**, not gaps: multi-tenancy,
  durable listeners, query-spec frames, and soft-delete are all deliberately deferred, matching
  Cosmos and RavenDb — see `CLAUDE.md` ("Parity Capabilities — Non-Goals") for the per-capability
  rationale. These should be called out as intentional scope in the upstream PR description, not
  fixed before contributing.

### Resolved since the previous revision of this document

- ~~`ISagaStoreDiagnostics` not implemented~~ — implemented (T2.1/T2.2); see Saga store
  diagnostics above.
- ~~`LeadershipElectionCompliance` is compile-gated behind `#if RUN_MULTINODE`~~ — un-gated
  (T4.5) after WolverineFx 6.9.0 reworked the underlying facts around the "any healthy node
  leads" model this provider already implements. Verified 5× consecutive green on net9.0 and
  net10.0 (10/10 runs, 17/17 facts each); the suite now runs unconditionally as part of CI's
  multinode step.
- ~~Generic entity persistence (`[Entity]`/`Insert`/`Update`/`Store`/`Delete<T>`) not
  implemented~~ — implemented (T1.1–T1.3); see the Compliance subclasses and Deliberate deltas
  sections above.
