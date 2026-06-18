# Saga Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. The companion prompts file is `docs/superpowers/plans/2026-06-18-saga-task-prompts.md`.

> **Independent review (2026-06-18, Codex / gpt-5.5, read-only against source):** No critical contradiction; the commit/frame-ordering mitigation was *positively verified* — `SagaChain` calls `ApplyTransactionSupport()` **before** appending saga frames (`external/wolverine/src/Wolverine/Persistence/Sagas/SagaChain.cs:252,:284`), so skipping the postprocessor for `SagaChain` and committing via `CommitUnitOfWorkFrame` yields exactly one commit. Seven refinements were folded in below (search "🔎 review"): `CanPersist` must be saga-scoped (S6); per-saga-type collection cleanup is **mandatory**, not optional (S4/S6/S9); `DetermineSagaIdType` only governs envelope-header-only messages (S7); precise old/new-version OCC semantics + delete-guard decision (S8); **S8 now depends on S7** (false parallelism removed); a `SagaConcurrencyException` retry policy is required before S14; and id/OCC rationale should reference Wolverine's Marten/EF/lightweight-SQL providers, not Cosmos/Raven (which are string-only, no OCC).

**Goal:** Add native MongoDB **saga persistence** to `Wolverine.MongoDB` so that stateful, message-correlated workflows (`Wolverine.Saga` subclasses) load, insert, update, complete (delete), and version through the MongoDB persistence provider — exercising the same Wolverine code-generation contracts the Cosmos and RavenDb providers use. Deliver it with the upstream saga compliance suite passing, custom atomicity/concurrency/idempotency tests, a full set of demo saga flows acting as a regression safety net, and verification that inbox, outbox, single-node, multi-node, and saga persistence all keep working together. The end state must be a clean candidate for a future upstream contribution to Wolverine (a `Wolverine.MongoDb` persistence provider).

**Architecture:** Saga support in a Wolverine document-store provider is **entirely code-generation**, not a separate storage service. Wolverine core owns saga identity resolution, the `SagaChain`, and the load→handle→store/delete→commit lifecycle; the provider supplies an `IPersistenceFrameProvider` that emits the MongoDB read/write frames woven into the generated handler. `Wolverine.MongoDB` **already** has `MongoDbPersistenceFrameProvider : IPersistenceFrameProvider`, but its saga members throw `NotSupportedException`, `CanPersist` returns `false`, and `CanApply` does not match `chain is SagaChain`. This plan turns those stubs into real frames that persist the saga POCO directly as a MongoDB document keyed by `_id`, run inside the **existing** Wolverine-managed session/transaction (so saga state and the outbox commit atomically), and — going one step beyond Cosmos/Raven — support the saga's **native id type** (Guid/string/int/long) and **`Saga.Version` optimistic concurrency**. 🔎 review: mirror **Cosmos/Raven** for the *codegen frame shape and saga-chain postprocessor handling*, but mirror Wolverine's **Marten / EF Core / lightweight-SQL** providers for the *native-id and OCC rationale* — Cosmos/Raven are string-only and do not use `Saga.Version`, whereas lightweight SQL already does `Version`-guarded updates throwing `SagaConcurrencyException` (`external/wolverine/.../DatabaseSagaSchema.cs:99-110`), and Marten/EF resolve the native id type. The demo's `OrderRepository.UpdateAsync` is the local precedent for the guarded-`ReplaceOneAsync` pattern.

**Tech Stack:** .NET 9/10, MongoDB.Driver 3.x, WolverineFx 6.2.2 (pinned `external/wolverine` submodule), JasperFx.CodeGeneration (frames), xUnit + Shouldly (library) / FluentAssertions (demo), Testcontainers.MongoDb (replica set).

**PREREQUISITE: The Solo Hardening and Multinode plans must be fully merged to `main` first** (`2026-06-09-solo-hardening.md`, `2026-06-09-multinode-support.md`). This plan assumes everything they delivered: the working transactional frame (`TransactionalFrame` + `CommitMongoTransactionFrame`), `MongoDbUnitOfWork`, `MongoDbPersistenceOptions`, the owner-filtered/CAS recovery loops, the `AppFixture` test harness, the `[Category=multinode]` split in CI, and the config-driven demo durability mode. Saga support builds directly on the transaction frame and the test/demo harnesses those plans created.

---

## Verified Saga API Facts (established by discovery — do NOT re-derive or invent)

These were read directly from the pinned `external/wolverine` submodule (V6.2.2) and the local source during planning. Tasks below reference them; an implementing session should *confirm* them against the submodule (a one-line grep), not reinvent them.

**Saga base class** — `external/wolverine/src/Wolverine/Saga.cs`:
- `public abstract class Saga` with `public int Version { get; set; }`, `public bool IsCompleted()`, `protected void MarkCompleted()`.
- `public class SagaConcurrencyException : Exception` lives in the same file. `Version` is an `int` (aligns with `JasperFx.IRevisioned`).

**The only provider contract** — `external/wolverine/src/Wolverine/Persistence/IPersistenceFrameProvider.cs`. Saga-relevant members:
- `bool CanApply(IChain chain, IServiceContainer container)` — must return `true` for saga chains.
- `bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)` — saga creation support.
- `Type DetermineSagaIdType(Type sagaType, IServiceContainer container)`
- `Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)`
- `Frame DetermineInsertFrame(Variable saga, IServiceContainer container)`
- `Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)`
- `Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)`
- `Frame DetermineStoreFrame(Variable saga, IServiceContainer container)` (upsert; may throw `NotSupportedException`)
- `Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)`
- Plus `ApplyTransactionSupport(...)` (already implemented for the outbox).

**Closest template** — `external/wolverine/src/Persistence/Wolverine.CosmosDb/Internals/CosmosDbPersistenceFrameProvider.cs`. Its pattern, verbatim-relevant:
- `ApplyTransactionSupport`: adds the provider's `TransactionalFrame`; adds a flush postprocessor **only** `if (chain is not SagaChain)`.
- `CanApply`: `if (chain is SagaChain) return true;` first.
- `CanPersist`: `persistenceService = typeof(Container); return true;`
- `DetermineSagaIdType`: `return typeof(string);` (string-only).
- Load = `LoadDocumentFrame` → `ReadItemAsync<T>(id, PartitionKey.None)` with `try/catch CosmosException(NotFound) → default`.
- Insert/Update/Store = `CosmosDbUpsertFrame` → `UpsertItemAsync(saga)`.
- Delete = `CosmosDbDeleteDocumentFrame` → `DeleteItemAsync<T>(id, None)`.
- `CommitUnitOfWorkFrame` = `new FlushOutgoingMessages()` (Cosmos writes are immediate).
- RavenDb is analogous but sets `session.Advanced.UseOptimisticConcurrency = true` for `SagaChain` in its `TransactionalFrame`; it implements `ISagaStoreDiagnostics` (Cosmos does not). **Neither maps a concurrency conflict to `SagaConcurrencyException` for sagas.**

**Existing Mongo integration points** (local):
- `src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs` — the file to change. Saga members currently throw `const string SagaNotSupported`. `CanApply` matches `IClientSessionHandle`/`MongoDbUnitOfWork` params and `IMongoDatabase`/`IMongoClient`/`IMongoCollection<>` dependencies but **not** `chain is SagaChain`.
- `src/Wolverine.MongoDB/Internals/TransactionalFrame.cs` — `TransactionalFrame` opens `IClientSessionHandle` (`mongoSession`), builds `MongoDbUnitOfWork`, enlists the outbox, wraps the chain in `try/catch`. `CommitMongoTransactionFrame` commits the transaction then flushes outgoing. Both `IClientSessionHandle` and `IMongoDatabase` are resolvable codegen variables inside a chain.
- `src/Wolverine.MongoDB/WolverineMongoDbExtensions.cs` — registers `IMessageStore`, `IMongoDatabase`, `InsertFirstPersistenceStrategy<MongoDbPersistenceFrameProvider>()`, `ReferenceAssembly`.
- `src/Wolverine.MongoDB/Internals/MongoConstants.cs` — collection-name constants live here.
- `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Admin.cs` — collection/index creation; `RebuildAsync()` (used by `AppFixture.ClearAll()`).

