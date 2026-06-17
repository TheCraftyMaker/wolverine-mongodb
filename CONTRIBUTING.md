# Contributing

Thanks for your interest in `Wolverine.MongoDB`! Contributions of all sizes are
welcome — bug reports, docs fixes, tests, and features.

## Getting started

1. Fork and clone the repo **with submodules**: `git clone --recursive <url>`
   (already cloned? run `git submodule update --init`).
2. Restore dependencies: `dotnet restore`.
3. Build: `dotnet build`.

The Wolverine source lives at a fixed in-repo path, `external/wolverine`, as a git submodule
pinned to the matching `WolverineFx` version (see [Running tests](#running-tests) for why).
Because the path is the same for everyone, `Wolverine.MongoDB.slnx` lists those source projects
directly, so IDEs (e.g. Rider) resolve them instead of flagging missing dependencies — no
per-machine configuration needed.

## Running tests

Tests project-reference the Wolverine source at `external/wolverine` because
`WolverineFx.ComplianceTests` is not yet published to NuGet — so the submodule
must be initialised (`git submodule update --init`). They also require **Docker**
— Testcontainers starts a MongoDB replica set for the run.

Tests run against a **real MongoDB replica set** — in-memory mocking misses the
concurrency behaviour this library depends on. The quickest local setup is a
Docker Compose single-node replica set:

```bash
docker compose up -d   # see docker-compose.yml for the replica-set config
dotnet test
```

## Design notes

### Domain-write atomicity

Wolverine opens an `IClientSessionHandle` for each handler and persists the
inbox/outbox envelopes on that session inside a multi-document transaction. The
"domain write + outbox write in one transaction" guarantee therefore only holds
when the handler enlists its own MongoDB writes in that same session — it must
accept the generated `IClientSessionHandle` and pass it to every MongoDB write
(`collection.InsertOneAsync(session, doc)`). The `IMongoDatabase` registered by
`UseMongoDbPersistence` does **not** auto-enlist; a write that omits the session
runs outside the transaction and is not atomic with the outbox. A session-bound
write helper to make this automatic is a planned follow-up enhancement.

## Pull requests

- Keep changes focused; one logical change per PR.
- Add or update tests for behaviour changes.
- Update `CHANGELOG.md` under `## [Unreleased]`.
- Make sure `dotnet build` and `dotnet test` pass before opening the PR.

## Questions and discussion

This project exists because Wolverine has no MongoDB durability provider. For
broader Wolverine questions, the [Wolverine Discord](https://discord.gg/WMxrvegf8H)
is the best place to reach other users and the maintainers.

## License

By contributing, you agree that your contributions will be licensed under the
[MIT License](LICENSE).
