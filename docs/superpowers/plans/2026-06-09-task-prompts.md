# Per-Task Session Prompts

One fresh Claude Code session per task, in the order below. Start each session **in the repository root** (not a stale worktree), set the session model first (`/model` — the recommended model is listed per task), then paste the prompt.

Conventions baked into every prompt:
- The session verifies its **precondition** (prerequisite PRs merged to `main`) before doing anything.
- Execution goes through `superpowers:executing-plans` against the referenced plan task **only** — no scope creep.
- Branch + PR mechanics come from the plan's "Git & PR Workflow" section.
- If a plan assumption doesn't hold (API missing, a test can't fail/pass as predicted), the session stops and reports instead of improvising. Two non-obvious verification failures → stop, report, re-run the task on Fable 5.

**Precondition for everything below:** the `docs/implementation-plans` PR (containing the plan files and this prompts file) is merged to `main`.

---

## Phase A — Solo Hardening (`docs/superpowers/plans/2026-06-09-solo-hardening.md`)

### A1. Task 5 — CI: library tests + fresh-nupkg demo  *(model: Fable 5 or Opus — run FIRST so all later PRs get CI coverage)*

```
Execute Task 5 ("CI — run the library suite against a pinned Wolverine clone; run the demo against the freshly packed nupkg") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

First read the plan's "Git & PR Workflow" and "Model guidance" sections and follow them exactly: branch ci/library-tests-and-fresh-nupkg-demo from origin/main; execute the task steps in order, running every verification command and confirming the expected output; finish with the task's commit, then push and open the PR titled "ci: run compliance suite against pinned Wolverine; demo consumes fresh nupkg". Watch the PR checks with rtk gh pr checks --watch and iterate until BOTH jobs are green — this task is not done until CI passes on the PR.

Stay strictly within Task 5's scope. If the Wolverine 6.2.2 tag does not exist or the compliance suite fails on the runner for reasons the plan doesn't anticipate, diagnose and fix within the CI configuration; if the cause is a library/test defect, stop and report it instead of patching the library in this PR.
```

### A2. Tasks 1–4 — the four correctness bugs *(model: Sonnet; four independent sessions, may run in parallel; no ordering between them)*

**Task 1 — outgoing owner filter:**

```
Execute Task 1 ("LoadOutgoingAsync must only return globally-owned envelopes, batch-limited") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

Follow the plan's "Git & PR Workflow" section: branch fix/outgoing-owner-filter from origin/main; TDD exactly as written (write the failing test, confirm it fails with the expected reason, implement, confirm pass, amend outbox_recovery.cs as specified); finish with the task's commit, push, and open the PR titled "fix: LoadOutgoingAsync only returns globally-owned envelopes, batch-limited". Watch checks until green.

Stay strictly within Task 1's scope. If a plan assumption doesn't hold, stop and report rather than improvising.
```

**Task 2 — KeepUntil on handled markers:**

```
Execute Task 2 ("Handled markers must carry KeepUntil so the TTL index can expire them") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

Follow the plan's "Git & PR Workflow" section: branch fix/handled-keep-until from origin/main; TDD exactly as written, including the additional assertion in eager_idempotency_transaction.cs; finish with the task's commit, push, and open the PR titled "fix: handled inbox markers carry KeepUntil so the TTL index expires them". Watch checks until green.

Stay strictly within Task 2's scope. If a plan assumption doesn't hold, stop and report rather than improvising.
```

**Task 3 — DLQ expiration opt-in:**

```
Execute Task 3 ("Dead-letter expiration must honor DeadLetterQueueExpirationEnabled") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

Follow the plan's "Git & PR Workflow" section: branch fix/dlq-expiration-opt-in from origin/main; TDD exactly as written (the nullable ExpirationTime + BsonIgnoreIfNull change is part of the fix); run the full dead_letter* test set before finishing; commit, push, and open the PR titled "fix: dead letters only expire when DeadLetterQueueExpirationEnabled is set". Watch checks until green.

Stay strictly within Task 3's scope. If a plan assumption doesn't hold, stop and report rather than improvising.
```