**Acceptance oracle** — `external/wolverine/src/Testing/Wolverine.ComplianceTests/Sagas/`:
- `StringIdentifiedSagaComplianceSpecs<T>` and `GuidIdentifiedSagaComplianceSpecs<T>` (also `Int`/`Long` variants) are concrete `[Fact]` classes parameterized by `T : ISagaHost, new()`. Sample sagas: `StringBasicWorkflow` / `GuidBasicWorkflow : BasicWorkflow<TStart,TCompleteThree,TId> : Saga` (member named `Id`, plus `Name`, bool flags; `FinishItAll → MarkCompleted()`).
- Scenarios covered: `start_1`, `start_2` (wildcard start), `complete` (verifies the doc is **deleted**), cascading messages (`CompleteOne → CompleteTwo`), `straight_up_update_with_the_saga_id_on_the_message`, `unknown_state` (throws `UnknownSagaException`), update via envelope header, update via `[SagaIdentity]` attribute, and two indeterminate-id cases (throw `IndeterminateSagaStateIdException`). **No concurrency scenario** — OCC must be proven by custom tests.
- `ISagaHost` requires `Task<IHost> BuildHostAsync<TSaga>()` and four `LoadState<T>(id)` overloads (Guid/int/long/string). Cosmos/Raven implement only the `string` overload (others throw `NotSupportedException`). `LoadState` reads the saga **directly from the store**, not via Wolverine, to independently verify persistence.
- Reference host: `external/wolverine/src/Persistence/CosmosDbTests/saga_storage_compliance.cs` (`CosmosDbSagaHost`) — sets `CodeGeneration.GeneratedCodeOutputPath`, `TypeLoadMode.Auto`, `Discovery.IncludeType<…Workflow>()` + `IncludeAssembly`, registers the client, `UseCosmosDbPersistence`, `DurabilityMode.Solo`.

**Existing local harness** (from the prior plans):
- `src/Wolverine.MongoDB.Tests/AppFixture.cs` — `[CollectionDefinition("mongodb")]`, `const DatabaseName = "wolverine_tests"`, `IMongoClient Client`, `ClearAll()` (→ `Admin.RebuildAsync()`), `BuildMessageStore()`; `mongo:7` replica-set Testcontainer; `net9.0;net10.0`; `UseWolverineSource` switch.
- Demo (`demo/`): `OrderDemo.{Api,Application,Contracts,Domain,Infrastructure}` + `OrderDemo.IntegrationTests`. `Program.cs` wires `UseMongoDbPersistence`, config-driven durability, RabbitMQ, `AutoApplyTransactions()`. Handlers take `IClientSessionHandle` + repositories. `OrderRepository.UpdateAsync` already does a **Version-guarded `ReplaceOneAsync`** that throws on `ModifiedCount == 0` — the precedent for saga OCC. `OrdersFixture` (Testcontainers, per-test DB name via `CreateDatabaseName()`, Solo) drives flows with `host.TrackActivity().Timeout(...).InvokeMessageAndWaitAsync(...)` and asserts with FluentAssertions.

---

## Lead Open Design Decision (resolve in S4 — affects scope & critical path)

**How rich should id-type and concurrency support be?** Two coherent options:

- **Option A — Cosmos/Raven parity (minimal, lowest-risk).** `DetermineSagaIdType → typeof(string)`; string-id sagas only; upsert/last-write-wins; ignore `Saga.Version`. Passes `StringIdentifiedSagaComplianceSpecs`. Smallest diff, identical to the two closest providers, trivially upstreamable.
- **Option B — Native id + optimistic concurrency (recommended).** `DetermineSagaIdType` returns the saga's **actual** identity-member type; support Guid/string (and int/long) `_id` natively (MongoDB handles all idiomatically — the demo already stores Guid `_id`); use `Saga.Version` for a guarded update that throws `SagaConcurrencyException` on conflict. Passes **both** `String`- and `Guid`-identified compliance specs **plus** custom concurrency tests. A strictly better contribution that directly satisfies the user's explicit "optimistic concurrency" + "identity handling" requirements.

**Recommendation: Option B, decomposed so Option A is a self-contained earlier milestone.** S6 lands the string-id baseline (Cosmos parity, fast green compliance, fallback floor). S7 generalizes to native id types. S8 layers `Version` optimistic concurrency. If S7 or S8 hit an upstream-acceptability or codegen wall, they can be dropped without invalidating S6's contribution. **The plan is written for Option B; S4 formally records the choice (and an implementing team may downscope to A there).**

---

## Git & PR Workflow (one branch + one PR per task)

Identical mechanics to the multinode plan — see `2026-06-09-multinode-support.md` → "Git & PR Workflow" for the full rationale. Summary:

- **One git worktree per task** (safe for parallel agents); `.worktrees/` is gitignored. If you were dispatched with worktree isolation already, confirm with `git branch --show-current` and skip straight to the task steps.

```bash
# Start: isolated worktree off CURRENT main (includes merged prerequisite tasks)
rtk git fetch origin
rtk git worktree add .worktrees/<branch-name> -b <branch-name> origin/main
cd .worktrees/<branch-name>          # every later step runs HERE, incl. the Commit step

# ... execute the task's steps, ending with its Commit step ...

rtk git push -u origin <branch-name>
rtk gh pr create --base main --head <branch-name> \
  --title "<PR title>" \
  --body "<what was missing, what changed, how it's tested. Reference docs/superpowers/plans/2026-06-18-saga-persistence.md Task Sx.>"
rtk gh pr checks --watch

# After MERGE: drop the worktree from the main checkout
cd <repo-root>
rtk git worktree remove .worktrees/<branch-name>
```

- `--head <branch-name>` is required. Commit messages end with the `Co-Authored-By: Claude <noreply@anthropic.com>` trailer; PR bodies end with the `🤖 Generated with [Claude Code](https://claude.com/claude-code)` line.
- **A dependent task starts only after its dependency's PR is merged.** Each PR must be independently green: the task's own tests plus the full library suite (and, where the task touches it, the demo suite).
- **Verify PR check state with `gh pr view --json statusCheckRollup`** (the rtk-summarized `--watch` tail can mislead).
- When finishing a task, **update this plan doc** in the task's PR: tick the step checkboxes and the status-table row (follow the repo's "mark plan task done" convention).

---

## Task Table

| Task | Branch | PR title | Depends on | Blocking status | Model |
|---|---|---|---|---|---|
| **S1** | `docs/saga-repo-analysis` | docs: saga — repository & convention analysis | Prereqs merged | ✅ Done | Sonnet |
| **S2** | `docs/saga-wolverine-api` | docs: saga — Wolverine saga API discovery | Prereqs merged | ✅ Done | Sonnet |
| **S3** | `docs/saga-cosmos-raven-compare` | docs: saga — Cosmos/RavenDb implementation comparison | Prereqs merged | Can start immediately | Sonnet |
| **S4** | `docs/saga-document-model-design` | docs: saga — MongoDB document model + identity + concurrency design | **S1, S2, S3** | Blocked by: S1, S2, S3 | **Opus / Fable 5** |
| **S5** | `docs/saga-demo-and-test-inventory` | docs: saga — demo flow design + test inventory | Prereqs merged | ✅ **Done** — merged #89 | Sonnet |
| **S6** | `feat/saga-codegen-string` | feat: MongoDB saga persistence (string-id baseline) | **S4** | Blocked by: S4 | **Fable 5 / Opus** |
| **S7** | `feat/saga-native-id-types` | feat: native Guid/int/long saga id support | **S6** | Blocked by: S6 | **Fable 5 / Opus** |
| **S8** | `feat/saga-optimistic-concurrency` | feat: saga optimistic concurrency via Saga.Version | **S6, S7** | Blocked by: S6, S7 | **Fable 5 / Opus** |
| **S9** | `test/saga-string-compliance` | test: string-identified saga storage compliance | **S6** (host skeleton needs only S2/S3) | Partially blocked by: S6 | Sonnet |
| **S10** | `test/saga-guid-compliance` | test: Guid/int/long-identified saga storage compliance | **S7, S9** | Blocked by: S7, S9 | Sonnet |
| **S11** | `test/saga-atomicity-concurrency` | test: saga atomicity, OCC, completion & idempotency | **S6, S8** (skeleton earlier) | Partially blocked by: S6, S8 | **Fable 5 / Opus** |
| **S12** | `demo/saga-order-fulfillment` | demo: OrderFulfillmentSaga contracts, saga & handlers | **S6, S7 merged** (contracts/skeleton need only S5) | Partially blocked by: S6, S7 | Sonnet |
| **S13** | `demo/saga-safety-net-tests` | demo: saga safety-net integration tests (Solo) | **S12** | Blocked by: S12 | Sonnet |
| **S14** | `test/saga-multinode` | test: cross-node exactly-once saga progression (Balanced) | **S12, S13** (+ multinode infra, merged) | Blocked by: S12, S13 | **Opus 4.8** |
| **S15** | *(no branch — runs on a verification branch)* | test: full cross-feature regression (inbox+outbox+solo+multinode+saga) | **S6–S14 merged** | Blocked by: S6–S14 | Sonnet |
| **S16** | `docs/saga-sweep` | docs: saga support documentation + upstream-contribution notes | **S6–S14 merged** | Blocked by: S6–S14 | Sonnet |
| **S17** | *(no branch/PR)* | final verification on `main` (+ optional release) | **S15, S16 merged** | Blocked by: S15, S16 | Sonnet |

