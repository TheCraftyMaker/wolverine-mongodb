# Wolverine.MongoDB

A native MongoDB message store for [Wolverine](https://wolverinefx.net)'s
transactional inbox/outbox. Wolverine ships first-class durability providers
for PostgreSQL and SQL Server, but none for MongoDB — and the EF Core + MongoDB
EF provider path does not work because Wolverine's outbox integration assumes a
relational ADO.NET connection and SQL-managed envelope tables. This package
implements `IMessageStore` directly against the MongoDB .NET driver, giving
MongoDB-backed applications reliable, durable message delivery without EF Core.

> **Status: pre-release (`0.1.0`).** Node coordination is still being hardened.
> The major version tracks Wolverine's major version (`6.x` ↔ `WolverineFx 6.x`).
> Not yet published to NuGet.

## Prerequisites

- **MongoDB running as a replica set.** Multi-document transactions — used when
  writing a domain change and outgoing envelopes atomically inside a handler —
  are not available on standalone MongoDB. Atlas and any production deployment
  already satisfy this; for local development use a Docker Compose replica set.
  Standalone MongoDB is explicitly unsupported.
- **Wolverine 6.x** (`WolverineFx 6.2.x`). The major version of this package
  must match the major version of `WolverineFx`.
- **.NET 9 or .NET 10.**
- **MongoDB.Driver** 3.x (2.x also supported).

## Quick start

```csharp
using Wolverine;
using Wolverine.MongoDB;
using MongoDB.Driver;

// connectionString must point at a replica set, e.g.:
// "mongodb://localhost:27017/?replicaSet=rs0"
var connectionString = "mongodb://localhost:27017/?replicaSet=rs0";

builder.UseWolverine(opts =>
{
    opts.Services.AddSingleton<IMongoClient>(new MongoClient(connectionString));
    opts.UseMongoDbPersistence("my_database");
});
```

`UseMongoDbPersistence` resolves `IMongoClient` from the DI container, so you
can configure the client however you like (Atlas connection string, custom
`MongoClientSettings`, etc.) before the call.

When a handler both modifies a MongoDB document and publishes outgoing messages,
the domain write and the outbox write are committed atomically inside a single
MongoDB multi-document transaction — this is why a replica set is required.

## How it works

The provider stores envelopes in dedicated collections
(`wolverine_incoming_envelopes`, `wolverine_outgoing_envelopes`,
`wolverine_dead_letters`) plus node-coordination collections
(`wolverine_nodes`, `wolverine_node_assignments`). Single-document atomic
operations (`findAndModify`) handle ownership claims and idempotency rather than
relying on multi-document transactions for the hot path — the approach proven in
the MassTransit MongoDB outbox.

Collections and indexes are created automatically when Wolverine starts.

## Building and testing

The compliance test suite currently requires a local clone of the Wolverine
source, because `WolverineFx.ComplianceTests` is not yet published to NuGet. The
clone is auto-detected via the `WolverineSourcePath` MSBuild property (default
`C:\source\external\wolverine`; override with the `WOLVERINE_SOURCE` environment
variable or `-p:WolverineSourcePath=...`). When the clone is present, both the
library and the test project project-reference it so there is a single
consistent `Wolverine.dll`.

When the clone is absent (for example in CI), the test project is not built and
the library uses the `WolverineFx` NuGet package. To pack the library locally,
pass `-p:UseWolverineSource=false` so the produced package declares the
`WolverineFx` dependency rather than a project reference:

```
dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false
```

The test suite runs against a real MongoDB replica set spun up via
[Testcontainers](https://dotnet.testcontainers.org/). No external setup is
required beyond Docker Desktop.

## Learn more

- [Wolverine durability guide](https://wolverinefx.net/guide/durability/)
- [MongoDB .NET driver transactions](https://www.mongodb.com/docs/drivers/csharp/current/fundamentals/transactions/)

## Known limitations

- **Standalone MongoDB is not supported** — a replica set is required for
  transactions.
- **Single-node coordination only in 0.1.0.** Multi-node agent balancing (having
  the `DurabilityAgent` distribute work across a cluster of application
  instances) and orphan-recovery hardening are deferred to a follow-up release.
  The lock-based leader election and heartbeat mechanism work correctly for a
  single running node.
- **Node coordination is approximate.** MongoDB has no advisory-lock primitive;
  leader election and agent assignment use a lock document with TTL-based expiry
  via `findAndModify`. This is the least mature part of the library pre-`1.0.0`.
- **Saga storage not yet supported.** Wolverine saga persistence requires
  provider-specific integration that is not yet implemented.
- **High-throughput contention.** The `findAndModify` lock approach serializes
  access per document; under very high concurrency this can bottleneck. Tune
  write concern and indexes accordingly.

## License

[MIT](LICENSE)
