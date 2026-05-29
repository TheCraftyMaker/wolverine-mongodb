# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The major version tracks Wolverine's major version.

## [Unreleased]

### Added
- Project scaffolding and DevSwarm parallel-workspace configuration.
- Repository setup: README, MIT license, contributing guide, and NuGet publish
  workflow. (Pre-existed from the config workspace and was reconciled here.)
- Central build configuration (`Directory.Build.props`, `Directory.Packages.props`)
  and the `Wolverine.MongoDB.sln` solution.
- Library project `Wolverine.MongoDB` (net9.0;net10.0) and test project
  `Wolverine.MongoDB.Tests` (net9.0).
- CI workflow (`ci.yml`); reconciled the NuGet publish workflow to pack only the
  library project.

### Notes
- The compliance test suite uses a local Wolverine source clone until
  `WolverineFx.ComplianceTests` is published to NuGet.

[Unreleased]: https://github.com/TheCraftyMaker/wolverine-mongodb/commits/main
