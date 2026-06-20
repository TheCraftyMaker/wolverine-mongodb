# Persistence Suite Completion — Per-Task Session Prompts

One fresh Claude Code session per task, grouped by parallel cohort and tier. Start each session **in the repository root** (not a stale worktree), set the session model first (`/model` — the recommended model is listed per task), then paste the prompt.

Conventions baked into every prompt (identical to `2026-06-18-saga-task-prompts.md`):
- The session verifies its **precondition** (prerequisite PRs merged to `main`) before doing anything.
- Execution goes through `superpowers:executing-plans` against the referenced plan task **only** — no scope creep.
- Branch + PR mechanics come from the plan's "Git & PR Workflow" section.
- **Do not invent Wolverine APIs.** The plan's "Verified API Facts" section is the source of truth; confirm each against the pinned `external/wolverine` submodule (a one-line grep/Read) rather than guessing. **If an API differs from the plan, STOP and report — do not improvise a substitute.**
- If a plan assumption doesn't hold (API missing, a test can't fail/pass as predicted), the session stops and reports instead of improvising. Two non-obvious verification failures → stop, report, re-run the task on Fable 5.
- When finishing, update the plan doc in the same PR (tick step checkboxes + the status-table row).

**Precondition for everything below:** the Solo Hardening (`2026-06-09-solo-hardening.md`), Multinode (`2026-06-09-multinode-support.md`), and Saga Persistence (`2026-06-18-saga-persistence.md`) plans are fully merged to `main`, and the PR containing this plan + prompts file (`2026-06-21-persistence-suite-completion*.md`) is merged to `main`.

**The plan:** `docs/superpowers/plans/2026-06-21-persistence-suite-completion.md`.

---

## Phase 0 — Discovery & Design

### D1 / D2 / D3 / D4 / D5 — five independent sessions, run in PARALLEL *(model: Sonnet)*

**D1 — Tier 1 entity/storage-action discovery** *(independent — can start immediately)*

```
Execute Task D1 ("Tier 1 — entity/storage-action API + Cosmos/Raven reference") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Read-only analysis producing a notes doc; no library code changes. Branch docs/entity-persistence-discovery from origin/main. Confirm against the pinned external/wolverine submodule (V6.9.0) and local source the plan's Tier-1 "Verified API Facts": the IPersistenceFrameProvider members (CanPersist, DetermineLoadFrame, DetermineInsertFrame/UpdateFrame/StoreFrame, both DetermineDeleteFrame overloads, DetermineStorageActionFrame, DetermineFrameToNullOutMaybeSoftDeleted, TryBuildFetchSpecificationFrame); the routing — Insert<T>/Delete<T>/Update<T>/Store<T> go to the TYPED factories (Insert.cs:26, Delete.cs:26) while generic IStorageAction<T> goes to DetermineStorageActionFrame (IStorageAction.cs:27); the [Entity] load path (EntityAttribute.cs:145,165,170-173); the StorageActionCompliance contract (configureWolverine + Load(string)/Persist(Todo), the Todo POCO, the ~17 facts); and the Cosmos (CosmosDbUpsertFrame/CosmosDbDeleteByVariableFrame/CosmosDbStorageActionApplier) + RavenDb (DeleteDocumentFrame/RavenDbStorageActionApplier) shapes. RESOLVE OQ1: does CosmosDbTests subclass StorageActionCompliance? Flag explicitly the one design tension: Mongo's DetermineInsertFrame/DetermineUpdateFrame are saga-specific (InsertSagaFrame/UpdateSagaFrame) and MUST branch saga-vs-entity, whereas Cosmos's are generic. Produce docs/superpowers/plans/2026-06-21-entity-persistence-discovery.md with signatures + file:line + the routing table; flag any drift from the Verified Facts. Commit, push, open the PR titled "docs: Tier 1 — entity/storage-action API + Cosmos/Raven reference". Watch checks until green.

Do not invent APIs — quote the submodule. Stay strictly within Task D1's scope.
```

**D2 — Tier 2 ISagaStoreDiagnostics discovery** *(independent — can start immediately)*

