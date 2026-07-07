# Review Findings Remediation — Per-Task Session Prompts

One fresh Claude Code session per task, in the order below. Start each session **in the repository root** (not a stale worktree), set the session model first (`/model` — the recommended model is listed per task), then paste the prompt.

Conventions baked into every prompt (identical to `2026-06-18-saga-task-prompts.md`):
- The session verifies its **precondition** (prerequisite PRs merged to `main`) before doing anything.
- Execution goes through `superpowers:executing-plans` against the referenced plan task **only** — no scope creep.
- Branch + PR mechanics come from the plan's "Git & PR Workflow" section (worktree per task; `gh pr view --json statusCheckRollup` to verify checks, not the rtk `--watch` tail).
- **Do not invent Wolverine or MongoDB.Driver APIs.** The plan's "Verified API Facts" section is the source of truth; confirm each against the pinned `external/wolverine` submodule (V6.16.0) or the driver with a one-line grep rather than guessing. If an API differs from the plan, **stop and report** — do not improvise a substitute.
- If a plan assumption doesn't hold (an API missing, a test that can't fail/pass as predicted), the session stops and reports instead of improvising. Two non-obvious verification failures → stop, report, re-run the task on Fable 5.
- Fix tasks are **red-first**: the new test must reproduce the original review finding's failure mode BEFORE the fix (run it, quote the failure), then pass after. A fix PR whose test never went red does not close its finding.
- When finishing, update the plan doc in the same PR (tick step checkboxes + the status-table row).

**Precondition for everything below:** `main` is at commit `cec7d93` ("chore: upgrade WolverineFx to 6.16.0 (#152)") or later, and the PR containing this plan + prompts file (`2026-07-07-review-findings-remediation*.md`) is merged to `main`.

**The plan:** `docs/superpowers/plans/2026-07-07-review-findings-remediation.md`.

---

## Cohort 1 — Discovery, design inputs & independent small fixes (SEVEN PARALLEL SESSIONS)

### F1 — identity-mapping discovery *(model: Sonnet; independent — can start immediately)*

```
Execute Task F1 ("Identity-mapping discovery") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Read-only analysis; no library code changes. Branch docs/identity-mapping-discovery from origin/main. Pin, with file:line: (1) SagaChain.DetermineSagaIdMember's full precedence and ValidSagaIdTypes; where core calls DetermineSagaIdType vs PullSagaIdFromMessageFrame; EntityAttribute's id resolution (EntityAttribute.cs:153); how Marten/EF Core/lightweight-SQL reconcile Wolverine identity with their storage; confirm NO upstream Saga guard exists on Delete<T>/IStorageAction<T>/Storage.TryApply. (2) MongoDB.Driver 3.9 BsonClassMap semantics: IsClassMapRegistered, RegisterClassMap idempotency/thread-safety, LookupClassMap auto-map+freeze, insert behavior for a type with NO mapped id member, [BsonId] interaction. (3) Every raw-"_id" filter site and IdMemberMap site in SagaFrames.cs / EntityFrames.cs / MongoDbSagaStoreDiagnostics.cs; confirm zero RegisterClassMap calls exist in src/Wolverine.MongoDB. (4) Confirm no upstream saga/storage-action compliance fact uses a non-Id identity member. Produce docs/superpowers/plans/2026-07-07-identity-mapping-discovery.md confirming or correcting the plan's Verified API Facts, plus the explicit list of code sites that must change. Commit, push, open the PR titled "docs: remediation — identity-mapping discovery (Wolverine conventions vs driver class map)". Watch checks until green.

Do not invent APIs — quote the submodule and driver. Stay strictly within Task F1's scope.
```

### F2 — durability-contracts discovery *(model: Sonnet; independent — can start immediately)*

