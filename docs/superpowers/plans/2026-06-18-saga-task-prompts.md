# Saga Persistence — Per-Task Session Prompts

One fresh Claude Code session per task, in the order below. Start each session **in the repository root** (not a stale worktree), set the session model first (`/model` — the recommended model is listed per task), then paste the prompt.

Conventions baked into every prompt (identical to `2026-06-09-task-prompts.md`):
- The session verifies its **precondition** (prerequisite PRs merged to `main`) before doing anything.
- Execution goes through `superpowers:executing-plans` against the referenced plan task **only** — no scope creep.
- Branch + PR mechanics come from the plan's "Git & PR Workflow" section.
- **Do not invent Wolverine APIs.** The plan's "Verified Saga API Facts" section is the source of truth; confirm each against the pinned `external/wolverine` submodule (a one-line grep) rather than guessing. If an API differs from the plan, **stop and report** — do not improvise a substitute.
- If a plan assumption doesn't hold (API missing, a test can't fail/pass as predicted), the session stops and reports instead of improvising. Two non-obvious verification failures → stop, report, re-run the task on Fable 5.
- When finishing, update the plan doc in the same PR (tick step checkboxes + the status-table row).

**Precondition for everything below:** the Solo Hardening (`2026-06-09-solo-hardening.md`) and Multinode (`2026-06-09-multinode-support.md`) plans are fully merged to `main`, and the PR containing this plan + prompts file (`2026-06-18-saga-*.md`) is merged to `main`.

**The plan:** `docs/superpowers/plans/2026-06-18-saga-persistence.md`.

---

## Phase 0 — Discovery & Design

### S1 / S2 / S3 / S5 — four independent sessions, run in PARALLEL *(model: Sonnet; S5 may be re-run after S4 to finalize the saga id/state shape)*

**S1 — repository & convention analysis** *(independent — can start immediately)*

```
Execute Task S1 ("Repository & convention analysis") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

This is a read-only analysis task that produces a notes doc; no library code changes. Branch docs/saga-repo-analysis from origin/main. Read MongoDbPersistenceFrameProvider.cs, TransactionalFrame.cs, WolverineMongoDbExtensions.cs, MongoConstants.cs, MongoDbMessageStore.Admin.cs, the test AppFixture.cs + message_store_compliance.cs, and the demo OrderRepository.cs + PlaceOrderHandler.cs. Produce docs/superpowers/plans/2026-06-18-saga-repo-analysis.md confirming the plan's "Verified Saga API Facts" (local) against the code on main, and flagging any drift. Commit, push, open the PR titled "docs: saga — repository & convention analysis". Watch checks until green.

Stay strictly within Task S1's scope.
```

**S2 — Wolverine saga API discovery** *(independent — can start immediately)*

```
Execute Task S2 ("Wolverine saga API discovery") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Read-only study of the pinned external/wolverine submodule (V6.2.2). Branch docs/saga-wolverine-api from origin/main. Pin exact signatures + file paths for: Saga.cs (Version, IsCompleted, MarkCompleted, SagaConcurrencyException); the saga members of IPersistenceFrameProvider.cs; SagaChain + saga identity resolution ([SagaIdentity], {Saga}Id/Id/SagaId precedence, ValidSagaIdTypes); and the exceptions UnknownSagaException / IndeterminateSagaStateIdException / SagaConcurrencyException and which failure expects which. Trace the generated load→handle→store/delete→commit order. Produce docs/superpowers/plans/2026-06-18-saga-wolverine-api.md. Commit, push, open the PR titled "docs: saga — Wolverine saga API discovery". Watch checks until green.

Do not invent APIs — quote the submodule. Stay strictly within Task S2's scope.
```

**S3 — Cosmos/RavenDb comparison** *(independent — can start immediately)*

