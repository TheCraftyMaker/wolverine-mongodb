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
- `Internals/MongoDbPersistenceFrameProvider.cs` — `IPersistenceFrameProvider` with full saga support
- `Internals/SagaFrames.cs` — saga codegen frames + `MongoSagaOperations` helpers
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

## Deliberate deltas vs Cosmos/RavenDb

These intentional improvements should be called out in the upstream PR description:

| Feature | Cosmos/RavenDb | MongoDB |
|---------|---------------|---------|
| **Saga id type** | String only | Native — `Guid`, `string`, `int`, `long` |
| **Optimistic concurrency** | Last-write-wins | `Saga.Version` guarded; throws `SagaConcurrencyException` on stale write |
| **Id mapping** | String stored as document/item key | Native BSON type via driver's Id-member convention; maps to `_id` |
| **Completion delete** | Unguarded ✓ | Unguarded ✓ (matches lightweight SQL provider) |

The OCC model matches Wolverine's own lightweight SQL provider (`DatabaseSagaSchema`) rather
than Cosmos/Raven — `SagaConcurrencyException` and `Saga.Version` are already part of the
framework; MongoDB is the first document-store provider to use them.

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

- **`ISagaStoreDiagnostics` not implemented.** RavenDb implements this; Cosmos does not.
  Deferred until the dashboard API stabilises — see `FOLLOWUPS.md`.
- **`LeadershipElectionCompliance` is compile-gated** behind `#if RUN_MULTINODE`.
  The 6.9.0 rework may make un-gating viable; see `FOLLOWUPS.md` for the re-evaluation
  criteria (5× consecutive green on net9.0 + net10.0).
- **No process-global BSON serializer registration.** The library does not mutate the host
  app's BSON registry — consistent with Wolverine's philosophy of not globally side-effecting
  a consumer's serialization setup. Saga types that need non-default serialization annotate
  their own members.
- **Testcontainers setup required.** The upstream test project needs the same
  Testcontainers MongoDB replica-set fixture this repo uses (`AppFixture.cs`).