> **Recommended execution order.** S1/S2/S3/S5 run **fully in parallel** (4 sessions) the moment the plan PR merges. S4 (design gate) follows once S1–S3 land. S6 is the **single most important task** and the head of the critical path. Once S6 merges, S7, S8, S9 (green), S11, and S12 (skeleton→build) fan out in parallel; S10 follows S7; the demo track S12→S13→S14 runs parallel to the library-test track S9/S10/S11. S15/S16 close out once everything is merged; S17 is the final on-`main` gate.

## Model Guidance

The risk here is **code-generation correctness and frame ordering**, not transcription (Agent `model`: `sonnet`, `opus`, `fable`):

- **Fable 5 / Opus mandatory** for **S6, S7, S8**: emitting `Frame`s and getting the generated load→handle→store→commit ordering right (so saga state and the outbox commit atomically, and the completed saga is deleted) is iterative work that requires reading the generated handler source (`codeFor<T>()` / `GeneratedCodeOutputPath`). A subtly wrong frame still compiles and can pass *some* compliance facts while corrupting atomicity. **S4** (the design gate) is also Opus/Fable — the id/concurrency decision shapes every downstream task.
- **Opus 4.8** for **S14**: cross-node saga exactly-once is the same class of flaky-timing diagnosis as multinode Task 7 — reason about *why* before turning a knob; no skips, no retries, no assertion-weakening.
- **Fable 5 / Opus** for **S11**: atomicity/rollback and concurrency-conflict tests simulate races; a wrong test passes most runs.
- **Sonnet** for S1, S2, S3, S5 (research/writing), S9, S10, S12, S13, S15, S16 (fully specified against a green oracle), S17 (verification).
- **Do not use Haiku** anywhere in this plan.
- **Escalation rule:** two non-obvious verification failures, or a broken plan assumption (an API that differs from the Verified Facts above), means **stop and report** — re-dispatch on Fable 5 with the failure context rather than improvising a different Wolverine API. For S6/S7/S8/S14 specifically, if green cannot be reached after the listed levers, write up the generated code + failing facts and stop.
- **Code review between tasks:** Fable 5/Opus, with extra scrutiny on S6/S7/S8 — atomicity bugs that pass a green suite are exactly what review must catch.

---

## File Structure Overview