**Task 4 — idempotent dead-letter replay:**

```
Execute Task 4 ("Make ReplayDeadLettersAsync idempotent and per-document fault-tolerant") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

Follow the plan's "Git & PR Workflow" section: branch fix/dead-letter-replay-idempotent from origin/main; TDD exactly as written (crash-window simulation + body-less poison letter); commit, push, and open the PR titled "fix: dead-letter replay converges after crashes". Watch checks until green.

Stay strictly within Task 4's scope. If a plan assumption doesn't hold, stop and report rather than improvising.
```

### A3. Tasks 6, 7, 10, 11 *(model: Sonnet; four independent sessions, may run in parallel once A1+A2 PRs are merged — they touch shared test files, so rebase on main if a conflict appears)*

**Task 6 — fail fast on Balanced:**

```
Execute Task 6 ("Fail fast on DurabilityMode.Balanced; fix the misleading recovery comment") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

Precondition: verify with rtk git log origin/main --oneline -20 that the Task 1–5 PRs are merged (the test-host sweep in Step 5 must see the final test files). Then: branch feat/fail-fast-balanced-mode from origin/main; execute all steps including the grep sweep adding DurabilityMode.Solo to every UseWolverine test host; run the FULL library suite before finishing; commit, push, and open the PR titled "feat: fail fast on DurabilityMode.Balanced". Watch checks until green.

Stay strictly within Task 6's scope. If Initialize/BuildAgent are not invoked before StartAsync returns (the guard test stays green-less), apply the plan's documented fallback (guard in MongoDbDurabilityAgent.StartAsync) rather than inventing another mechanism.
```

**Task 7 — per-property BSON dates:**

```
Execute Task 7 ("Replace the process-global DateTimeOffset serializer with per-property representations") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

Branch refactor/per-property-bson-dates from origin/main. Apply [BsonRepresentation(BsonType.DateTime)] to EVERY property in the plan's table — a missed property silently reverts to the [ticks, offset] array format and breaks TTL/range queries on existing data. Delete MongoSerializerRegistration.cs and its call site. The existing datetime_serialization tests are the regression oracle: they must pass unchanged. Run the full suite, commit, push, open the PR titled "refactor: per-property BSON DateTime representation". Watch checks until green.

Stay strictly within Task 7's scope. If a plan assumption doesn't hold, stop and report rather than improvising.
```

**Task 10 — pin write/read concerns:**

```
Execute Task 10 ("Pin majority write/read concerns on the store's database handle") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

Branch fix/pin-majority-write-concern from origin/main; TDD exactly as written; the app-facing IMongoDatabase registration must remain untouched (only the store's internal handle is pinned). Run the full suite, commit, push, open the PR titled "fix: pin majority/journaled write + majority read concern on the store". Watch checks until green.

Stay strictly within Task 10's scope. If a plan assumption doesn't hold, stop and report rather than improvising.
```

**Task 11 — index tuning + aggregation summaries:**

```
Execute Task 11 ("Index tuning + server-side aggregation for summaries") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

Branch perf/index-tuning-aggregations from origin/main; implement EnsureIndexesAsync, the index-shape test in admin_smoke.cs (if the driver-generated index names differ from the plan's expectations, print the actual names and assert on those — presence is the point, not naming), and both aggregation conversions. Run the full suite, commit, push, open the PR titled "perf: compound/TTL index tuning and aggregation summaries". Watch checks until green.

Stay strictly within Task 11's scope. If a plan assumption doesn't hold, stop and report rather than improvising.
```

### A4. Task 8 — broaden transaction detection *(model: Fable 5 or Opus)*

```
Execute Task 8 ("Broaden CanApply so collection-, client-, and session-typed handlers get transactions") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

Branch fix/broaden-transaction-detection from origin/main. Before implementing, verify IChain.HandlerCalls() and the ServiceDependencies semantics against the local Wolverine clone (C:\source\external\wolverine, or the path in the WOLVERINE_SOURCE env var) as the plan instructs — do not guess the API. TDD exactly as written; run the full suite; commit, push, open the PR titled "fix: apply transactions for IMongoCollection/IMongoClient/IClientSessionHandle handlers". Watch checks until green.

Stay strictly within Task 8's scope. If the codegen behaves differently than the plan predicts, diagnose using Wolverine's generated-code output before changing approach, and report any plan deviation in the PR description.
```