```
Execute Task D2 ("Tier 2 — ISagaStoreDiagnostics API + Raven reference") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Read-only study of external/wolverine. Branch docs/saga-diagnostics-discovery from origin/main. Pin: the ISagaStoreDiagnostics interface (ISagaStoreDiagnostics.cs:22-63 — GetRegisteredSagasAsync/ReadSagaAsync/ListSagaInstancesAsync); the exact shapes of SagaDescriptor, SagaInstanceState, and SagaDescriptorBuilder.Build(handlerGraph, sagaType, providerTag); RavenDbSagaStoreDiagnostics (ctor IWolverineRuntime+IDocumentStore, the lazy saga index built from HandlerGraph.Chains.OfType<SagaChain>() filtered by provider.CanPersist, reflection dispatch, count clamp 0..1000, state serialization, the [UnconditionalSuppressMessage] AOT annotations); the DI registration (WolverineRavenDbExtensions.cs:33-36); the runtime aggregation (WolverineRuntime.cs:41,66-68,269 + AggregateSagaStoreDiagnostics + IWolverineRuntime.SagaStorage); and the per-provider test pattern (raven_saga_store_diagnostics_tests.cs). Note: NO unified compliance spec; Cosmos does NOT implement it. Produce docs/superpowers/plans/2026-06-21-saga-diagnostics-discovery.md with a RavenDb→MongoDB method mapping (IDocumentStore→IMongoClient/IMongoDatabase; session.LoadAsync<TSaga>→collection.Find(Eq("_id",id)); session.Query<TSaga>().Take(n)→Find(Empty).Limit(n); collection name = MongoConstants.SagaCollectionName(sagaType)) and the registration line to add. Commit, push, open the PR titled "docs: Tier 2 — ISagaStoreDiagnostics API + Raven reference". Watch checks until green.

Do not invent APIs — quote the submodule. Stay strictly within Task D2's scope.
```

**D3 — Tier 3 parity decisions discovery** *(independent — can start immediately)*

```
Execute Task D3 ("Tier 3 — parity capabilities + implement-vs-defer recommendation") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Read-only study. Branch docs/parity-decisions-discovery from origin/main. Confirm the plan's Tier-3 Verified Facts for the four capabilities — multi-tenancy (ITenantedMessageSource, IMessageStore.TenantIds), durable listeners (IListenerStore, NullListenerStore, IMessageStore.Listeners, RdbmsListenerStore), query-spec (TryBuildFetchSpecificationFrame default-false + Marten/EF overrides), soft-delete (DetermineFrameToNullOutMaybeSoftDeleted + Marten's SetVariableToNullIfSoftDeletedFrame) — and what Cosmos/Raven do for each (all four DEFER). CRITICAL: confirm what the LOCAL MongoDbMessageStore currently exposes for TenantIds and Listeners (empty list? NullListenerStore?). Produce docs/superpowers/plans/2026-06-21-parity-decisions-discovery.md: a table per capability (contract / Cosmos / Raven / Marten-RDBMS / current Mongo / recommendation) with a firm implement-vs-defer recommendation each. Default recommendation: DEFER (document-as-non-goal) for all four — multi-tenancy, query-spec, soft-delete as hard non-goals; durable listeners as defer-keep-NullListenerStore with the cheap optional follow-up shape noted. Commit, push, open the PR titled "docs: Tier 3 — parity capabilities + implement-vs-defer recommendation". Watch checks until green.

Stay strictly within Task D3's scope.
```

**D4 — Tier 4 FOLLOWUPS audit** *(independent — can start immediately)*

```
Execute Task D4 ("Tier 4 — FOLLOWUPS audit + multinode un-gate scoping") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Read-only audit. Branch docs/tier4-followups-audit from origin/main. For EACH FOLLOWUPS.md item (unkeyed IMongoDatabase; INodeAgentPersistence.ClearAllAsync scope; node-number reuse; pre-1.0 index migration; lease fencing token; IListenerStore still NullListenerStore; demo MongoDbUnitOfWork example; demo saga-cascade consumer; saga-specific indexes; multinode leadership re-eval; ISagaStoreDiagnostics), confirm the current behavior in code (file:line) and classify implement / document-as-non-goal / verify, mapping each to its Tier-4 task (T4.1–T4.6). For the multinode item, read docs/superpowers/plans/2026-06-16-task6-multinode-compliance-findings.md and the gated leadership_election_compliance.cs, and write the exact un-gate runbook (which #if RUN_MULTINODE guard, how to run 5× per TFM, which CI multinode step to add the suite to). Produce docs/superpowers/plans/2026-06-21-tier4-followups-audit.md (the mapping table + the un-gate runbook). Commit, push, open the PR titled "docs: Tier 4 — FOLLOWUPS audit + multinode un-gate scoping". Watch checks until green.

Stay strictly within Task D4's scope.
```