| File | Change |
|---|---|
| `src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs` | Flip `CanApply`/`CanPersist`/`DetermineSagaIdType`; implement Load/Insert/Update/Store/Delete/Commit saga frames; saga-aware `ApplyTransactionSupport` (S6, S7, S8) |
| `src/Wolverine.MongoDB/Internals/SagaFrames.cs` | **New** — `LoadSagaFrame`, `StoreSagaFrame` (insert/upsert/OCC), `DeleteSagaFrame` (S6, S8) |
| `src/Wolverine.MongoDB/Internals/MongoConstants.cs` | Saga collection-naming helper / convention (S6) |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Admin.cs` | **Required** — `ClearAllAsync`/`RebuildAsync` only clear the known Wolverine system collections today (`:67-78`); they MUST also drop the per-saga-type collections (e.g. every `wolverine_saga_*` collection) or compliance facts leak between tests on the shared fixture DB (S6) |
| `src/Wolverine.MongoDB/WolverineMongoDbExtensions.cs` | (Optional) register `ISagaStoreDiagnostics` for parity with RavenDb (S6/S16) |
| `src/Wolverine.MongoDB.Tests/MongoDbSagaHost.cs` | **New** — `ISagaHost` implementation (S9) |
| `src/Wolverine.MongoDB.Tests/string_saga_storage_compliance.cs` | **New** — `: StringIdentifiedSagaComplianceSpecs<MongoDbSagaHost>` (S9) |
| `src/Wolverine.MongoDB.Tests/guid_saga_storage_compliance.cs` | **New** — Guid (+ int/long) compliance (S10) |
| `src/Wolverine.MongoDB.Tests/saga_atomicity.cs` | **New** — saga+outbox atomicity, completion delete, OCC conflict, duplicate-message idempotency (S11) |
| `src/Wolverine.MongoDB.Tests/saga_multinode.cs` | **New** — `[Category=multinode]` cross-node exactly-once saga progression (S14) |
| `demo/src/OrderDemo.Contracts/...` | **New** — saga trigger/continue/complete messages (S12) |
| `demo/src/OrderDemo.Application/Sagas/OrderFulfillmentSaga.cs` | **New** — the demo saga (S12) |
| `demo/src/OrderDemo.Api/Program.cs` | Discovery + routing for saga messages (S12) |
| `demo/tests/OrderDemo.IntegrationTests/SagaFlowTests.cs` | **New** — all saga flows as safety-net tests (S13) |
| `docs/superpowers/plans/2026-06-18-saga-*.md` | This plan, the prompts, and the S1–S5 discovery/design notes |
| `README.md`, `CLAUDE.md`, `FOLLOWUPS.md`, `CHANGELOG.md`, `demo/README.md`, `demo/CLAUDE.md` | Documentation (S16) |
| `.github/workflows/ci.yml` | Verify saga compliance runs in `library` job and demo saga tests in `demo` job (likely **no change** — confirm in S15) |

---

## Phase 0 — Discovery & Design (parallelizable)

### Task S1: Repository & convention analysis

- **Goal:** Produce a precise, current map of where saga support plugs into `Wolverine.MongoDB` and the conventions new code must follow, so implementation tasks never guess.
- **Scope:** Read-only analysis of the existing provider, transaction frame, extension method, constants, Admin collection/index code, the test `AppFixture`/compliance patterns, and the demo's repository/session pattern (esp. `OrderRepository.UpdateAsync` OCC). Output a short notes doc. No code changes.
- **Expected output:** `docs/superpowers/plans/2026-06-18-saga-repo-analysis.md` listing: the three flips needed in `MongoDbPersistenceFrameProvider`; how `IClientSessionHandle`/`IMongoDatabase` are codegen-resolvable; how `RebuildAsync` clears collections; the `AppFixture` members; the demo OCC precedent. Confirms the "Verified Saga API Facts" local section against the code on `main`.
- **Files/areas likely to change:** docs only.
- **Dependencies:** none (prereq plans merged).
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Read and summarize `MongoDbPersistenceFrameProvider.cs`, `TransactionalFrame.cs`, `WolverineMongoDbExtensions.cs`, `MongoConstants.cs`, `MongoDbMessageStore.Admin.cs`.
- [x] **Step 2:** Read and summarize `AppFixture.cs`, one compliance subclass (`message_store_compliance.cs`), and the demo `OrderRepository.cs` + `PlaceOrderHandler.cs`.
- [x] **Step 3:** Write the notes doc; cross-check each "Verified Fact" still holds on `main`; flag any drift.
- [x] **Step 4:** Commit (`docs: saga repository & convention analysis`).

### Task S2: Wolverine saga API discovery

- **Goal:** Pin the exact Wolverine core saga contracts the implementation must satisfy, from the pinned submodule.
- **Scope:** Read-only study of `external/wolverine`: `Saga.cs`, `IPersistenceFrameProvider.cs`, `SagaChain` + saga identity resolution (`SagaIdentityAttribute`, `[SagaIdentity]`, `{Saga}Id`/`Id`/`SagaId` precedence, `ValidSagaIdTypes`), the exception types (`UnknownSagaException`, `IndeterminateSagaStateIdException`, `SagaConcurrencyException`), and how `CommitUnitOfWorkFrame`/`DetermineStoreFrame` participate in the generated chain.
- **Expected output:** `docs/superpowers/plans/2026-06-18-saga-wolverine-api.md` — signatures + file paths for every saga member; the identity-resolution precedence; the supported id types; which exception each failure mode expects.
- **Files/areas likely to change:** docs only.
- **Dependencies:** none.
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Grep/read the files above in `external/wolverine/src/Wolverine/Persistence/Sagas/` and `Saga.cs`.
- [x] **Step 2:** Trace how `SagaChain` calls the provider's `DetermineLoadFrame`/`Insert`/`Update`/`Delete`/`CommitUnitOfWorkFrame` and in what order relative to the handler call.
- [x] **Step 3:** Write the notes doc with exact signatures. Commit (`docs: Wolverine saga API discovery`).

### Task S3: Cosmos & RavenDb saga implementation comparison

- **Goal:** Extract the concrete document-store template (and the deltas a MongoDB provider should make).
- **Scope:** Read-only study of `Wolverine.CosmosDb` and `Wolverine.RavenDb` saga code: their `IPersistenceFrameProvider` saga methods, the load/upsert/delete frames, `ApplyTransactionSupport`'s `chain is SagaChain` handling, registration, and their `*SagaHost`/`saga_storage_compliance` subclasses. Note where each ignores `Saga.Version` and that neither maps `SagaConcurrencyException`.
- **Expected output:** `docs/superpowers/plans/2026-06-18-saga-cosmos-raven-compare.md` — a side-by-side table and an explicit "MongoDB should mirror Cosmos for structure, add native-id + Version OCC like the demo's `OrderRepository`" recommendation, with the exact frame snippets to adapt.
- **Files/areas likely to change:** docs only.
- **Dependencies:** none.
- **Blocking status:** **Can start immediately.**

- [ ] **Step 1:** Read `CosmosDbPersistenceFrameProvider.cs`, `LoadDocumentFrame.cs`, `TransactionalFrame.cs`, `WolverineCosmosDbExtensions.cs`, `CosmosDbTests/saga_storage_compliance.cs`.
- [ ] **Step 2:** Read the RavenDb equivalents incl. `RavenDbSagaStoreDiagnostics.cs` and the `UseOptimisticConcurrency` line.
- [ ] **Step 3:** Write the comparison doc + recommendation. Commit (`docs: Cosmos/RavenDb saga comparison`).

### Task S4: MongoDB saga document model + identity + concurrency design (DESIGN GATE)

- **Goal:** Make and record the binding design decisions every implementation task depends on: document shape, collection strategy, `_id` mapping, supported id types, and the concurrency model. Resolve the **Lead Open Design Decision** (Option A vs B).
- **Scope:** Design-only synthesis of S1–S3. Decide and justify:
  1. **Document shape:** store the saga POCO **directly** (no envelope wrapper), matching Cosmos/Raven; the member named `Id` maps to `_id` via the driver's default convention (verify against the demo's Guid `_id`).
  2. **Collection strategy:** **one collection per saga type** (recommended — idiomatic Mongo, no cross-type `_id` collision; Cosmos crams all into one container only because it has one). Define the naming helper (e.g. sanitized saga type name, or `wolverine_saga_{name}`). 🔎 review: cleanup is **mandatory, not optional** — `AppFixture` uses a fixed DB and `ClearAll()`→`RebuildAsync()`→`ClearAllAsync()` only clears the known system collections (`MongoDbMessageStore.Admin.cs:67-78`), so per-saga collections would leak between compliance facts. Decide the mechanism now: either drop all `wolverine_saga_*`-prefixed collections in `ClearAllAsync`, **or** use a per-test database for saga compliance.
  3. **Id types:** Option B — `DetermineSagaIdType` returns the saga's native identity-member type; support Guid + string (+ int/long). Document the Option A fallback (string-only) explicitly.
  4. **Concurrency:** use `Saga.Version` — guarded `ReplaceOneAsync` on `(_id, Version)`, increment on write, throw `SagaConcurrencyException` on `ModifiedCount == 0` (mirror `OrderRepository.UpdateAsync`). Insert sets the initial version.
  5. **Frame ordering / atomicity:** saga reads/writes run on the **`TransactionalFrame` session**; saga writes must be emitted **before** the commit so saga state + outbox commit atomically. Prescribe the Cosmos-style rule: in `ApplyTransactionSupport`, add the commit postprocessor **only** `if (chain is not SagaChain)`, and let `CommitUnitOfWorkFrame` emit commit+flush for saga chains — to be confirmed against generated code in S6.
  6. **No global serializer mutation** (consistent with the per-property BSON-date decision): rely on default `_id` conventions; add `[BsonId]`/`[BsonRepresentation]` only if a compliance fact proves it necessary, and only on test/demo saga types (never the host registry).
  7. **Upstream-readiness:** note the target namespace/folder if contributed (`Wolverine.MongoDb`), and that native-id + OCC are deliberate improvements over Cosmos/Raven to call out in the PR.
- **Expected output:** `docs/superpowers/plans/2026-06-18-saga-document-model-design.md` — the decisions above with rationale, the chosen Option (A or B) stated unambiguously, and the exact frame/collection contracts S6–S8 will implement.
- **Files/areas likely to change:** docs only.
- **Dependencies:** **S1, S2, S3.**
- **Blocking status:** **Blocked by: S1, S2, S3.**

- [ ] **Step 1:** Synthesize S1–S3 notes; resolve each decision 1–7 above.
- [ ] **Step 2:** Write the design doc; state the chosen Option and the downscope path. Commit (`docs: MongoDB saga document model & concurrency design`).

### Task S5: Demo saga flow design + library/demo test inventory

- **Goal:** Define the demo saga (covering every required flow) and a complete inventory of library + demo tests, so test skeletons and contracts can be authored early in parallel with implementation.
- **Scope:** Design-only. Specify the demo **`OrderFulfillmentSaga`** keyed on `OrderId` (Guid), and map each required flow to a concrete trigger/assertion:
  - **Start:** `OrderPlacedApplicationEvent` (already emitted by `PlaceOrderHandler`) starts the saga.
  - **Continue:** `OrderShippedApplicationEvent` advances it; a new `ConfirmDeliveryCommand` advances further.
  - **Complete:** delivery confirmed → `MarkCompleted()` (saga doc deleted).
  - **Missing state:** a `ConfirmDeliveryCommand`/ship event for an unknown order id → `UnknownSagaException` path.
  - **Duplicate/repeated messages:** re-deliver the same event; inbox idempotency + saga guard keep state correct.
  - **Across restarts:** start the saga, dispose the host, rebuild on the same DB, send a continue message → state survives.
  - **Inbox/outbox interaction:** saga handlers cascade events through the durable outbox; the projector/inbox stays consistent.
  - **Single vs multi-node:** Solo for the safety-net suite; a Balanced two-instance scenario validates exactly-once saga progression (S14).
  - Also specify a **scheduled timeout** option (e.g. `FulfillmentTimedOut`) to exercise saga + scheduled-message + multi-node together (optional/stretch).
- **Expected output:** `docs/superpowers/plans/2026-06-18-saga-demo-and-test-inventory.md` — the saga state shape, the message contracts, the handler signatures, and a table mapping each flow → library test and/or demo test (file + assertion). Confirms the library compliance subclasses to add (String + Guid) and the custom test list.
- **Files/areas likely to change:** docs only.
- **Dependencies:** none to start; **refine after S4** (id type + collection decisions feed the saga state shape).
- **Blocking status:** **Can start immediately** (finalize the saga state/id shape once S4 lands).

- [x] **Step 1:** Draft the saga + contracts + handler signatures reusing existing demo events where possible.
- [x] **Step 2:** Build the flow→test mapping table (library + demo). Commit (`docs: saga demo flow design + test inventory`).

---

## Phase 1 — Persistence Implementation

### Task S6: MongoDB saga persistence — string-id baseline (Cosmos parity)

- **Goal:** Turn the throwing saga stubs into working frames so string-identified sagas load/insert/update/complete through MongoDB inside the existing transaction. Pass `StringIdentifiedSagaComplianceSpecs`.
- **Scope:** `MongoDbPersistenceFrameProvider` saga members + a new `SagaFrames.cs`. **Preserve all inbox/outbox/transaction behavior** — the only `ApplyTransactionSupport` change is the saga-chain branch. No id-type generalization yet (string only), no OCC yet.
- **Expected output:** Saga chains generate code that, inside the `TransactionalFrame` session: loads the saga (null if absent), runs the handler, upserts on insert/update / deletes on completion, then commits+flushes. `string_saga_storage_compliance` (delivered in S9) is green. Full library suite green.
- **Files/areas likely to change:** `MongoDbPersistenceFrameProvider.cs`, new `Internals/SagaFrames.cs`, `MongoConstants.cs` (collection-name helper), **and `MongoDbMessageStore.Admin.cs` (REQUIRED — `ClearAllAsync`/`RebuildAsync` must drop the per-saga-type collections, see S4 #2)**.
- **Dependencies:** **S4.**
- **Blocking status:** **Blocked by: S4.**

Implementation shape (adapt from Cosmos; confirm the generated order with `codeFor<T>()`):

```csharp
// MongoDbPersistenceFrameProvider.cs — the three flips
public bool CanApply(IChain chain, IServiceContainer container)
{
    if (chain is SagaChain) return true;          // NEW — without this saga chains are ignored
    // ... existing IClientSessionHandle / MongoDbUnitOfWork / IMongoDatabase / IMongoClient / IMongoCollection<> checks unchanged
}