```
Execute Task S3 ("Cosmos & RavenDb saga implementation comparison") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Read-only study of external/wolverine. Branch docs/saga-cosmos-raven-compare from origin/main. Read the CosmosDb saga provider + frames + extension + CosmosDbTests/saga_storage_compliance.cs, and the RavenDb equivalents (incl. RavenDbSagaStoreDiagnostics.cs and the UseOptimisticConcurrency line). Produce docs/superpowers/plans/2026-06-18-saga-cosmos-raven-compare.md: a side-by-side table and the recommendation "mirror Cosmos for structure; add native-id + Saga.Version optimistic concurrency like the demo's OrderRepository", with the exact frame snippets to adapt. Commit, push, open the PR titled "docs: saga — Cosmos/RavenDb implementation comparison". Watch checks until green.

Stay strictly within Task S3's scope.
```

**S5 — demo flow design + test inventory** *(independent to start; finalize the saga id/state shape after S4 merges)*

```
Execute Task S5 ("Demo saga flow design + library/demo test inventory") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Branch docs/saga-demo-and-test-inventory from origin/main. Design the demo OrderFulfillmentSaga (Guid id = OrderId) and map every required flow (start, continue, complete, missing-state, duplicate/repeated, across-restart, inbox/outbox interaction, single vs multi-node) to concrete trigger messages + assertions, reusing existing demo events (OrderPlacedApplicationEvent, OrderShippedApplicationEvent) where possible and adding the minimum new contracts. Produce docs/superpowers/plans/2026-06-18-saga-demo-and-test-inventory.md: saga state shape, message contracts, handler signatures, and a flow→test mapping table (library compliance subclasses String+Guid, custom atomicity/OCC/idempotency tests, demo SagaFlowTests, multinode test). Commit, push, open the PR titled "docs: saga — demo flow design + test inventory". Watch checks until green.

If S4 has merged, align the saga state/id shape with its decisions. Stay strictly within Task S5's scope.
```

### S4 — MongoDB saga document model + concurrency design (DESIGN GATE) *(model: Opus / Fable 5; only after S1, S2, S3 PRs are merged)*

```
Execute Task S4 ("MongoDB saga document model + identity + concurrency design") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Precondition: verify with rtk git log origin/main --oneline -15 that the S1, S2, and S3 PRs are merged (their notes docs exist on main). Then: branch docs/saga-document-model-design from origin/main. Synthesize S1–S3 and make the binding decisions in the task (document shape = store the saga POCO directly; collection strategy = one collection per saga type with a naming helper; id types; concurrency via Saga.Version guarded ReplaceOneAsync → SagaConcurrencyException; frame ordering/atomicity = saga writes on the TransactionalFrame session, commit postprocessor only for non-SagaChain; no global serializer mutation; upstream-readiness notes). Resolve the Lead Open Design Decision explicitly: state whether the implementation targets Option B (native id + OCC — recommended) or downscopes to Option A (Cosmos parity). Produce docs/superpowers/plans/2026-06-18-saga-document-model-design.md. Commit, push, open the PR titled "docs: saga — MongoDB document model + identity + concurrency design". Watch checks until green.

This doc gates S6–S8; be precise about the frame/collection contracts. Stay strictly within Task S4's scope.
```

---

## Phase 1 — Persistence Implementation

### S6 — saga codegen, string-id baseline *(model: Fable 5 / Opus; only after the S4 PR is merged — HEAD OF THE CRITICAL PATH)*