**D5 — demo flow design + cross-tier test inventory** *(independent to start; refine the entity shape after D6 merges)*

```
Execute Task D5 ("Demo flow design + cross-tier test inventory") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Design-only. Branch docs/demo-and-test-inventory from origin/main. Design: (1) the Tier-1 demo entity written via [Entity] + IStorageAction<T> rather than the repository pattern — recommend an OrderNote (or CustomerProfile) with handlers Handle(AddOrderNoteCommand)→Insert<OrderNote>, Handle(EditOrderNoteCommand,[Entity] OrderNote)→Update<OrderNote>, Handle(DeleteOrderNoteCommand,[Entity] OrderNote)→Delete<OrderNote>; (2) the Tier-4 MongoDbUnitOfWork example handler (a variant write path, e.g. Handle(RecordOrderAuditCommand, MongoDbUnitOfWork)) and the fulfillment read-model projector consuming the saga's FulfillmentShippedEvent/FulfillmentCompletedEvent cascades; (3) a cross-tier flow→test inventory mapping each capability to a library test (subclass StorageActionCompliance for Tier 1; per-provider integration test for Tier 2; none for Tier 3/most Tier 4) and/or demo test (file + assertion). Confirm the entity collection-naming convention the host/Load/Persist must mirror (feeds D6). Produce docs/superpowers/plans/2026-06-21-demo-and-test-inventory.md. Commit, push, open the PR titled "docs: demo flow design + cross-tier test inventory". Watch checks until green.

If D6 has merged, align the entity id/collection shape with its decisions. Stay strictly within Task D5's scope.
```

### D6 — Tier 1 entity document model + frame-branching design (DESIGN GATE) *(model: Opus / Fable 5; only after D1 and D5 PRs are merged)*

```
Execute Task D6 ("Tier 1 — entity document model + frame-branching design") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify with rtk git log origin/main --oneline -15 that the D1 and D5 PRs are merged (their notes docs exist on main). Then: branch docs/entity-document-model-design from origin/main. Synthesize D1 + D5 and make the binding decisions in the task (LD1: broaden CanPersist to unconditional true, saga-vs-entity branching moves to the frame factories; LD2: entity write semantics = upsert for Insert/Update/Store, DeleteOne for Delete, no entity OCC — Cosmos parity; LD3: EntityCollectionName(Type)=type.Name.ToLowerInvariant() un-prefixed, _id value via BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap getter NOT Cosmos's ToString(); session enlistment identical to the saga frames; [Entity] not-found is core behavior (load frame returns null); soft-delete stays []; upstream-readiness notes). Be precise about the branch predicate (variable.VariableType.CanBeCastTo<Saga>()) and that the saga frames are UNTOUCHED. Produce docs/superpowers/plans/2026-06-21-entity-document-model-design.md. Commit, push, open the PR titled "docs: Tier 1 — entity document model + frame-branching design". Watch checks until green.

This doc gates T1.1; be precise about the frame/collection contracts. Stay strictly within Task D6's scope.
```

---

## Phase 1 — Tier 1: Generic Entity / Side-Effect Persistence (CRITICAL PATH)

### T1.1 — generic entity + IStorageAction persistence *(model: Opus / Fable 5; only after the D6 PR is merged — HEAD OF THE CRITICAL PATH)*

