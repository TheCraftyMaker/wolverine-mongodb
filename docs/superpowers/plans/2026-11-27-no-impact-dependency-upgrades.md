# No-Impact Dependency Upgrades Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the open dependabot PRs that need no code change, and close the PRs that cannot or need not land.

**Architecture:** This plan changes no source code. Every change is a package pin in `Directory.Packages.props` or
`demo/Directory.Packages.props`, already open as a green dependabot PR. The work is to merge those PRs one at a time,
close three PRs, add one CHANGELOG line, and prove that `main` stays green. The library keeps WolverineFx 6.38.0.
The WolverineFx 6.65.1 upgrade is a separate plan (`2026-11-27-wolverine-6.65.1-upgrade.md`), which starts after
this plan is complete.

**Tech Stack:** GitHub CLI (`gh`), GitHub Actions CI (`library` and `demo` jobs), dependabot.

**Spec:** the impact analysis of 2026-11-27, summarised in "Verified facts" below.

## Global Constraints

- Merging and closing PRs are outward-facing actions. Get the user's explicit go-ahead in chat before you start,
  and again before any action that this plan does not list.
- Merge method: squash. This keeps the `Bump ... (#NNN)` subject that `main` already uses.
- Merge one PR at a time. Before each merge, read the PR's check state with
  `gh pr view <n> --json statusCheckRollup` (not the `rtk`-summarised `gh pr checks --watch` tail).
- Do not merge a PR whose `library` or `demo` check is not `SUCCESS` on its current head commit.
- Do not change any `.cs` file. If a PR needs a code change to go green, stop and move it to the WolverineFx plan.
- Do not touch the root `WolverineFx`, `WolverineFx.ComplianceTests`, `WolverineFx.Http`, `xunit.v3` or
  `GitHubActionsTestLogger` pins. The WolverineFx plan owns them.
- Branch protection on `main` is off, so GitHub does not force a rebase between merges. Dependabot rebases a PR
  by itself only when a merge makes it conflict.

## Review Focus

- **Two PRs change the same line.** #302 and #303 both set `WolverineFx` in `demo/Directory.Packages.props`.
  After #302 merges, #303 conflicts. Expect dependabot to rebase #303 so that it changes only
  `WolverineFx.RuntimeCompilation`. Read the rebased diff before you merge it.
- **Each PR is green on its own, not with the others.** Each PR ran CI against the `main` of its own day. The
  combined state is proven only by the `main` CI run after the last merge (Task 4).
- **The driver bump moves the package's dependency floor.** `MongoDB.Driver` 3.14.1 becomes the minimum version
  that the `Wolverine.MongoDB` nupkg declares. This is a consumer-visible change, so it needs a CHANGELOG line.
- **A stale CI result.** #299 failed once because the runner lost contact. The re-run on 2026-11-27 is green. If a
  rebase starts a new run, wait for the new result. Do not trust the old one.
- **The demo runs the library on a newer Wolverine than the library builds against.** After #302/#303 the demo uses
  WolverineFx 6.64.2 with a nupkg built against 6.38.0. CI on both PRs proves the demo flows on that combination.
  This is expected, because the nupkg declares `WolverineFx >= 6.38.0`.

---

## Verified facts (2026-11-27)

| PR | Change | Files | CI on current head | Decision |
|---|---|---|---|---|
| #300 | Testcontainers.MongoDb 4.15.0 → 4.17.0 | root + demo props | green | merge (Task 2) |
| #292 | Microsoft.NET.Test.Sdk 18.10.1 → 18.12.0 | root + demo props | green | merge (Task 2) |
| #272 | xunit.runner.visualstudio 4.0.0 → 4.0.1 | root + demo props | green | merge (Task 2) |
| #294 | Microsoft.AspNetCore.OpenApi 9.0.20 → 9.0.23 | demo props | green | merge (Task 2) |
| #295 | Scalar.AspNetCore 2.17.6 → 2.21.0 | demo props | green | merge (Task 2) |
| #302 | demo WolverineFx + WolverineFx.RabbitMQ 6.39.1 → 6.64.2 | demo props | green | merge (Task 3) |
| #303 | demo WolverineFx + WolverineFx.RuntimeCompilation 6.39.1 → 6.64.2 | demo props | green | merge after rebase (Task 3) |
| #299 | MongoDB.Driver 3.12.0 → 3.14.1 | root + demo props | green (re-run) | merge + CHANGELOG (Task 3) |
| #259 | MongoDB.Driver 3.11.0 → 3.12.0 | none (empty diff) | green | close: #264 already shipped it (Task 1) |
| #261 | xunit.v3 3.2.2 → 4.0.1 | root props | red | close: `dependabot.yml` ignores it, and the WolverineFx plan takes 4.0.2 (Task 1) |
| #254 | GitHubActionsTestLogger 2.4.1 → 3.0.5 | root props | red | close: Wolverine still pins 2.4.1 at V6.65.1 (Task 1) |