```
Execute Task S6 ("MongoDB saga persistence — string-id baseline") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Precondition: verify the S4 design PR is merged to main (read docs/superpowers/plans/2026-06-18-saga-document-model-design.md and follow its decisions). Then: branch feat/saga-codegen-string from origin/main.

Implement saga persistence as code-generation only, mirroring the verified Cosmos provider: in MongoDbPersistenceFrameProvider.cs flip CanApply (add `if (chain is SagaChain) return true;`), CanPersist (IMPORTANT — do NOT return true unconditionally like Cosmos: Cosmos also implements DetermineStorageActionFrame but Mongo's still throws, so an unconditional true advertises generic [Entity]/Insert<T>/Update<T> persistence and blows up at codegen — scope it to sagas: `return entityType.CanBeCastTo<Saga>();`), DetermineSagaIdType (typeof(string) for this baseline), and implement Load/Insert/Update/Store/Delete/CommitUnitOfWork via a new Internals/SagaFrames.cs whose frames resolve the TransactionalFrame's IClientSessionHandle + IMongoDatabase and operate INSIDE the transaction (Find/ReplaceOneAsync(IsUpsert)/DeleteOneAsync on a per-saga-type collection keyed by _id). Change ApplyTransactionSupport to add the CommitMongoTransactionFrame postprocessor only `if (chain is not SagaChain)` so saga chains commit via CommitUnitOfWorkFrame (no double-commit). MANDATORY: extend MongoDbMessageStore.Admin.cs ClearAllAsync/RebuildAsync to also drop the per-saga-type collections (e.g. wolverine_saga_* prefix) — the shared fixture DB otherwise leaks saga docs between compliance facts. PRESERVE all inbox/outbox/transaction behavior — the only ApplyTransactionSupport change is the saga branch.

CRITICAL verification: dump the generated handler (codeFor<T>() or CodeGeneration.GeneratedCodeOutputPath) and confirm the order is open session → load saga → handler → upsert/delete saga → commit → flush, all inside the try-block, with exactly one commit. The acceptance oracle is the string compliance suite: author MongoDbSagaHost.cs + string_saga_storage_compliance.cs (: StringIdentifiedSagaComplianceSpecs<MongoDbSagaHost>, mirroring CosmosDbSagaHost: Solo, GeneratedCodeOutputPath, TypeLoadMode.Auto, Discovery.IncludeType<StringBasicWorkflow>() + assembly, register _fixture.Client, UseMongoDbPersistence(AppFixture.DatabaseName); LoadState<T>(string) reads the saga directly from Mongo, other overloads throw NotSupportedException) IN THIS BRANCH so the PR ships green. Run the full library suite. Commit, push, open the PR titled "feat: MongoDB saga persistence (string-id baseline)". Watch checks until green.

If the generated order or commit count is wrong, fix the frame wiring before proceeding — do not weaken assertions. If a Wolverine API differs from the plan's Verified Facts, stop and report. Stay strictly within Task S6's scope.
```

### S7 — native Guid/int/long id support *(model: Fable 5 / Opus; only after the S6 PR is merged)*

```
Execute Task S7 ("Native Guid/int/long saga id support") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Precondition: verify the S6 PR ("feat: MongoDB saga persistence (string-id baseline)") is merged to main. Then: branch feat/saga-native-id-types from origin/main. Implement DetermineSagaIdType via Wolverine's own resolver — `SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType()` (mirrors LightweightSagaPersistenceFrameProvider.cs:80-82) — do not hand-roll reflection. NOTE the scope: core only calls DetermineSagaIdType for the envelope-header-only identity path (SagaChain.cs:290-292); messages that carry a saga-id member use PullSagaIdFromMessageFrame with that member's type directly. So make SagaFrames key _id off whatever typed sagaId variable they're handed (Guid/int/long/string). Confirm Guid _id round-trips (the demo already stores Guid _id on Order; add a focused assertion). The oracle is S10's Guid (and int/long) compliance, plus the still-green string compliance. Run the full suite. Commit, push, open the PR titled "feat: native Guid/int/long saga id support". Watch checks until green.

Stay strictly within Task S7's scope. If int/long prove fiddly, the plan permits deferring them to FOLLOWUPS — note it in the PR and keep Guid+string green.
```

### S8 — saga optimistic concurrency *(model: Fable 5 / Opus; only after BOTH the S6 and S7 PRs are merged)*