### A5. Task 9 — MongoDbUnitOfWork *(model: Fable 5 or Opus; **only after the Task 8 PR is merged**)*

```
Execute Task 9 ("MongoDbUnitOfWork — session-bound write helper") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

Precondition: verify with rtk git log origin/main --oneline -10 that the Task 8 PR ("fix: apply transactions for IMongoCollection/IMongoClient/IClientSessionHandle handlers") is merged — this task extends the CanApply predicate Task 8 introduced. Then: branch feat/mongodb-unit-of-work from origin/main; TDD exactly as written (commit test AND rollback test), including the TransactionalFrame variable emission and the CanApply extension; run the full suite; commit, push, open the PR titled "feat: MongoDbUnitOfWork session-bound write helper". Watch checks until green.

Stay strictly within Task 9's scope. If codegen variable resolution fails, inspect the generated handler source before changing the frame design, and report any plan deviation in the PR description.
```

### A6. Task 12 — documentation sweep *(model: Sonnet; **only after Tasks 1–11 PRs are all merged**)*

```
Execute Task 12 ("Documentation sweep") of docs/superpowers/plans/2026-06-09-solo-hardening.md using the superpowers:executing-plans skill.

Precondition: verify with rtk git log origin/main --oneline -20 that the PRs for Tasks 1–11 are all merged. Then: branch docs/solo-hardening-sweep from origin/main; update README.md, CLAUDE.md, FOLLOWUPS.md, CHANGELOG.md, demo/README.md, demo/CLAUDE.md exactly per the task's steps. Critical: every documented claim must be true of the code on main RIGHT NOW — verify each statement against the source before writing it. Commit, push, open the PR titled "docs: hardening pass documentation sweep". Watch checks until green.
```

### A7. Task 13 — final verification *(model: Sonnet; **after Task 12 merges**; no branch, no PR)*

```
Execute Task 13 ("Final verification") of docs/superpowers/plans/2026-06-09-solo-hardening.md.

This task runs on main itself: rtk git checkout main && rtk git pull, run the full library suite and the package-ref Release build, confirm the post-merge CI run on main is green (rtk gh run list --branch main), and review the merged history — one PR per task 1–12, every file in the plan's File Structure Overview touched. Report a short summary of the verification results. If anything is missing or red, report it — do not fix anything in this session. The Solo Hardening plan is complete only when this report is clean; the Multinode plan may then begin.
```

---

## Phase B — Multinode (`docs/superpowers/plans/2026-06-09-multinode-support.md`)

**Precondition for ALL Phase B prompts: Phase A is fully merged and Task 13's verification report is clean.**

### B1. Task 1 — MongoDbPersistenceOptions + allow Balanced *(model: Sonnet)*

```
Execute Task 1 ("Introduce MongoDbPersistenceOptions and downgrade the Balanced guard") of docs/superpowers/plans/2026-06-09-multinode-support.md using the superpowers:executing-plans skill.

Precondition: the Solo Hardening plan is fully merged (verify the fail-fast guard from its Task 6 exists on main — this task replaces it). Then: branch feat/mongodb-persistence-options from origin/main; execute the steps in order including amending durability_mode_guard.cs; run the full suite; commit, push, open the PR titled "feat: MongoDbPersistenceOptions; allow DurabilityMode.Balanced". Watch checks until green.

Stay strictly within Task 1's scope. If a plan assumption doesn't hold, stop and report rather than improvising.
```

### B2. Task 2 — configurable leader lease *(model: Fable 5 or Opus; **only after the B1 PR is merged**)* — plus Tasks 3, 4, 5 in parallel

**Task 2 — configurable leader lease:**