MongoDB.Driver 3.13.0 to 3.14.1 fix CVEs. Their breaking changes (redacted `ToString()` on `MongoUrl`,
`MongoServerAddress` and `MongoCredential`, stricter discriminator deserialization, removed `*Preview` string APIs)
touch no API that `src/` or `demo/src/` calls. The library compiles against 3.14.1.

## Task overview

| # | Task | Depends on | Parallel with | Model |
|---|---|---|---|---|
| 1 | Close #259, #261, #254 | none | 2, 3 | haiku |
| 2 | Merge the tooling and demo-only PRs | none | 1 | sonnet |
| 3 | Merge the Wolverine demo PRs and the driver PR | 2 (merges are serial) | 1 | sonnet |
| 4 | Verify `main` and hand over to the WolverineFx plan | 1, 2, 3 | none | haiku |

The Status table at the end of this plan tracks progress. Every task updates its own row when it finishes.

Tasks 2 and 3 cannot run in parallel. They merge into the same two files on `main`, and each merge can make the
next PR conflict. One agent runs them in order. Task 1 only closes PRs, so it can run at any time.

---

### Task 1: Close the PRs that cannot or need not land

**Files:** none.

- [x] **Step 1: Confirm each PR is still in the state the table shows**

Run: `gh pr view <n> --json state,files,statusCheckRollup` for 259, 261, 254.
Expected: #259 is open with no files. #261 and #254 are open with a failed `library` check.

- [x] **Step 2: Close each PR with a reason**

```powershell
gh pr close 259 --comment "Superseded: #264 already moved MongoDB.Driver to 3.12.0, so this PR has no diff."
gh pr close 261 --comment ("Blocked by the xunit.v3 major-version ignore in dependabot.yml. The WolverineFx " + `
  "6.65.1 upgrade moves the test host to xunit.v3 4.0.2 on Microsoft.Testing.Platform 2.x.")
