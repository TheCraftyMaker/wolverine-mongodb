# Release Agent & GitHub Release Automation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every release produce a GitHub Release with curated notes, driven by a Claude Code release agent that proposes the version, gates on human review of a CHANGELOG/version-bump PR, then tags, monitors, and verifies the publish.

**Architecture:** Bump version + CHANGELOG on `main` *before* tagging (so the tagged commit carries the notes the workflow reads). `publish.yml` gains a step that extracts the tagged version's section from `CHANGELOG.md` and creates a GitHub Release; the old post-tag auto-bump PR steps are removed. A `.claude/agents/release.md` agent orchestrates the human-gated flow. The **git tag is the version source of truth** (`Directory.Build.props` has drifted stale — currently `beta.2` while the latest tag is `beta.5`).

**Tech Stack:** GitHub Actions, `gh` CLI, bash/awk, Keep a Changelog, Claude Code subagents.

---

## File Structure

- **Create** `CHANGELOG.md` — human-maintained release notes (Keep a Changelog format).
- **Create** `.github/scripts/extract-changelog.sh` — extracts one version's section; the only real logic, independently testable.
- **Modify** `.github/workflows/publish.yml` — add GitHub Release step, remove auto-bump PR steps.
- **Create** `.claude/agents/release.md` — the release agent (orchestration prose).
- **Modify** `CLAUDE.md` — update the "Versioning & Release" section to the new flow.

Note: `docs/superpowers/*` is gitignored, so this plan and the spec are local-only. The deliverables above are all tracked files.

---

## Task 1: Create the extraction script and test it

The extraction script is pure logic, so it goes first and gets a real test.

**Files:**
- Create: `.github/scripts/extract-changelog.sh`
- Test (local, manual): a temp sample CHANGELOG

- [ ] **Step 1: Write the script**

Create `.github/scripts/extract-changelog.sh`:

```bash
#!/usr/bin/env bash
# Print the CHANGELOG.md section for a given version.
# Usage: extract-changelog.sh <version> [changelog-path]
# Exits non-zero (and prints nothing to stdout) if the section is missing/empty.
set -euo pipefail

version="${1:?usage: extract-changelog.sh <version> [changelog-path]}"
changelog="${2:-CHANGELOG.md}"

section="$(awk -v ver="$version" '
  /^## \[/ {
    header = $0
    sub(/^## \[/, "", header)
    sub(/\].*/, "", header)
    if (capture && header != ver) exit
    if (header == ver) { capture = 1; next }
  }
  capture { print }
' "$changelog")"

# Fail loudly if the section has no non-whitespace content.
if [ -z "${section//[$'\t\r\n ']/}" ]; then
  echo "::error::No CHANGELOG.md section found for version '$version'" >&2
  exit 1
fi

printf '%s\n' "$section"
```

- [ ] **Step 2: Create a sample CHANGELOG and verify a happy-path extraction**

Run:

```bash
cd C:/source/personal/wolverine-mongodb
cat > /tmp/sample-changelog.md <<'EOF'
# Changelog

## [Unreleased]

### Added
- work in progress

## [0.1.0-beta.6] - 2026-06-15

### Added
- GitHub Release automation

### Fixed
- stale version bump

## [0.1.0-beta.5] - 2026-05-01

### Fixed
- something older
EOF
bash .github/scripts/extract-changelog.sh "0.1.0-beta.6" /tmp/sample-changelog.md
```

Expected output (exactly the beta.6 body, nothing from beta.5 or Unreleased):

```
### Added
- GitHub Release automation

### Fixed
- stale version bump
```

- [ ] **Step 3: Verify the missing-version case fails**

Run:

```bash
cd C:/source/personal/wolverine-mongodb
bash .github/scripts/extract-changelog.sh "9.9.9" /tmp/sample-changelog.md; echo "exit=$?"
```

Expected: prints `::error::No CHANGELOG.md section found for version '9.9.9'` to stderr and `exit=1`.