public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
{
    persistenceService = typeof(IMongoDatabase);
    // 🔎 review: do NOT return true unconditionally. Cosmos can, because it also implements
    // DetermineStorageActionFrame (CosmosDbStorageActionApplier). Mongo's DetermineStorageActionFrame
    // still throws, so an unconditional true advertises generic [Entity]/Insert<T>/Update<T>/
    // IStorageAction<T> persistence and then throws at codegen. Scope it to sagas:
    return entityType.CanBeCastTo<Saga>();         // was: return false  (needs JasperFx.Core.Reflection + Wolverine)
}

public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    => typeof(string);                             // baseline; S7 generalizes

public Frame DetermineLoadFrame(IServiceContainer c, Type sagaType, Variable sagaId)
    => new LoadSagaFrame(sagaType, sagaId);
public Frame DetermineInsertFrame(Variable saga, IServiceContainer c) => new StoreSagaFrame(saga, upsert: true);
public Frame DetermineUpdateFrame(Variable saga, IServiceContainer c) => new StoreSagaFrame(saga, upsert: true);
public Frame DetermineStoreFrame(Variable saga, IServiceContainer c)  => DetermineUpdateFrame(saga, c);
public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer c) => new DeleteSagaFrame(sagaId, saga);
public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer c) => new CommitMongoTransactionFrame();
```

```csharp
// ApplyTransactionSupport — mirror Cosmos's saga-chain handling so commit isn't double-emitted
public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
{
    if (!chain.Middleware.OfType<TransactionalFrame>().Any())
    {
        chain.Middleware.Add(new TransactionalFrame(chain));
        if (chain is not SagaChain)                // saga chains get commit+flush from CommitUnitOfWorkFrame
            chain.Postprocessors.Add(new CommitMongoTransactionFrame());
    }
}
```

```csharp
// SagaFrames.cs — frames resolve the TransactionalFrame session + database and operate within the transaction.
// LoadSagaFrame: var saga = await db.GetCollection<TSaga>(coll)
//     .Find(session, Builders<TSaga>.Filter.Eq("_id", sagaId)).FirstOrDefaultAsync(ct);
// StoreSagaFrame (upsert): await db.GetCollection<TSaga>(coll)
//     .ReplaceOneAsync(session, Builders<TSaga>.Filter.Eq("_id", saga.Id), saga,
//         new ReplaceOptions { IsUpsert = true }, ct);
// DeleteSagaFrame: await db.GetCollection<TSaga>(coll)
//     .DeleteOneAsync(session, Builders<TSaga>.Filter.Eq("_id", sagaId), cancellationToken: ct);
// `coll` = MongoConstants.SagaCollectionName(typeof(TSaga)); FindVariables() resolves IClientSessionHandle + IMongoDatabase + CancellationToken.
```

- [ ] **Step 1:** Implement `SagaFrames.cs` and the provider flips above.
- [ ] **Step 2:** Add a temporary scratch saga + handler in the **test** project (or reuse the compliance `StringBasicWorkflow`) and dump generated code via `codeFor<T>()`; confirm order: open session → load → handler → upsert/delete → commit → flush, all in the try-block.
- [ ] **Step 3:** Stand up the `MongoDbSagaHost` + `string_saga_storage_compliance` from S9 **in this branch** as the oracle (or land S9's host file here and the spec in S9) and make them green.
- [ ] **Step 4:** Run the full suite. Commit (`feat: MongoDB saga persistence (string-id baseline)`).

> **Coordination note:** S6 and S9 are tightly coupled (impl needs the oracle). Recommended: author `MongoDbSagaHost.cs` + `string_saga_storage_compliance.cs` **in the S6 branch** so S6 ships green, and let S9 be a thin "host hardening + int/long stubs" follow-up — OR run them as one combined session. The table keeps them separate for the dependency graph; collapse if a single agent owns both.

### Task S7: Native Guid/int/long saga id support

- **Goal:** Generalize beyond string so the saga's native id type is honored; pass `GuidIdentifiedSagaComplianceSpecs` (and Int/Long).
- **Scope:** `DetermineSagaIdType` returns the resolved identity-member type; frames key `_id` with the typed variable; confirm BSON maps Guid `_id` (the demo already does). No new public API.
- **Expected output:** `guid_saga_storage_compliance` (S10) green; string compliance still green; full suite green.
- **Files/areas likely to change:** `MongoDbPersistenceFrameProvider.cs` (`DetermineSagaIdType`), `SagaFrames.cs` (typed filters).
- **Dependencies:** **S6.**
- **Blocking status:** **Blocked by: S6.**

- [ ] **Step 1:** Implement `DetermineSagaIdType` exactly like Wolverine's own lightweight provider: `SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType()` (`external/wolverine/.../LightweightSagaPersistenceFrameProvider.cs:80-82`) — do **not** hand-roll reflection. 🔎 review: scope-check — core only calls `DetermineSagaIdType` for the **envelope-header-only** identity path (`SagaChain.cs:290-292`); when the message *has* a saga-id member, `PullSagaIdFromMessageFrame` uses that member's type directly. So the load/delete frames must key `_id` off whatever typed `sagaId` variable they're handed (Guid/int/long/string), and `DetermineSagaIdType` mainly governs the header-only case. Confirm against S2 notes.
- [ ] **Step 2:** Ensure frames build `_id` filters from the typed variable; verify Guid `_id` round-trips (add a focused assertion).
- [ ] **Step 3:** Make S10's Guid (+ int/long) compliance green; re-run string compliance + full suite. Commit (`feat: native Guid/int/long saga id support`).

### Task S8: Saga optimistic concurrency via `Saga.Version`

- **Goal:** Detect concurrent saga updates and surface `SagaConcurrencyException`, going beyond Cosmos/Raven (which last-write-wins).
- **Scope:** Make `StoreSagaFrame`'s **update** path version-guarded with **precise old/new-version semantics** (🔎 review — "filter on `(_id, Version)` and increment" is ambiguous and a naive reading fails every update). The exact contract: capture `oldVersion = saga.Version`; set `saga.Version = oldVersion + 1`; `ReplaceOneAsync` with filter `(_id == sagaId && Version == oldVersion)` and `IsUpsert = false`; throw `SagaConcurrencyException` when `ModifiedCount == 0`. **Insert** is unguarded and sets the initial `Version` (e.g. `1`). This forces insert and update frames to **diverge** (the S6 baseline used upsert for both — revisit that here). **Delete (completion):** decide and document whether the delete is version-guarded — recommended **not** guarded, matching Wolverine's lightweight SQL provider (`DatabaseSagaSchema.cs:113-119`); state the choice explicitly.
- **Expected output:** A custom concurrency test (in S11) proves a stale-version update throws `SagaConcurrencyException`; all compliance specs still green (they don't exercise concurrency, so must not regress).
- **Files/areas likely to change:** `SagaFrames.cs`, `MongoDbPersistenceFrameProvider.cs` (insert vs update frame distinction).
- **Dependencies:** **S6, S7.** 🔎 review: S8 and S7 both edit `MongoDbPersistenceFrameProvider.cs` **and** `SagaFrames.cs`, and the version guard must work for every id type — so S8 depends on S7 (not just S6). If you must run S8 before S7, scope OCC to string ids and add an explicit follow-up merge task.
- **Blocking status:** **Blocked by: S6, S7.**

- [ ] **Step 1:** Split insert (unguarded; set initial `Version`) from update (capture `oldVersion`, set `Version = oldVersion + 1`, guarded `ReplaceOneAsync` filter `(_id, oldVersion)`, throw `SagaConcurrencyException` on `ModifiedCount == 0`). Decide+document the completion-delete guard (recommended: unguarded).
- [ ] **Step 2:** Verify generated code; ensure the throw aborts the transaction (rolls back saga + outbox together).
- [ ] **Step 3:** Land the OCC test from S11 as the oracle; re-run all compliance + full suite. Commit (`feat: saga optimistic concurrency via Saga.Version`).

---

## Phase 2 — Library Tests

### Task S9: `MongoDbSagaHost` + string-identified compliance

- **Goal:** Provide the reusable `ISagaHost` and the string compliance subclass — the primary acceptance oracle.
- **Scope:** New `MongoDbSagaHost.cs` (mirror `CosmosDbSagaHost`: Solo, `GeneratedCodeOutputPath`, `TypeLoadMode.Auto`, `Discovery.IncludeType<StringBasicWorkflow>()` + assembly, register `_fixture.Client`, `UseMongoDbPersistence(AppFixture.DatabaseName)`; `LoadState<T>(string)` reads the saga directly from Mongo; other overloads throw `NotSupportedException` until S10). New `string_saga_storage_compliance : StringIdentifiedSagaComplianceSpecs<MongoDbSagaHost>`, `[Collection("mongodb")]`.
- **Expected output:** All `StringIdentified…` facts green.
- **Files/areas likely to change:** `src/Wolverine.MongoDB.Tests/MongoDbSagaHost.cs`, `string_saga_storage_compliance.cs`.
- **Dependencies:** host skeleton needs only S2/S3 facts; **green requires S6**.
- **Blocking status:** **Partially blocked by: S6** (skeleton authorable earlier; see the S6 coordination note).

- [ ] **Step 1:** Write `MongoDbSagaHost` (clear the fixture DB before build; `LoadState` via `Client.GetDatabase(DatabaseName).GetCollection<T>(coll).Find(Eq("_id", id)).FirstOrDefaultAsync()`).
- [ ] **Step 2:** Add the compliance subclass; run `--filter "FullyQualifiedName~string_saga_storage_compliance"` → green. Full suite green. Commit.

### Task S10: Guid/int/long-identified compliance

- **Goal:** Prove native id-type support across all Wolverine saga id types.
- **Scope:** New `guid_saga_storage_compliance : GuidIdentifiedSagaComplianceSpecs<MongoDbSagaHost>` (+ int/long subclasses if S7 covers them); implement the matching `LoadState<T>(Guid/int/long)` overloads on `MongoDbSagaHost`.
- **Expected output:** Guid (+ int/long) facts green; string still green.
- **Files/areas likely to change:** `guid_saga_storage_compliance.cs`, `MongoDbSagaHost.cs` (add overloads).
- **Dependencies:** **S7, S9.**
- **Blocking status:** **Blocked by: S7, S9.**

- [ ] **Step 1:** Implement the typed `LoadState` overloads.
- [ ] **Step 2:** Add the Guid (and int/long) compliance subclasses; run them green + full suite. Commit.

### Task S11: Saga atomicity, concurrency, completion & idempotency tests

- **Goal:** Cover what the compliance suite does **not**: transactional atomicity of saga-state + outbox, completion deletion, optimistic-concurrency conflict, and duplicate-message idempotency.
- **Scope:** Custom `[Collection("mongodb")]` tests built on `AppFixture`, modeled on `OutboxAtomicityTests` and the existing plain integration tests:
  1. **Atomicity:** a saga handler that writes saga state **and** cascades an outgoing message; force a post-handler failure and assert **neither** the saga doc **nor** the outgoing envelope persisted (rolled back together); success path persists both.
  2. **Completion:** `MarkCompleted()` deletes the saga doc.
  3. **OCC (after S8):** two stores load the same saga; one updates; the other's stale-version update throws `SagaConcurrencyException` and does not clobber.
  4. **Idempotency:** redeliver the **same envelope** (same envelope id, via the durable inbox) → saga state correct, not double-applied. 🔎 review: inbox idempotency keys on envelope identity, *not* business payload — a brand-new message with an identical payload legitimately re-runs saga logic. Test "same-envelope redelivery" here; only add a semantic-duplicate test if the saga itself carries a guard.
- **Expected output:** All four green; no regression to compliance or the full suite.
- **Files/areas likely to change:** `src/Wolverine.MongoDB.Tests/saga_atomicity.cs` (+ a small in-test saga type).
- **Dependencies:** **S6** (atomicity/completion/idempotency), **S8** (OCC).
- **Blocking status:** **Partially blocked by: S6, S8** (skeleton + non-OCC tests can land after S6; the OCC test after S8).

- [ ] **Step 1:** Skeleton + atomicity/completion/idempotency tests (post-S6).
- [ ] **Step 2:** Add the OCC conflict test (post-S8). Run green + full suite. Commit.

---

## Phase 3 — Demo

### Task S12: Demo `OrderFulfillmentSaga` (contracts, saga, handlers, wiring)

- **Goal:** Add a realistic saga to the demo that exercises every required flow, acting as a living reference and a regression safety net.
- **Scope:** Per S5's design. New contracts (e.g. `ConfirmDeliveryCommand`, `FulfillmentCompleted`/timeout messages); `OrderFulfillmentSaga` (Guid id = OrderId; starts on `OrderPlacedApplicationEvent`, continues on `OrderShippedApplicationEvent` + `ConfirmDeliveryCommand`, completes via `MarkCompleted()`); `Program.cs` discovery/routing. Keep saga writes inside the Wolverine transaction (no manual session needed — the generated saga frames handle it). Preserve all existing order flows.
- **Expected output:** Demo builds against the saga-enabled package (a fresh local/CI nupkg from S6/S7) and runs; the saga progresses start→continue→complete.
- **Files/areas likely to change:** `demo/src/OrderDemo.Contracts/...`, `demo/src/OrderDemo.Application/Sagas/OrderFulfillmentSaga.cs`, `demo/src/OrderDemo.Api/Program.cs`.
- **Dependencies:** contracts/saga/handler **skeletons** need only S5; **building/running requires S6 (+S7 for Guid id) merged and packed** (the demo consumes the package, per CI's `0.0.0-ci` nupkg; for local dev, pack the library first).
- **Blocking status:** **Partially blocked by: S6, S7** (author skeletons against S5 early; compile/run once a saga-enabled package exists).

- [ ] **Step 1:** Add contracts + `OrderFulfillmentSaga` + handlers; wire discovery/routing in `Program.cs`.
- [ ] **Step 2:** Build the demo against a saga-enabled package (local pack or CI nupkg); manual smoke: place → ship → confirm delivery → saga doc gone. Commit.

### Task S13: Demo saga safety-net integration tests (Solo)

- **Goal:** Lock the saga flows behind automated tests so future library changes can't silently break them.
- **Scope:** New `SagaFlowTests.cs` on `OrdersFixture` (Solo, per-test DB, `TrackActivity().InvokeMessageAndWaitAsync`, FluentAssertions). One test per flow: start, continue, complete (doc deleted), missing-state, duplicate/repeated message, **across-restart** (dispose host, rebuild on same DB name, continue), inbox/outbox interaction (saga + projector consistent).
- **Expected output:** All flows green in the demo suite; existing demo tests unaffected.
- **Files/areas likely to change:** `demo/tests/OrderDemo.IntegrationTests/SagaFlowTests.cs`.
- **Dependencies:** **S12.**
- **Blocking status:** **Blocked by: S12.**

- [ ] **Step 1:** Implement the per-flow tests (the across-restart test reuses the same `CreateDatabaseName()` across two `CreateHostAsync` calls).
- [ ] **Step 2:** Run the demo suite green. Commit.

### Task S14: Cross-node exactly-once saga progression (Balanced)

- **Goal:** Validate saga correctness under multi-node coordination — the saga advances exactly once across two nodes, and in-flight saga messages owned by a crashed node are rescued.
- **Scope:** A library `[Category=multinode]` test (`saga_multinode.cs`) using two in-proc Balanced hosts (mirror `multinode_end_to_end.cs`): drive a saga start + several continue messages across both nodes; assert exactly-once application (no double-advance) and that completion deletes the doc. Optionally a dead-node rescue variant. 🔎 review: if S8 OCC is in, two nodes racing the same saga will legitimately throw `SagaConcurrencyException` — the **retry policy from R11 must be wired** so the loser retries rather than failing the test (this is *not* an assertion to weaken; it is the correct production behavior). Same hard rules as multinode Task 7: **no skips, no retries-as-a-bandaid, no assertion-weakening**; five consecutive green runs on net9.0 + net10.0.
- **Expected output:** Five-in-a-row green; full suite green.
- **Files/areas likely to change:** `src/Wolverine.MongoDB.Tests/saga_multinode.cs`.
- **Dependencies:** **S12, S13** conceptually (shares the saga + flow understanding) and the merged multinode infra; the test itself only needs S6/S7 + the multinode harness, so it **may run in parallel with S13** if the saga design is settled.
- **Blocking status:** **Blocked by: S12, S13** (relaxable to S6/S7 + multinode infra if an agent owns the saga design directly).

- [ ] **Step 1:** Write the two-Balanced-node saga test (short `LockLeaseDuration`, durable inbox, shared static counter to observe process-wide application count).
- [ ] **Step 2:** Stabilize to five-in-a-row per TFM; full suite green. Commit.

---

## Phase 4 — Integration, Documentation & Verification

### Task S15: Full cross-feature regression

- **Goal:** Prove inbox, outbox, single-node, multi-node, and saga persistence all work together with no regression.
- **Scope:** On a verification branch off current `main` (after S6–S14 merged): full library suite (both TFMs), `--filter "Category=multinode"` five-in-a-row, `dotnet pack` (package-ref build), demo build + integration suite (incl. the new saga tests), and confirm CI's `library`/`demo` jobs cover the new tests (the saga compliance is non-multinode → auto-included in the single-node step; demo saga tests run in the `demo` job against the fresh nupkg). Add a tiny CI change only if a gap is found.
- **Expected output:** A clean regression report; any gap fixed (or filed). No new product behavior.
- **Files/areas likely to change:** none expected (`.github/workflows/ci.yml` only if a gap is found).
- **Dependencies:** **S6–S14 merged.**
- **Blocking status:** **Blocked by: S6–S14.**

- [ ] **Step 1:** Run all suites + pack + demo; record results.
- [ ] **Step 2:** Confirm CI coverage; fix any gap; report. Commit only if CI changed.

### Task S16: Documentation sweep + upstream-contribution notes

- **Goal:** Document saga support truthfully and prepare the upstream-contribution framing.
- **Scope:** Update `README.md` (saga usage: define a `Saga` subclass, supported id types, OCC behavior, the per-saga-type collection convention, replica-set/transaction requirement), `CLAUDE.md` (saga files in the file map + key design decisions: codegen-only, direct-document, native-id, Version OCC, atomic with outbox), `FOLLOWUPS.md` (any deferred items, e.g. int/long if dropped, `ISagaStoreDiagnostics`), `CHANGELOG.md` (`### Added` saga persistence), `demo/README.md` + `demo/CLAUDE.md` (the demo saga + how to run its flows). Add a short **upstream-contribution notes** section: target `Wolverine.MongoDb` namespace/folder, the compliance subclasses to add, and the deliberate deltas vs Cosmos/Raven (native id + `SagaConcurrencyException`). Every claim verified against `main`.
- **Expected output:** Accurate docs; an upstream checklist.
- **Files/areas likely to change:** the six docs above (+ a contribution-notes doc).
- **Dependencies:** **S6–S14 merged.**
- **Blocking status:** **Blocked by: S6–S14.**

