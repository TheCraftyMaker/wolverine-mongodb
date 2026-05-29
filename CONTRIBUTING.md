# Contributing

Thanks for your interest in `Wolverine.MongoDB`! Contributions of all sizes are
welcome — bug reports, docs fixes, tests, and features.

## Getting started

1. Fork and clone the repo.
2. Restore dependencies: `dotnet restore`.
3. Build: `dotnet build`.

## Running tests

Tests run against a **real MongoDB replica set** — in-memory mocking misses the
concurrency behaviour this library depends on. The quickest local setup is a
Docker Compose single-node replica set:

```bash
docker compose up -d   # see docker-compose.yml for the replica-set config
dotnet test
```

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