```
Execute Task T1.1 ("Generic entity + IStorageAction<T> persistence") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the D6 design PR is merged to main (read docs/superpowers/plans/2026-06-21-entity-document-model-design.md and follow its decisions). Then: branch feat/entity-storage-action-persistence from origin/main.

Implement generic entity persistence as code-generation, mirroring the verified Cosmos provider but BRANCHING saga-vs-entity so saga behavior is untouched. In MongoDbPersistenceFrameProvider.cs: broaden CanPersist to unconditional true (the saga-vs-entity distinction now lives in the frame factories, NOT CanPersist; this is required for [Entity] loads which key on CanPersist(parameterType)); branch DetermineInsertFrame/DetermineUpdateFrame/DetermineStoreFrame/DetermineLoadFrame on `variable.VariableType.CanBeCastTo<Saga>()` (Saga → the existing InsertSagaFrame/UpdateSagaFrame/LoadSagaFrame UNCHANGED; else → new entity frames); implement the throwing DetermineDeleteFrame(Variable,…) (entity Delete<T>) and DetermineStorageActionFrame (generic IStorageAction<T>) via a MethodCall to a new MongoEntityOperations.ApplyStorageActionAsync<T> (set call.Arguments[2]=action, letting Wolverine resolve IMongoDatabase + IClientSessionHandle + CancellationToken). Add a new Internals/EntityFrames.cs (MongoEntityOperations: LoadAsync/UpsertAsync/DeleteAsync/ApplyStorageActionAsync — upsert for Insert/Update/Store, DeleteOne for Delete, Nothing no-op; _id via BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap getter) plus LoadEntityFrame/MongoUpsertEntityFrame/MongoDeleteEntityByVariableFrame structurally identical to the SagaFrames.cs frames (resolve session+database+ct via FindVariable, call the static helper to keep generated code free of extension-method/using concerns). Add MongoConstants.EntityCollectionName(Type)=type.Name.ToLowerInvariant(). PRESERVE all saga/inbox/outbox/transaction behavior.

CRITICAL verification: dump the generated handler for a handler returning Insert<Todo> AND one taking [Entity] Todo (codeFor<T>() or CodeGeneration.GeneratedCodeOutputPath); confirm the entity load/upsert/delete runs INSIDE the try-block on the session, BEFORE the single commit+flush (atomic with the outbox), and confirm a SAGA handler's generated code is UNCHANGED. The acceptance oracle is the upstream StorageActionCompliance suite: author a single self-contained storage_action_compliance.cs (: StorageActionCompliance, [Collection("mongodb")] — NOT generic over a host like ISagaHost, so no separate host file; mirror RavenDb's using_storage_return_types_and_entity_attributes.cs) with configureWolverine registering _fixture.Client + UseMongoDbPersistence(AppFixture.DatabaseName) + AutoApplyTransactions + Solo + TypeLoadMode.Dynamic, and Load/Persist targeting MongoConstants.EntityCollectionName(typeof(Todo)) (clear that collection per fact) IN THIS BRANCH so the PR ships green. Run --filter "FullyQualifiedName~storage_action_compliance" green, then the FULL library suite on net9.0 + net10.0 (all four saga compliance suites + saga_atomicity + saga_optimistic_concurrency must stay green). Commit, push, open the PR titled "feat: generic entity + IStorageAction persistence". Watch checks until green.

If the generated order/atomicity is wrong, or any saga test regresses, fix the frame wiring — do not weaken assertions. If a Wolverine API differs from the plan's Verified Facts, stop and report. Stay strictly within Task T1.1's scope.
```

### T1.2 — entity atomicity + saga/entity coexistence regression *(model: Opus / Fable 5; only after the T1.1 PR is merged)*

```
Execute Task T1.2 ("Entity atomicity + saga/entity coexistence regression") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the T1.1 PR is merged to main. Then: branch test/entity-atomicity-coexistence from origin/main. Add src/Wolverine.MongoDB.Tests/entity_atomicity.cs ([Collection("mongodb")], on AppFixture, modeled on saga_atomicity.cs) covering: (1) entity atomicity — a handler returning Insert<T> AND cascading an outgoing message via a durable local queue; a forced post-write failure leaves NEITHER the entity doc NOR the outgoing envelope persisted, and the success path persists both (prove the cascade by its downstream effect, the deterministic technique from saga S11 — NOT a racy outbox-relay observation); (2) coexistence regression — a single handler that writes saga state (a Saga subclass) AND an entity (via [Entity]/Store<T>); assert the saga still gets Version-stamped/OCC behavior and the entity persists (proves the frame-branching did not cross wires); (3) [Entity] not-found — a required [Entity] for a missing id does not execute the handler, end-to-end through the Mongo load frame. Verify each test fails for the right reason before it passes (drop the cascade → success RED; drop the throw → failure RED). Run all three green + the full suite on net9.0 + net10.0. Commit, push, open the PR titled "test: entity atomicity + saga/entity coexistence regression". Watch checks until green.

These tests simulate races/failures — a wrong test passes most runs; verify red-for-the-right-reason. Stay strictly within Task T1.2's scope.
```