- [ ] **Step 1:** Edit each doc; truth-check against code. Commit (`docs: saga support + upstream contribution notes`).

### Task S17: Final verification on `main` (+ optional release)

- **Goal:** Confirm the merged result is clean and decide on release.
- **Scope:** On `main` after S15/S16 merge: full suite, multinode five-in-a-row, pack, demo suite, CI green, history review (one PR per task S1–S16). Optionally invoke the `release` agent to publish a saga-enabled version and re-point the demo (only if the user wants a release; otherwise leave under `## [Unreleased]`).
- **Expected output:** A clean final report; release done only if requested.
- **Files/areas likely to change:** none (release handled by the `release` agent if invoked).
- **Dependencies:** **S15, S16 merged.**
- **Blocking status:** **Blocked by: S15, S16.**

- [ ] **Step 1:** Run all verifications on `main`; report. Do not fix in this session — file anything red.
- [ ] **Step 2 (optional):** If releasing, follow CLAUDE.md "Versioning & Release" via the `release` agent.

---

## Risks, Assumptions & Open Questions

| # | Risk / assumption | Mitigation |
|---|---|---|
| R1 | **Frame ordering / atomicity.** The generated saga chain must read/write the saga on the `TransactionalFrame` session and commit *after* the saga write so saga state + outbox are atomic. A wrong order silently breaks atomicity while passing some compliance facts. | S6 Step 2 mandates inspecting generated code via `codeFor<T>()`; the prescribed Cosmos-style rule (`commit postprocessor only if not SagaChain`; `CommitUnitOfWorkFrame` emits commit+flush for sagas) is verified against the real Cosmos provider. S11 atomicity test is the regression oracle. |
| R2 | **`CommitUnitOfWorkFrame` double-commit.** Mongo's existing `ApplyTransactionSupport` adds the commit postprocessor unconditionally; naively reusing it for sagas could commit twice. | S6 changes `ApplyTransactionSupport` to skip the postprocessor for `SagaChain` (mirrors Cosmos), routing commit through `CommitUnitOfWorkFrame`. Confirm exactly-one commit in generated code. |
| R3 | **`_id` mapping for non-string ids.** Guid/int/long `Id` must map to `_id` and round-trip. | The demo already stores Guid `_id` on `Order`; S7 adds a focused round-trip assertion. Add `[BsonId]` only if a fact proves it necessary, on test/demo types only (no global registry mutation — consistent with the per-property BSON-date decision). |
| R4 | **Collection-per-saga-type vs single collection.** Cosmos uses one container; per-type is more idiomatic for Mongo but adds a naming helper and test-cleanup enumeration. | S4 decides and documents the convention; `RebuildAsync`/tests clear the saga collections they use. |
| R5 | **Upstream may prefer Cosmos/Raven parity** (string-only, no OCC) over the richer Option B. | Decomposition: S6 = exact-parity baseline; S7/S8 are independently droppable. S16 calls out the deltas explicitly so upstream can accept or trim. |
| R6 | **Multinode saga flakiness (S14).** Cross-node timing, like multinode Task 7. | Opus 4.8; five-in-a-row bar; reason-before-knob; no skips/retries; stop-and-report if unreachable. |
| R7 | **Demo depends on a packed library.** Saga demo code can't compile until a saga-enabled package exists. | S12 skeletons authored against S5 early; build/run gated on S6/S7 merged (local pack or CI `0.0.0-ci`). |
| R8 | **Saga identity-member resolution API.** S7 needs Wolverine's id-member determination. | Use `SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType()` (mirrors `LightweightSagaPersistenceFrameProvider.cs:80-82`); precedence `[SagaIdentity]` → `{Saga}Id` → `Id` → `SagaId`. Note `DetermineSagaIdType` only governs envelope-header-only messages; member-bearing messages use `PullSagaIdFromMessageFrame` with the member type directly. Confirmed in S2, not invented. |
| R9 | **🔎 review — `CanPersist` over-broad.** Returning `true` unconditionally advertises Mongo as a generic `[Entity]`/`Insert<T>`/`Update<T>`/`IStorageAction<T>` provider, but `DetermineStorageActionFrame` still throws → codegen blow-ups for non-saga entities. | S6 scopes `CanPersist` to `entityType.CanBeCastTo<Saga>()`. Full generic document persistence is out of scope (separate future work). |
| R10 | **🔎 review — saga `_id` from envelope-only starts.** Core does not set the saga state's `Id` from the envelope (`SetSagaIdFrame` only sets context/activity tags; `DetermineInsertFrame` gets only the saga var). A start handler that derives its id solely from the envelope (never assigning `state.Id`) could insert a default `_id`. Compliance passes because `BasicWorkflow.Start` assigns `Id` — and Cosmos/Raven share this exact limitation. | S6/S7: add a focused test for an envelope-id-only start, **or** document that start handlers must assign the saga state id (parity with Cosmos/Raven). |
| R11 | **🔎 review — `SagaConcurrencyException` retry policy.** Under multinode contention (S14) OCC conflicts will throw; without a retry/error policy the multinode saga test flakes. | Decide the policy before S14 (e.g. a Wolverine retry policy on `SagaConcurrencyException`, or accept-and-retry at the handler). Record the decision in S4/S8 and wire it in S14. |
| **OQ1** | **Lead decision: Option A vs B** (string-only vs native-id+OCC). | **Recommended B**; formally recorded in **S4**. Affects S7/S8/S10 existence and the critical path. |
| **OQ2** | Support int/long ids, or just Guid+string? | Default: include int/long in S7/S10 (cheap once native-id works); drop to FOLLOWUPS if they prove fiddly. |
| **OQ3** | Register `ISagaStoreDiagnostics` (RavenDb does, Cosmos doesn't)? | Optional; default **defer** to FOLLOWUPS unless the saga-explorer surface is wanted for upstream parity. |
| **OQ4** | Release a saga-enabled NuGet version now (S17) or stay `[Unreleased]`? | Default: stay `[Unreleased]`; release only on explicit user request. |
| **OQ5** | Is the completion **delete** version-guarded, or last-writer-wins? | Default **unguarded** (matches lightweight SQL `DatabaseSagaSchema.cs:113-119`); decide in S8. |

## Acceptance Criteria

- `string_saga_storage_compliance` and `guid_saga_storage_compliance` (+ int/long if in scope) pass; the upstream saga compliance facts are green with **no skips, retries, or weakened assertions**.
- Custom S11 tests prove: saga state + outbox commit/roll back **atomically**; `MarkCompleted()` deletes the doc; a stale-version update throws `SagaConcurrencyException` (Option B); duplicate messages are idempotent.
- The demo runs all saga flows (start, continue, complete, missing, duplicate, across-restart, inbox/outbox) green in `OrderDemo.IntegrationTests`, and the Balanced multi-node saga test (S14) is five-in-a-row green on both TFMs.
- Full library suite (both TFMs), `dotnet pack`, demo suite, and CI (`library` + `demo`) are green; inbox/outbox/single-node/multi-node behavior is unchanged.
- No process-global serializer registration is introduced; existing public API is preserved (saga support adds behavior, not new required API).
- Docs (README/CLAUDE/CHANGELOG/FOLLOWUPS/demo docs) accurately describe saga support; an upstream-contribution checklist exists.

---

## Dependency Map

**Immediate parallel tasks (start the moment the plan PR merges):**
- **S1** Repository & convention analysis
- **S2** Wolverine saga API discovery
- **S3** Cosmos/RavenDb comparison
- **S5** Demo flow design + test inventory *(finalize saga state/id shape after S4)*

**Blocked by discovery/design:**
- **S4** ← S1, S2, S3 *(the design gate)*
- **S6** ← S4 *(head of the critical path)*
- **S9** host skeleton ← S2/S3 facts *(green ← S6)*
- **S11** skeleton ← S4 *(green ← S6/S8)*
- **S12** contracts/saga skeleton ← S5 *(build/run ← S6/S7)*

**Blocked by implementation:**
- **S7** ← S6 · **S8** ← S6, S7 *(🔎 review: S8↔S7 share files — sequence, don't parallelize)*
- **S9** green ← S6 · **S10** ← S7, S9 · **S11** green ← S6 (atomicity/idempotency) + S8 (OCC)
- **S12** build ← S6, S7 · **S13** ← S12 · **S14** ← S12, S13

**Final verification tasks:**
- **S15** ← S6–S14 merged · **S16** ← S6–S14 merged · **S17** ← S15, S16 merged

```
plan PR ──► S1 ┐
            S2 ├─► S4 ──► S6 ─┬─► S7 ─┬─► S10
            S3 ┘              │       └─► S12* ─► S13 ─► S14
            S5 ───────────────┘       ├─► S8 ─► S11(OCC)
                                      ├─► S9 ─► (S10)
                                      └─► S11(atomicity/idempotency)
   S7/S8/S9/S10/S11/S12/S13/S14 ──► S15 ─┐
                                    S16 ─┴─► S17
   (* S12 build/run also needs S7)
```

## Critical Path Analysis

**Minimum sequence to deliver saga persistence (library-proven):**
`plan PR → {S2, S3} → S4 → S6 → S9 (green compliance)`.
This is the irreducible core: discover the contracts (S2/S3), commit the design (S4), implement the frames (S6), and prove them against the upstream oracle (S9). Everything else extends or hardens this.

**Full-scope critical path (Option B, user's required coverage):**
`… → S6 → S7 → S8 → S11(OCC)` on the library side, with the demo arm `S6/S7 → S12 → S13 → S14` running **in parallel**, converging at `S15 → S16 → S17`. The longest chain is the demo/multinode arm (`S6 → S7 → S12 → S13 → S14 → S15 → S16 → S17`), so **S14 (multinode saga, Opus, five-in-a-row) is the schedule risk** — start its design (S5) and harness reuse early.

**Tasks that can be delayed without affecting the critical path:**
- **S5** can lag until just before S12 (only its *output* gates S12).
- **S8 (OCC)** and **S10/S11** are off the *green-compliance* core (compliance doesn't test concurrency); they are required for *full scope* but not for first-green. If schedule-pressed, ship S6+S9 first, then S7/S8/S10/S11.
- **int/long ids** (within S7/S10) are deferrable to FOLLOWUPS.
- **S16 docs** can be drafted in parallel and finalized last.

**Opportunities to compress wall-clock via parallelism:**
- Run **S1+S2+S3+S5** as four concurrent sessions on day one.
- The moment **S6** merges, fan out **S7, S8, S9-green, S11, S12-skeleton** concurrently; **S10** follows S7.
- Run the **library-test track (S9/S10/S11)** and the **demo track (S12/S13/S14)** as two independent lanes after S6/S7 — they touch disjoint files.
- Fold **S9's host file into S6's branch** (see the S6 coordination note) so S6 ships already-green, removing a serialization point.
- With full fan-out, the realistic span is roughly: 1 unit (S1–S3,S5) → 1 unit (S4) → 1 unit (S6) → ~2 units (S7→S10, S8→S11, S12→S13→S14 overlapping) → 1 unit (S15/S16) → S17 — i.e. the 17 tasks collapse to about **6–7 sequential “waves,”** dominated by the S6→S7→S12→S13→S14 demo/multinode chain.