```
Execute Task F2 ("Inbox/recovery/shutdown contract discovery") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Read-only analysis; no library code changes. Branch docs/durability-contracts-discovery from origin/main. Pin, with file:line: (1) DurableReceiver's batch-store catch path + per-envelope retry + the contract comment (DurableReceiver.cs:631-641, :493-530), and the RDBMS batch transaction with its rollback rationale (MessageDatabase.Incoming.cs:183-200). (2) MessageIdentity modes (DurabilitySettings.cs:103-107) and every local use of _inboxIdentity/EnvelopeId in claims and re-reads (MongoDbMessageStore.cs:46-48, :131-135; Durability.cs:154-158). (3) The RDBMS ReleaseOrphanedMessagesOperation single-statement semantics (:32,:34); the local release's tick ordering; PROVE (trace NodeAgentController startup) that a node registers before its durability agent issues any claim. (4) Shutdown ordering: WolverineRuntime.HostService.StopAsync release-vs-teardown (:380,:402); what the RDBMS DurabilityAgent.StopAsync awaits (:140-166); SafeDispose semantics on a running Task. (5) The MongoDB in-transaction failure mode for InsertMany(IsOrdered:false) — fail-fast on first write error, and the exact exception type the driver surfaces. Produce docs/superpowers/plans/2026-07-07-durability-contracts-discovery.md with explicit confirmation or correction of the plan's LD2/LD3 caveats. Commit, push, open the PR titled "docs: remediation — inbox/recovery/shutdown contract discovery". Watch checks until green.

Do not invent APIs — quote the submodule and driver. Stay strictly within Task F2's scope.
```

### F5 — test inventory + demo impact design *(model: Sonnet; independent to start; finalize assertions after F3/F4 merge)*

```
Execute Task F5 ("Test inventory + demo impact design") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Design-only; no library code changes. Branch docs/remediation-test-inventory from origin/main. (1) Verify which upstream compliance suites exist and what identity shapes they cover: the four saga compliance spec families, StorageActionCompliance (confirm its only entity is Todo { string Id } at StorageActionCompliance.cs:272-274), LeadershipElectionCompliance — and confirm the conclusion that NO upstream spec covers any review finding, so all new tests are custom on AppFixture (structural models: saga_atomicity.cs, storage_action_compliance.cs). (2) Build the finding→test mapping table: for each of the plan's new test files (saga_identity_conventions, entity_identity_conventions, inbox_batch_atomicity, dead_letter_edit_replay, incoming_claims_id_and_destination, dead_node_release, the diagnostics extension) specify test names, trigger, and assertion. (3) Design the F18 demo extension: a new entity using the {TypeName}Id convention (e.g. CustomerFeedback { Guid CustomerFeedbackId }) with [Entity]-load + Insert<T> handlers and OrderNoteFlowTests-style safety-net tests; existing demo types unchanged. (4) List which custom tests are upstream-contribution candidates. Produce docs/superpowers/plans/2026-07-07-remediation-test-inventory.md. Commit, push, open the PR titled "docs: remediation — test inventory + demo impact design". Watch checks until green.

If F3/F4 have merged, align assertion details with their decided exception contracts. Stay strictly within Task F5's scope.
```

### F9 — DLQ edit-replay empty-body guard *(model: Sonnet; independent — can start immediately)*

```
Execute Task F9 ("EditAndReplayAsync tolerates body-less poison letters") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Branch fix/dlq-edit-replay-empty-body from origin/main. RED FIRST: write the failing test (src/Wolverine.MongoDB.Tests/dead_letter_edit_replay.cs, [Collection("mongodb")] on AppFixture) — store a dead letter created via DeadLetterMessage.ForUnserializableEnvelope (Body = [], see DeadLetterMessage.cs:45 and the call site MongoDbMessageStore.Inbox.cs:136), call EditAndReplayAsync with a valid new body, and run it to prove today's EndOfStreamException (EnvelopeSerializer.Deserialize does br.ReadInt64() on the empty stream). THEN fix MongoDbMessageStore.DeadLetters.cs:91 by routing envelope reconstruction through doc.ToEnvelope() — which already carries the Body is { Length: > 0 } guard (DeadLetterMessage.cs:70-72) — before applying the edited body. Add a normal-bodied-letter regression test proving unchanged behavior. Run both tests green + the full library suite on both TFMs. Add the CHANGELOG "### Fixed" entry. Commit, push, open the PR titled "fix: EditAndReplayAsync tolerates body-less poison dead letters". Watch checks until green.

Stay strictly within Task F9's scope.
```

### F13 — diagnostics identity coercion *(model: Sonnet; independent — can start immediately)*