- [ ] **Step 4: Verify the Unreleased section is extractable too** (used by the agent for previews)

Run:

```bash
cd C:/source/personal/wolverine-mongodb
bash .github/scripts/extract-changelog.sh "Unreleased" /tmp/sample-changelog.md
```

Expected: prints the Unreleased body (`### Added` / `- work in progress`).

- [ ] **Step 5: Commit**

```bash
cd C:/source/personal/wolverine-mongodb
git add .github/scripts/extract-changelog.sh
git commit -m "feat: add CHANGELOG section extraction script for releases"
```

---

## Task 2: Create CHANGELOG.md

**Files:**
- Create: `CHANGELOG.md`

- [ ] **Step 1: Write the seed CHANGELOG**

Create `CHANGELOG.md`. Seed `Unreleased` with the work in flight (the release automation itself) and backfill the most recent shipped version for shape. Older versions are intentionally summarized — we don't reconstruct full history.

```markdown
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
```

- [ ] **Step 2: Verify the real extraction script works against the real file**

Run:

```bash
cd C:/source/personal/wolverine-mongodb
bash .github/scripts/extract-changelog.sh "0.1.0-beta.5" CHANGELOG.md
```

Expected: prints the beta.5 bullet block, exit 0.

- [ ] **Step 3: Commit**

```bash
cd C:/source/personal/wolverine-mongodb
git add CHANGELOG.md
git commit -m "docs: add CHANGELOG.md (Keep a Changelog format)"
```

---

## Task 3: Update publish.yml — add GitHub Release, remove auto-bump

**Files:**
- Modify: `.github/workflows/publish.yml`

- [ ] **Step 1: Replace the auto-bump steps with a GitHub Release step**

In `.github/workflows/publish.yml`, delete these three steps (lines 27–37 region):

```yaml
      - name: Bump version in Directory.Build.props
        run: |
          sed -i "s|<Version>.*</Version>|<Version>${{ steps.version.outputs.VERSION }}</Version>|" Directory.Build.props
      - name: Create version bump PR
        uses: peter-evans/create-pull-request@v8
        with:
          base: main
          commit-message: "chore: bump version to ${{ steps.version.outputs.VERSION }}"
          branch: version-bump/${{ steps.version.outputs.VERSION }}
          title: "chore: bump version to ${{ steps.version.outputs.VERSION }}"
          body: "Auto-generated after publishing `${{ steps.version.outputs.VERSION }}` to NuGet."
          labels: automated
```

Replace them with:

```yaml
      - name: Create GitHub Release
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          notes="$(bash .github/scripts/extract-changelog.sh "${{ steps.version.outputs.VERSION }}")"
          prerelease=""
          case "${{ steps.version.outputs.VERSION }}" in
            *-*) prerelease="--prerelease" ;;
          esac
          gh release create "${GITHUB_REF_NAME}" \
            ./artifacts/*.nupkg \
            --title "${GITHUB_REF_NAME}" \
            --notes "$notes" \
            $prerelease
```

Notes on this step:
- `bash .github/scripts/extract-changelog.sh` avoids depending on the file's exec bit after checkout.
- A version containing `-` (e.g. `0.1.0-beta.6`) is a SemVer prerelease → mark the GitHub Release as prerelease.
- Attaching `./artifacts/*.nupkg` puts the built package on the Release page as a download. The `artifacts/` dir was produced by the earlier `Pack` step.
- The job already declares `permissions: contents: write`, which `gh release create` requires. `pull-requests: write` is now unused but harmless — leave it for now.

- [ ] **Step 2: Sanity-check the YAML parses**

Run:

```bash
cd C:/source/personal/wolverine-mongodb
python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/publish.yml')); print('YAML OK')"
```

Expected: `YAML OK`. (If `python` is unavailable, use `pwsh -c "ConvertFrom-Yaml"` is not built-in; instead visually confirm indentation matches the surrounding steps.)

- [ ] **Step 3: Commit**