gh pr close 254 --comment ("Blocked: Wolverine pins GitHubActionsTestLogger 2.4.1 up to V6.65.1, and 3.x " + `
  "needs Microsoft.Testing.Platform 2.0 next to a matching xunit.v3.")
```

Expected: `gh pr list --state open` no longer shows 259, 261 or 254.

**Prompt:**

```text
Execute Task 1 of docs/superpowers/plans/2026-11-27-no-impact-dependency-upgrades.md in the wolverine-mongodb repo.
Read the plan in full first, and read CLAUDE.md. The user has approved closing PRs #259, #261 and #254.
Confirm each PR's state with gh pr view, then close each one with the comment the plan gives. Change no files.
Report the final state of the three PRs and mark Task 1 Completed in the plan's Status table.
```

---

### Task 2: Merge the tooling and demo-only PRs

**Files:** `Directory.Packages.props`, `demo/Directory.Packages.props` (changed by the PRs, not by hand).

- [x] **Step 1: For each PR in the order #300, #292, #272, #294, #295, confirm it is mergeable and green**

Run: `gh pr view <n> --json mergeable,headRefOid,statusCheckRollup`
Expected: `mergeable` is `MERGEABLE`, and `library` and `demo` are `SUCCESS` on that `headRefOid`.
If dependabot rebased the PR, wait for the new run to finish.

- [x] **Step 2: Merge it**

Run: `gh pr merge <n> --squash`
Expected: the PR state is `MERGED`. Do the next PR only after this one is merged.

- [x] **Step 3: After the last merge, read the pins on `main`**

Run: `git fetch origin; git show origin/main:Directory.Packages.props; git show origin/main:demo/Directory.Packages.props`
Expected: Testcontainers.MongoDb 4.17.0, Microsoft.NET.Test.Sdk 18.12.0 and xunit.runner.visualstudio 4.0.1 in both
files; Microsoft.AspNetCore.OpenApi 9.0.23 and Scalar.AspNetCore 2.21.0 in the demo file.

**Prompt:**

```text
Execute Task 2 of docs/superpowers/plans/2026-11-27-no-impact-dependency-upgrades.md in the wolverine-mongodb repo.
Read the plan in full first (Global Constraints and Review Focus apply), and read CLAUDE.md. The user has approved
merging PRs #300, #292, #272, #294 and #295. Merge them one at a time, in that order, with gh pr merge --squash.
Merge a PR only when gh pr view --json statusCheckRollup shows library and demo SUCCESS on its current head.
If a PR conflicts or goes red, stop and report; do not edit files. Finish by checking the pins on origin/main
(Step 3), then mark Task 2 Completed in the plan's Status table.
```

---

### Task 3: Merge the Wolverine demo PRs and the driver PR

**Files:**
- `demo/Directory.Packages.props`, `Directory.Packages.props` (changed by the PRs)
- Modify: `CHANGELOG.md` (`## [Unreleased]`)

**Blocked by:** Task 2. This task merges into the same files.

- [x] **Step 1: Merge #302 (WolverineFx + WolverineFx.RabbitMQ 6.64.2)**

Confirm it as in Task 2 Step 1, then run `gh pr merge 302 --squash`.

- [x] **Step 2: Get #303 rebased onto the new `main`**

#303 now conflicts on the `WolverineFx` line. Wait for dependabot to rebase it. If it has not rebased after an
hour, comment `@dependabot rebase` on the PR.
Expected: the rebased diff changes only `WolverineFx.RuntimeCompilation` 6.39.1 → 6.64.2.
Check it with `gh api repos/{owner}/{repo}/pulls/303/files --jq '.[].patch'`.

- [x] **Step 3: Merge #303 once its new CI run is green**

Run: `gh pr merge 303 --squash`.

- [x] **Step 4: Merge #299 (MongoDB.Driver 3.14.1)**

Confirm it as in Task 2 Step 1, then run `gh pr merge 299 --squash`.

- [x] **Step 5: Add the driver change to the CHANGELOG**

Create branch `chore/changelog-driver-3.14.1` from `origin/main` in its own worktree. Under `## [Unreleased]`,
add a `### Changed` entry. Write it in the CHANGELOG's existing style, and include these facts:
the package now declares `MongoDB.Driver` 3.14.1 or later (was 3.12.0). Versions 3.13.0 to 3.14.1 fix several driver CVEs.
consumers who build connection strings from `MongoUrl.ToString()`, `MongoServerAddress.ToString()` or
`MongoCredential.ToString()` must change that code, because 3.14.1 redacts those values.

- [x] **Step 6: Commit, push and open the PR**

```powershell
git add CHANGELOG.md
git commit -m "docs: note the MongoDB.Driver 3.14.1 dependency floor"
git push -u origin chore/changelog-driver-3.14.1
gh pr create --fill
```

Expected: CI on the PR is green (`library` and `demo`). Merge it only after the user approves.

**Prompt:**

```text
Execute Task 3 of docs/superpowers/plans/2026-11-27-no-impact-dependency-upgrades.md in the wolverine-mongodb repo.
Read the plan in full first, and read CLAUDE.md. Task 2 must show Completed in the plan; if it does not, stop.
The user has approved merging PRs #302, #303 and #299 and opening a CHANGELOG PR. Merge #302, wait for dependabot
to rebase #303 and check that the rebased diff changes only WolverineFx.RuntimeCompilation, merge #303 when its
new CI run is green, then merge #299. Next, write the CHANGELOG entry from Step 5 on its own branch and worktree,
push it, and open a PR. Do not merge that PR. Use the humanizer skill on the CHANGELOG text and keep every line
under 140 characters. Mark Task 3 Completed in the plan's Status table when the CHANGELOG PR is open.
```

---

### Task 4: Verify `main` and hand over to the WolverineFx plan

**Files:** this plan (status table only).

**Blocked by:** Tasks 1, 2 and 3.

- [x] **Step 1: Read the CI result of the last push to `main`**

Run: `gh run list --branch main --workflow CI --limit 1 --json headSha,status,conclusion`
Expected: `conclusion` is `success` on the head commit of `origin/main`.

- [x] **Step 2: Confirm that only the planned PRs are left open**

Run: `gh pr list --state open --json number,title`
Expected: none of #254, #259, #261, #272, #292, #294, #295, #299, #300, #302, #303. If new dependabot PRs exist,
list them in the report and leave them alone.

- [x] **Step 3: Update this plan**

Set every row of the Status table to `Completed`, and fill in its Result column with the `main` commit SHA and
the CI run URL. If the CHANGELOG PR from Task 3 is still open, commit the plan update on its branch. If not, commit it on
a new `docs/` branch with its own PR.

**Prompt:**

```text
Execute Task 4 of docs/superpowers/plans/2026-11-27-no-impact-dependency-upgrades.md in the wolverine-mongodb repo.
Read the plan in full first. Tasks 1-3 must show Completed; if one does not, stop and report. Check the latest CI
run on main and the list of open PRs as Steps 1 and 2 describe. Report the main commit SHA, the CI conclusion and
any new PRs. Update the plan's Status table as Step 3 describes.
```

---

## Status

| # | Task | Status | Result |
|---|---|---|---|
| 1 | Close #259, #261, #254 | Completed | #259 and #261 were already closed; #254 closed with the plan's reason. |
| 2 | Merge #300, #292, #272, #294, #295 | Completed | All five squash-merged; pins checked on `origin/main`. |
| 3 | Merge #302, #303, #299; CHANGELOG PR | Completed | #302 and #299 merged, CHANGELOG PR #305 open; see Deviations for #302 and #303. |
| 4 | Verify `main` | Completed | `main` at `2b7d186`, CI `success` (run below). |

CI run: https://github.com/TheCraftyMaker/wolverine-mongodb/actions/runs/46066152367

### Deviations

- The "Main branch protection" ruleset requires an up-to-date branch and one approving review. Each PR was merged
  with `gh pr merge --squash --admin` on a head where `library` and `demo` were green. The `main` CI run above proves
  the combined state.
- Dependabot moved #302 to WolverineFx 6.65.1 when it rebased the PR.
- Dependabot closed #303 as "no longer updatable". The demo's `WolverineFx.RuntimeCompilation` stays at 6.39.1 next to
  WolverineFx 6.65.1. The `main` CI run passes with that pair, and the WolverineFx 6.65.1 plan moves the pin to 6.65.1.