```
Execute Task F13 ("Diagnostics identity coercion") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Branch fix/diagnostics-identity-coercion from origin/main. RED FIRST: extend src/Wolverine.MongoDB.Tests/saga_store_diagnostics.cs with a Guid-keyed diagnostics saga and assert ReadSagaAsync finds it when handed the identity BOTH as a Guid and as its ToString() string — run to prove the string case returns null today (MongoDbSagaStoreDiagnostics.cs:101-104 filters Eq("_id", identity) as-is). THEN add a coerceIdentity(object identity, Type idType) helper mirroring Marten's (external/wolverine/src/Persistence/Wolverine.Marten/MartenSagaStoreDiagnostics.cs:147-168 — pass-through on type match; Guid.Parse/int.Parse/long.Parse for string input; unparseable → not-found, never throw): the ISagaStoreDiagnostics contract explicitly says "the implementation is expected to coerce as needed" (ISagaStoreDiagnostics.cs:42-46). Add int/long variants analogously. Fix the class doc at :23 ("the identity is used as-is"). While in the file you may drop the vestigial Type sagaType parameter on the private generic helpers (always typeof(TSaga)) — nothing else. Run the extended tests + full suite green on both TFMs. CHANGELOG entry. Commit, push, open the PR titled "fix: ISagaStoreDiagnostics identity coercion (Marten parity)". Watch checks until green.

Stay strictly within Task F13's scope.
```

### F14 — bounded NuGet dependency ranges *(model: Sonnet; independent — can start immediately)*

```
Execute Task F14 ("Bounded NuGet dependency ranges") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Branch fix/nuget-dependency-ranges from origin/main. In Directory.Packages.props change WolverineFx to Version="[6.16.0,7.0.0)" and MongoDB.Driver to Version="[3.9.0,4.0.0)"; leave WolverineFx.ComplianceTests (test-only, never packed) bare. Rationale (quote in the PR): the produced nuspec today declares open-ended ">= x.y.z" dependencies, so a consumer restoring WolverineFx 7.x would break at RUNTIME — and the MongoDbSagaStoreDiagnostics reflection bridges are non-throwing by design, so they'd degrade silently. Verify: dotnet build + full library test suite green (both TFMs); then dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false and inspect the produced .nuspec — quote the bracketed dependency ranges in the PR body. Note dependabot's behavior with bracketed ranges in Directory.Packages.props in the PR. CHANGELOG entry. Commit, push, open the PR titled "fix: bounded NuGet dependency ranges for WolverineFx + MongoDB.Driver". Watch checks until green.

Stay strictly within Task F14's scope.
```

### F15 — docs truth sweep *(model: Sonnet; independent — can start immediately)*

```
Execute Task F15 ("Post-1.0.0 documentation truth sweep") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Branch docs/truth-sweep from origin/main. Fix the four verified drifts, RE-VERIFYING each against source before writing (do not trust the review or this prompt): (1) CLAUDE.md says "currently 0.1.0-beta.7" but Directory.Build.props:16 is 1.0.0 and CHANGELOG.md records [1.0.0] - 2026-07-06 as released. (2) CLAUDE.md's versioning rule "0.1.x ↔ WolverineFx 6.x" is stale — align with the actual post-1.0 policy in CHANGELOG.md. (3) The T4.6 "pre-1.0 index migration" bullet's premise lapsed on 2026-07-06 — restate it as a live post-1.0 decision and mirror it in FOLLOWUPS.md. (4) CLAUDE.md says IMessageStoreAdmin.ClearAllAsync clears "all six system collections" but MongoDbMessageStore.Admin.cs:69-77 deletes from NINE (incoming, outgoing, dead_letters, nodes, node_assignments, node_records, agent_restrictions, counters, locks) — fix the count/list in CLAUDE.md AND the mirroring comment at MongoDbMessageStore.NodeAgents.cs:180 (comment-only code change). No behavior changes anywhere. Commit, push, open the PR titled "docs: post-1.0.0 truth sweep (version, versioning rule, collection counts)". Watch checks until green.

Stay strictly within Task F15's scope.
```

---

## Cohort 2 — Design gates (TWO PARALLEL SESSIONS, after their discovery PRs merge)

### F3 — identity-mapping design (DESIGN GATE) *(model: Fable 5 / Opus; only after the F1 PR is merged)*