### T1.3 — demo [Entity]/IStorageAction handler + safety-net tests *(model: Sonnet; build/run requires T1.1 merged & packed)*

```
Execute Task T1.3 ("Demo [Entity]/IStorageAction handler + safety-net tests") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the T1.1 PR is merged to main (the demo consumes the Wolverine.MongoDB package; generic-persistence support must be in the package). For local dev, pack the library first (dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false); CI's demo job uses the fresh 0.0.0-ci nupkg. Then: branch demo/entity-and-storage-action from origin/main. Implement per the D5 design: a new OrderNote (or CustomerProfile) entity + contracts in OrderDemo.Contracts/Domain, handlers Handle(AddOrderNoteCommand)→Insert<OrderNote>, Handle(EditOrderNoteCommand,[Entity] OrderNote)→Update<OrderNote>, Handle(DeleteOrderNoteCommand,[Entity] OrderNote)→Delete<OrderNote> in OrderDemo.Application, and Program.cs discovery/routing. Keep entity writes inside the Wolverine transaction (the generated frames handle the session — do NOT add a manual session). Preserve all existing order flows. Build the demo (dotnet build OrderDemo.slnx -c Release) and add OrderNoteFlowTests.cs on OrdersFixture (add/edit/delete + [Entity]-not-found, reading the ordernote collection directly). Run the demo suite green. Commit, push, open the PR titled "demo: [Entity]/IStorageAction handler + safety-net tests". Watch checks until green.

Stay strictly within Task T1.3's scope. If the entity design needs a tweak vs D5, note it in the PR.
```

---

## Phase 2 — Tier 2: ISagaStoreDiagnostics *(independent track — runs in parallel with the whole Tier-1 chain)*

### T2.1 — MongoDbSagaStoreDiagnostics + registration *(model: Opus / Fable 5; only after the D2 PR is merged)*

```
Execute Task T2.1 ("MongoDbSagaStoreDiagnostics + registration") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the D2 PR is merged to main (read docs/superpowers/plans/2026-06-21-saga-diagnostics-discovery.md and follow its RavenDb→MongoDB mapping). Then: branch feat/saga-store-diagnostics from origin/main. Add Internals/MongoDbSagaStoreDiagnostics.cs implementing ISagaStoreDiagnostics, mirroring RavenDbSagaStoreDiagnostics: ctor (IWolverineRuntime runtime, IMongoClient client) + the configured database name threaded from UseMongoDbPersistence; a lazy saga index from runtime.Options.HandlerGraph.Chains.OfType<SagaChain>().Select(c=>c.SagaType) filtered by provider.CanPersist(sagaType, container, out _), indexed by FullName + Name; reflection (MakeGenericMethod) to Find(Eq("_id",id)) (read) and Find(Empty).Limit(count) (list) against MongoConstants.SagaCollectionName(sagaType); clamp count to [0,1000]; build SagaInstanceState/SagaDescriptor via the core helpers (SagaDescriptorBuilder.Build(handlerGraph, sagaType, "MongoDb")). Apply the same [UnconditionalSuppressMessage] AOT annotations RavenDb uses on the reflection dispatch. Register options.Services.AddSingleton<ISagaStoreDiagnostics>(...) in UseMongoDbPersistence. Confirm the FULL library suite still green on net9.0 + net10.0 (registration must not break startup or existing tests). Commit, push, open the PR titled "feat: MongoDbSagaStoreDiagnostics + registration". Watch checks until green.

Reflection + handler-graph walking — reason carefully; if SagaDescriptor/SagaInstanceState/SagaDescriptorBuilder differ from D2's recorded shapes, stop and report. Stay strictly within Task T2.1's scope.
```

### T2.2 — MongoDb saga store diagnostics test *(model: Sonnet; only after the T2.1 PR is merged)*

