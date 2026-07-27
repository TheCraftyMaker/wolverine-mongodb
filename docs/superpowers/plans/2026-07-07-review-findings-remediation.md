# Review Findings Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. The companion prompts file is `docs/superpowers/plans/2026-07-07-review-findings-remediation-prompts.md`.

> **Origin (2026-07-07 full-solution review):** an 8-angle review (3 correctness angles, reuse/simplification/efficiency, altitude, conventions) over the shipped 1.0.0 library produced 40 candidates; every correctness-relevant candidate was then adversarially **verified one-by-one against the pinned `external/wolverine` submodule (V6.16.0) and the sibling providers** (RDBMS/Postgres, Marten, EF Core, Cosmos, RavenDb). 11 findings were CONFIRMED with quoted evidence, 2 were REFUTED (the DLQ prefix-filter matches RavenDb/Cosmos exactly; the node-renumber-on-re-register behavior is byte-for-byte Postgres parity). The review also verified the 6.9.0→6.16.0 upgrade broke nothing: every reflective bridge target in `MongoDbSagaStoreDiagnostics` still exists with a matching signature. This plan fixes the 11 confirmed findings plus the verified documentation drift, tiered by severity.

**Goal:** Remediate every confirmed finding from the 2026-07-07 review of `Wolverine.MongoDB` 1.0.0 — two silent-data-corruption identity-mapping bugs (saga and entity), three inbox/DLQ correctness bugs, two coordination/shutdown races, a diagnostics contract violation, an open-ended NuGet dependency range, and post-1.0.0 documentation drift — each with a red-first test, without regressing inbox, outbox, saga, entity, single-node, or multi-node behavior, and keeping the library a clean candidate for upstream contribution to WolverineFx.

**Architecture:** The fixes cluster into four mechanisms. **(1) Identity reconciliation:** Wolverine resolves document identity via `SagaChain.DetermineSagaIdMember` (precedence `[SagaIdentity]` → `{TypeName}Id` → `{Name-minus-Saga}Id` → `SagaId` → `Id`), but the MongoDB driver's default `NamedIdMemberConvention` maps only `Id`/`id`/`_id` to `_id` — and every saga/entity frame filters raw `"_id"`. The fix is a single codegen-time identity-mapping helper (ensure-or-fail `BsonClassMap` id-member alignment) shared by saga frames, entity frames, and the runtime `IdOf` helper, so read and write sides key the same member for every legal Wolverine identity convention. **(2) Inbox atomicity:** batch `StoreIncomingAsync` must present all-or-nothing semantics on duplicate (the `DurableReceiver` retry contract the RDBMS provider satisfies with a rolled-back transaction). **(3) Coordination hardening:** the dead-node ownership release becomes two-tick-confirmed (sound because node numbers are monotonic and never reused), incoming claims become destination-scoped (claim by document `_id`, which already encodes the destination in `IdAndDestination` mode), and the durability agent's `StopAsync` actually awaits its loops. **(4) Contract/packaging parity:** diagnostics identity coercion mirroring Marten/EF Core, bounded NuGet version ranges, and a docs truth sweep.

**Tech Stack:** .NET 9/10, MongoDB.Driver 3.9.0, WolverineFx 6.16.0 (pinned `external/wolverine` submodule at V6.16.0), JasperFx.CodeGeneration (frames), xUnit + Shouldly (library) / FluentAssertions (demo), Testcontainers.MongoDb (replica set).