```
Execute Task S8 ("Saga optimistic concurrency via Saga.Version") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Precondition: verify the S6 AND S7 PRs are merged to main (S8 shares MongoDbPersistenceFrameProvider.cs + SagaFrames.cs with S7, and the guard must work for all id types — do NOT run S8 in parallel with S7). Then: branch feat/saga-optimistic-concurrency from origin/main. Make the saga UPDATE path version-guarded with PRECISE old/new-version semantics (not the vague "filter on (_id, Version) and increment", which fails every update): capture oldVersion = saga.Version; set saga.Version = oldVersion + 1; ReplaceOneAsync with filter (_id == sagaId && Version == oldVersion), IsUpsert = false; throw SagaConcurrencyException when ModifiedCount == 0. The INSERT path is unguarded and sets the initial Version. This forces insert and update frames to diverge (revisit S6's upsert-for-both). Decide and document whether the completion DELETE is version-guarded — recommended NOT guarded, matching Wolverine's lightweight SQL provider (DatabaseSagaSchema.cs:113-119). Verify in the generated code that the thrown exception aborts the transaction so the saga AND outbox roll back together. The compliance specs do NOT test concurrency, so they must stay green; the OCC oracle is the S11 conflict test (land it here as the proof). Run the full suite. Commit, push, open the PR titled "feat: saga optimistic concurrency via Saga.Version". Watch checks until green.

This is concurrency-correctness work — reason about the race before adjusting, and never weaken an assertion. Stay strictly within Task S8's scope.
```

---

## Phase 2 — Library Tests

### S9 — string compliance host + spec *(model: Sonnet; green requires S6)*

```
Execute Task S9 ("MongoDbSagaHost + string-identified compliance") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

NOTE: if S6 already shipped MongoDbSagaHost.cs + string_saga_storage_compliance.cs (the plan's S6 coordination note recommends this), this task hardens the host (add NotSupportedException overloads, tidy LoadState) and is otherwise a no-op — verify and report. Otherwise: precondition — the S6 PR is merged. Branch test/saga-string-compliance from origin/main. Add MongoDbSagaHost.cs (mirror CosmosDbSagaHost; [Collection("mongodb")] on the spec; clear the fixture DB before BuildHostAsync; LoadState<T>(string) reads the saga directly from Mongo via _fixture.Client) and string_saga_storage_compliance : StringIdentifiedSagaComplianceSpecs<MongoDbSagaHost>. Run --filter "FullyQualifiedName~string_saga_storage_compliance" → all green; full suite green. Commit, push, open the PR titled "test: string-identified saga storage compliance". Watch checks until green.

Stay strictly within Task S9's scope.
```

### S10 — Guid/int/long compliance *(model: Sonnet; only after the S7 and S9 PRs are merged)*

```
Execute Task S10 ("Guid/int/long-identified compliance") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Precondition: verify the S7 and S9 PRs are merged to main. Then: branch test/saga-guid-compliance from origin/main. Add guid_saga_storage_compliance : GuidIdentifiedSagaComplianceSpecs<MongoDbSagaHost> (and Int/Long variants if S7 covered them), and implement the matching LoadState<T>(Guid/int/long) overloads on MongoDbSagaHost (replacing the NotSupportedException stubs). Run the new specs green + the full suite. Commit, push, open the PR titled "test: Guid/int/long-identified saga storage compliance". Watch checks until green.

Stay strictly within Task S10's scope.
```

### S11 — atomicity, OCC, completion & idempotency *(model: Fable 5 / Opus; atomicity/idempotency after S6, OCC test after S8)*

```
Execute Task S11 ("Saga atomicity, concurrency, completion & idempotency tests") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Precondition: the S6 PR is merged (for atomicity/completion/idempotency); the S8 PR is merged (for the OCC conflict test). Then: branch test/saga-atomicity-concurrency from origin/main. Add src/Wolverine.MongoDB.Tests/saga_atomicity.cs ([Collection("mongodb")], on AppFixture, modeled on OutboxAtomicityTests) covering: (1) atomicity — a saga handler that writes state AND cascades an outgoing message; a forced post-handler failure leaves NEITHER the saga doc NOR the outgoing envelope persisted, and the success path persists both; (2) completion — MarkCompleted() deletes the doc; (3) OCC — a stale-version update throws SagaConcurrencyException without clobbering; (4) idempotency — a redelivered start/continue message (durable inbox) yields correct, not-double-applied state. Run all four green + the full suite. Commit, push, open the PR titled "test: saga atomicity, optimistic concurrency & idempotency". Watch checks until green.

These tests simulate races/failures — a wrong test passes most runs; verify each fails for the right reason before it passes. Stay strictly within Task S11's scope.
```