```
Execute Task F3 ("Identity-mapping design") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify the F1 PR is merged to main (docs/superpowers/plans/2026-07-07-identity-mapping-discovery.md exists on main); read it and follow its verified facts. Then: branch docs/identity-mapping-design from origin/main. Resolve the plan's LD1 and LD4 into binding contracts: (1) LD1 Option A (fail-fast + [BsonId]) vs Option B (ensure-or-fail class map — recommended); specify the exact MongoIdentityMapping.EnsureIdMember(Type, MemberInfo) contract (fast-path no-op when the driver already maps the member; AutoMap+MapIdMember registration when unregistered; precise thrown error when a conflicting map is frozen), the invocation points (every saga/entity frame constructor — codegen time, cached per type), and the thread-safety story per F1's verified driver semantics. (2) The exact CLAUDE.md wording reconciling this with the "no process-global serializer mutation" stance. (3) UpdateSagaFrame's silent `?? "Id"` fallback (SagaFrames.cs:227-229) becomes the same throwing resolution DetermineSagaIdType uses. (4) LD4: throw vs route for Delete<TSaga>/IStorageAction<TSaga> (recommended throw; specify exception type + message text). (5) The on-disk compatibility statement (Id-keyed documents byte-identical; non-Id types never worked, so no migration owed). (6) The identity test matrix. Produce docs/superpowers/plans/2026-07-07-identity-mapping-design.md. Commit, push, open the PR titled "docs: remediation — identity-mapping design (DESIGN GATE, LD1+LD4)". Watch checks until green.

This doc gates F6 and F7 — be precise about the helper and exception contracts. Stay strictly within Task F3's scope.
```

### F4 — durability & coordination design (DESIGN GATE) *(model: Fable 5 / Opus; only after the F2 PR is merged)*

```
Execute Task F4 ("Durability & coordination design") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify the F2 PR is merged to main (docs/superpowers/plans/2026-07-07-durability-contracts-discovery.md exists on main); read it and follow its verified facts. Then: branch docs/durability-coordination-design from origin/main. Resolve LD2 and LD3 plus the F10/F12 semantics into binding contracts: (1) LD2 — batch StoreIncomingAsync via session/transaction (recommended) vs compensating delete, incl. the in-transaction fail-fast/partial-dupe-list caveat F2 verified and the preserved DuplicateIncomingEnvelopeException contract. (2) LD3 — the two-tick-confirmed dead-node release: exact algorithm (distinct-owners aggregation, deadNow ∩ previousTickDead, per-store tick state), the full soundness argument (registration-before-claim, proven in F2, + monotonic never-reused node numbers per the documented T4.6 decision), the one-recovery-interval latency cost, and the corrected wording for the method comment + CLAUDE.md bullet. Record and reject the alternatives (transaction wrap — snapshot isolation does not conflict with a node-doc insert, so it does NOT fix the race; per-number recheck — shrinks but keeps the window). (3) F10 — ReassignIncomingAsync claims by document _id (Filter.In(x => x.Id, envelopes.Select(InboxIdentity))), the orphan-recovery re-read keys the same _id set; byte-equivalent in IdOnly mode; no IMessageStore signature changes. (4) F12 — StopAsync awaits both loop tasks with a bounded timeout (e.g. 5s), swallows OperationCanceledException/TimeoutException with a log, disposes _cancellation and _combined; the residual upstream release-before-teardown window (HostService.cs:380/:402) is documented as out of library control. Produce docs/superpowers/plans/2026-07-07-durability-coordination-design.md. Commit, push, open the PR titled "docs: remediation — durability & coordination design (DESIGN GATE, LD2+LD3)". Watch checks until green.

This doc gates F8, F10, F11, F12. Stay strictly within Task F4's scope.
```

---

## Cohort 3 — Implementation fan-out (after the design gates; F6 alone gates the identity lane, F8/F10/F11/F12 run in PARALLEL on the durability lane)

### F6 — saga identity mapping *(model: Fable 5 / Opus; only after the F3 PR is merged — HEAD OF THE IDENTITY CRITICAL PATH)*

```
Execute Task F6 ("Saga identity conventions map to _id") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify the F3 design PR is merged to main; read docs/superpowers/plans/2026-07-07-identity-mapping-design.md and implement ITS decisions (the plan's snippet is the default shape; F3 overrides it where they differ). Then: branch fix/saga-identity-mapping from origin/main.

RED FIRST: write src/Wolverine.MongoDB.Tests/saga_identity_conventions.cs — a saga keyed by the {Name-minus-Saga}Id convention (e.g. ShipmentSaga { Guid ShipmentId }) and a saga keyed by a [SagaIdentity]-attributed member, each driven start→update→complete through a real IHost with real generated frames; assert via DIRECT Mongo reads that the document _id is the identity value in its native BSON type and that update/complete find the same document. Run them and QUOTE the current failure (loads return null / duplicate saga documents accumulate — the review's exact failure mode; SagaFrames.cs filters raw "_id" at :39/:84/:115 while no class map maps the convention member).

THEN implement: new Internals/MongoIdentityMapping.cs (ensure-or-fail per F3: no-op when the driver already maps the member; AutoMap+MapIdMember when unregistered; precise error when a conflicting map is frozen; ConcurrentDictionary-cached, codegen-time only); wire it into ALL FOUR saga frame constructors; replace UpdateSagaFrame's silent `?? "Id"` fallback (SagaFrames.cs:227-229) with the throwing resolution. Include a test proving the conflict branch fires (a type whose map was frozen with a different id member gets the actionable error, not a silent wrong map). Dump the generated handler source for one convention saga (repo convention: force compile via HandlerGraph, read HandlerChain.SourceCode reflectively) and confirm the emitted operations are unchanged in shape.

Regression bar: ALL existing saga compliance (String/Guid/Int/Long), saga_atomicity, OCC, diagnostics, and the full library suite green on net9.0 + net10.0 with ZERO changed facts — Id-keyed documents must be byte-identical on disk. CHANGELOG "### Fixed" entry. Commit, push, open the PR titled "fix: saga identity conventions map to _id (+ fail-fast id resolution)". Watch checks until green.

If BsonClassMap semantics differ from F1's verified facts, stop and report — do not improvise a different mapping mechanism. Stay strictly within Task F6's scope.
```