**PREREQUISITE: current `main` (commit `cec7d93` or later).** The WolverineFx 6.16.0 upgrade (PR #152) and the demo pin to Wolverine.MongoDB 1.0.0 (PR #151) are already merged. `Directory.Build.props` is `1.0.0`; 1.0.0 shipped to NuGet on 2026-07-06. **Post-1.0 consequence:** behavior-visible fixes here are semver-relevant — each task's PR description must state whether the change is a pure bug fix (patch) or tightens previously-silent misbehavior into a thrown error (arguably minor); the release decision itself is deferred to F20/OQ6.

---

## Verified API Facts (established by the 2026-07-07 review — do NOT re-derive or invent)

Every fact below was read from the pinned `external/wolverine` submodule (V6.16.0) or local `main` during the review's verification pass. Implementing sessions should *confirm* them (a one-line grep), not reinvent them. **If a fact does not hold, stop and report — do not improvise.**

**Identity resolution (Wolverine core):**
- `SagaChain.DetermineSagaIdMember(sagaType, messageType)` — `external/wolverine/src/Wolverine/Persistence/Sagas/SagaChain.cs:210,220-224`. Precedence: `[SagaIdentity]`-attributed member → `{SagaTypeName}Id` → `{SagaTypeName-minus-"Saga"}Id` (e.g. `OrderId` on `OrderSaga`) → `SagaId` → any member named `Id` (case-insensitive last fallback at `:224`). Core uses it self-referentially for the saga's own identity (`LightweightSagaPersistenceFrameProvider.cs:82`).
- Entities route through the same resolver: `EntityAttribute` gets the id type from `provider.DetermineSagaIdType(parameterType, container)` — `external/wolverine/src/Wolverine/Persistence/EntityAttribute.cs:153` — which this provider implements as `SagaChain.DetermineSagaIdMember(entityType, entityType)?.GetRawMemberType()` (`MongoDbPersistenceFrameProvider.cs:90-93`, throws on null).
- Core calls `DetermineSagaIdType` only for the **envelope-header-only** identity path (`SagaChain.cs:291-292`); member-bearing messages use `PullSagaIdFromMessageFrame` with the member's type directly. Nothing upstream validates the saga type's own identity member before `DetermineUpdateFrame` is called (`SagaChain.cs:424`).
- No Saga guard exists upstream on the storage-action paths: `Delete<T>.BuildFrame` → single-variable `DetermineDeleteFrame` (`external/wolverine/src/Wolverine/Persistence/Delete.cs:22-26`), `IStorageAction<T>.BuildFrame` (`IStorageAction.cs:23-27`), `Storage.TryApply` (`Storage.cs:63-74`) — all gated only by `CanPersist` (`GenerationRulesExtensions.cs:68`), which this provider hardcodes `true` (`MongoDbPersistenceFrameProvider.cs:79`).

**Local identity-keyed code (the defect sites):**
- Saga frames filter raw `"_id"`: `MongoSagaOperations.LoadSagaAsync` `Builders<TSaga>.Filter.Eq("_id", sagaId)` (`src/Wolverine.MongoDB/Internals/SagaFrames.cs:39`), update `:84`, delete `:115`; `InsertSagaAsync` is a bare `InsertOneAsync` relying on the driver class map (`:58`). **No `BsonClassMap.RegisterClassMap`/`MapIdMember` exists anywhere in `src/Wolverine.MongoDB`** — the only class-map usages are read-only `LookupClassMap` calls (`EntityFrames.cs:134`, `MongoDbSagaStoreDiagnostics.cs:148`).
- `UpdateSagaFrame` ctor silently falls back: `_idMember = idMember?.Name ?? "Id"; _idType = idMember?.GetRawMemberType() ?? typeof(string);` (`SagaFrames.cs:227-229`) where `DetermineSagaIdType` throws for the same null (`MongoDbPersistenceFrameProvider.cs:90-93`) — and the null case is reachable (see SagaChain fact above), yielding a cryptic codegen compile error referencing `saga.Id`.
- Entity read/write disagree: `LoadEntityFrame` filters `Eq("_id", id)` with the **Wolverine-resolved** member's value (`EntityFrames.cs:41,60`); `UpsertAsync`/`DeleteAsync` extract `_id` via `BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap` (`EntityFrames.cs:79,97,133-136`) — the **driver-resolved** member. `MongoEntityOperations.DeleteAsync` targets `MongoConstants.EntityCollectionName(typeof(T))` (`EntityFrames.cs:94`), un-prefixed, vs the saga frames' `wolverine_saga_` prefix (`SagaFrames.cs:37`; `MongoConstants.cs:25-27`).
- Provider saga/entity branching: Insert/Update/Store branch on `CanBeCastTo<Saga>()` (`MongoDbPersistenceFrameProvider.cs:108-111,116-119,128-131`); the single-variable `DetermineDeleteFrame` (`:136-137`) and `DetermineStorageActionFrame` (`:147-156`) do **not**.
- Upstream compliance coverage gap: the storage-action suite's only entity is `Todo { public string Id }` (`external/wolverine/src/Testing/Wolverine.ComplianceTests/StorageActionCompliance.cs:272-274`); the saga compliance sagas all use a member literally named `Id` (`BasicWorkflow` — `Sagas/TestMessages.cs:50`). **No upstream spec exercises a non-`Id` identity member** — custom tests are required (and are an upstream-contribution candidate).

**Inbox batch / DurableReceiver contract:**
- `StoreIncomingAsync(IReadOnlyList<Envelope>)` uses `InsertManyAsync(docs, new InsertManyOptions { IsOrdered = false })` (`src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Inbox.cs:28`) — unordered bulk commits every non-duplicate before the catch at `:30-42` throws `DuplicateIncomingEnvelopeException(dupes)`.
- `DurableReceiver.ProcessReceivedMessagesAsync` catches that exception and re-posts **every** envelope through the per-envelope path (`external/wolverine/src/Wolverine/Runtime/WorkerQueues/DurableReceiver.cs:631-641`); each now-persisted fresh envelope hits `StoreIncomingAsync(envelope)` (`:493`) → duplicate → `handleDuplicateIncomingEnvelope` (`:496-500`), which calls `envelope.Listener.CompleteAsync` (`:530`) and returns **before** `EnqueueAsync` (`:511`). The catch-block comment (`:637-640`) states the contract: the retry works only because a failed batch persisted nothing.
- The RDBMS provider wraps the batch in an explicit transaction and rolls back on duplicate, with a comment explaining partial persistence "would … leave the inbox in a state that is indistinguishable from 'envelope was already there'" (`external/wolverine/src/Persistence/Wolverine.RDBMS/MessageDatabase.Incoming.cs:183-200`).
- Envelopes carry `OwnerId = AssignedNodeNumber` at batch-store time (`Envelope.MarkReceived`, `external/wolverine/src/Wolverine/Envelope.Internals.cs:220`); orphan recovery matches only `OwnerId == AnyNode` (`MongoDbMessageStore.Durability.cs:126`), so stranded fresh envelopes are invisible until drain/restart.

**Incoming claims / MessageIdentity:**
- `MessageIdentity.IdAndDestination` is a supported mode (`external/wolverine/src/Wolverine/DurabilitySettings.cs:103-107`); in it the local `_inboxIdentity` makes the document `_id` = `$"{e.Id}|{destination}"` (`src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs:46-48`), so multiple documents legitimately share one `EnvelopeId` Guid (`IncomingMessage.Id` is the `[BsonId]` string; `EnvelopeId` is a separate Guid field — `IncomingMessage.cs:25-26`).
- `ReassignIncomingAsync` claims by `Filter.In(x => x.EnvelopeId, ids) && OwnerId == AnyNode` — **no destination scope** (`MongoDbMessageStore.cs:131-135`). The RDBMS provider scopes per destination: `where id = @id and received_at = @destination` (`external/wolverine/src/Persistence/Wolverine.RDBMS/MessageDatabase.Incoming.cs:27-30`).
- `RecoverOrphanedIncomingAsync` loads listener-scoped pages (`LoadPageOfGloballyOwnedIncomingAsync` filters `ReceivedAt == listenerAddress`, `MongoDbMessageStore.cs:106-109`) but re-reads the claimed set unscoped: `Filter.In(x => x.EnvelopeId, ids) && OwnerId == nodeNumber` (`MongoDbMessageStore.Durability.cs:154-158`).

**Dead-node release / shutdown:**
- `ReleaseDeadNodeOwnershipAsync` (`src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs:175-194`): reads live numbers (`:177-180`), adds `AnyNode` (`:183`), then two separate non-transactional `UpdateManyAsync(Nin(OwnerId, liveNumbers))` (`:185-193`). The doc comment's safety claim (`:172-173`) is falsified by the stale snapshot. The RDBMS mirror is a single atomic statement: `update … set owner_id = 0 where owner_id != 0 and owner_id not in (select node_number from nodes)` (`external/wolverine/src/Persistence/Wolverine.RDBMS/Durability/ReleaseOrphanedMessagesOperation.cs:32,34`). Runs on every non-Solo node each recovery tick (`MongoDbDurabilityAgent.cs:56-58`).
- **Node numbers are monotonic and never reused** — documented T4.6 decision; the counter in `MongoDbMessageStore.NodeAgents.cs:16-20` only increments. A node registers (`PersistAsync`, `NodeAgents.cs:14-27`, visible at the `ReplaceOneAsync` `:24`) **before** its durability agent starts claiming. These two facts make a two-tick-confirmed release sound (see F4/F11).
- `MongoDbDurabilityAgent.StopAsync` (`MongoDbDurabilityAgent.cs:130-140`): `_cancellation.Cancel(); _recoveryTask?.SafeDispose(); _scheduledJob?.SafeDispose(); Status = AgentStatus.Stopped; return Task.CompletedTask;` — JasperFx `SafeDispose` swallows `Task.Dispose()`'s `InvalidOperationException` on a running task, so nothing is awaited; `_cancellation`/`_combined` are never disposed. Upstream shutdown ordering: `WolverineRuntime.HostService.StopAsync` runs `ReleaseAllOwnershipAsync` at `:380` **before** `teardownAgentsAsync()` at `:402`; `NodeAgentController.cs:136` deletes the node document immediately after the agent's instant-return stop. The RDBMS `DurabilityAgent.StopAsync` at least awaits `Timer.DisposeAsync()` (`Wolverine.RDBMS/DurabilityAgent.cs:140-166`).

**Dead letters:**
- `EditAndReplayAsync` calls `EnvelopeSerializer.Deserialize(doc.Body)` with no guard (`src/Wolverine.MongoDB/Internals/MongoDbMessageStore.DeadLetters.cs:91`); `DeadLetterMessage.ToEnvelope()` has the guard `Body is { Length: > 0 } ? Deserialize(Body) : new Envelope { … }` (`DeadLetterMessage.cs:70-72`); `ForUnserializableEnvelope` writes `Body = []` (`DeadLetterMessage.cs:45`) and is called from `MongoDbMessageStore.Inbox.cs:136`. `EnvelopeSerializer.Deserialize` immediately does `br.ReadInt64()` (`external/wolverine/src/Wolverine/Runtime/Serialization/EnvelopeSerializer.cs:192-214`) → `EndOfStreamException` on empty input.

**Diagnostics:**
- `ISagaStoreDiagnostics` contract: "Wolverine boxes whatever the saga's id member returns — Guid, int, long, string, or a strong-typed id — and **the implementation is expected to coerce as needed**" (`external/wolverine/src/Wolverine/Persistence/Sagas/ISagaStoreDiagnostics.cs:42-46`).
- `MongoDbSagaStoreDiagnostics.ReadSagaAsync` filters `Builders<TSaga>.Filter.Eq("_id", identity)` with the boxed identity untouched (`src/Wolverine.MongoDB/Internals/MongoDbSagaStoreDiagnostics.cs:101-104`; the class doc at `:23` says "the identity is used as-is").
- Reference coercers: Marten `coerceIdentity` with `Guid.Parse`/`int.Parse`/`long.Parse` for string input, comment naming the JSON/URL round-trip scenario (`external/wolverine/src/Persistence/Wolverine.Marten/MartenSagaStoreDiagnostics.cs:147-168`); EF Core identical (`EFCoreSagaStoreDiagnostics.cs:184-196`); RavenDb `identity?.ToString()` (string-only store, `RavenDbSagaStoreDiagnostics.cs:67`).
- The local test `saga_store_diagnostics.cs` covers only a string-keyed saga with exact-typed identities (`:71-75,137`).

**Packaging:**
- `Directory.Packages.props:6` pins bare `<PackageVersion Include="WolverineFx" Version="6.16.0" />` (MongoDB.Driver `3.9.0` at `:8`); the pack-time reference (`src/Wolverine.MongoDB/Wolverine.MongoDB.csproj:21-23`, `Condition="'$(UseWolverineSource)' != 'true'"`) adds no range. A produced nuspec proves open-ended semantics: `<dependency id="WolverineFx" version="6.9.0" …/>` = `>= 6.9.0`, no ceiling (`src/Wolverine.MongoDB/obj/Release/Wolverine.MongoDB.0.0.0-ci.nuspec:16-17` from an older local pack). NuGet central package management accepts bracketed ranges in `PackageVersion`.

**Documentation drift (verified against `main`):**
- `CLAUDE.md:7` says "currently `0.1.0-beta.7`" but `Directory.Build.props:16` is `1.0.0` and `CHANGELOG.md` records `## [1.0.0] - 2026-07-06` as released (demo pinned to 1.0.0, PR #151).
- `CLAUDE.md` "Major version tracks Wolverine: `0.1.x` ↔ `WolverineFx 6.x`" is stale vs shipped 1.0.0 ↔ WolverineFx 6.16.0.
- The T4.6 index-migration bullet's premise ("every consumer today is pre-1.0 beta", "add … only if needed before 1.0") lapsed on 2026-07-06.
- `CLAUDE.md` says `IMessageStoreAdmin.ClearAllAsync` "clears all six system collections" but `MongoDbMessageStore.Admin.cs:69-77` deletes from **nine** (incoming, outgoing, dead_letters, nodes, node_assignments, node_records, agent_restrictions, counters, locks); the mirroring comment at `MongoDbMessageStore.NodeAgents.cs:180` repeats it.

---

## Lead Open Design Decisions (resolved in F3/F4 — they gate all Tier-1/2/3 implementation)

**LD1 — Identity-mapping mechanism (F3).** Three coherent options for making the driver's `_id` agree with Wolverine's resolved identity member:
- **Option A — Fail fast only.** At frame construction (codegen time), if `DetermineSagaIdMember` resolves a member the driver's class map does not map to `_id`, throw a clear `InvalidOperationException` telling the user to add `[BsonId]` (or align member naming). Zero registry mutation; non-`Id` conventions work only with explicit annotation.
- **Option B — Ensure-or-fail class map (recommended).** Same check, but when the type has **no** registered class map yet, register one (`AutoMap()` + `MapIdMember(resolvedMember)`) so every legal Wolverine convention just works; if a class map is already registered/frozen with a **different** id member, throw the clear error (the app owns its map). Per-type, additive, idempotent (cached), and a no-op for `Id`-named members — existing consumers see zero change. This is *not* the "process-global serializer/convention mutation" the library forswears (no serializers, no conventions, no behavior change for any type Wolverine doesn't persist), but F3 must state that distinction explicitly in CLAUDE.md terms.
- **Option C — Key frames off the member's mapped element name instead of `_id`.** Rejected up front: the document would carry a server ObjectId `_id` plus a secondary identity field needing its own unique index, diverging from every sibling provider and from 1.0.0's on-disk shape for `Id`-keyed documents.

**LD2 — Batch-store atomicity mechanism (F4).** Two options for `StoreIncomingAsync(IReadOnlyList<Envelope>)`:
- **Option A — Session/transaction wrap (recommended).** `StartSessionAsync` → `StartTransaction` → `InsertManyAsync(session, docs, IsOrdered:false)` → commit; on `MongoBulkWriteException` abort and classify. Matches the RDBMS contract exactly (nothing persisted on duplicate). Caveat to verify in F8: **inside a transaction the server aborts on first write error**, so the reported duplicate list may be partial — acceptable because `DurableReceiver` retries *every* envelope individually regardless (`DurableReceiver.cs:641`), but the F8 test must not assert a complete dupe list.
- **Option B — Compensating delete.** Keep the unordered insert; on duplicate, delete the successfully-inserted fresh docs by `_id`, then throw. Preserves the complete dupe list but has a crash window between insert and compensation that re-creates the stranding bug. Choose A unless F8 finds a transaction-specific blocker (e.g. the replica-set requirement is already a hard library constraint, so no new deployment demand).

**LD3 — Dead-node release soundness (F4).** Recommended: **two-tick-confirmed release.** Each tick computes `deadNow` = (distinct `OwnerId`s present in incoming/outgoing) − (live node numbers ∪ {0}); release only numbers in `deadNow ∩ previousTickDeadSet`. Soundness argument (record it in the design doc *and* the method comment): a node registers before its first claim, and node numbers are monotonic/never-reused, so a number freshly claimed by a live node cannot have been in the *previous* tick's dead set — closing the read-then-write window entirely rather than shrinking it. Cost: dead-node rescue latency grows by one recovery interval (document it). Alternatives to record-and-reject: wrapping in a Mongo transaction (snapshot isolation does **not** conflict with the node-doc insert, so it does not fix the race), and per-number liveness recheck (shrinks but keeps the window).

**LD4 — Saga types on storage-action paths (F3).** When a plain handler returns `Delete<TSaga>`/`IStorageAction<TSaga>`, today the entity frames silently target the wrong (un-prefixed) collection. Options: route to the saga frames, or **throw a clear codegen-time `InvalidOperationException`** ("saga types are managed by saga chains — return the saga from a saga handler or use MarkCompleted()"). Recommended: throw — routing writes to saga collections without `Version` stamping would corrupt OCC, and no sibling provider supports this path for sagas.

---

## Git & PR Workflow (one branch + one PR per task)

Identical mechanics to the saga plan — see `2026-06-18-saga-persistence.md` → "Git & PR Workflow" for the full commands. Summary: **one git worktree per task** off current `origin/main`; commit; push; `rtk gh pr create --base main --head <branch>`; verify with `gh pr view --json statusCheckRollup` (not the rtk `--watch` tail); dependent tasks start only after their dependency's PR is **merged**; each PR independently green (its own tests + the full library suite, and the demo suite where touched); update this plan doc (checkboxes + status table) in the task's PR. Commit messages end with the `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` trailer; PR bodies end with the `🤖 Generated with [Claude Code](https://claude.com/claude-code)` line.

---

## Task Table

| Task | Branch | PR title | Depends on | Blocking status | Model |
|---|---|---|---|---|---|
| **F1** | `docs/identity-mapping-discovery` | docs: remediation — identity-mapping discovery (Wolverine conventions vs driver class map) | — | Can start immediately | Sonnet |
| **F2** | `docs/durability-contracts-discovery` | docs: remediation — inbox/recovery/shutdown contract discovery | — | Can start immediately | Sonnet |
| **F3** | `docs/identity-mapping-design` | docs: remediation — identity-mapping design (DESIGN GATE, LD1+LD4) | **F1** | **Done** (#179) — **amended** (#180) after F6 hit a base-declared-identity gap; contracts binding for F6/F7/F18 | **Fable 5 / Opus** |
| **F4** | `docs/durability-coordination-design` | docs: remediation — durability & coordination design (DESIGN GATE, LD2+LD3) | **F2** | **Done** (#178) — contracts binding for F8/F10/F11/F12 | **Fable 5 / Opus** |
| **F5** | `docs/remediation-test-inventory` | docs: remediation — test inventory + demo impact design | — | Can start immediately | Sonnet |
| **F6** | `fix/saga-identity-mapping` | fix: saga identity conventions map to _id (+ fail-fast id resolution) | **F3** | **Done** (#181) — 197 facts green net9.0+net10.0, multinode 18/18, zero edited compliance facts | **Fable 5 / Opus** |
| **F7** | `fix/entity-identity-and-saga-guards` | fix: entity load/write identity agreement + saga guard on Delete/IStorageAction | **F3, F6** | **Done** ([#186](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/186)) — 212 single-node + 18 multinode green on net9.0 and net10.0, zero edited facts; `MongoIdentityMapping` unchanged as the F3 amendment predicted | **Fable 5 / Opus** |
| **F8** | `fix/batch-inbox-atomicity` | fix: all-or-nothing batch StoreIncomingAsync on duplicate | **F4** | **Done** (#183) — 201 single-node + 18 multinode green on net9.0 and net10.0; in-transaction failure shape empirically pinned (see Step 2) | **Fable 5 / Opus** |
| **F9** | `fix/dlq-edit-replay-empty-body` | fix: EditAndReplayAsync tolerates body-less poison dead letters | — | Can start immediately | Sonnet |
| **F10** | `fix/destination-scoped-claims` | fix: destination-scoped incoming claims (IdAndDestination) | **F4** | **Done** (#184) — 204 single-node + 18 multinode green on net9.0 and net10.0; `IdOnly` byte-equivalence verified by inspection | **Fable 5 / Opus** |
| **F11** | `fix/dead-node-release-race` | fix: two-tick-confirmed dead-node ownership release | **F4** | **Done** (#185) — 206 single-node green on net9.0+net10.0; multinode **10/10 (5× consecutive per TFM)**, no widened timeouts. Rebased onto F10 (#184); the old single-tick fact was folded in and deleted per F4 §2g. **Unblocks F17** | **Fable 5 / Opus** |
| **F12** | `fix/durability-agent-shutdown` | fix: durability agent StopAsync awaits its loops + disposes CTSes | **F4** | **Done** — 218 single-node + 18 multinode green on net9.0 and net10.0, zero edited facts | Sonnet |
| **F13** | `fix/diagnostics-identity-coercion` | fix: ISagaStoreDiagnostics identity coercion (Marten parity) | — | Can start immediately | Sonnet |
| **F14** | `fix/nuget-dependency-ranges` | fix: bounded NuGet dependency ranges for WolverineFx + MongoDB.Driver | — | Can start immediately | Sonnet |
| **F15** | `docs/truth-sweep` | docs: post-1.0.0 truth sweep (version, versioning rule, collection counts) | — | Can start immediately | Sonnet |
| **F16** | `chore/store-dedup-cleanup` | chore: deduplicate inbox/outbox update definitions + AnyNode sentinel | **F8, F10** | **Done** (#187) — 214 single-node + 18 multinode green on net9.0 and net10.0 | Sonnet |
| **F17** | `chore/store-efficiency` | chore: store efficiency sweep (DLQ replay batching, cached collections, parallel node loads) | **F11** | **Done** ([#188](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/188)) — 430 single-node + 36 multinode green on net9.0 and net10.0; zero edited facts | Sonnet |
| **F18** | `demo/identity-convention-coverage` | demo: non-`Id` identity-convention entity + safety-net tests | **F5, F6, F7** | Blocked by: F5, F6, F7 (merged & packed) | Sonnet |
| **F19** | `test/remediation-regression` | test: full cross-feature regression (inbox+outbox+saga+entity+solo+multinode) | **F6–F18 merged** | **Done** (report-only close, no PR) — 218 single-node green on both TFMs; multinode 5× consecutive per TFM, 18/18 facts each (10/10 total); pack succeeds; demo suite 45/45 green; CI coverage confirmed adequate. Found F14's bracketed dependency ranges silently reverted by later dependabot bumps — filed, not fixed here (see F21) | Sonnet |
| **F20** | *(no branch/PR)* | final verification on `main` (+ release decision) | **F15, F19, F21 complete** | Blocked by: F15, F19, F21 | Sonnet |
| **F21** | `docs/post-remediation-sweep` | docs: post-remediation documentation sweep (F12/F19 status, dependency-range regression note) | **F12, F19 complete** | Not Started | Sonnet |

> **Recommended execution order.** Day one, run **F1, F2, F5, F9, F13, F14, F15** as seven concurrent sessions (four are independent small fixes/docs; three are discovery/design inputs). F3 and F4 (the two design gates) follow their discovery tasks and can run in parallel with each other. The moment F3 merges, **F6** starts (head of the critical path); the moment F4 merges, **F8, F10, F11, F12** fan out in parallel (disjoint primary files — see File Structure). F7 follows F6; F18 follows F6+F7; F16/F17 trail their file-sharing predecessors. F19/F20 close out.

## Model Guidance

The dominant risk is **concurrency/codegen correctness that passes a green suite** (Agent `model`: `sonnet`, `opus`, `fable`):

- **Fable 5 / Opus mandatory** for **F3, F4** (the design gates — LD1–LD4 shape every downstream task and the on-disk document contract), **F6, F7** (class-map + frame changes: a subtly wrong mapping still compiles and passes the `Id`-keyed compliance suites while corrupting non-`Id` apps — the exact defect class under repair), **F8** (transaction semantics inside a bulk write; the DurableReceiver contract is easy to satisfy in the test and break in production), **F10, F11** (distributed claim races; a wrong fix passes most runs).
- **Sonnet** for F1, F2, F5 (research/writing), F9, F12, F13, F14, F15 (small, fully-specified fixes with named reference implementations), F16, F17 (mechanical cleanup against green suites), F18, F19, F20 (verification against green oracles).
- **Do not use Haiku** anywhere in this plan.
- **Escalation rule:** two non-obvious verification failures, or a broken plan assumption (an API differing from the Verified Facts above), means **stop and report** — re-dispatch on Fable 5 with the failure context rather than improvising a different Wolverine API. For F6/F7/F8/F10/F11 specifically: if green cannot be reached after the listed levers, write up the failing evidence and stop.
- **Code review between tasks:** Fable 5/Opus, with extra scrutiny on F6/F7/F11 — identity and race bugs that pass a green suite are exactly what the original review caught; don't reintroduce the pattern.

---

## File Structure Overview

| File | Change |
|---|---|
| `src/Wolverine.MongoDB/Internals/MongoIdentityMapping.cs` | **New** — codegen-time ensure-or-fail id-member alignment helper (F6), shared by saga + entity frames |
| `src/Wolverine.MongoDB/Internals/SagaFrames.cs` | Frames key `_id` via the resolved identity member; `UpdateSagaFrame` fallback → throw; call `MongoIdentityMapping` (F6) |
| `src/Wolverine.MongoDB/Internals/EntityFrames.cs` | Load + upsert/delete agree on the identity member via `MongoIdentityMapping`; `IdOf` keeps working through the aligned class map (F7) |
| `src/Wolverine.MongoDB/Internals/MongoDbPersistenceFrameProvider.cs` | Saga guard (throw) in single-variable `DetermineDeleteFrame` + `DetermineStorageActionFrame` (F7, per LD4) |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Inbox.cs` | Batch `StoreIncomingAsync` all-or-nothing (F8); dedup mark-handled/reschedule update definitions (F16) |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs` | `ReassignIncomingAsync` claims by document `_id` (F10) |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Durability.cs` | Claimed-set re-read by `_id` (F10); two-tick dead-node release + corrected comment (F11); DLQ replay batch cap + bulk ops (F17) |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.DeadLetters.cs` | Empty-body guard in `EditAndReplayAsync` (F9) |
| `src/Wolverine.MongoDB/Internals/MongoDbDurabilityAgent.cs` | `StopAsync` awaits loops with bounded timeout; dispose CTSes (F12) |
| `src/Wolverine.MongoDB/Internals/MongoDbSagaStoreDiagnostics.cs` | `coerceIdentity` mirroring Marten (F13) |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Outbox.cs` | `DiscardAndReassignOutgoingAsync` delegates to `DeleteOutgoingAsync` (F16) |
| `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.NodeAgents.cs` | Cached collection properties; bulk restriction persist; parallel `LoadAllNodesAsync` + lookup join; "six collections" comment fix (F15/F17) |
| `Directory.Packages.props` | `[6.16.0,7.0.0)` WolverineFx range; `[3.9.0,4.0.0)` MongoDB.Driver range (F14) |
| `src/Wolverine.MongoDB.Tests/saga_identity_conventions.cs` | **New** — sagas keyed by `{Name-minus-Saga}Id` and `[SagaIdentity]` through real generated frames (F6) |
| `src/Wolverine.MongoDB.Tests/entity_identity_conventions.cs` | **New** — entities keyed by `{TypeName}Id` (incl. the both-members shape) through `[Entity]`/`Insert<T>`/`Delete<T>` (F7) |
| `src/Wolverine.MongoDB.Tests/inbox_batch_atomicity.cs` | **New** — batch-with-duplicate persists nothing; all-fresh batch persists all (F8) |
| `src/Wolverine.MongoDB.Tests/dead_letter_edit_replay.cs` | **New/extend** — poison (empty-body) letter edit-and-replay (F9) |
| `src/Wolverine.MongoDB.Tests/incoming_claims_id_and_destination.cs` | **New** — two-destination same-Guid claim scoping (F10) |
| `src/Wolverine.MongoDB.Tests/dead_node_release.cs` | **New** — two-tick confirmation semantics, deterministic (F11) |
| `src/Wolverine.MongoDB.Tests/saga_store_diagnostics.cs` | Extend — Guid-keyed saga + string identity coercion (F13) |
| `demo/src/OrderDemo.Application/...` + `demo/tests/OrderDemo.IntegrationTests/...` | **New** — non-`Id`-convention entity handler + safety-net tests (F18, shape decided in F5) |
| `CLAUDE.md`, `README.md`, `CHANGELOG.md`, `FOLLOWUPS.md` | Truth sweep (F15); per-fix `### Fixed` CHANGELOG entries land in each fix PR |
| `docs/superpowers/plans/2026-07-07-*.md` | This plan, the prompts, and the F1/F2/F3/F4/F5 discovery/design notes |
| `.github/workflows/ci.yml` | Expected **no change** — new tests are non-multinode xUnit files auto-included; confirm in F19 |

---

## Phase 0 — Discovery & Design (parallelizable)

### Task F1: Identity-mapping discovery

- **Goal:** Pin every fact the identity fix depends on, from the submodule and the driver, so F3's design and F6/F7's implementation never guess.
- **Scope:** Read-only. (1) Wolverine: `SagaChain.DetermineSagaIdMember` full precedence + `ValidSagaIdTypes`; where core calls `DetermineSagaIdType` vs `PullSagaIdFromMessageFrame`; `EntityAttribute`'s id resolution path; what Marten/EF Core/lightweight-SQL do to reconcile identity with their storage (Marten's id conventions, `SagaTableDefinition`); confirm no upstream Saga guard on `Delete<T>`/`IStorageAction<T>`. (2) MongoDB.Driver 3.9: `BsonClassMap` semantics — `IsClassMapRegistered`, `RegisterClassMap` idempotency/thread-safety, `LookupClassMap` auto-map+freeze behavior, what happens on `InsertOneAsync` for a type with no mapped id member (server-assigned ObjectId), `[BsonId]` interaction. (3) Local: every raw-`"_id"` filter site and every `IdMemberMap` site in `SagaFrames.cs`/`EntityFrames.cs`/`MongoDbSagaStoreDiagnostics.cs`; confirm zero `RegisterClassMap` calls exist. (4) Upstream compliance coverage: confirm no saga/storage-action compliance fact uses a non-`Id` identity member.
- **Expected output:** `docs/superpowers/plans/2026-07-07-identity-mapping-discovery.md` — each fact with file:line, confirming/correcting this plan's Verified API Facts; an explicit list of every code site that must change.
- **Files/areas likely to change:** docs only.
- **Dependencies:** none.
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Read the Wolverine identity-resolution and storage-action call sites in `external/wolverine`; confirm the Verified Facts, flag drift.
- [x] **Step 2:** Verify the driver's class-map semantics against MongoDB.Driver 3.9 source/docs (freeze rules, unmapped-id insert behavior); record exact behaviors.
- [x] **Step 3:** Enumerate the local defect sites; write the notes doc. Commit (`docs: identity-mapping discovery`).

### Task F2: Inbox/recovery/shutdown contract discovery

- **Goal:** Pin the durability contracts F4's design and F8/F10/F11/F12's implementation depend on.
- **Scope:** Read-only. (1) `DurableReceiver`'s batch-store catch path and per-envelope retry, incl. the contract comment; the RDBMS batch transaction (`MessageDatabase.Incoming.cs:183-200`). (2) `MessageIdentity` modes and every local use of `_inboxIdentity`/`EnvelopeId` in claims and re-reads. (3) The RDBMS `ReleaseOrphanedMessagesOperation` single-statement semantics; the local release's call ordering within the recovery tick; node-registration happens-before-first-claim (trace `NodeAgentController` startup). (4) Shutdown ordering: `WolverineRuntime.HostService.StopAsync` release-vs-teardown order; what the RDBMS `DurabilityAgent.StopAsync` awaits; `SafeDispose` semantics on a running `Task`. (5) MongoDB transaction behavior for `InsertMany(IsOrdered:false)` inside a transaction (fail-fast on first error — verify against driver docs/source).
- **Expected output:** `docs/superpowers/plans/2026-07-07-durability-contracts-discovery.md` — each contract with file:line; explicit confirmation (or correction) of the LD2/LD3 caveats (partial-dupe reporting in transactions; two-tick soundness preconditions).
- **Files/areas likely to change:** docs only.
- **Dependencies:** none.
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Trace `DurableReceiver` + RDBMS batch semantics; record the contract verbatim.
- [x] **Step 2:** Trace claim scoping, release atomicity, registration-before-claim, and shutdown ordering in the submodule + local source.
- [x] **Step 3:** Verify the in-transaction bulk-write failure mode; write the notes doc. Commit (`docs: durability contract discovery`).

### Task F3: Identity-mapping design (DESIGN GATE)

- **Goal:** Resolve **LD1** and **LD4** into binding contracts F6/F7 implement without further debate.
- **Scope:** Design-only synthesis of F1. Decide and justify: (1) LD1 Option A vs B (recommended B — ensure-or-fail class map), including the exact helper contract (`MongoIdentityMapping.EnsureIdMember(Type, MemberInfo)`: fast-path no-op when the driver already maps the member; register `AutoMap()+MapIdMember` when unregistered; throw a precise error when a conflicting map is frozen), where it is invoked (every frame constructor + `DetermineLoadFrame`/entity write frames — codegen time, once per type, cached), and the thread-safety story. (2) The reconciliation with CLAUDE.md's "no process-global serializer registration" stance — state the exact wording change (per-type additive id-member mapping for Wolverine-persisted types only). (3) `UpdateSagaFrame`'s silent `?? "Id"` fallback → throw with the same message as `DetermineSagaIdType`. (4) LD4: throw vs route for `Delete<TSaga>`/`IStorageAction<TSaga>` (recommended throw; specify the exact exception + message). (5) On-disk compatibility statement: `Id`-keyed documents (every 1.0.0 consumer that currently *works*) are byte-identical before/after; non-`Id` sagas/entities were broken (never loadable), so no migration path is owed — say so explicitly. (6) The test matrix: which identity shapes get covered ({`{Name-minus-Saga}Id`, `[SagaIdentity]` string, entity `{TypeName}Id`, entity with both `{TypeName}Id` and `Id`, plain `Id` regression}).
- **Expected output:** `docs/superpowers/plans/2026-07-07-identity-mapping-design.md` with the decisions, exact helper/exception contracts, and the test matrix.
- **Files/areas likely to change:** docs only.
- **Dependencies:** **F1.**
- **Blocking status:** **Blocked by: F1.**

- [x] **Step 1:** Synthesize F1; resolve decisions 1–6; write the design doc. Commit (`docs: identity-mapping design`). **Done** — `docs/superpowers/plans/2026-07-07-identity-mapping-design.md`. Resolutions: **LD1 = Option B′** (Option B refined so the helper never registers a class map for a type the driver already maps correctly — every `Id`-keyed consumer stays byte-identical with the BSON registry untouched); **LD4 = throw** (`InvalidOperationException`, exact prefixes in the design's §6.1). Three findings that change F6/F7's scope beyond this plan's snippets (full delta table in the design's §10): (a) **codegen-time-only alignment is insufficient** — `TypeLoadMode.Static` attaches pre-generated types without calling `AssembleTypes` (`HandlerChain.cs:309-325`), so frame constructors never run; the design adds a runtime leg via collection accessors in `MongoSagaOperations`/`MongoEntityOperations`; (b) `ConcurrentDictionary.GetOrAdd` is replaced by memo→`lock`→double-check (resolves F1 correction 2: `RegisterClassMap` is not idempotent); (c) the unresolvable-identity throw is `ArgumentException` (parity with `DetermineSagaIdType`/`LightweightSagaPersistenceFrameProvider`), not `InvalidOperationException`. An isolated MongoDB.Driver 3.9.0 probe empirically confirmed the design's load-bearing fact that F1 established only from source-reading: `MapIdMember` leaves `ElementName` as the member name, and **`Freeze()` normalizes it to `_id`** — so the existing `Eq("_id", …)` filters need no change (design §2).

- [x] **Step 1 — AMENDED 2026-07-26** (PR pending; design doc updated in place). F6 implemented the gate faithfully, got its 11 new facts green, and then **broke 40 previously-green compliance facts**, stopping per the escalation rule (local commit `75b5b9d` on `fix/saga-identity-mapping`, deliberately unpushed — no PR, regression bar violated). Cause: a `BsonClassMap` semantic neither F1 nor the original design §2 probe covered — **an identity member declared on a base class**, which is the shape of *every* upstream compliance saga (`BasicWorkflow<TStart,TCompleteThree,TId>.Id`, inherited by `String`/`Guid`/`Int`/`LongBasicWorkflow`). `AutoMap()` maps only a class's **own declared** members, so the design's no-op predicate — a single unfrozen probe of the document type — read `null` for every inherited id member, concluded "the driver disagrees", and called `MapIdMember`, which the driver rejects for a foreign `DeclaringType` (`ArgumentOutOfRangeException`, 79 occurrences). **Amendment (2026-07-26):** LD1 Option B′ and LD4 are unchanged in mechanism; the *predicate* is replaced by a base-chain **walk** (most-derived-first, reading each level's registered map or auto-mapping a throwaway one), verified exact against the authoritative `LookupClassMap` answer on 8 shapes **and** on all four real `*BasicWorkflow` types, with zero registry mutation and byte-identical documents (hex-diffed against an untouched twin). Two new decisions (**D6** predicate, **D7** two codegen throws) cover the shapes the mechanism provably cannot fix: an inherited identity member (the driver refuses to map it) and a *different* base-declared member already occupying `_id` (which would register cleanly and then throw `BsonSerializationException` on the type's **first write** — so it is pre-detected at codegen). 30 new verbatim probe facts are recorded in design §2.8; §10.1 is an F6-facing diff naming every change relative to `75b5b9d`. Also corrected: §2.6's "the probe step has no global side effects" (true of `AutoMap` alone, false of `Freeze()`, which registers base maps globally — so the predicate never freezes).

### Task F4: Durability & coordination design (DESIGN GATE)

- **Goal:** Resolve **LD2** and **LD3** plus the F10/F12 semantics into binding contracts.
- **Scope:** Design-only synthesis of F2. Decide and justify: (1) LD2 Option A vs B for batch `StoreIncomingAsync` (recommended A — transaction), incl. the partial-dupe-list caveat and the exact exception contract preserved (`DuplicateIncomingEnvelopeException` with ≥1 dupe envelope). (2) LD3 two-tick release: the exact algorithm (distinct-owners aggregation, `deadNow ∩ previous`, per-store tick state), the soundness argument (registration-before-claim + monotonic numbers), the one-interval latency cost, and the corrected method/CLAUDE.md wording. (3) F10 contract: `ReassignIncomingAsync` claims `Filter.In(x => x.Id, envelopes.Select(InboxIdentity))` (document `_id` — destination-scoped by construction in `IdAndDestination`, identical to today in `IdOnly`); `RecoverOrphanedIncomingAsync`'s re-read keys the same `_id` set and maps winners back to envelopes; state that no `IMessageStore` signature changes. (4) F12 contract: `StopAsync` awaits `_recoveryTask`/`_scheduledJob` with a bounded timeout (e.g. 5 s), swallows `OperationCanceledException`, disposes both CTSes; note the residual upstream-ordering window (release-before-teardown at `HostService.cs:380/:402`) is out of this library's control — document, don't chase.
- **Expected output:** `docs/superpowers/plans/2026-07-07-durability-coordination-design.md` with the four contracts.
- **Files/areas likely to change:** docs only.
- **Dependencies:** **F2.**
- **Blocking status:** **Blocked by: F2.**

- [x] **Step 1:** Synthesize F2; resolve decisions 1–4; write the design doc. Commit (`docs: durability & coordination design`).

**Status: done.** `docs/superpowers/plans/2026-07-07-durability-coordination-design.md`, branch
`docs/durability-coordination-design`. All four contracts are binding; **OQ3 → LD2 Option A**
(transaction wrap) and **OQ4 → LD3 two-tick confirmation** are settled. Deviations from this plan's
sketches, each argued in the design doc: (a) F8 uses the store's existing
`session.WithTransactionAsync(...)` shape (`Inbox.cs:154-165` precedent) instead of manual
`StartTransaction`/`Commit`/`Abort`, and **must** pass explicit `TransactionOptions`
(`w:majority + j:true`) because MongoDB ignores the database-handle write-concern pin inside a
transaction; (b) the duplicate list is rebuilt from a **single post-abort `_id` existence probe**
(complete and driver-shape-independent) rather than `WriteErrors[i].Index`, which removes the
partial-dupe-list caveat — tests still assert persistence count as the load-bearing fact;
(c) LD3 additionally binds **owned-read-before-live-read** ordering and `Filter.In(confirmed)`
(never `Filter.Nin(liveSnapshot)`), which closes the plan's cited race on its own — two-tick
confirmation plus monotonic node numbers is the second, independent leg; (d) F12 needs **no** csproj
change (`InternalsVisibleTo Wolverine.MongoDB.Tests` already exists at
`src/Wolverine.MongoDB/Wolverine.MongoDB.csproj:14`). Also flagged: F11 must **amend** the existing
single-tick assertion in `dead_node_ownership_release.cs:45-52` (strictly stronger, not weakened),
and §0 records that the fact base was re-verified against the current submodule pin **V6.21.0**
(F2 ran against V6.16.0 — substance unchanged, `DurableReceiver.cs` citations moved).

### Task F5: Test inventory + demo impact design

- **Goal:** A complete inventory of new tests per finding (library + demo) and the demo-extension design, so test files and demo skeletons are specified before implementation starts.
- **Scope:** Design-only. (1) Verify which upstream compliance suites exist and what they cover: saga compliance (`String/Guid/Int/Long IdentifiedSagaComplianceSpecs` — all `Id`-membered), `StorageActionCompliance` (`Todo { string Id }` only), `LeadershipElectionCompliance` (already subclassed). Conclusion to confirm: **no upstream spec covers any finding — all new tests are custom on `AppFixture`**, in the existing style (`saga_atomicity.cs`, `storage_action_compliance.cs` as structural models). (2) Map each finding → test file, test names, trigger, and assertion (the File Structure table above is the starting inventory; refine it). (3) Design the demo extension (F18): a small entity using the `{TypeName}Id` convention (e.g. `CustomerFeedback { Guid CustomerFeedbackId; … }`) with `[Entity]`-load + `Insert<T>` handlers alongside the existing `OrderNote` handlers, plus safety-net tests mirroring `OrderNoteFlowTests.cs`; explicitly decide the demo does **not** change existing types (no churn on `OrderNote`/sagas). (4) Note which custom tests are upstream-contribution candidates (the non-`Id` identity matrix).
- **Expected output:** `docs/superpowers/plans/2026-07-07-remediation-test-inventory.md` — the finding→test mapping table and the demo design.
- **Files/areas likely to change:** docs only.
- **Dependencies:** none to start; **refine after F3/F4** (exception contracts feed assertions).
- **Blocking status:** **Can start immediately** (finalize assertion details once F3/F4 land).

- [x] **Step 1:** Verify upstream suite coverage; build the finding→test mapping.
- [x] **Step 2:** Design the demo entity + tests; write the doc. Commit (`docs: remediation test inventory + demo design`).

---

## Phase 1 — Tier 1: Identity & Codegen Correctness

### Task F6: Saga identity conventions map to `_id`

- **Goal:** Every legal Wolverine saga identity convention (`[SagaIdentity]`, `{TypeName}Id`, `{Name-minus-Saga}Id`, `SagaId`, `Id`) loads/inserts/updates/deletes correctly; an unresolvable identity fails **loudly at codegen** with one consistent message.
- **Scope:** Per F3's design. New `Internals/MongoIdentityMapping.cs`; wire it into all four saga frame constructors (`LoadSagaFrame`, `InsertSagaFrame`, `UpdateSagaFrame`, `DeleteSagaFrame` — codegen time, cached per type); replace `UpdateSagaFrame`'s `?? "Id"` fallback with the throwing resolution. **Preserve the on-disk shape and all behavior for `Id`-keyed sagas** — the compliance suites are the regression oracle. No entity changes here (F7).
- **Expected output:** New `saga_identity_conventions.cs` green (sagas keyed by `{Name-minus-Saga}Id` and `[SagaIdentity]` progressing start→update→complete through **real generated frames**, verified by direct Mongo reads of the native-typed `_id`); all existing saga compliance (String/Guid/Int/Long), `saga_atomicity`, OCC, and diagnostics tests still green; full suite green.
- **Files/areas likely to change:** `Internals/MongoIdentityMapping.cs` (new), `Internals/SagaFrames.cs`, `src/Wolverine.MongoDB.Tests/saga_identity_conventions.cs` (new), `CHANGELOG.md` (`### Fixed`).
- **Dependencies:** **F3.**
- **Blocking status:** **Blocked by: F3.**

Implementation shape (per LD1 Option B — adjust only if F3 decided otherwise):

```csharp
// Internals/MongoIdentityMapping.cs — codegen-time, once per type, cached.
internal static class MongoIdentityMapping
{
    private static readonly ConcurrentDictionary<Type, bool> _ensured = new();

    /// <summary>
    /// Ensures the MongoDB driver maps Wolverine's resolved identity member to _id.
    /// No-op when the driver's map already agrees (member named Id/id/_id, or an
    /// app-registered map with the same id member). Registers an additive per-type
    /// class map when none exists. Throws when a conflicting map is already frozen.
    /// </summary>
    internal static void EnsureIdMember(Type documentType, MemberInfo idMember)
    {
        _ensured.GetOrAdd(documentType, _ =>
        {
            if (BsonClassMap.IsClassMapRegistered(documentType))
            {
                var map = BsonClassMap.LookupClassMap(documentType);
                if (map.IdMemberMap?.MemberName == idMember.Name) return true;
                throw new InvalidOperationException(
                    $"The registered BsonClassMap for {documentType.FullNameInCode()} maps " +
                    $"'{map.IdMemberMap?.MemberName ?? "(none)"}' as the document id, but Wolverine " +
                    $"resolved '{idMember.Name}' as the identity member. Align them with [BsonId] " +
                    $"or a class map that maps '{idMember.Name}' as the id.");
            }

            var classMap = new BsonClassMap(documentType);
            classMap.AutoMap();
            if (classMap.IdMemberMap?.MemberName != idMember.Name)
                classMap.MapIdMember(idMember);           // AutoMap found no id (or a different one)
            BsonClassMap.RegisterClassMap(classMap);
            return true;
        });
    }
}
```

```csharp
// SagaFrames.cs — every frame ctor resolves the member ONCE, throws on null (no "Id" fallback):
var idMember = SagaChain.DetermineSagaIdMember(sagaType, sagaType)
    ?? throw new InvalidOperationException(
        $"Unable to determine the identity member for saga type {sagaType.FullNameInCode()}");
MongoIdentityMapping.EnsureIdMember(sagaType, idMember);
// The raw Eq("_id", value) filters in MongoSagaOperations stay — the class map now guarantees
// the resolved member serializes AS _id, so read and write agree for every convention.
```

- [x] **Step 1:** Write the failing tests first (`saga_identity_conventions.cs`): a `ShipmentSaga { Guid ShipmentId }`-style saga (`{Name-minus-Saga}Id`) and a `[SagaIdentity] string`-keyed saga, each driven start→update→complete via `IHost` + real handlers; assert via direct Mongo reads that the document `_id` is the identity value (native type) and that update/complete find it. Run → **FAIL today** (load returns null / duplicate saga docs — the review's exact failure mode).
- [x] **Step 2:** Implement `MongoIdentityMapping` + wire the four saga frame ctors + replace the `UpdateSagaFrame` fallback with the throw. Dump generated code for one convention saga (`HandlerGraph`/`SourceCode` reflection per the repo's "dump generated handler source" convention) and confirm the frames still emit the same session-bound operations.
- [x] **Step 3:** Run the new tests → PASS. Run the full library suite (both TFMs) → green, **zero changes to compliance facts**. Add the CHANGELOG `### Fixed` entry.
- [x] **Step 4:** Commit (`fix: saga identity conventions map to _id`), push, PR, checks green, update this plan doc.

**Status: done**, in two passes either side of the F3 amendment.

**Pass 1** implemented F3 as originally written and covered all four non-`Id` precedence tiers plus the
helper's branch matrix — all green (11 facts). It then **broke 40 previously-green compliance facts** and
stopped per the escalation rule rather than improvising. Cause: `AutoMap()` maps only a class's *own*
declared members, so the design's single-unfrozen-probe predicate reported "no id member" for an
identity member declared on a **base** class — the shape of every upstream compliance saga
(`BasicWorkflow<TStart,TCompleteThree,TId>.Id`) — and the write path then hit the driver's refusal to
`MapIdMember` a member it does not own (`ArgumentOutOfRangeException: The memberInfo argument must be
for class StringBasicWorkflow, but was for class BasicWorkflow`3`). Neither semantic was in F1 or F3's
original §2 probe. Reported with a probe transcript instead of a repair.

**F3 amendment** (PR #180) added §2.8's probe battery and resolved it: **D6**, the "driver already
agrees" predicate becomes a base-chain walk (most-derived-first, reading registered maps or auto-mapping
a throwaway per level — verified exact against `LookupClassMap` on 8 shapes and all four real
`*BasicWorkflow` types, registering nothing); **D7**, two shapes the mechanism cannot fix now throw at
codegen with verified remedies. Freezing a probe was rejected because `Freeze()` registers the *base*
type's map globally.

**Pass 2** implemented §10.1's F6-facing diff. Everything from pass 1 survived unchanged — the memo/lock
structure, `ResolveIdMember`, the four frame ctors, `sagaCollection<TSaga>()`, `DetermineSagaIdType`'s
delegation, the `?? "Id"` removal — confirming the mechanism was sound and only the predicate was wrong.
Final: **197 facts green on net9.0 and net10.0** (181 pre-existing + 16 new), multinode green, **zero
edited compliance facts**, and the generated source verified identical in shape to an `Id`-keyed saga
(only the emitted member name differs). §7(d)'s LD4 CLAUDE.md bullet is deliberately **left to F7** —
it documents the `Delete<TSaga>`/`IStorageAction<TSaga>` throws, which do not exist until F7 ships, and
CLAUDE.md must not describe unimplemented behavior. The upstream-contribution note for the identity
matrix is in `FOLLOWUPS.md`.

### Task F7: Entity identity agreement + saga guards on storage-action paths

- **Goal:** `[Entity]` loads and `Insert/Update/Store/Delete<T>`/`IStorageAction<T>` writes key the **same** member for every entity shape; `Delete<TSaga>`/`IStorageAction<TSaga>` from non-saga handlers fail loudly at codegen instead of silently no-oping against the wrong collection.
- **Scope:** Per F3 (LD1 + LD4). `LoadEntityFrame` + the entity write frames call `MongoIdentityMapping.EnsureIdMember` at construction with the Wolverine-resolved member (`MongoDbPersistenceFrameProvider.DetermineSagaIdType`'s member resolution); the runtime `IdOf` (`BsonClassMap.LookupClassMap(...).IdMemberMap`) then agrees by construction — keep it. Add the `CanBeCastTo<Saga>()` guard (throwing, per LD4) to the single-variable `DetermineDeleteFrame` and `DetermineStorageActionFrame`. Sequenced after F6 because both edit frame files and F7 consumes `MongoIdentityMapping`.
- **Expected output:** New `entity_identity_conventions.cs` green: (a) entity with only `{TypeName}Id` round-trips through `[Entity]` load + `Insert<T>` + `Delete<T>`; (b) the poisoned shape from the review (`OrderNote`-like entity with **both** `{TypeName}Id` and `Id`) reads and writes the same key; (c) `Storage.Delete(saga)` from a plain handler fails at codegen with the LD4 message. `storage_action_compliance` (upstream suite) still green; full suite green.
- **Files/areas likely to change:** `Internals/EntityFrames.cs`, `Internals/MongoDbPersistenceFrameProvider.cs`, `src/Wolverine.MongoDB.Tests/entity_identity_conventions.cs` (new), `CHANGELOG.md`.
- **Dependencies:** **F3, F6.**
- **Blocking status:** **Blocked by: F3, F6.**

- [x] **Step 1:** Write the failing tests (`entity_identity_conventions.cs`) for shapes (a) and (b) → FAIL today (write under one member, read by the other). Write the codegen-guard test (c) asserting the thrown message → FAIL today (silent no-op). **Confirmed red: 7 of 8 new facts failed**, each in its predicted shape — (a) `Crate` and the runtime-leg `Pallet`: `InvalidOperationException : Wolverine.MongoDB.Tests.Crate has no mapped _id member` (the driver resolves nothing, so `IdOf` cannot even name a key); (b) `Ledger`: `document["_id"].BsonType should be BsonType.Binary but was BsonType.String` — the write keyed the driver's `Id`, the read filtered Wolverine's `LedgerId`; (c) and the two §3.4/§3.5 entity shapes: `should throw … but did not`. The eighth (row 24(a), inherited `Id`) passed before and after — it is the regression guard for the no-op path.
- [x] **Step 2:** Wire `MongoIdentityMapping.EnsureIdMember` into `LoadEntityFrame` + `MongoUpsertEntityFrame` + `MongoDeleteEntityByVariableFrame` constructors; add the LD4 throws to `DetermineDeleteFrame(Variable, …)` and `DetermineStorageActionFrame`. Per design §4.2/delta 6 the runtime leg landed too: `MongoEntityOperations`'s four inline `GetCollection` calls now route through one private `entityCollection<T>` accessor that aligns first, and `DetermineStorageActionFrame` calls `EnsureIdMember` itself (it builds a bare `MethodCall`, so there is no constructor to hook). `IdOf` is unchanged, as specified — once aligned, `LookupClassMap(...).IdMemberMap` **is** Wolverine's resolved member.
- [x] **Step 3:** Run new tests → PASS; `storage_action_compliance` + full suite green on both TFMs. CHANGELOG entry. **212 single-node facts green on net9.0 and net10.0** (204 pre-existing + 8 new), **zero edited facts**; multinode 18/18. No `GenerateCode` body was touched, so the emitted handler source is unchanged by construction — the only delta is which member the driver serializes as `_id`.
- [x] **Step 4:** Commit (`fix: entity identity agreement + saga guards on storage-action paths`), push, PR [#186](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/186), checks green, update plan doc.

**Test-isolation note (worth carrying to any future "must fail at codegen" fact).** Unlike F6's saga row 23 — which surfaces lazily inside `HandlerGraph.HandlerFor` → `AssembleTypes` — an entity **return-value side effect** is resolved eagerly during `HandlerGraph.Compile` (`SideEffectPolicy` → `Insert<T>.BuildFrame`), i.e. at `StartAsync`. Two consequences: the guard facts assert on **host build**, not on `HandlerFor`; and the four deliberately mis-shaped handlers must carry `[WolverineIgnore]`, because conventional discovery is assembly-wide and every other suite's host would otherwise fail to start. (`HandlerDiscovery.FindCalls` concatenates the explicit `IncludeType` list *after* the filtered query, so the attribute excludes them from the scan while this file's host still registers them.) Without it, 100+ unrelated facts fail with F7's own error message.

---

## Phase 2 — Tier 2: Inbox & Dead-Letter Correctness

### Task F8: All-or-nothing batch `StoreIncomingAsync`

- **Goal:** A batch containing ≥1 duplicate persists **nothing**, so `DurableReceiver`'s per-envelope retry (which completes duplicates without enqueuing) cannot strand fresh envelopes.
- **Scope:** Per F4 (LD2 — expected Option A). Wrap the batch insert in a session/transaction on the store's pinned database handle; abort on `MongoBulkWriteException`; classify: non-duplicate error → rethrow; duplicate → `DuplicateIncomingEnvelopeException` (≥1 dupe listed; the list may be partial inside a transaction — the F2-verified fail-fast behavior — which the receiver's retry-all contract tolerates). Single-envelope `StoreIncomingAsync` unchanged.
- **Expected output:** New `inbox_batch_atomicity.cs` green: (a) batch of N with 1 pre-stored duplicate → `DuplicateIncomingEnvelopeException` **and** direct Mongo count proves none of the N−1 fresh envelopes persisted; (b) all-fresh batch persists all N; (c) existing single-store dedup tests unaffected. Full suite + multinode suite green.
- **Files/areas likely to change:** `Internals/MongoDbMessageStore.Inbox.cs`, `src/Wolverine.MongoDB.Tests/inbox_batch_atomicity.cs` (new), `CHANGELOG.md`.
- **Dependencies:** **F4.**
- **Blocking status:** **Blocked by: F4.**

Implementation shape (LD2 Option A) — **superseded by F4's binding contract**
(`2026-07-07-durability-coordination-design.md` §1): use `session.WithTransactionAsync` with explicit
`TransactionOptions` (`w:majority + j:true`), and build the dupe list from a post-abort `_id`
existence probe. The snippet below is kept for context only:

```csharp
public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
{
    if (envelopes.Count == 0) return;
    var docs = envelopes.Select(e => new IncomingMessage(e, InboxIdentity(e))).ToList();

    using var session = await _database.Client.StartSessionAsync();
    session.StartTransaction();
    try
    {
        await Incoming.InsertManyAsync(session, docs, new InsertManyOptions { IsOrdered = false });
        await session.CommitTransactionAsync();
    }
    catch (MongoBulkWriteException<IncomingMessage> ex)
    {
        await session.AbortTransactionAsync();      // nothing from this batch survives
        var dupes = ex.WriteErrors
            .Where(w => w.Category == ServerErrorCategory.DuplicateKey)
            .Select(w => envelopes[w.Index])
            .ToList();
        if (ex.WriteErrors.Any(w => w.Category != ServerErrorCategory.DuplicateKey)) throw;
        if (dupes.Count > 0) throw new DuplicateIncomingEnvelopeException(dupes);
        throw;
    }
}
```

- [x] **Step 1:** Write the failing test (a): pre-store one envelope, batch-store it plus 4 fresh ones, assert the exception **and** `Incoming` count unchanged for the fresh ids → FAIL today (4 fresh docs persist). **Confirmed red:** `should be 0L but was 4L`. The intra-batch-duplicate fact failed the same way (`should be 0 but was 2`).
- [x] **Step 2:** Implement the transaction wrap; confirm the duplicate-classification against the F2-verified in-transaction failure mode. **Empirically pinned** (MongoDB.Driver 3.10.0 / mongo:7, throwaway probe): an in-transaction duplicate surfaces as `MongoBulkWriteException<IncomingMessage>` with **one** write error (`index` = first offending document, `code=11000`, `category=DuplicateKey`), no inner exception — i.e. the same type as the non-transactional path, but fail-fast, so the write-error list cannot enumerate duplicates. Confirms §1d's post-abort probe as the dupe-list source; the classifier keeps the other shapes per §1b's tolerance requirement. Recorded in the `isDuplicateKeyFailure` doc comment.
- [x] **Step 3:** Tests (a)+(b) PASS; full suite + `--filter "Category=multinode"` green on both TFMs — **201 single-node + 18 multinode on net9.0 and net10.0**. CHANGELOG entry added.
- [x] **Step 4:** Commit (`fix: all-or-nothing batch StoreIncomingAsync on duplicate`), push, PR, checks green, update plan doc.

**Existing-test amendment (not a weakened assertion).** `inbox.cs`'s
`bulk_store_throws_for_dupe_subset_and_persists_the_rest` asserted the *defect* by name — that the
non-duplicate envelopes "must still have persisted (unordered insert)". It is renamed
`bulk_store_reports_only_the_dupe_and_persists_nothing` and its two `ShouldBeTrue()` persistence
assertions inverted. Its distinct value is retained and deliberately not duplicated in the new file:
it asserts the dupe list is **exact** (`Count == 1`, the pre-stored envelope), which the post-abort
probe makes achievable, whereas `inbox_batch_atomicity.cs` asserts only `≥1` per LD2's robustness
preference.

### Task F9: `EditAndReplayAsync` tolerates body-less poison letters

- **Goal:** The one remediation tool for unserializable envelopes works on exactly the letters it exists for.
- **Scope:** Mirror `DeadLetterMessage.ToEnvelope()`'s guard (`DeadLetterMessage.cs:70-72`) in `EditAndReplayAsync` (`MongoDbMessageStore.DeadLetters.cs:91`): when `doc.Body` is empty, rebuild the envelope from the document's metadata (reuse `ToEnvelope()` — it already does this) instead of calling `EnvelopeSerializer.Deserialize` directly. Small, self-contained; no design gate needed.
- **Expected output:** New/extended `dead_letter_edit_replay.cs` green: a dead letter created via `ForUnserializableEnvelope` (empty body) is edited with a new body and replays; a normal-bodied letter's behavior unchanged. Full suite green.
- **Files/areas likely to change:** `Internals/MongoDbMessageStore.DeadLetters.cs`, `src/Wolverine.MongoDB.Tests/dead_letter_edit_replay.cs`, `CHANGELOG.md`.
- **Dependencies:** none.
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Write the failing test: store a poison letter (empty `Body`), call `EditAndReplayAsync` with a valid new body → today throws `EndOfStreamException` (assert the current failure first to prove the repro).
- [x] **Step 2:** Route the envelope reconstruction through `doc.ToEnvelope()` (which carries the guard) before applying the edited body; run → PASS. Normal-letter regression test PASS.
- [x] **Step 3:** Full suite green; CHANGELOG entry. Commit (`fix: EditAndReplayAsync tolerates body-less poison letters`), push, PR, checks green, update plan doc. PR [#157](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/157), checks green.

### Task F10: Destination-scoped incoming claims

- **Goal:** In `MessageIdentity.IdAndDestination` mode, recovery claims exactly the documents it loaded — no cross-destination stranding, no false claim certification.
- **Scope:** Per F4 decision 3. `ReassignIncomingAsync` claims by document `_id` (`Filter.In(x => x.Id, envelopes.Select(InboxIdentity))` + `OwnerId == AnyNode`); `RecoverOrphanedIncomingAsync`'s post-claim re-read uses the same `_id` set (`&& OwnerId == nodeNumber`) and maps winning `_id`s back to envelopes. In `IdOnly` mode `_id` **is** the Guid string, so behavior is identical to today — the compliance and multinode suites prove no regression. No `IMessageStore` signature changes.
- **Expected output:** New `incoming_claims_id_and_destination.cs` green: with `IdAndDestination` configured, two stored documents sharing one envelope Guid at two destinations, both `OwnerId == 0`; claiming a page for destination 1 leaves destination 2's document untouched (`OwnerId` still 0) and certifies only destination 1's doc. Full suite + multinode green.
- **Files/areas likely to change:** `Internals/MongoDbMessageStore.cs` (`ReassignIncomingAsync`), `Internals/MongoDbMessageStore.Durability.cs` (re-read), `src/Wolverine.MongoDB.Tests/incoming_claims_id_and_destination.cs` (new), `CHANGELOG.md`.
- **Dependencies:** **F4.**
- **Blocking status:** **Blocked by: F4.**

- [x] **Step 1:** Write the failing test: configure a store with `IdAndDestination` (via `DurabilitySettings.MessageIdentity`), store the two-destination same-Guid pair with `OwnerId = 0`, call `ReassignIncomingAsync` for the destination-1 envelope → today destination 2's doc gets claimed too (assert its `OwnerId` — FAIL). Two facts failed as predicted: destination 2's `OwnerId` `should be 0 but was 7`, and its own recovery page (`LoadPageOfGloballyOwnedIncomingAsync(destinationTwo)`) came back **empty** — the stranding, observed end-to-end. The third fact (`IdOnly` still claims) passed before the fix, pinning the no-regression baseline.
- [x] **Step 2:** Switch claim + re-read to `_id`-keyed filters; re-run → PASS. `IdOnly` byte-equivalence verified by inspection: `_inboxIdentity` is `e => e.Id.ToString()` when `Durability.MessageIdentity == MessageIdentity.IdOnly` (`MongoDbMessageStore.cs:46-48`), so `Filter.In(x => x.Id, …)` receives exactly the values `Filter.In(x => x.EnvelopeId, …)` did — same as the RDBMS provider, which scopes on `id + received_at` (`MessageDatabase.Incoming.cs:27-30`). The re-read's `ToDictionary(InboxIdentity)` is injective because the page comes from distinct documents.
- [x] **Step 3:** Full suite + `Category=multinode` green on both TFMs — 204 single-node + 18 multinode on net9.0 **and** net10.0 (recovery claims run in every Balanced tick). `envelopeId` index comment corrected per the contract's scope guard; `RescheduleAsync` untouched. CHANGELOG entry. Commit (`fix: destination-scoped incoming claims (IdAndDestination)`), push, PR [#184](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/184), checks green.

---

## Phase 3 — Tier 3: Coordination & Shutdown

### Task F11: Two-tick-confirmed dead-node ownership release

- **Goal:** `ReleaseDeadNodeOwnershipAsync` can never strip a live node's in-flight envelopes, restoring the atomicity guarantee the RDBMS single-statement release provides.
- **Scope:** Per F4 (LD3). Replace the snapshot-`Nin` release with: (1) aggregate the distinct `OwnerId`s present in incoming+outgoing; (2) `deadNow` = owned − (live ∪ {0}); (3) release only `deadNow ∩ _previousTickDead` via `Filter.In(x => x.OwnerId, confirmed)`; (4) store `deadNow` for the next tick (per-store field — one durability agent per store instance). Correct the method's doc comment (the current safety claim at `Durability.cs:172-173` is the falsified invariant) and the CLAUDE.md dead-node bullet. The soundness argument (registration-before-claim + monotonic never-reused numbers ⇒ a number in *last* tick's dead set cannot belong to a node that registered since) goes in the comment.
- **Expected output:** New `dead_node_release.cs` green, deterministic (no sleeps/races): (a) an owner number with no node doc is **not** released on the first observing tick, **is** released on the second; (b) an owner number whose node doc appears between the two ticks is not released; (c) `OwnerId == 0` and live owners never touched. Multinode suite (incl. `LeadershipElectionCompliance` and `multinode_end_to_end`) green **5× consecutively per TFM** — the release participates in every Balanced tick, so this is the regression bar.
- **Files/areas likely to change:** `Internals/MongoDbMessageStore.Durability.cs`, `src/Wolverine.MongoDB.Tests/dead_node_release.cs` (new), `CLAUDE.md` (dead-node bullet), `CHANGELOG.md`.
- **Dependencies:** **F4.**
- **Blocking status:** **Blocked by: F4.**

Implementation shape (LD3) — refined by F4 §2: read the **owned set before the live set**, and note
that F11 must also amend the existing single-tick assertion in `dead_node_ownership_release.cs:45-52`:

```csharp
private HashSet<int>? _previousDeadOwners;   // recovery loop = one caller; no locking needed

internal async Task ReleaseDeadNodeOwnershipAsync(CancellationToken token)
{
    var live = (await NodeDocs.Find(FilterDefinition<NodeDocument>.Empty)
        .Project(x => x.AssignedNodeNumber).ToListAsync(token)).ToHashSet();
    live.Add(MongoConstants.AnyNode);

    var owned = new HashSet<int>();
    owned.UnionWith(await Incoming.Distinct(x => x.OwnerId, FilterDefinition<IncomingMessage>.Empty, cancellationToken: token).ToListAsync(token));
    owned.UnionWith(await Outgoing.Distinct(x => x.OwnerId, FilterDefinition<OutgoingMessage>.Empty, cancellationToken: token).ToListAsync(token));

    var deadNow = owned.Where(n => !live.Contains(n)).ToHashSet();
    var confirmed = _previousDeadOwners is null ? [] : deadNow.Intersect(_previousDeadOwners).ToList();
    _previousDeadOwners = deadNow;
    if (confirmed.Count == 0) return;

    await Incoming.UpdateManyAsync(Builders<IncomingMessage>.Filter.In(x => x.OwnerId, confirmed),
        Builders<IncomingMessage>.Update.Set(x => x.OwnerId, MongoConstants.AnyNode), cancellationToken: token);
    await Outgoing.UpdateManyAsync(Builders<OutgoingMessage>.Filter.In(x => x.OwnerId, confirmed),
        Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, MongoConstants.AnyNode), cancellationToken: token);
}
```

- [x] **Step 1:** Write the failing tests (a)–(c) driving the method directly on the store (two calls = two ticks; deterministic) → (a) FAILS today (released on the *first* tick). Observed: `owner_with_no_node_doc_is_not_released_on_first_tick_only_second` — *"should be 901 but was 0 / a dead owner observed for the FIRST time must not be released yet"*. (b) `owner_that_registers_a_node_between_ticks_is_never_released` also FAILED (*"should be 902 but was 0"*) — the number was already released at tick 1, so registering its node document before tick 2 came too late. (c) `anynode_and_live_owners_never_touched_across_two_ticks` passed today as designed: it is the regression guard folded in from the deleted single-tick fact.
- [x] **Step 2:** Implement two-tick confirmation + rewrite the comment with the soundness argument; run → PASS (3/3). Per §2g the old single-tick fact was **folded into** `dead_node_release.cs` and `dead_node_ownership_release.cs` deleted (its live-node/`AnyNode` assertions live on in fact (c); its release assertion is now two-tick in fact (a)) — a deliberate strengthening, called out in the PR body.
- [x] **Step 3:** Done — PR #185, all checks green (`library`, `demo`, CodeQL, Trivy), `mergeable=MERGEABLE`.
  - **Rebased onto F10 (#184)**, which merged mid-task. Exactly the collision F4 §2's disjointness note predicted; F11 was second, so it rebased. Only `CHANGELOG.md` genuinely conflicted (both added a top `### Fixed` entry — both kept, F10's first); `Durability.cs` auto-merged and was **read** to confirm F10's `_id`-keyed claim/re-read in `RecoverOrphanedIncomingAsync` survived intact above the untouched release method. The two changes are disjoint: F10 changed the claim *identity*, F11 the release *gating*, and `Distinct(x => x.OwnerId, …)` is indifferent to `MessageIdentity` mode.
  - Single-node suite on the rebased tree (`f86c725`): **206/206 green on net9.0 and net10.0** (203 + F10's 3 facts).
  - Multinode bar on `f86c725`: **10/10 runs green, 18/18 facts each — 5× consecutive per TFM** (net9.0 2m29–2m43, net10.0 2m35–2m40). **No timeout widened and no assertion weakened**, and none looked likely to be needed: the recovery tick is `ScheduledJobPollingTime` (500 ms in `multinode_end_to_end`), so the extra tick costs ≈1 s against that suite's 20 s poll deadline for `envelopes_owned_by_a_dead_node_are_rescued_by_a_survivor`.
  - Process note: an earlier bar run tested the *pre-rebase* tree and a stale background loop overlapped a replacement one, so both were discarded and the bar re-run as a single sequential batch that records the HEAD sha it tested. The 10/10 above is from that clean batch.
  - `CLAUDE.md` dead-node bullet rewritten per F4 §2e, with the reciprocal ⚠️ monotonicity warning added to the T4.6 node-number bullet (plan risk **R6**). `FOLLOWUPS.md`: ownership-fencing motivation appended to the existing epoch entry (it previously covered only the leadership lease). CHANGELOG `### Fixed` entry incl. the behavior-timing change.

### Task F12: Durability agent shutdown awaits its loops

- **Goal:** `StopAsync` completes only after the recovery/scheduled loops have observed cancellation (bounded), so no claim write is issued after the node begins deregistration; CTS leaks fixed.
- **Scope:** Per F4 decision 4. `StopAsync`: cancel; `await Task.WhenAll(_recoveryTask ?? Task.CompletedTask, _scheduledJob ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5))` inside a try/catch swallowing `OperationCanceledException`/`TimeoutException` (log the timeout); dispose `_cancellation` and `_combined`; then `Status = AgentStatus.Stopped`. Document the residual upstream-ordering window (out of library control) in a comment.
- **Expected output:** A focused test: start the agent, `StopAsync`, assert both loop tasks are completed (not running) when it returns and a second `StopAsync` is idempotent. Full suite + multinode green.
- **Files/areas likely to change:** `Internals/MongoDbDurabilityAgent.cs`, a test in `src/Wolverine.MongoDB.Tests/` (extend the existing durability-agent coverage or add `durability_agent_shutdown.cs`), `CHANGELOG.md`.
- **Dependencies:** **F4** (the await/timeout contract).
- **Blocking status:** **Partially blocked by: F4** (CTS disposal is mechanical and could start now; keep it one PR — wait for F4).

- [x] **Step 1:** Write the failing test: after `StopAsync` returns, `_recoveryTask.IsCompleted` (observed via agent status/behavior, or an internal accessor via `InternalsVisibleTo`) → FAIL today (task still running).
- [x] **Step 2:** Implement await-with-timeout + disposal; run → PASS; multinode suite green (shutdown paths run in every Balanced test teardown). CHANGELOG. Commit (`fix: durability agent StopAsync awaits its loops`), push, PR, checks green, update plan doc.

**Status: done.** Branch `fix/durability-agent-shutdown`. RED wasn't the loop-completion assertion
itself — an isolated probe showed `PeriodicTimer.WaitForNextTickAsync`'s cancellation resolving
synchronously through the linked token, so a tick idling between polls looks "completed" even
under the old code. The reliable RED signal was CTS disposal: `agent.CancellationSource.Cancel()`
was expected to throw `ObjectDisposedException` after `StopAsync` and did not
(`Shouldly.ShouldAssertException: ... should throw System.ObjectDisposedException but did not`),
confirming `_cancellation`/`_combined` were never disposed. Implemented per F4 §4b/§4c exactly:
`StopAsync` is `async`, guarded by an interlocked `_stopping` flag; cancels; awaits
`Task.WhenAll(loops).WaitAsync(StopTimeout, cancellationToken)` (internal 5s default), swallowing
`OperationCanceledException` and logging a warning on `TimeoutException`; disposes `_combined`
then `_cancellation` in `finally`; reports `Stopped`. Added `RecoveryTask`/`ScheduledJobTask`/
`CancellationSource` internal test-only accessors (no csproj change — `InternalsVisibleTo` already
covers the test project). All three new facts green; full suite + multinode green on both net9.0
and net10.0 (218 single-node + 18 multinode per TFM). CHANGELOG entry added.

---

## Phase 4 — Tier 4: Contracts, Packaging, Documentation (all independent)

### Task F13: Diagnostics identity coercion

- **Goal:** `ReadSagaAsync` honors the `ISagaStoreDiagnostics` contract ("the implementation is expected to coerce as needed") for Guid/int/long-keyed sagas handed string identities.
- **Scope:** Add a `coerceIdentity(object identity, Type idType)` helper to `MongoDbSagaStoreDiagnostics` mirroring Marten's (`MartenSagaStoreDiagnostics.cs:147-168`): pass-through when types match; `Guid.Parse`/`int.Parse`/`long.Parse` when handed a string; return-null → "not found" upstream on unparseable input. Fix the `:23` class doc ("used as-is"). Optionally drop the vestigial `Type sagaType` parameter on the private generic helpers (`:92` — always `typeof(TSaga)`) while in the file.
- **Expected output:** `saga_store_diagnostics.cs` extended with a **Guid-keyed** saga: `ReadSagaAsync` finds it when handed the identity as a `Guid` **and** as its `ToString()` string; int/long variants analogous. Existing string-saga tests green; full suite green.
- **Files/areas likely to change:** `Internals/MongoDbSagaStoreDiagnostics.cs`, `src/Wolverine.MongoDB.Tests/saga_store_diagnostics.cs`, `CHANGELOG.md`.
- **Dependencies:** none.
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Write the failing test: Guid-keyed diag saga, `ReadSagaAsync(sagaName, guid.ToString())` → null today (FAIL).
- [x] **Step 2:** Implement `coerceIdentity`; run → PASS; full suite green. CHANGELOG. Commit (`fix: diagnostics identity coercion`), push, PR, checks green, update plan doc.

**Status: done.** PR [#158](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/158), branch `fix/diagnostics-identity-coercion`. RED confirmed both failure modes: the Guid-keyed identity case failed with `GuidSerializer cannot serialize a Guid when GuidRepresentation is Unspecified` (a second, pre-existing bug the string-coercion test surfaced — the filter boxed identity as `object`, forcing the driver through `ObjectSerializer` instead of the class map), and the string-identity case returned null. Fix: `coerceIdentity(object, Type)` mirroring Marten, plus making `readSagaAsync` generic over `TSaga, TId` so the filter resolves through the collection's class-map serializer. Full suite green on both TFMs (179/179 each), all CI checks green.

### Task F14: Bounded NuGet dependency ranges

- **Goal:** The nupkg refuses WolverineFx 7.x / MongoDB.Driver 4.x at **restore** time instead of degrading silently at runtime (the reflection bridges are non-throwing by design).
- **Scope:** `Directory.Packages.props`: `WolverineFx` → `[6.16.0,7.0.0)`; `MongoDB.Driver` → `[3.9.0,4.0.0)`. Leave `WolverineFx.ComplianceTests` (test-only, never packed) bare. Verify: `dotnet pack -c Release -p:UseWolverineSource=false` then inspect the produced nuspec for both bracketed ranges; full library build/test unaffected; check dependabot keeps working with ranges (its `Directory.Packages.props` support updates bracketed versions — note the outcome in the PR).
- **Expected output:** Nuspec shows `version="[6.16.0, 7.0.0)"`-style dependencies; suite green.
- **Files/areas likely to change:** `Directory.Packages.props`, `CHANGELOG.md`.
- **Dependencies:** none.
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Apply the ranges; `dotnet build` + full test suite green.
- [x] **Step 2:** Pack and inspect the nuspec (quote it in the PR body). CHANGELOG. Commit (`fix: bounded NuGet dependency ranges`), push, PR, checks green, update plan doc. **Done: [PR #160](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/160), all 7 checks green.**

### Task F15: Post-1.0.0 documentation truth sweep

- **Goal:** Every stale claim the review verified against source is corrected; CLAUDE.md stops describing a pre-1.0 beta.
- **Scope:** `CLAUDE.md`: package version (`1.0.0`+, or "see Directory.Build.props"); the versioning rule (align with CHANGELOG's actual post-1.0 policy); the T4.6 index-migration bullet (the "before 1.0" checkpoint lapsed — re-state it as a live post-1.0 decision and mirror it in `FOLLOWUPS.md`); "six system collections" → nine (list them; also fix the mirroring comment at `MongoDbMessageStore.NodeAgents.cs:180`). Cross-check every touched sentence against `main` (the review's conventions angle is the source list; re-verify, don't trust). No behavior changes.
- **Expected output:** Docs match code; a FOLLOWUPS entry for the index-migration decision.
- **Files/areas likely to change:** `CLAUDE.md`, `FOLLOWUPS.md`, `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.NodeAgents.cs` (comment only), `CHANGELOG.md` (docs note).
- **Dependencies:** none.
- **Blocking status:** **Can start immediately.**

- [x] **Step 1:** Fix the four verified drifts + the code comment; re-verify each corrected claim against source. Commit (`docs: post-1.0.0 truth sweep`), push, PR, checks green, update plan doc. — [PR #156](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/156), all checks green.

---

## Phase 5 — Cleanup (Tier 5, optional but recommended)

### Task F16: Deduplicate inbox/outbox definitions + AnyNode sentinel

- **Goal:** One definition per semantic write, so future fixes (like the KeepUntil TTL fix documented in CLAUDE.md) can't be applied to one of two copies.
- **Scope:** (1) `MarkIncomingEnvelopeAsHandledAsync(Envelope)` delegates to the list overload. (2) `RescheduleExistingEnvelopeForRetryAsync` shares one scheduling `UpdateDefinition` builder with `ScheduleExecutionAsync`. (3) `DiscardAndReassignOutgoingAsync` calls `DeleteOutgoingAsync(discards)` instead of inlining its body. (4) Replace bare `0` owner literals and the local `MongoConstants.AnyNode` duplication with a single spelling — keep `MongoConstants.AnyNode` (referencing `TransportConstants.AnyNode` in its comment) and use it everywhere (`Inbox.cs:103/115/173`, `Admin.cs:113-124`, `Inbox.cs:109`'s direct `TransportConstants` use). Behavior-preserving refactor; the existing suite is the oracle. Skip document-schema changes (`AgentAssignmentDocument.AgentUri` dup field → FOLLOWUPS entry instead).
- **Expected output:** No new tests; full suite + multinode green proves equivalence.
- **Files/areas likely to change:** `Internals/MongoDbMessageStore.Inbox.cs`, `Internals/MongoDbMessageStore.Outbox.cs`, `Internals/MongoDbMessageStore.Admin.cs`, `Internals/MongoConstants.cs`, `FOLLOWUPS.md`.
- **Dependencies:** **F8, F10** (same files — avoid conflicts).
- **Blocking status:** **Unblocked** — F8 ([#183](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/183)) and F10 ([#184](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/184)) are both merged.

- [x] **Step 1:** Apply refactors 1–4; full suite + multinode green on both TFMs. FOLLOWUPS entry for `AgentUri`. Commit (`chore: deduplicate inbox/outbox definitions + AnyNode sentinel`), push, PR, checks green, update plan doc. — Done via [#187](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/187).

### Task F17: Store efficiency sweep

- **Goal:** Remove the verified per-tick/per-item waste without changing semantics.
- **Scope:** (1) `ReplayDeadLettersAsync`: cap with `.Limit(_options.Durability.RecoveryBatchSize)` like every sibling recovery path; replace the per-letter store+delete pair with batch `StoreIncomingAsync(list)` + one `DeleteManyAsync(Filter.In(id))` — **note the F8 interaction:** batch store now throws on any duplicate and persists nothing, so replay must fall back to per-letter handling when the batch throws `DuplicateIncomingEnvelopeException` (preserving the documented idempotent-replay behavior). (2) Cache `NodeDocs`/`AssignmentDocs`/`RecordDocs`/`RestrictionDocs`/`Counters` in the constructor like `Incoming`/`Outgoing` already are. (3) `LoadAllNodesAsync`: `Task.WhenAll` the two finds + `ToLookup(a => a.NodeId)` join. (4) `PersistAgentRestrictionsAsync`: one `BulkWriteAsync` (the `AssignAgentsAsync` pattern). Behavior-preserving; suite is the oracle.
- **Expected output:** Full suite + multinode green; the replay dedup fallback covered by one focused test (replay a batch where one letter's envelope already exists in the inbox).
- **Files/areas likely to change:** `Internals/MongoDbMessageStore.Durability.cs`, `Internals/MongoDbMessageStore.NodeAgents.cs`, one test file, `CHANGELOG.md`.
- **Dependencies:** **F11** (shares `Durability.cs`); items 2–4 are independent of it.
- **Blocking status:** **Partially blocked by: F11** (start items 2–4 anytime after F8 merges; item 1 after F8 + F11).

- [x] **Step 1:** Items 2–4 (NodeAgents); suite green.
- [x] **Step 2:** Item 1 (replay batching + dedup fallback test, red-first for the fallback); suite + multinode green. Commit (`chore: store efficiency sweep`), push, PR, checks green, update plan doc.

**Status: done.** `MongoDbMessageStore.NodeAgents.cs`: `NodeDocs`/`AssignmentDocs`/`RecordDocs`/
`RestrictionDocs`/`Counters` are now constructor-cached fields (assigned in
`MongoDbMessageStore.cs`'s ctor) instead of expression-bodied properties re-resolving
`_database.GetCollection<T>` on every call; `LoadAllNodesAsync` fetches nodes and assignments
concurrently via `Task.WhenAll` and joins with `ToLookup(a => a.NodeId)`; `PersistAgentRestrictionsAsync`
issues one `BulkWriteAsync` mixing `DeleteOneModel`/`ReplaceOneModel`, mirroring `AssignAgentsAsync`.
`ReplayDeadLettersAsync` caps its `Find` with `.Limit(_options.Durability.RecoveryBatchSize)` and
replays the fetched batch via one `StoreIncomingAsync(list)` + one `DeadLetterDocs.DeleteManyAsync`;
on `DuplicateIncomingEnvelopeException` (the F8 all-or-nothing interaction) it falls back to the
original per-letter `StoreIncomingAsync`/`DeleteOneAsync` loop. Red-first verified: the new test
`dead_letter_replay.replay_falls_back_to_per_letter_when_batch_has_a_duplicate` fails with the batch
duplicate exception (confirmed on both net9.0/net10.0) when the fallback catch is disabled, and
passes once restored. Full suite (430 single-node facts) + multinode (36 facts) green on both TFMs;
zero edited pre-existing facts.

---

## Phase 6 — Demo & Verification

### Task F18: Demo non-`Id` identity-convention coverage

- **Goal:** Prove the Tier-1 fixes end-to-end through the **packaged** library (the demo consumes the nupkg), and leave a living reference for the identity conventions.
- **Scope:** Per F5's design: add the designed entity (e.g. `CustomerFeedback { Guid CustomerFeedbackId; … }`) with `[Entity]`-load + `Insert<T>` handlers beside the existing `OrderNote` handlers; safety-net tests in the `OrderNoteFlowTests.cs` style (insert → load-by-convention-id → assert the Mongo document's `_id` is the `CustomerFeedbackId` value). Do **not** modify existing demo types. Local dev: pack the library first (`dotnet pack src/Wolverine.MongoDB/... -p:UseWolverineSource=false`); CI's demo job uses the fresh `0.0.0-ci` nupkg.
- **Expected output:** Demo build + full demo integration suite green including the new tests.
- **Files/areas likely to change:** `demo/src/OrderDemo.Application/...` (new entity + handlers), `demo/tests/OrderDemo.IntegrationTests/...` (new test file), `demo/CLAUDE.md`/`demo/README.md` (brief mention).
- **Dependencies:** **F5, F6, F7** (merged, so the CI nupkg carries the fixes).
- **Blocking status:** **Blocked by: F5, F6, F7.**

- [ ] **Step 1:** Add the entity + handlers per F5; write the safety-net tests (direct Mongo `_id` assertion included).
- [ ] **Step 2:** Demo suite green against a fresh local pack. Commit (`demo: non-Id identity-convention entity + safety-net tests`), push, PR, checks green, update plan doc.

### Task F19: Full cross-feature regression

- **Goal:** Prove inbox, outbox, saga, entity, diagnostics, single-node, and multi-node all work together after every fix.
- **Scope:** On a verification branch off current `main` (after F6–F18 merged): full library suite (both TFMs); `--filter "Category=multinode"` **five consecutive** runs per TFM; `dotnet pack` (package-ref build) + nuspec range check (F14's outcome); demo build + full integration suite; confirm CI coverage (new tests are non-multinode xUnit → auto-included; the demo job exercises the nupkg). Add a CI change only if a gap is found.
- **Expected output:** A clean regression report in the PR (or a report-only close if no file changed); any gap fixed or filed.
- **Files/areas likely to change:** none expected (`.github/workflows/ci.yml` only on a found gap).
- **Dependencies:** **F6–F18 merged.**
- **Blocking status:** **Blocked by: F6–F18.**

- [x] **Step 1:** Run all suites + pack + demo; record results verbatim.
- [x] **Step 2:** Confirm CI coverage; fix or file gaps; report. Commit only if CI changed.

**Status: done (report-only close, no PR — no file changed).** Precondition check first found F12
unmerged (no PR existed despite the table saying otherwise) — it turned out already implemented on
a separate worktree branch and merged as [PR #196](https://github.com/TheCraftyMaker/wolverine-mongodb/pull/196)
(squash commit `fbcad16`) before the re-check completed. Branch `test/remediation-regression` cut
from `origin/main` at `fbcad16`. Results: single-node suite 218/218 green on net9.0 **and** net10.0;
multinode `--filter "Category=multinode"` 5 consecutive runs per TFM, 18/18 facts each (10/10 total,
2m29s–2m44s, no widened timeouts); `dotnet pack -p:UseWolverineSource=false` succeeds; demo build +
full integration suite 45/45 green, including F18's `CustomerFeedbackFlowTests`; CI coverage
confirmed adequate — the four `Category=multinode`-tagged files are the only ones with that trait,
so `dead_node_release.cs` and the extended `saga_store_diagnostics.cs` land in the
`Category!=multinode` step via the test project's default SDK glob (no explicit `<Compile>` list);
the `demo` job already downloads and exercises the same-commit nupkg. No `ci.yml` change needed.
**One red finding, filed not fixed (out of F19's verification-only scope):** the packed nuspec shows
plain pinned versions (`WolverineFx 6.21.0`, `MongoDB.Driver 3.10.0`), not F14's bracketed ranges
(`[6.16.0,7.0.0)` / `[3.9.0,4.0.0)`) — later dependabot bump PRs (#162, #165, #166, #167) overwrote
the whole `Version="..."` string instead of only the lower bound. See **F21**.

### Task F20: Final verification on `main` (+ release decision)

- **Goal:** Confirm the merged result is clean; decide the release.
- **Scope:** On `main` after F15 + F19 + F21 complete: full suite, multinode 5×, pack, demo suite, CI green, history review (one PR per task F1–F19). Then surface **OQ6** to the user: the fixes include user-visible bug fixes (a 1.0.1 patch is defensible) and new loud failures where silence reigned (arguably 1.1.0); recommend and let the user decide. If releasing, invoke the `release` agent per CLAUDE.md "Versioning & Release"; otherwise everything stays under `## [Unreleased]`.
- **Expected output:** A clean final report; release only on explicit user approval.
- **Files/areas likely to change:** none (release handled by the `release` agent if invoked).
- **Dependencies:** **F15, F19, F21 complete.**
- **Blocking status:** **Blocked by: F15, F19, F21.**

- [ ] **Step 1:** Run all verifications on `main`; report. File anything red — do not fix in this session.
- [ ] **Step 2 (gated):** Present the release recommendation (OQ6); proceed via the `release` agent only on approval.

### Task F21: Post-remediation documentation sweep

- **Goal:** Reconcile this plan doc and `FOLLOWUPS.md` with what F12/F19 actually found, so F20 runs against accurate docs.
- **Scope:** Docs-only. Add the missing PR link to F12's row/status note (`#196`); add a dated `FOLLOWUPS.md` entry for the F14 dependency-range regression F19 found (dependabot bumps clobbering the bracket syntax). R7's cross-reference and F19/F20's status/dependency updates are already done (part of scaffolding this task). No source, test, or `Directory.Packages.props` changes — restoring the ranges is separate, already-flagged follow-up work, not this task.
- **Expected output:** Plan doc and `FOLLOWUPS.md` match current reality; PR merged.
- **Files/areas likely to change:** `docs/superpowers/plans/2026-07-07-review-findings-remediation.md`, `FOLLOWUPS.md`.
- **Dependencies:** **F12, F19 complete.**
- **Blocking status:** **Can start immediately** (both dependencies satisfied).

- [ ] **Step 1:** Add `#196` to F12's row and status note; add the FOLLOWUPS.md entry.
- [ ] **Step 2:** Commit (`docs: post-remediation documentation sweep`), push, PR, checks green, update plan doc.

---

## Risks, Assumptions & Open Questions

| # | Risk / assumption | Mitigation |
|---|---|---|
| R1 | **Class-map registration timing.** `BsonClassMap.RegisterClassMap` throws if the type's map is already registered, and `LookupClassMap` freezes an auto-map — so `EnsureIdMember` must run before any other code touches the type's map. Frames construct at codegen (host build), normally first — but app code (or `LoadState`-style test helpers) touching the type earlier auto-freezes a map with the wrong id member. | The helper's conflict branch throws a precise, actionable error (add `[BsonId]`); F1 verifies the freeze semantics; F6's tests include a type pre-touched by the driver to prove the error message fires (not a silent wrong map). |
| R2 | **CLAUDE.md's "no global serializer mutation" stance vs LD1-B.** Registering per-type class maps is registry mutation, however scoped. | **Closed by F3.** Option B′ narrows the mutation to types that are otherwise *broken*: when the driver's own conventions already resolve the id member (every `Id`-keyed and every `[BsonId]` type), the helper registers nothing at all. Exact replacement wording for `CLAUDE.md:156`/`:159` plus two new decision bullets are specified verbatim in the design's §7; LD1-A remains the documented downscope. |
| R3 | **On-disk compatibility.** Changing identity mapping must not move any working consumer's data. | For `Id`-named members the helper is a no-op (driver already maps them); non-`Id` types never round-tripped (the bug), so no working data exists to migrate. F3 records this; F6/F7 keep every existing compliance fact byte-identical. |
| R4 | **In-transaction bulk-write failure shape (F8).** Inside a transaction the server fails fast; the driver may surface `MongoCommandException`/`MongoBulkWriteException` differently than the non-transactional path, and the dupe list may be partial. | F2 verifies the actual exception shape before F8 codes to it; the `DurableReceiver` contract only needs "nothing persisted + a duplicate-typed exception" — the F8 test asserts persistence-count, not dupe-list completeness. |
| R5 | **Two-tick release adds one recovery interval to dead-node rescue latency.** | Documented in F4/F11 and CLAUDE.md; the multinode suite (which exercises real rescue) must stay green 5× — if a fact times out, widen only the observation window with written justification. |
| R6 | **Soundness precondition drift.** Two-tick correctness leans on monotonic never-reused node numbers (T4.6) and registration-before-claim. A future "reuse freed slots" change would silently break it. | The soundness argument lives in the method comment **and** the CLAUDE.md bullet, cross-referencing the node-number decision so a future change trips over it. |
| R7 | **Version-range change vs dependabot/demo.** Bracketed ranges in `Directory.Packages.props` must not break dependabot PRs or the demo's separate solution. | F14 verifies a pack + restore locally and states the dependabot behavior in the PR; the demo pins `Wolverine.MongoDB 1.0.0` and has its own packages file — untouched. **Materialized post-F14:** dependabot bump PRs replaced the whole bracketed `Version` string with a plain pin instead of only bumping the lower bound; F19 caught it via the nuspec check. See the dated `FOLLOWUPS.md` entry (F21) and the separately flagged restoration task. |
| R8 | **F8/F17 interaction.** Batch-store becoming all-or-nothing changes `ReplayDeadLettersAsync`'s bulk-replay semantics (one already-present envelope would abort the whole batch). | F17 explicitly adds the per-letter fallback on `DuplicateIncomingEnvelopeException` and a red-first test for it; sequencing (F17 after F8) is enforced in the dependency graph. |
| R9 | **Multinode flakiness amplification.** F10/F11/F12 all touch code on the Balanced hot path. | Each carries the 5×-consecutive multinode bar; Fable/Opus on F10/F11; the escalation rule (stop and report, never weaken) applies verbatim. |
| R10 | **Post-1.0 semver.** Several fixes turn silent misbehavior into thrown exceptions (F6 codegen throw, F7 LD4 throw). | Each PR states its semver character; F20 aggregates into the release recommendation (OQ6). |
| **OQ1** | LD1 Option A vs B (fail-fast vs ensure-or-fail class map). | **RESOLVED in F3 → Option B′** (ensure-or-fail, *minimal-mutation*): Option B refined so the helper registers nothing for a type the driver already maps correctly, keeping every `Id`-keyed consumer byte-identical with the BSON registry untouched. Contract in `2026-07-07-identity-mapping-design.md` §3; LD1-A retained there as the documented downscope. |
| **OQ2** | LD4: throw vs route for `Delete<TSaga>`/`IStorageAction<TSaga>`. | **RESOLVED in F3 → throw.** `InvalidOperationException` at codegen, two exact message prefixes (design §6.1). Routing rejected: no `SagaChain` means no captured `oldVersion`, so it would swap a visible bug for silent OCC corruption. |
| **OQ3** | LD2 Option A vs B (transaction vs compensating delete). | **Recommended A**; recorded in **F4**. |
| **OQ4** | LD3 two-tick vs per-number recheck. | **Recommended two-tick**; recorded in **F4**. |
| **OQ5** | Upstream-contribute the non-`Id` identity test matrix to `Wolverine.ComplianceTests`? | Defer; note as a FOLLOWUPS item in F6's PR (the tests are deliberately written compliance-style to ease it). |
| **OQ6** | Release 1.0.1 vs 1.1.0 vs stay `[Unreleased]` after F19. | Decided by the user at **F20**; default stay `[Unreleased]`. |

## Acceptance Criteria

- Every confirmed finding has a **red-first** test that reproduced the original failure mode before the fix and passes after; no finding is closed by inspection alone.
- Saga compliance (String/Guid/Int/Long), `storage_action_compliance`, `LeadershipElectionCompliance`, `saga_atomicity`, and every other existing test pass unchanged — **no skips, no retries-as-bandaid, no weakened assertions**.
- New identity-convention tests prove: `{Name-minus-Saga}Id` and `[SagaIdentity]` sagas, and `{TypeName}Id` entities (incl. the both-members shape), round-trip with the identity value as the native-typed `_id`; unresolvable identities and saga-typed storage actions fail loudly at codegen with actionable messages.
- Batch inbox: a duplicate-containing batch persists nothing; `EditAndReplayAsync` handles empty-body letters; `IdAndDestination` claims are destination-scoped; the dead-node release is two-tick-confirmed with its soundness argument in-code; `StopAsync` awaits its loops.
- Diagnostics coerce string identities for Guid/int/long-keyed sagas (Marten parity).
- The packed nuspec declares `[6.16.0,7.0.0)` / `[3.9.0,4.0.0)` dependency ranges.
- Full library suite (both TFMs), `Category=multinode` 5× consecutive per TFM, `dotnet pack`, demo suite, and CI are green; CLAUDE.md/README/FOLLOWUPS/CHANGELOG match the code.
- On-disk document shape unchanged for every previously-working consumer; public API surface unchanged (new behavior is fixes + loud failures, no new required API).

---

## Dependency Map

**Immediate parallel tasks (start the moment the plan PR merges) — seven lanes:**
- **F1** Identity-mapping discovery
- **F2** Durability-contracts discovery
- **F5** Test inventory + demo design *(finalize assertions after F3/F4)*
- **F9** DLQ edit-replay guard *(independent small fix)*
- **F13** Diagnostics coercion *(independent small fix)*
- **F14** NuGet ranges *(independent small fix)*
- **F15** Docs truth sweep *(independent)*

**Blocked by discovery/design:**
- **F3** ← F1 *(identity design gate)* · **F4** ← F2 *(durability design gate)* — F3 ∥ F4
- **F6** ← F3 *(head of the identity critical path)*
- **F8, F10, F11** ← F4 *(fan out in parallel — disjoint primary files)* · **F12** ← F4 (partially)

**Blocked by implementation:**
- **F7** ← F3, F6 *(shares frame files + consumes `MongoIdentityMapping`)*
- **F16** ← F8, F10 *(same store files)* · **F17** ← F11 (partially; item 1 also ← F8)
- **F18** ← F5, F6, F7 *(demo consumes the packed fixes)*

**Final verification tasks:**
- **F19** ← F6–F18 merged (done — report-only close) · **F21** ← F12, F19 complete · **F20** ← F15, F19, F21 complete

```
plan PR ──► F1 ──► F3 ──► F6 ──► F7 ──► F18 ─────────────┐
        ──► F2 ──► F4 ──► F8 ─┬─► F16                    │
        │                 F10 ┘                          ├─► F19 ─► F20
        │                 F11 ──► F17                    │
        │                 F12                            │
        ──► F5 ───────────────────────────► (F18)        │
        ──► F9, F13, F14 ────────────────────────────────┘
        ──► F15 ────────────────────────────────► (F20)
```

## Critical Path Analysis

**Minimum sequence to close the two data-corruption findings (the review's #1 and #5):**
`plan PR → F1 → F3 → F6 → F7`. This is the irreducible core — everything else hardens durability, contracts, packaging, or docs. If only one lane can run, run this one.

**Full-scope critical path:** `plan PR → F1 → F3 → F6 → F7 → F18 → F19 → F20` (7 sequential merges). The durability lane `F2 → F4 → {F8,F10,F11,F12} → F16/F17` is one merge shorter and runs **entirely in parallel** with the identity lane — it converges at F19. The small-fix lane (F9/F13/F14/F15) is depth 1 and never gates anything except F20 (F15 only).

**Tasks that can be delayed without affecting the critical path:**
- **F16/F17** (cleanup) are droppable-to-FOLLOWUPS entirely without touching any correctness outcome.
- **F12** is off the critical path (its residual window is upstream-owned anyway).
- **F5** can lag until just before F18.
- **F9/F13/F14/F15** can land any time before F19/F20.

**Opportunities to compress wall-clock via parallel execution:**
- Day one: **seven concurrent sessions** (F1, F2, F5, F9, F13, F14, F15) — four of them merge outright.
- The two design gates (F3, F4) run concurrently; their fan-outs (F6 ∥ {F8, F10, F11, F12}) give **up to five concurrent implementation sessions** in wave 3.
- The identity lane's only true serialization is F6 → F7 (shared files by design — do **not** parallelize them).
- With full fan-out the 20 tasks collapse to about **6 sequential waves**: (F1/F2/F5 + small fixes) → (F3 ∥ F4) → (F6 ∥ F8/F10/F11/F12) → (F7 ∥ F16/F17) → F18 → F19/F20 — dominated by the identity lane, so staff F1/F3/F6/F7 with the strongest (Fable 5) sessions first.
