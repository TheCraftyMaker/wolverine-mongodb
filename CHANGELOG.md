# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Release agent (`.claude/agents/release.md`) that proposes the next version, prepares a CHANGELOG + version-bump PR, then tags and verifies the publish.
- `publish.yml` now creates a GitHub Release from the tagged version's CHANGELOG section.

### Changed
- Version + CHANGELOG are now bumped on `main` before tagging; the post-publish auto-bump PR has been removed.

## [0.1.0-beta.5] - 2026-05-01

- Latest version published to NuGet.org prior to release-automation work.
  (Earlier beta history is summarized; see git tags `v0.1.0-beta.1`..`v0.1.0-beta.5`.)