```
Execute Task T2.2 ("MongoDb saga store diagnostics test") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the T2.1 PR is merged to main. Then: branch test/saga-store-diagnostics from origin/main. Add src/Wolverine.MongoDB.Tests/saga_store_diagnostics.cs ([Collection("mongodb")], on AppFixture), mirroring raven_saga_store_diagnostics_tests: build a host with a known saga (reuse a compliance saga or a small in-test one), start an instance, then assert GetRegisteredSagasAsync includes the saga tagged "MongoDb"; ReadSagaAsync(typeName, id) returns the instance via BOTH FullName and short-Name routing; ListSagaInstancesAsync returns it; and an unknown saga-type name returns null/empty. Run the new test green + the full suite on net9.0 + net10.0. Commit, push, open the PR titled "test: MongoDb saga store diagnostics". Watch checks until green.

Stay strictly within Task T2.2's scope.
```

---

## Phase 3 — Tier 3: Parity Decisions *(independent track)*

### T3.1 — parity capabilities: non-goals + rationale *(model: Sonnet; only after the D3 PR is merged)*

```
Execute Task T3.1 ("Parity capabilities — non-goals + rationale") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the D3 PR is merged to main (follow its per-capability recommendation). Then: branch docs/parity-non-goals from origin/main. Default per the plan: document-as-non-goal for all four. First CONFIRM each capability is at its documented default in code (no behavioral change): TenantIds empty, IMessageStore.Listeners == NullListenerStore.Instance, TryBuildFetchSpecificationFrame default-false, DetermineFrameToNullOutMaybeSoftDeleted returns []. Then document — in CLAUDE.md (key design decisions), a new docs/superpowers/plans/2026-06-21-parity-non-goals.md, and FOLLOWUPS.md cross-links — for multi-tenancy / durable listeners / query-spec / soft-delete: the contract, what Cosmos/Raven do, the decision (non-goal; durable listeners = defer-keep-NullListenerStore with the cheap optional implementation shape noted), the rationale, and the app-level workaround. Verify no code change is needed (if D3 surfaced a divergence, that is a separate scoped task — report it and stop). Commit, push, open the PR titled "docs: parity capabilities — non-goals + rationale". Watch checks until green.

This is a documentation task unless D3 recommended implementing durable listeners — in that case, report and request a scoped follow-up rather than expanding scope here. Stay strictly within Task T3.1's scope.
```

---

## Phase 4 — Tier 4: Hardening + Tracked Follow-ups *(independent track, fan-out)*

### T4.1 — demo MongoDbUnitOfWork example handler + test *(model: Sonnet; only after the D5 PR is merged)*

```
Execute Task T4.1 ("Demo MongoDbUnitOfWork example handler + test") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the D5 PR is merged to main (follow its UoW handler design). Then: branch demo/unit-of-work-example from origin/main. Add a handler (or variant endpoint) that accepts MongoDbUnitOfWork and writes through Collection<T>() — e.g. Handle(RecordOrderAuditCommand, MongoDbUnitOfWork uow) writing an audit doc — plus its contract and Program.cs discovery/routing. This showcases the recommended unit-of-work write surface alongside the existing repository + IClientSessionHandle pattern (closing the FOLLOWUPS "demo has no MongoDbUnitOfWork example" gap). Add a demo test asserting the write committed atomically with the outbox (and rolled back on a forced failure). Build the demo (dotnet build OrderDemo.slnx -c Release) and run the demo suite green. Commit, push, open the PR titled "demo: MongoDbUnitOfWork example handler + test". Watch checks until green.

Stay strictly within Task T4.1's scope.
```

### T4.2 — demo fulfillment read-model projector (saga cascade consumer) *(model: Sonnet; only after the D5 PR is merged)*

```
Execute Task T4.2 ("Demo fulfillment read-model projector") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the D5 PR is merged to main. Then: branch demo/saga-cascade-consumer from origin/main. Add a projector Handle(FulfillmentShippedEvent)/Handle(FulfillmentCompletedEvent) recording a delivery status/timestamp the OrderSummary does not track (closing the FOLLOWUPS "demo saga cascade events have no consumer" gap), with a matching local-queue route in OrdersFixture and Program.cs, and an assertion in SagaFlowTests (or a new test) that the projection updates from the saga cascade — exercising the full saga → outbox → consumer path. Keep opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated (already set) so the saga and any co-handler run independently. Run the demo suite green. Commit, push, open the PR titled "demo: fulfillment read-model projector for saga cascades". Watch checks until green.

Stay strictly within Task T4.2's scope.
```

### T4.3 — resolve unkeyed IMongoDatabase registration *(model: Opus / Fable 5; only after the D6 and T1.1 PRs are merged)*