### F8 — batch inbox atomicity *(model: Fable 5 / Opus; only after the F4 PR is merged; parallel with F10/F11/F12)*

```
Execute Task F8 ("All-or-nothing batch StoreIncomingAsync") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify the F4 design PR is merged to main; read docs/superpowers/plans/2026-07-07-durability-coordination-design.md and implement its LD2 decision. Then: branch fix/batch-inbox-atomicity from origin/main.

RED FIRST: write src/Wolverine.MongoDB.Tests/inbox_batch_atomicity.cs — pre-store one envelope, then batch-StoreIncomingAsync it plus 4 fresh envelopes; assert DuplicateIncomingEnvelopeException AND (the load-bearing assertion) that NONE of the 4 fresh envelopes persisted (direct Mongo count by their ids). Run and quote today's failure: the 4 fresh docs persist (Inbox.cs:28 unordered InsertMany commits non-duplicates before the catch throws). This is the DurableReceiver stranding contract: its per-envelope retry completes duplicates WITHOUT enqueuing (DurableReceiver.cs:493-530), so partially-persisted fresh envelopes are stranded owned by a live node.

THEN implement per F4 (default: session/transaction wrap on _database.Client, abort on failure, classify duplicate vs other write errors, rethrow non-duplicates). IMPORTANT: code to the ACTUAL in-transaction exception shape F2/F4 verified (fail-fast on first error; the dupe list may be partial — the test must assert persistence-count, NOT dupe-list completeness). Keep the single-envelope StoreIncomingAsync untouched. Add the all-fresh-batch-persists-all test.

Regression bar: full library suite + --filter "Category=multinode" green on both TFMs (the durable receiver path runs everywhere). CHANGELOG entry. Commit, push, open the PR titled "fix: all-or-nothing batch StoreIncomingAsync on duplicate". Watch checks until green.

Stay strictly within Task F8's scope.
```

### F10 — destination-scoped incoming claims *(model: Fable 5 / Opus; only after the F4 PR is merged; parallel with F8/F11/F12)*

```
Execute Task F10 ("Destination-scoped incoming claims") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify the F4 design PR is merged to main; read its F10 contract. Then: branch fix/destination-scoped-claims from origin/main.

RED FIRST: write src/Wolverine.MongoDB.Tests/incoming_claims_id_and_destination.cs — build a store whose DurabilitySettings uses MessageIdentity.IdAndDestination (so _inboxIdentity makes _id = "{envelopeId}|{destination}", MongoDbMessageStore.cs:46-48), store two documents sharing ONE envelope Guid at two destinations, both OwnerId = 0; call ReassignIncomingAsync for the destination-1 envelope only; assert destination-2's document still has OwnerId == 0. Run and quote today's failure: the claim filters In(EnvelopeId, ids) unscoped (MongoDbMessageStore.cs:131-135), so destination-2's doc is claimed too (stranded: claimed but never enqueued for its listener).

THEN implement: ReassignIncomingAsync claims by document _id (Filter.In(x => x.Id, envelopes.Select(InboxIdentity)) && OwnerId == AnyNode); RecoverOrphanedIncomingAsync's post-claim re-read (Durability.cs:154-158) keys the same _id set (&& OwnerId == nodeNumber) and maps winning _ids back to envelopes. No IMessageStore signature changes. Verify by inspection (state it in the PR) that in IdOnly mode InboxIdentity(e) == e.Id.ToString(), so behavior is byte-equivalent to today — the RDBMS provider scopes the same way (id + received_at, MessageDatabase.Incoming.cs:27-30).

Regression bar: full suite + --filter "Category=multinode" green on both TFMs (recovery claims run in every Balanced test). CHANGELOG entry. Commit, push, open the PR titled "fix: destination-scoped incoming claims (IdAndDestination)". Watch checks until green.

Stay strictly within Task F10's scope.
```