```
Execute Task 2 ("Configurable, renewal-aware leader lease") of docs/superpowers/plans/2026-06-09-multinode-support.md using the superpowers:executing-plans skill.

Precondition: verify the Task 1 PR ("feat: MongoDbPersistenceOptions...") is merged to main. Then: branch feat/configurable-leader-lease from origin/main; TDD exactly as written. These tests are timing-sensitive by design — if one flakes, reason about WHY (lease math, clock granularity, container latency) before adjusting any duration, and never weaken an assertion to make it pass. Include the failed-takeover cache fix in TryAttainAsync. Run the full suite; commit, push, open the PR titled "feat: configurable leader lock lease with renewal margin". Watch checks until green.

Stay strictly within Task 2's scope; report any plan deviation in the PR description.
```

**Task 3 — CAS outgoing recovery** *(model: Fable 5 or Opus; independent of Tasks 1–2)*:

```
Execute Task 3 ("CAS-guarded outgoing recovery") of docs/superpowers/plans/2026-06-09-multinode-support.md using the superpowers:executing-plans skill.

Precondition: Solo Hardening fully merged (this task builds on the owner-filtered LoadOutgoingAsync). Then: branch fix/cas-outgoing-recovery from origin/main; TDD exactly as written. The contention test simulates a competing node's claim — if it unexpectedly passes BEFORE your implementation change, follow the plan's caveat and inspect DiscardAndReassignOutgoingAsync before concluding anything. A subtly wrong claim filter can still pass most runs: re-run the contention test 5 times before declaring done. Run the full suite; commit, push, open the PR titled "fix: CAS-guarded outgoing recovery prevents cross-node double-claims". Watch checks until green.
```

**Task 4 — dead-node ownership release** *(model: Sonnet; independent)*:

```
Execute Task 4 ("Release ownership held by dead node numbers") of docs/superpowers/plans/2026-06-09-multinode-support.md using the superpowers:executing-plans skill.

Precondition: Solo Hardening fully merged. Then: branch feat/release-dead-node-ownership from origin/main; TDD exactly as written, including wiring ReleaseDeadNodeOwnershipAsync as the FIRST call in the durability agent's recovery tick, guarded to non-Solo modes. Run the full suite; commit, push, open the PR titled "feat: release envelope ownership held by dead node numbers". Watch checks until green.

Stay strictly within Task 4's scope. If a plan assumption doesn't hold, stop and report rather than improvising.
```

**Task 5 — DeleteOldNodeRecordsAsync** *(model: Sonnet; independent)*:

```
Execute Task 5 ("Implement DeleteOldNodeRecordsAsync") of docs/superpowers/plans/2026-06-09-multinode-support.md using the superpowers:executing-plans skill.

Precondition: Solo Hardening fully merged. Then: branch feat/delete-old-node-records from origin/main; TDD exactly as written; run the node-related tests and the full suite; commit, push, open the PR titled "feat: implement DeleteOldNodeRecordsAsync". Watch checks until green.

Stay strictly within Task 5's scope. If a plan assumption doesn't hold, stop and report rather than improvising.
```

### B3. Task 6 — un-gate multinode compliance *(model: **Fable 5 mandatory**; **only after Tasks 1, 2, 3, 4 PRs are merged**)*

```
Execute Task 6 ("Un-gate and stabilize the multinode compliance suite") of docs/superpowers/plans/2026-06-09-multinode-support.md using the superpowers:executing-plans skill.

Precondition: verify the PRs for multinode Tasks 1, 2, 3, and 4 are merged to main. Then: branch test/ungate-multinode-compliance from origin/main; replace the #if RUN_MULTINODE gate per the plan and stabilize the suite. The acceptance bar is FIVE consecutive green runs of dotnet test --filter "Category=multinode". When a fact flakes, diagnose WHY (lease vs heartbeat cadence, takeover race, balance-assertion timing) before turning a stabilization knob; use the plan's levers in order, and compare against the RavenDb provider's subclass in the Wolverine clone if stuck. HARD RULES: do not skip facts, do not add retries, do not lengthen test timeouts as a substitute for understanding. If five-in-a-row cannot be reached after exhausting the levers, stop and write up the exact failing facts, observed interleavings, and your hypothesis — that report is the deliverable in that case, not a green-at-any-cost suite.

When stable: run the FULL suite, commit, push, open the PR titled "test: un-gate multinode leadership election compliance". Watch checks until green.
```