```bash
cd C:/source/personal/wolverine-mongodb
git add .github/workflows/publish.yml
git commit -m "feat: create GitHub Release from CHANGELOG on publish; drop auto-bump PR"
```

---

## Task 4: Create the release agent

**Files:**
- Create: `.claude/agents/release.md`

- [ ] **Step 1: Write the agent definition**

Create `.claude/agents/release.md`:

```markdown
---
name: release
description: Cuts a release of Wolverine.MongoDB. Invoke with intent like "cut the next beta", "release a patch", or "release 0.1.0-beta.6". Proposes the next version (waits for approval), prepares a CHANGELOG + version-bump PR (waits for you to merge), then tags, monitors the publish workflow, and verifies NuGet + the GitHub Release.
tools: Bash, Read, Edit, Glob, Grep
---

You are the release manager for the Wolverine.MongoDB library. You drive a release
end to end with exactly two human gates. Be precise; never fabricate success.

## Source of truth

The **latest git tag** is the authoritative current version, NOT
`Directory.Build.props` (which has drifted in the past). Always derive the next
version from `git tag --sort=-v:refname | head -1`.

## Flow

### 1. Inspect
- `git fetch --tags origin`
- Latest tag: `git tag --sort=-v:refname | head -1` (e.g. `v0.1.0-beta.5`).
- Commits since: `git log <latest-tag>..origin/main --oneline`.
- Read the `## [Unreleased]` section of `CHANGELOG.md`.

### 2. Propose version — GATE 1
- From the user's intent + the latest tag, compute the next SemVer:
  - "next beta" / "another beta": bump the prerelease counter (`beta.5` → `beta.6`).
  - "patch" / "minor" / "major": bump that component, drop the prerelease.
  - explicit version given: use it verbatim (strip a leading `v`).
- Present the proposed version AND the draft release notes (the current Unreleased
  bullets). **Stop and wait for the user's explicit approval.** Do not proceed
  until they confirm.

### 3. Pre-flight checks (abort before mutating anything)
- On `main`: `git rev-parse --abbrev-ref HEAD` → must be `main`.
- Clean tree: `git status --porcelain` → must be empty.
- Up to date: `git rev-parse HEAD` == `git rev-parse origin/main` (after fetch).
- CI green on main: `gh run list --branch main --limit 1 --json conclusion,status`
  → latest run `status=completed` and `conclusion=success`.
- If any check fails, STOP and report exactly which one and why. Make no changes.

### 4. Prepare the release PR — GATE 2
- Branch: `git checkout -b release/v<version>`.
- Edit `CHANGELOG.md`: rename `## [Unreleased]` to `## [<version>] - <YYYY-MM-DD>`
  (use the real current date) and insert a fresh empty `## [Unreleased]` above it.
- Edit `Directory.Build.props`: set `<Version><version></Version>`.
- Verify the notes will extract:
  `bash .github/scripts/extract-changelog.sh "<version>" CHANGELOG.md` → non-empty, exit 0.
- Commit, push, open PR:
  `gh pr create --base main --title "chore: release v<version>" --body "<the extracted notes>"`.
- Notify the user with the PR URL and tell them you are waiting for them to review
  and merge it.
- **Poll** until merged:
  `gh pr view <number> --json state,mergedAt` every ~30s.
  - merged (`state == MERGED`) → continue to step 5.
  - closed unmerged (`state == CLOSED`) → abort: report that the release was
    cancelled; leave the branch for inspection.

### 5. Tag & push
- `git fetch origin main && git checkout main && git pull --ff-only`.
- Tag the merge commit: `git tag v<version> origin/main`.
- `git push origin v<version>`. This triggers `publish.yml`.

### 6. Monitor
- Find the run: `gh run list --workflow publish.yml --limit 1 --json databaseId,status`.
- `gh run watch <databaseId>` until it completes.