### F11 — dead-node release race *(model: Fable 5 / Opus; only after the F4 PR is merged; parallel with F8/F10/F12)*

```
Execute Task F11 ("Two-tick-confirmed dead-node ownership release") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify the F4 design PR is merged to main; read its LD3 contract (algorithm + soundness argument). Then: branch fix/dead-node-release-race from origin/main.

RED FIRST: write src/Wolverine.MongoDB.Tests/dead_node_release.cs driving ReleaseDeadNodeOwnershipAsync directly (two calls = two ticks; fully deterministic, NO sleeps or real races): (a) an owner number with no node document must NOT be released on the first observing tick and MUST be on the second; (b) an owner whose node document appears between the ticks is NOT released; (c) OwnerId == 0 and live owners are never touched. Run (a) and quote today's failure: released on the FIRST tick (Durability.cs:185-193 Nin-releases immediately off a stale snapshot — the method comment's safety claim at :172-173 is the falsified invariant; the RDBMS mirror is a single atomic not-in-subselect statement, ReleaseOrphanedMessagesOperation.cs:32,34).

THEN implement the two-tick algorithm per F4/the plan snippet (distinct-owners from incoming+outgoing; deadNow = owned − live − {0}; release only deadNow ∩ previousTickDead; store deadNow per-store). REWRITE the method comment with the soundness argument (a node registers before its first claim + node numbers are monotonic/never-reused ⇒ a number in the previous tick's dead set cannot belong to a node registered since) and update the CLAUDE.md dead-node bullet, cross-referencing the T4.6 node-number-monotonicity decision.

Regression bar: full suite green; --filter "Category=multinode" FIVE consecutive runs per TFM (net9.0 + net10.0) — release latency grew by one recovery interval; if a multinode fact times out, widen only the OBSERVATION window with written justification, never weaken an assertion; if exactly-once fails, that is a real bug — write it up and stop. CHANGELOG entry. Commit, push, open the PR titled "fix: two-tick-confirmed dead-node ownership release". Watch checks until green.

This is concurrency-correctness work — reason about the interleaving before adjusting anything. Stay strictly within Task F11's scope.
```

### F12 — durability agent shutdown *(model: Sonnet; only after the F4 PR is merged; parallel with F8/F10/F11)*

```
Execute Task F12 ("Durability agent shutdown awaits its loops") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify the F4 design PR is merged to main; read its F12 contract. Then: branch fix/durability-agent-shutdown from origin/main.

RED FIRST: write the failing test (extend the existing durability-agent coverage or add durability_agent_shutdown.cs): start the agent, call StopAsync, assert both loop tasks are completed when it returns, and that a second StopAsync is idempotent. Run and quote today's failure: StopAsync cancels and returns immediately (MongoDbDurabilityAgent.cs:130-140 — SafeDispose on a running Task is a swallowed no-op, so nothing is awaited; _cancellation/_combined are never disposed).

THEN implement per F4: cancel; await Task.WhenAll(_recoveryTask ?? Task.CompletedTask, _scheduledJob ?? Task.CompletedTask) with a bounded timeout (~5s) in a try/catch swallowing OperationCanceledException/TimeoutException (log the timeout case); dispose _cancellation and _combined; then set Status = Stopped. Add a comment documenting the residual upstream ordering window (HostService.cs releases ownership at :380 before teardownAgentsAsync at :402 — out of this library's control; the RDBMS sibling at least awaits Timer.DisposeAsync, Wolverine.RDBMS/DurabilityAgent.cs:140-166).

Regression bar: full suite + --filter "Category=multinode" green on both TFMs (shutdown runs in every Balanced test teardown — a hang here shows up as suite timeouts, so watch durations). CHANGELOG entry. Commit, push, open the PR titled "fix: durability agent StopAsync awaits its loops + disposes CTSes". Watch checks until green.

Stay strictly within Task F12's scope.
```

### F7 — entity identity + saga guards *(model: Fable 5 / Opus; only after BOTH the F3 and F6 PRs are merged — do NOT run in parallel with F6, they share frame files)*