### B4. Tasks 7 and 9 *(parallel once their preconditions hold)*

**Task 7 — cross-node end-to-end tests** *(model: **Fable 5 mandatory**; after Tasks 1–4 merged)*:

```
Execute Task 7 ("Cross-node end-to-end integration tests") of docs/superpowers/plans/2026-06-09-multinode-support.md using the superpowers:executing-plans skill.

Precondition: verify the PRs for multinode Tasks 1–4 are merged to main. Then: branch test/multinode-end-to-end from origin/main. The plan flags several Wolverine APIs as unverified (PortFinder, the explicit-port control-endpoint registration, LocalQueueFor) — verify each against the local Wolverine clone (C:\source\external\wolverine or WOLVERINE_SOURCE) and use the plan's documented fallbacks where the API differs. Acceptance bar: five consecutive green runs of the new tests. Same hard rules as the compliance task: no skips, no retries, no assertion-weakening; if exactly-once fails, that is a REAL coordination bug — write it up and stop.

When stable: run the FULL suite, commit, push, open the PR titled "test: cross-node exactly-once scheduling and dead-node rescue". Watch checks until green.
```

**Task 9 — demo config-driven durability mode** *(model: Sonnet; after Task 1 merged)*:

```
Execute Task 9 ("Demo — config-driven durability mode + multinode runbook") of docs/superpowers/plans/2026-06-09-multinode-support.md using the superpowers:executing-plans skill.

Precondition: verify the multinode Task 1 PR is merged to main. Then: branch demo/config-driven-durability-mode from origin/main; implement the Program.cs/appsettings changes and the README runbook exactly as written; build the demo solution and run its integration tests (they stay Solo). The manual two-instance smoke (plan Step 6) is recommended if Docker + RabbitMQ are available — record the outcome in the PR description either way. Commit, push, open the PR titled "demo: config-driven durability mode with multinode runbook". Watch checks until green.
```

### B5. Task 8 — CI multinode category *(model: Sonnet; **only after the Task 6 PR is merged**)*

```
Execute Task 8 ("CI runs the multinode category") of docs/superpowers/plans/2026-06-09-multinode-support.md using the superpowers:executing-plans skill.

Precondition: verify the multinode Task 6 PR is merged to main (the test category must exist). Then: branch ci/multinode-category from origin/main; split the CI test step per the plan; commit, push, open the PR titled "ci: run multinode test category as a separate step"; watch checks until BOTH test steps are green. If the multinode step flakes on CI but not locally, do NOT add retries — report back so Task 6's stabilization can be revisited.
```

### B6. Task 10 — documentation sweep *(model: Sonnet; **only after Tasks 1–9 PRs are all merged**)*

```
Execute Task 10 ("Documentation sweep") of docs/superpowers/plans/2026-06-09-multinode-support.md using the superpowers:executing-plans skill.

Precondition: verify the PRs for multinode Tasks 1–9 are all merged to main. Then: branch docs/multinode-sweep from origin/main; update README.md, CLAUDE.md, FOLLOWUPS.md, CHANGELOG.md per the task — including the honest "known limits" section (lease-based leadership is not fenced; clock-skew assumptions). Every claim must be verified against the code on main before writing it. Commit, push, open the PR titled "docs: multinode support documentation". Watch checks until green.
```

### B7. Task 11 — final verification *(model: Sonnet; **after Task 10 merges**; no branch, no PR)*

```
Execute Task 11 ("Final verification") of docs/superpowers/plans/2026-06-09-multinode-support.md.

This runs on main itself: rtk git checkout main && rtk git pull; run the full suite; run the multinode category five consecutive times (all must pass); pack the library package-ref build; run the demo integration tests; confirm CI on main is green including the multinode step; review the merged history (one PR per task 1–10). Report a verification summary. If anything is red or missing, report it — do not fix anything in this session.
```