### 7. Confirm & report — claim success ONLY if all three pass
- (a) Workflow run `conclusion == success`.
- (b) Package is live: poll
  `https://api.nuget.org/v3-flatcontainer/wolverine.mongodb/index.json` (via curl)
  until `<version>` appears (NuGet indexing can lag a few minutes; retry briefly).
- (c) GitHub Release exists: `gh release view v<version>`.
- Report a summary with: the version, the NuGet URL, the GitHub Release URL, and
  the workflow run URL.
- If the workflow FAILED: report the failed run and its logs
  (`gh run view <id> --log-failed`). Do NOT re-tag or delete the tag automatically.
  Tell the user that NuGet push uses `--skip-duplicate`, so once the underlying
  problem is fixed, re-running the workflow (or re-pushing the tag after deleting
  it) is safe.

## Hard rules
- Never skip a gate. Version approval and PR merge are both the user's decisions.
- Never claim the release succeeded without verifying (a), (b), and (c).
- If a pre-flight check fails, change nothing.
```

- [ ] **Step 2: Verify the agent file is discovered**

Run:

```bash
cd C:/source/personal/wolverine-mongodb
test -f .claude/agents/release.md && head -5 .claude/agents/release.md
```

Expected: prints the YAML frontmatter (`---`, `name: release`, ...). (Discovery in the running session may require a restart; file presence + valid frontmatter is what we verify here.)

- [ ] **Step 3: Commit**

```bash
cd C:/source/personal/wolverine-mongodb
git add .claude/agents/release.md
git commit -m "feat: add release agent for end-to-end version releases"
```

---

## Task 5: Update CLAUDE.md release docs

**Files:**
- Modify: `CLAUDE.md` (the "Versioning & Release" section)

- [ ] **Step 1: Rewrite the release flow section**

In `CLAUDE.md`, replace the existing **Versioning & Release** section's "Release flow" and warning with:

```markdown
**Release flow (via the `release` agent):**
1. Invoke the release agent with intent, e.g. "cut the next beta" or "release 0.1.0-beta.6".
2. Approve the proposed version (gate 1).
3. Review and merge the CHANGELOG + version-bump PR it opens (gate 2).
4. The agent tags `main`, the `publish.yml` workflow packs + pushes to NuGet and
   creates the GitHub Release from the CHANGELOG section, and the agent verifies
   NuGet + the GitHub Release before reporting.

The **git tag is the version source of truth** for the pack. `Directory.Build.props`
is bumped in the gate-2 PR *before* tagging, so the tagged commit already matches —
there is no post-publish auto-bump PR.

Day-to-day, add notes under `## [Unreleased]` in `CHANGELOG.md` as you merge work.

⚠️ **Always tag a commit on main** — the workflow runs from the tagged commit, so
that commit must contain the latest workflow file and the matching CHANGELOG section.
```

Also update the bullet near the top of that section that currently says the auto-bump PR keeps `Directory.Build.props` in sync — change it to note the version is set in the gate-2 PR before tagging.

- [ ] **Step 2: Verify no stale references to the auto-bump PR remain**

Run:

```bash
cd C:/source/personal/wolverine-mongodb
grep -ni "auto-bump\|auto-created\|version-bump/" CLAUDE.md || echo "no stale references"
```

Expected: `no stale references` (or only references that are part of the new, accurate description).

- [ ] **Step 3: Commit**

```bash
cd C:/source/personal/wolverine-mongodb
git add CLAUDE.md
git commit -m "docs: update release flow for release agent + GitHub Releases"
```

---

## Post-implementation notes

- **PR #54** (`base: main` fix for the auto-bump PR) becomes moot — Task 3 deletes
  the step it patched. Close #54 referencing this work, or let this branch's PR
  supersede it.
- **First real release after merge** will be `0.1.0-beta.6`, which also corrects
  the stale `Directory.Build.props` (`beta.2` → `beta.6`) via the gate-2 PR.
- The `pull-requests: write` permission in `publish.yml` is now unused; left in
  place intentionally and can be pruned later.
```