---

## Phase 3 — Demo

### S12 — demo OrderFulfillmentSaga *(model: Sonnet; build/run requires S6 + S7 merged & packed)*

```
Execute Task S12 ("Demo OrderFulfillmentSaga — contracts, saga & handlers") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Precondition: verify the S6 and S7 PRs are merged to main (the demo consumes the Wolverine.MongoDB package; saga support must be in the package). For local dev, pack the library first (dotnet pack src/Wolverine.MongoDB/... -p:UseWolverineSource=false) so the demo can resolve saga support; CI's demo job uses the fresh 0.0.0-ci nupkg. Then: branch demo/saga-order-fulfillment from origin/main. Implement per the S5 design: new contracts in OrderDemo.Contracts (e.g. ConfirmDeliveryCommand + completion/timeout messages), OrderDemo.Application/Sagas/OrderFulfillmentSaga.cs (Guid id = OrderId; starts on OrderPlacedApplicationEvent, continues on OrderShippedApplicationEvent + ConfirmDeliveryCommand, completes via MarkCompleted()), and Program.cs discovery/routing for the saga messages. Keep saga writes inside the Wolverine transaction (the generated saga frames handle the session — do NOT add a manual session). Preserve all existing order flows. Build the demo (dotnet build OrderDemo.slnx -c Release) and manually smoke the start→ship→confirm→completed(deleted) progression. Commit, push, open the PR titled "demo: OrderFulfillmentSaga contracts, saga & handlers". Watch checks until green.

Stay strictly within Task S12's scope. If the saga design needs a tweak vs S5, note it in the PR.
```

### S13 — demo saga safety-net tests *(model: Sonnet; only after the S12 PR is merged)*

```
Execute Task S13 ("Demo saga safety-net integration tests (Solo)") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Precondition: verify the S12 PR is merged to main. Then: branch demo/saga-safety-net-tests from origin/main. Add demo/tests/OrderDemo.IntegrationTests/SagaFlowTests.cs on OrdersFixture (Solo, per-test CreateDatabaseName(), host.TrackActivity().Timeout(...).InvokeMessageAndWaitAsync, FluentAssertions). One test per flow: start, continue, complete (assert the saga doc is deleted via a direct Mongo query), missing-state, duplicate/repeated message, across-restart (dispose the host then CreateHostAsync again on the SAME database name and continue — state survives), and inbox/outbox interaction (saga + projector stay consistent). Run the demo suite green. Commit, push, open the PR titled "demo: saga safety-net integration tests". Watch checks until green.

Stay strictly within Task S13's scope.
```

### S14 — cross-node saga progression (Balanced) *(model: Opus 4.8; after S12/S13 merged; shares the multinode harness)*

```
Execute Task S14 ("Cross-node exactly-once saga progression (Balanced)") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Precondition: verify the S12 and S13 PRs are merged to main and the multinode infrastructure (multinode plan Tasks 1–8) is on main. Then: branch test/saga-multinode from origin/main. Add src/Wolverine.MongoDB.Tests/saga_multinode.cs ([Trait("Category","multinode")], [Collection("mongodb")]) using two in-proc Balanced hosts, mirroring multinode_end_to_end.cs (UseTcpForControlEndpoint, short LockLeaseDuration, durable inbox, a shared static counter to observe process-wide saga application). Drive a saga start + several continue messages across both nodes; assert exactly-once application (no double-advance) and that completion deletes the doc. IMPORTANT (plan risk R11): if S8 OCC is in, two nodes racing the same saga will legitimately throw SagaConcurrencyException — wire the agreed retry policy (so the losing node retries rather than failing) BEFORE asserting; this retry is correct production behavior, not an assertion-weakening bandaid. Acceptance bar: FIVE consecutive green runs on net9.0 + net10.0. Same hard rules as multinode Task 7: no skips, no retries-as-a-bandaid, no assertion-weakening; if exactly-once fails after the legitimate OCC-retry is in place, that is a REAL coordination bug — write it up and stop. When stable: full suite green. Commit, push, open the PR titled "test: cross-node exactly-once saga progression". Watch checks until green.

Stay strictly within Task S14's scope.
```