```
Execute Task T4.3 ("Resolve unkeyed IMongoDatabase registration") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the D6 and T1.1 PRs are merged to main (a registration change must be validated against BOTH saga and entity codegen). Then: branch feat/mongo-database-registration from origin/main. Address the FOLLOWUPS constraint that UseMongoDbPersistence registers a single unkeyed IMongoDatabase (WolverineMongoDbExtensions.cs:59-60), conflicting with an app that registers its own. DEFAULT (recommended): document it as a consumer constraint in README + CLAUDE + a FOLLOWUPS resolution note — because the saga AND entity frames resolve IMongoDatabase via chain.FindVariable(typeof(IMongoDatabase)), switching to keyed/dedicated registration is a high-blast-radius codegen change. ONLY if D6/D4 explicitly favored a dedicated type: introduce a thin internal wrapper the frames resolve (leaving the app's IMongoDatabase untouched), update every frame's FindVariable/MethodCall resolution AND MongoDbUnitOfWork, and prove the full suite (saga + entity + UoW) green on net9.0 + net10.0. Commit, push, open the PR titled "feat/docs: resolve unkeyed IMongoDatabase registration". Watch checks until green.

If you choose the dedicated-registration route and any saga/entity/UoW test regresses, the change is not contained — revert to documenting the constraint and report. Stay strictly within Task T4.3's scope.
```

### T4.4 — INodeAgentPersistence.ClearAllAsync scope *(model: Sonnet; only after the D4 PR is merged)*

```
Execute Task T4.4 ("INodeAgentPersistence.ClearAllAsync scope") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the D4 PR is merged to main (follow its classification for this item). Then: branch feat/node-clearall-scope from origin/main. Resolve the FOLLOWUPS ambiguity: the node-level ClearAllAsync clears only node + assignment collections. Per D4, either widen it to also clear counter/locks/node-records/agent-restrictions, OR document that IMessageStoreAdmin.RebuildAsync/ClearAllAsync is the full reset and the node-level one is intentionally narrow. Implement or document per D4's recommendation; add/adjust a focused node-persistence test if behavior changes. Run the full suite green on net9.0 + net10.0. Commit, push, open the PR titled "feat: INodeAgentPersistence.ClearAllAsync scope". Watch checks until green.

Stay strictly within Task T4.4's scope.
```

### T4.5 — re-evaluate + un-gate multinode leadership compliance *(model: Opus 4.8; only after the D4 PR is merged)*

```
Execute Task T4.5 ("Re-evaluate + un-gate multinode leadership compliance") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the D4 PR is merged to main (follow its un-gate runbook); the multinode infrastructure is already on main. Then: branch test/multinode-leadership-ungate from origin/main. Run the leadership_election_compliance suite under RUN_MULTINODE FIVE consecutive times on net9.0 AND net10.0 (25 runs total). If ALL green: remove the #if RUN_MULTINODE guard and add the suite to the CI multinode step (.github/workflows/ci.yml), update FOLLOWUPS.md + the multinode-leadership-model-decision memory. If ANY run flakes: do NOT un-gate — write up the failure mode, keep the gate, update the FOLLOWUPS note. Same hard rules as multinode Task 7 / saga S14: no skips, no retries-as-bandaid, no assertion-weakening. Commit, push, open the PR titled "test: re-evaluate + un-gate multinode leadership compliance". Watch checks until green.

If exactly-once/leadership fails, that is a real coordination signal — document it and keep the gate; do not paper over it. Stay strictly within Task T4.5's scope.
```

### T4.6 — pre-1.0 hardening backlog (document/defer bundle) *(model: Sonnet; only after the D4 PR is merged)*

```
Execute Task T4.6 ("Pre-1.0 hardening backlog") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the D4 PR is merged to main. Then: branch docs/pre-1.0-hardening-backlog from origin/main. For each remaining FOLLOWUPS item, write a clear, dated decision (default: document/defer with rationale) in FOLLOWUPS.md + CLAUDE.md: node-number reuse (monotonic counter — document acceptable, track lowest-free-slot for post-1.0); pre-1.0 index migration (old single-field indexes harmless on beta deployments — document; add a migration step only if needed); lease fencing token/epoch (future hardening option, not needed for store-only leader work); saga-specific indexes (only the implicit _id index today — document the RebuildAsync extension point; add per-collection indexes only when query patterns demand). No behavioral change unless an item is explicitly chosen to implement. Commit, push, open the PR titled "docs: pre-1.0 hardening backlog decisions". Watch checks until green.

Stay strictly within Task T4.6's scope.
```