```
Execute Task F7 ("Entity identity agreement + saga guards on storage-action paths") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify the F3 AND F6 PRs are merged to main (F7 consumes Internals/MongoIdentityMapping.cs from F6 and follows F3's LD4 decision). Then: branch fix/entity-identity-and-saga-guards from origin/main.

RED FIRST: write src/Wolverine.MongoDB.Tests/entity_identity_conventions.cs — (a) an entity with ONLY a {TypeName}Id member round-trips through [Entity] load + Insert<T> + Delete<T>; (b) the review's poisoned shape (an entity with BOTH {TypeName}Id and Id members) reads and writes the SAME key; (c) a plain (non-saga) handler returning Storage.Delete(someSaga) fails at CODEGEN with F3's LD4 message. Run and quote today's failures: (a)/(b) — LoadEntityFrame filters _id with the Wolverine-resolved member's value (EntityFrames.cs:41,60) while UpsertAsync/DeleteAsync key off BsonClassMap.IdMemberMap (EntityFrames.cs:133-136), so writes land under one member and reads probe another (silent null); (c) — DetermineDeleteFrame(Variable)/DetermineStorageActionFrame have no Saga branch (MongoDbPersistenceFrameProvider.cs:136-137,147-156), so the delete silently no-ops against the un-prefixed entity collection while the saga lives in wolverine_saga_*.

THEN implement: wire MongoIdentityMapping.EnsureIdMember into LoadEntityFrame + MongoUpsertEntityFrame + MongoDeleteEntityByVariableFrame constructors (the runtime IdOf then agrees by construction — keep it); add the LD4 throwing guards (CanBeCastTo<Saga>()) to the single-variable DetermineDeleteFrame and DetermineStorageActionFrame.

Regression bar: storage_action_compliance (the upstream suite) + full library suite green on both TFMs with zero changed facts. CHANGELOG entry. Commit, push, open the PR titled "fix: entity load/write identity agreement + saga guard on Delete/IStorageAction". Watch checks until green.

Stay strictly within Task F7's scope.
```

---

## Cohort 4 — Cleanup (after their file-sharing predecessors)

### F16 — dedup cleanup *(model: Sonnet; only after the F8 and F10 PRs are merged)*

```
Execute Task F16 ("Deduplicate inbox/outbox definitions + AnyNode sentinel") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify the F8 and F10 PRs are merged to main (same files). Then: branch chore/store-dedup-cleanup from origin/main. Behavior-preserving refactors ONLY — the existing suite is the oracle, no new tests: (1) MarkIncomingEnvelopeAsHandledAsync(Envelope) delegates to the list overload. (2) RescheduleExistingEnvelopeForRetryAsync and ScheduleExecutionAsync share ONE scheduling UpdateDefinition builder. (3) DiscardAndReassignOutgoingAsync calls DeleteOutgoingAsync(discards) instead of inlining its filter. (4) One AnyNode spelling everywhere: keep MongoConstants.AnyNode (comment referencing TransportConstants.AnyNode), replacing the bare 0 literals (Inbox.cs, Admin.cs) and the direct TransportConstants use (Inbox.cs). Do NOT change any document schema — add a FOLLOWUPS.md entry for the AgentAssignmentDocument.AgentUri/Id duplication instead. Full suite + --filter "Category=multinode" green on both TFMs. Commit, push, open the PR titled "chore: deduplicate inbox/outbox definitions + AnyNode sentinel". Watch checks until green.

Stay strictly within Task F16's scope.
```

### F17 — efficiency sweep *(model: Sonnet; items 2–4 after F8; item 1 also needs F11 merged)*

```
Execute Task F17 ("Store efficiency sweep") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify the F8 and F11 PRs are merged to main. Then: branch chore/store-efficiency from origin/main. (1) ReplayDeadLettersAsync: cap the Find with .Limit(_options.Durability.RecoveryBatchSize) like every sibling recovery path, and replace the per-letter StoreIncomingAsync+DeleteOneAsync pair with batch StoreIncomingAsync(list) + one DeleteManyAsync(Filter.In on ids). CRITICAL F8 interaction: batch store is now all-or-nothing and throws on ANY duplicate — replay must catch DuplicateIncomingEnvelopeException and fall back to per-letter handling so the documented idempotent-replay behavior survives; write a RED-FIRST test for exactly that (one letter's envelope already present in the inbox → the batch throws → the fallback still replays the others and deletes handled letters). (2) Cache NodeDocs/AssignmentDocs/RecordDocs/RestrictionDocs/Counters in the constructor like Incoming/Outgoing already are. (3) LoadAllNodesAsync: Task.WhenAll the two finds + a ToLookup(a => a.NodeId) join. (4) PersistAgentRestrictionsAsync: one BulkWriteAsync mixing DeleteOneModel/ReplaceOneModel (the AssignAgentsAsync pattern). Full suite + --filter "Category=multinode" green on both TFMs. CHANGELOG entry. Commit, push, open the PR titled "chore: store efficiency sweep (DLQ replay batching, cached collections, parallel node loads)". Watch checks until green.

Stay strictly within Task F17's scope.
```