---

## Phase 4 — Integration, Documentation & Verification

### S15 — full cross-feature regression *(model: Sonnet; only after S6–S14 PRs are all merged)*

```
Execute Task S15 ("Full cross-feature regression") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Precondition: verify with rtk git log origin/main --oneline -25 that the S6–S14 PRs are all merged. Then: branch test/saga-regression-sweep from origin/main (a branch only in case a CI tweak is needed). Run: the full library suite on both TFMs; --filter "Category=multinode" five consecutive times (all pass); dotnet pack the package-ref build; the demo build + integration suite (incl. the new saga tests). Confirm CI's library job runs the saga compliance in its single-node step and the demo job runs the saga tests against the fresh nupkg — the saga compliance is non-multinode so it should be auto-included; add a CI change ONLY if a coverage gap is found. Report a regression summary proving inbox + outbox + single-node + multi-node + saga all pass together. If a CI change was needed, commit, push, open the PR titled "test: full saga cross-feature regression"; otherwise report that no change was required. If anything is red, report it — do not paper over it.

Stay strictly within Task S15's scope.
```

### S16 — documentation sweep + upstream notes *(model: Sonnet; only after S6–S14 PRs are all merged; may run parallel to S15)*

```
Execute Task S16 ("Documentation sweep + upstream-contribution notes") of docs/superpowers/plans/2026-06-18-saga-persistence.md using the superpowers:executing-plans skill.

Precondition: verify the S6–S14 PRs are merged to main. Then: branch docs/saga-sweep from origin/main. Update README.md (saga usage: define a Saga subclass, supported id types, optimistic-concurrency behavior, the per-saga-type collection convention, replica-set/transaction requirement), CLAUDE.md (saga files in the file map + key design decisions: codegen-only, direct-document, native id, Version OCC, atomic with the outbox), FOLLOWUPS.md (deferred items — int/long if dropped, ISagaStoreDiagnostics), CHANGELOG.md (### Added saga persistence under [Unreleased]), and demo/README.md + demo/CLAUDE.md (the demo saga + how to run its flows). Add a short upstream-contribution-notes section (target Wolverine.MongoDb namespace/folder, the compliance subclasses to add, the deliberate deltas vs Cosmos/Raven: native id + SagaConcurrencyException). CRITICAL: verify every documented claim against the code on main before writing it. Commit, push, open the PR titled "docs: saga support + upstream contribution notes". Watch checks until green.

Stay strictly within Task S16's scope.
```

### S17 — final verification on `main` *(model: Sonnet; after S15 and S16 merge; no branch, no PR)*

```
Execute Task S17 ("Final verification on main") of docs/superpowers/plans/2026-06-18-saga-persistence.md.

This runs on main itself: rtk git checkout main && rtk git pull. Run the full library suite (both TFMs); run --filter "Category=multinode" five consecutive times (all pass); dotnet pack the package-ref build; run the demo integration suite. Confirm CI on main is green (library + demo). Review the merged history — one PR per task S1–S16, every file in the plan's File Structure Overview touched. Report a verification summary. If anything is red or missing, report it — do not fix anything in this session.

OPTIONAL (only if the user explicitly wants a release): per CLAUDE.md "Versioning & Release", invoke the release agent to publish a saga-enabled version and re-point the demo. Otherwise leave the saga work under CHANGELOG "## [Unreleased]". The saga plan is complete only when this report is clean.
```