---

## Phase 5 — Integration, Documentation & Verification

### V1 — full cross-feature regression sweep *(model: Sonnet; only after T1.1–T4.6 PRs are all merged)*

```
Execute Task V1 ("Full cross-feature regression sweep") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify with rtk git log origin/main --oneline -30 that the T1.1–T4.6 PRs are all merged. Then: branch test/suite-completion-regression from origin/main (a branch only in case a CI tweak is needed). Run: the full library suite on net9.0 + net10.0; --filter "Category=multinode" five consecutive times (all pass — incl. the newly un-gated leadership suite if T4.5 added it); dotnet pack the package-ref build; the demo build (dotnet build OrderDemo.slnx -c Release) + integration suite (incl. the new entity, UoW, and projector tests). Confirm CI's library job runs storage_action_compliance + saga_store_diagnostics in its single-node step and the demo job runs the new demo tests against the fresh nupkg — add a CI change ONLY if a coverage gap is found. Report a regression summary proving inbox + outbox + single-node + multi-node + saga + saga-diagnostics + generic entity persistence all pass together. If a CI change was needed, commit, push, open the PR titled "test: full suite-completion regression"; otherwise report no change required. If anything is red, report it — do not paper over it.

Stay strictly within Task V1's scope.
```

### V2 — documentation sweep + upstream-contribution notes *(model: Sonnet; only after T1.1–T4.6 PRs are all merged; may run parallel to V1)*

```
Execute Task V2 ("Documentation sweep + upstream-contribution notes") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md using the superpowers:executing-plans skill.

Precondition: verify the T1.1–T4.6 PRs are merged to main. Then: branch docs/suite-completion-sweep from origin/main. Update README.md (generic entity persistence: [Entity] loads + Insert/Update/Store/Delete<T>/IStorageAction<T> returns, the per-type collection convention, upsert/no-OCC semantics with the repository pattern as the OCC path; ISagaStoreDiagnostics availability; the Tier-3 non-goals), CLAUDE.md (file map + key design decisions: entity frames branch saga-vs-entity, entity collection naming, diagnostics, parity non-goals), CHANGELOG.md (### Added entity persistence + saga diagnostics under [Unreleased]), FOLLOWUPS.md (close resolved items; keep deferred), demo/README.md + demo/CLAUDE.md (the new entity/UoW/projector flows). Add/extend an upstream-contribution-notes section (target Wolverine.MongoDb; the compliance subclasses to bring — StorageActionCompliance + the saga specs; the diagnostics impl; the deliberate deltas vs Cosmos/Raven — native saga id + SagaConcurrencyException; class-map _id extraction for entities). CRITICAL: verify every documented claim against the code on main before writing it. Commit, push, open the PR titled "docs: suite completion + upstream contribution notes". Watch checks until green.

Stay strictly within Task V2's scope.
```

### V3 — final verification on `main` *(model: Sonnet; after V1 and V2 merge; no branch, no PR)*

```
Execute Task V3 ("Final verification on main") of docs/superpowers/plans/2026-06-21-persistence-suite-completion.md.

This runs on main itself: rtk git checkout main && rtk git pull. Run the full library suite (both TFMs); run --filter "Category=multinode" five consecutive times (all pass, incl. leadership if un-gated); dotnet pack the package-ref build; run the demo integration suite. Confirm CI on main is green (library + demo). Review the merged history — one PR per task D1–V2, every file in the plan's File Structure Overview touched. Report a verification summary proving the full suite (inbox + outbox + single-node + multi-node + saga + saga-diagnostics + generic entity persistence) is green and the Tier-3 non-goals + Tier-4 decisions are documented. If anything is red or missing, report it — do not fix anything in this session.

OPTIONAL (only if the user explicitly wants a release): per CLAUDE.md "Versioning & Release", invoke the release agent to publish a suite-complete version and re-point the demo. Otherwise leave the work under CHANGELOG "## [Unreleased]". The plan is complete only when this report is clean.
```