---

## Cohort 5 — Demo

### F18 — demo identity-convention coverage *(model: Sonnet; only after the F5, F6, and F7 PRs are merged)*

```
Execute Task F18 ("Demo non-Id identity-convention coverage") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify the F5, F6, and F7 PRs are merged to main; read docs/superpowers/plans/2026-07-07-remediation-test-inventory.md and implement ITS demo design. For local dev, pack the library first (dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false) so the demo resolves the fixed package; CI's demo job uses the fresh 0.0.0-ci nupkg. Then: branch demo/identity-convention-coverage from origin/main. Add the F5-designed entity (e.g. CustomerFeedback { Guid CustomerFeedbackId; ... }) with [Entity]-load + Insert<T> handlers beside the existing OrderNote handlers — do NOT modify existing demo types. Add safety-net tests in the OrderNoteFlowTests.cs style (OrdersFixture, per-test database, TrackActivity().InvokeMessageAndWaitAsync, FluentAssertions), including a direct Mongo assertion that the stored document's _id IS the CustomerFeedbackId value in its native BSON type. Briefly mention the new entity in demo/CLAUDE.md and demo/README.md. Run the full demo integration suite green. Commit, push, open the PR titled "demo: non-Id identity-convention entity + safety-net tests". Watch checks until green.

Stay strictly within Task F18's scope.
```

---

## Cohort 6 — Integration & final verification

### F19 — full cross-feature regression *(model: Sonnet; only after F6–F18 PRs are all merged)*

```
Execute Task F19 ("Full cross-feature regression") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md using the superpowers:executing-plans skill.

Precondition: verify with rtk git log origin/main --oneline -25 that the F6–F18 PRs are all merged. Then: branch test/remediation-regression from origin/main (a branch only in case a CI tweak is needed). Run: the full library suite on both TFMs; --filter "Category=multinode" FIVE consecutive times per TFM (all pass); dotnet pack the package-ref build AND inspect the nuspec for F14's bracketed dependency ranges; the demo build + full integration suite (incl. F18's new tests). Confirm CI coverage: the new test files are non-multinode xUnit → auto-included in the library job's single-node step; F11's dead_node_release is also single-node (deterministic); the demo job exercises the fresh nupkg. Add a CI change ONLY if a coverage gap is found. Report a regression summary proving inbox + outbox + saga + entity + diagnostics + single-node + multi-node all pass together. If a CI change was needed, commit, push, open the PR titled "test: full remediation cross-feature regression"; otherwise report that no change was required. If anything is red, report it — do not paper over it.

Stay strictly within Task F19's scope.
```

### F20 — final verification on `main` + release decision *(model: Sonnet; after F15 and F19 merge; no branch, no PR)*

```
Execute Task F20 ("Final verification on main + release decision") of docs/superpowers/plans/2026-07-07-review-findings-remediation.md.

This runs on main itself: rtk git checkout main && rtk git pull. Run the full library suite (both TFMs); --filter "Category=multinode" five consecutive times per TFM; dotnet pack the package-ref build; the demo integration suite. Confirm CI on main is green (library + demo). Review the merged history — one PR per task F1–F19, every file in the plan's File Structure Overview accounted for. Report a verification summary. If anything is red or missing, report it — do not fix anything in this session.

THEN present the release recommendation (plan OQ6) and STOP for the user's decision: the remediation contains user-visible bug fixes (patch-shaped) plus new loud codegen failures where 1.0.0 was silently wrong (arguably minor-shaped) — recommend 1.0.1 or 1.1.0 with a one-paragraph rationale, but do NOT release without explicit approval. If approved, invoke the release agent per CLAUDE.md "Versioning & Release"; otherwise everything stays under CHANGELOG "## [Unreleased]". The remediation plan is complete only when this report is clean.
```
