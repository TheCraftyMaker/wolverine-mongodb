# Remediation Test Inventory + Demo Impact Design (Task F5)

> Design-only. Produces the finding→test mapping every F6–F17 implementation task should treat as
> its starting checklist, and the F18 demo-extension design. No library or demo code was touched.
> Companion to the plan: `docs/superpowers/plans/2026-07-07-review-findings-remediation.md`.
> Built on `docs/superpowers/plans/2026-07-07-identity-mapping-discovery.md` (Task F1, merged
> #154). **F3 and F4 (the identity and durability design gates) had not yet merged when this
> document was written** — every assertion below follows the plan's *recommended* options (LD1-B,
> LD2-A, LD3 two-tick, LD4 throw). Section 5 lists exactly which assertions are sensitive to an F3/F4
> reversal and must be re-checked once those PRs land, per this task's instructions.

---

## 1. Upstream compliance-suite coverage — confirmed, no finding is caught by an existing spec

Verified directly (not re-derived from the plan) against `external/wolverine` (submodule pinned at
V6.16.0) and the local test project:

| Suite | Identity/behavior shape it exercises | Covers any 2026-07-07 finding? |
|---|---|---|
| `StringIdentifiedSagaComplianceSpecs<MongoDbSagaHost>` / `GuidIdentifiedSagaComplianceSpecs` / `IntIdentifiedSagaComplianceSpecs` / `LongIdentifiedSagaComplianceSpecs` (`string_saga_storage_compliance.cs`, `guid_saga_storage_compliance.cs`, `int_saga_storage_compliance.cs`, `long_saga_storage_compliance.cs`) | All four instantiate the upstream generic `BasicWorkflow<TStart,TCompleteThree,TId> : Saga { public TId Id { get; set; } }` (`external/wolverine/src/Testing/Wolverine.ComplianceTests/Sagas/TestMessages.cs:46-50`) — the identity member is **always literally named `Id`**, regardless of the closed `TId`. | **No.** Cannot exercise `{TypeName}Id`/`{Name-minus-Saga}Id`/`[SagaIdentity]` conventions — structurally excluded by the base class's own field name. |
| `storage_action_compliance.cs` : `StorageActionCompliance` | Upstream's only fixture type is `Todo { public string Id { get; set; } }` (`StorageActionCompliance.cs:272-274`, confirmed by F1 §4 and re-confirmed by direct read this task). `[Entity]` load + `Insert/Update/Store/Delete<Todo>`/`IStorageAction<Todo>`. | **No.** `Id`-only; and it never constructs a storage action for a **saga** type, so it cannot exercise the LD4 guard either (`Todo` is not a `Saga`). |
| `leadership_election_compliance.cs` : `LeadershipElectionCompliance` (13 upstream facts, `[Category=multinode]`, un-gated per T4.5) | Node liveness, control-endpoint heartbeats, leader handoff. Exercises `ReleaseDeadNodeOwnershipAsync` only incidentally (it runs every recovery tick in Balanced mode) — it asserts leadership state, never inbox/outbox `OwnerId` release directly. | **No** for F11's specific two-tick contract (no fact asserts on envelope ownership after node death); it **is**, however, the regression oracle F11's PR must keep green 5× per the plan (§ Task F11 Expected output) precisely because it runs the changed code path on every tick without testing it directly. |
| Any other `Wolverine.ComplianceTests` fixture (`using_storage_return_types_and_entity_attributes.cs`, `saga_id_member_determination.cs`, `async_method_name_saga.cs`, `auditing_determination.cs`, `Bug_2521_saga_identity_from_ordering.cs`) | All confirmed `Id`-named by F1 §4's repo-wide search; none are wired into this provider's test project regardless. | **No.** |

**Conclusion confirmed, no correction needed:** every one of the 11 confirmed findings requires a
**custom** test on `AppFixture`, written in this repo's own structural idiom. The two existing
files the plan names as structural models are confirmed fit-for-purpose:

- **`saga_atomicity.cs`** — the model for driving real generated saga frames through an `IHost` with
  `TypeLoadMode.Dynamic` + `Discovery.DisableConventionalDiscovery().IncludeType(...)`, then reading
  the persisted document directly via `_fixture.Client...Find(Builders<T>.Filter.Eq("_id", ...))` to
  verify independently of Wolverine. `saga_identity_conventions.cs` (F6) and
  `entity_identity_conventions.cs` (F7) follow this shape.
- **`storage_action_compliance.cs`** — the model for a `[Collection("mongodb")]` class with its own
  `Load`/`Persist`/collection-drop `initialize()`, useful where a finding is best proven through a
  small custom fixture type rather than a full saga workflow (not used verbatim by any F6–F13 test,
  but its "target the exact collection the generated frame writes to, never a hard-coded literal"
  discipline applies to every new file below).
- **Direct-store tests** (`dead_node_ownership_release.cs`, `reassign_incoming.cs`,
  `dead_letter_replay.cs`) are the model for F8/F10/F11/F12/F17: build a bare `MongoDbMessageStore`
  via `_fixture.BuildMessageStore()` and call its internal methods directly — no `IHost`, no codegen,
  deterministic and fast. This is the correct pattern for every Tier 2/3 test below (none of them
  need real generated frames; they test store-level contracts).

---

## 2. Finding → test mapping

Each row is one **red-first** test (or a small group of `[Fact]`s in one new/extended file) per the
plan's Acceptance Criteria ("no finding is closed by inspection alone"). "Trigger" is the setup that
reproduces the pre-fix defect; "Assertion" is what must hold true post-fix. File paths are relative
to `src/Wolverine.MongoDB.Tests/` unless marked `demo/`.

### F6 — `saga_identity_conventions.cs` (new)

Structural model: `saga_atomicity.cs` (real `IHost`, `TypeLoadMode.Dynamic`, direct-Mongo read-back).

| Test | Trigger | Assertion |
|---|---|---|
| `name_minus_saga_convention_round_trips_through_generated_frames` | `ShipmentSaga : Saga { public Guid ShipmentId; ... }` (no member literally named `Id`) started via a `StartShipment(Guid ShipmentId)` message, then updated via a message carrying the same `ShipmentId`, then completed. | Direct Mongo read: `wolverine_saga_shipmentsaga` collection's document `_id` **equals the native `Guid` `ShipmentId` value** (not a server `ObjectId`); the update finds and mutates the *same* document (no duplicate); completion deletes it. Today: **FAILS** — insert writes a server-generated `ObjectId` `_id` (per F1 §2.7), the update's `Filter.Eq("_id", shipmentId)` matches nothing, so `SagaConcurrencyException`/silent no-op or a second phantom document is created (the review's exact "load returns null / duplicate saga docs" failure mode). |
| `sagaidentity_attribute_string_convention_round_trips` | A saga whose start message has a member tagged `[SagaIdentity]` with a name that does **not** match any of the other four precedence tiers (e.g. `[SagaIdentity] public string TrackingCode`), saga itself keeps a `string` field of a *different* name holding the same value. Full start→update→complete. | Same shape of assertion: native-typed (`string`) `_id` equals the identity value at every stage; no duplicate document. **FAILS today** for the same reason — `[SagaIdentity]` wins Wolverine's resolution but the driver's `NamedIdMemberConvention` never heard about it. |
| `plain_id_saga_still_round_trips_unchanged` | A minimal `Saga { public Guid Id }` (the existing, always-worked shape) driven the same way. | **Regression guard**, not red-first — must pass both before and after F6; proves the fix is a no-op for the `Id` convention (on-disk byte-identical per LD1's compatibility statement). |
| `unresolvable_identity_throws_at_codegen_with_actionable_message` | A saga type with **no** member satisfying any of the five `DetermineSagaIdMember` tiers (deliberately pathological — e.g. only a `Name` property, no `Id`/`{Type}Id`/`SagaId`/`[SagaIdentity]`). Force codegen (host build / `HandlerFor` per the repo's "dump generated handler source" convention). | Host build throws `InvalidOperationException` with a message naming the saga type and stating no identity member could be resolved — **not** the cryptic `UpdateSagaFrame` fallback compile error referencing a nonexistent `saga.Id` member that occurs today (F1 §3.1, the `?? "Id"` fallback). |

**Existing-suite regression bar (unchanged, must stay green):** all four
`*IdentifiedSagaComplianceSpecs<MongoDbSagaHost>` files, `saga_atomicity.cs`,
`saga_optimistic_concurrency.cs`, `guid_saga_id_roundtrip.cs`, `saga_store_diagnostics.cs`,
`saga_multinode.cs` (`Category=multinode`).

### F7 — `entity_identity_conventions.cs` (new)

Structural model: `entity_atomicity.cs` (entity write + coexistence patterns) plus `saga_atomicity.cs`'s
direct-Mongo-read discipline.

| Test | Trigger | Assertion |
|---|---|---|
| `typename_id_only_entity_round_trips_through_entity_frames` | `Widget { public Guid WidgetId; ... }` (no `Id` member at all) persisted via `Insert<Widget>`, loaded via `[Entity]` in a follow-up handler, then removed via `Delete<Widget>`. | Direct Mongo read on the `widget` collection: `_id` equals the native `WidgetId` `Guid` at every stage; the `[Entity]`-loaded instance in the second handler is the *same* document (not null, not a phantom). **FAILS today** — `LoadEntityFrame` resolves `WidgetId` via `DetermineSagaIdType` (Wolverine's convention) while `MongoUpsertEntityFrame`'s `IdOf` resolves via the driver's `NamedIdMemberConvention`, which finds no `Id`/`id`/`_id` member and returns a server `ObjectId` instead — read and write key different values. |
| `both_typename_id_and_id_member_entity_agrees_on_one_key` | The review's exact poisoned shape: an entity with **both** `Id` (string) and `WidgetId` (Guid) properties present (Wolverine's resolver picks `WidgetId` per tier 2 of `DetermineSagaIdMember`; the driver's `NamedIdMemberConvention` would otherwise pick `Id` per tier-1 name match). Insert → `[Entity]` load → update. | The persisted document's `_id` is the `WidgetId` value (Wolverine's resolution wins, per `EnsureIdMember`'s ensure-or-fail contract keying off the Wolverine-resolved member) and the load finds it. **FAILS today** — write keys off `Id` (driver-resolved, wins `NamedIdMemberConvention`'s tier-1 exact-name match) while load filters on `WidgetId`'s value — the two never intersect. |
| `plain_id_entity_still_round_trips_unchanged` | Existing `Id`-only shape (mirrors `OrderNote`/`Todo`). | Regression guard — passes before and after F7. |
| `delete_saga_from_plain_handler_throws_at_codegen` | A plain (non-saga) handler returns `Delete<SomeSaga>` where `SomeSaga : Saga` (not dispatched through a `SagaChain`). Force codegen. | Host build throws the LD4 message (exact wording decided in F3 — placeholder until F3 merges, see §5) naming the saga type and directing the caller to a saga chain or `MarkCompleted()`. **FAILS today** — silently succeeds, upserting into the un-prefixed `someSaga` collection instead of `wolverine_saga_somesaga`, with no `Version` stamping (silent OCC corruption). |
| `storage_action_saga_from_plain_handler_throws_at_codegen` | Same shape via `IStorageAction<SomeSaga>`/`Storage.Delete(saga)` return value instead of `Delete<T>`. | Same throw, from `DetermineStorageActionFrame`. **FAILS today** for the same reason (F1 §1.5/§3.3 confirms both call sites are ungated). |

**Existing-suite regression bar:** `storage_action_compliance.cs` (the upstream `Todo`-based suite,
`Id`-only — must stay green unchanged), `entity_atomicity.cs`, `entity_multinode.cs`.

### F8 — `inbox_batch_atomicity.cs` (new)

Structural model: direct-store tests (`reassign_incoming.cs`, `dead_letter_replay.cs`) — no `IHost`, call `store.Inbox.StoreIncomingAsync(IReadOnlyList<Envelope>)` directly.

| Test | Trigger | Assertion |
|---|---|---|
| `batch_with_one_duplicate_persists_nothing` | Pre-store one envelope (`StoreIncomingAsync(single)`). Batch-store a list of 5: that same envelope (now a duplicate) plus 4 fresh ones. | `DuplicateIncomingEnvelopeException` thrown **and** `store.Admin.AllIncomingAsync()` count is unchanged at 1 (only the pre-stored one) — none of the 4 fresh envelopes persisted. Per LD2's F2-verified caveat, the exception's dupe list may be partial (assert ≥1, not the full list of duplicates — the persistence-count assertion is the load-bearing one, not dupe-list completeness). **FAILS today** — the unordered `InsertManyAsync` commits all 4 fresh docs before the duplicate-key error surfaces (F2/plan's confirmed defect). |
| `all_fresh_batch_persists_all` | Batch-store 5 brand-new envelopes, no duplicates. | All 5 present in `AllIncomingAsync()`. Regression guard — must hold before and after. |
| `single_envelope_store_unaffected` | The existing single-envelope `StoreIncomingAsync(Envelope)` overload, duplicate and fresh cases. | Unchanged behavior — this overload is out of F8's scope per the plan; the test proves it. |

**Existing-suite regression bar:** `inbox.cs`, `inbox_identity.cs`, full suite + `Category=multinode`
(per the plan's explicit call-out that DurableReceiver retries interact with multinode recovery).

### F9 — `dead_letter_edit_replay.cs` (extend existing file's coverage, or new file if the plan's naming is taken literally — confirm against current `dead_letter_replay.cs`/`dead_letters.cs` contents at implementation time)

Structural model: `dead_letter_replay.cs`'s `replay_skips_and_unflags_bodyless_poison_dead_letters` fact — same poison-letter construction, different code path (`EditAndReplayAsync` instead of `ReplayDeadLettersAsync`).

| Test | Trigger | Assertion |
|---|---|---|
| `edit_and_replay_body_less_poison_letter_repro_current_failure` | Insert a `DeadLetterMessage` with `Body = []` (mirrors `ForUnserializableEnvelope`'s empty-body construction, same as the existing poison-letter fact) directly into the DLQ collection. Call `EditAndReplayAsync` with a valid replacement body. | **Written first to confirm the current failure**: today throws `EndOfStreamException` from `EnvelopeSerializer.Deserialize`'s `br.ReadInt64()` on the null/empty stream (F1-adjacent fact from the plan's Verified Facts, §"Dead letters"). This fact should assert the throw *before* the fix lands (a documented red-first step), then be deleted or inverted once F9 implements the guard — see Step wording in the plan's F9 Step 1. |
| `edit_and_replay_body_less_poison_letter_succeeds_after_fix` | Same setup. Call `EditAndReplayAsync` with the new body. | Envelope re-enters the inbox with the edited body; DLQ document removed (mirrors the normal `dead_letter_replay.cs` replay assertions: `AllIncomingAsync` count, `FetchCountsAsync().DeadLetter`). |
| `edit_and_replay_normal_bodied_letter_unaffected` | A DLQ document with a real serialized body (the common case). | Unchanged behavior — proves the empty-body guard doesn't alter the normal path (mirrors `ToEnvelope()`'s existing branch for non-empty `Body`). |

### F10 — `incoming_claims_id_and_destination.cs` (new)

Structural model: `reassign_incoming.cs` (direct `store.ReassignIncomingAsync(...)` call, no `IHost`).

| Test | Trigger | Assertion |
|---|---|---|
| `claim_scopes_to_the_destination_that_was_loaded` | Configure `DurabilitySettings.MessageIdentity = IdAndDestination`. Store two `IncomingMessage` documents sharing one `EnvelopeId` (same logical envelope) but two different `Destination`s (mirrors the plan's Verified Facts on `_inboxIdentity` producing `$"{e.Id}|{destination}"`), both `OwnerId == AnyNode`. Call `ReassignIncomingAsync` for the destination-1 envelope only. | Destination-1's document is claimed (`OwnerId` becomes the claiming node number); destination-2's document is **untouched** (`OwnerId` still `AnyNode`). **FAILS today** — `ReassignIncomingAsync`'s `Filter.In(x => x.EnvelopeId, ids)` matches both documents (same `EnvelopeId`, no destination scoping), claiming destination-2's document too. |
| `orphan_recovery_rereads_only_the_destination_it_claimed` | Same two-destination setup, exercised through `RecoverOrphanedIncomingAsync`'s listener-scoped load + claim + re-read path rather than the direct `ReassignIncomingAsync` call. | The re-read step (`Filter.In(x => x.EnvelopeId, ids) && OwnerId == nodeNumber`) must not certify destination-2's document as "won" by this node — assert only destination-1's envelope is returned/enqueued for recovery. **FAILS today** for the same unscoped-filter reason, one call site later in the pipeline (F1/plan's `MongoDbMessageStore.Durability.cs:154-158` citation). |
| `id_only_mode_unaffected` | Repeat both scenarios with the default `MessageIdentity.IdOnly` (no destination suffix). | Byte-identical behavior to today — `InboxIdentity(e) == e.Id.ToString()` regardless of the `_id`-based claim rewrite, proving `IdOnly` consumers see zero change (plan's explicit no-signature-change guarantee). |

**Existing-suite regression bar:** full suite + `Category=multinode`.

### F11 — `dead_node_release.cs` (new)

Structural model: `dead_node_ownership_release.cs` (direct `store.ReleaseDeadNodeOwnershipAsync(...)` calls — extend this exact pattern to *two* sequential calls for the two-tick semantics).

| Test | Trigger | Assertion |
|---|---|---|
| `owner_with_no_node_doc_is_not_released_on_first_tick_only_second` | Store an incoming envelope owned by node number 999 (no `wolverine_nodes` document for it — crashed). Call `ReleaseDeadNodeOwnershipAsync` **once**, then assert, then call it a **second time**, then assert again. | First call: `OwnerId` for the 999-owned envelope is **still 999** (not yet released — first observation only). Second call: `OwnerId` is now `AnyNode` (0) — released on the confirmed second tick. **FAILS today** — the current single-snapshot implementation releases on the *first* call (the exact race the plan's LD3 fixes). |
| `owner_that_registers_a_node_between_ticks_is_never_released` | Store an envelope owned by node number N with no node doc yet. Call tick 1 (N enters the "dead" set). Before tick 2, persist a live `wolverine_nodes` document for node number N (simulating a node that was mid-registration, per the plan's registration-before-claim soundness argument). Call tick 2. | `OwnerId` remains N after tick 2 — never released, because N is no longer in the *live-minus-dead* computation once its node doc exists. Proves the soundness argument (a number that registers cannot have been in *last* tick's confirmed-dead intersection incorrectly) rather than just asserting the two-tick mechanics abstractly. |
| `anynode_and_live_owners_never_touched_across_two_ticks` | Mirrors the existing `dead_node_ownership_release.cs` fact's live-node and `AnyNode`-owned envelopes, run across two `ReleaseDeadNodeOwnershipAsync` calls. | Both remain untouched after both ticks — regression guard extending the existing single-tick fact. |

**Existing-suite regression bar:** full suite; `Category=multinode` **5× consecutive per TFM** — this
is the plan's explicit bar (release latency grows one recovery interval; `LeadershipElectionCompliance`
and `multinode_end_to_end` exercise the changed method on every Balanced tick without asserting on it
directly, so flakiness would show up as a timeout in those suites, not a direct assertion failure).

### F12 — `durability_agent_shutdown.cs` (new, or extend an existing durability-agent test file if the implementer finds a closer home — confirm current file layout at implementation time)

| Test | Trigger | Assertion |
|---|---|---|
| `stop_async_awaits_the_recovery_and_scheduled_loops` | Start a `MongoDbDurabilityAgent`, let it run briefly, call `StopAsync`. | By the time `StopAsync` returns, both loop tasks are observably completed (via an internal accessor — the plan notes `InternalsVisibleTo` may be needed) — not merely cancelled-but-still-running. **FAILS today** — `SafeDispose` swallows the running-task exception without awaiting completion (F1-adjacent Verified Fact). |
| `stop_async_is_idempotent` | Call `StopAsync` twice in sequence on the same agent. | Second call does not throw (no double-dispose of `_cancellation`/`_combined`), returns promptly. |
| `stop_async_bounded_by_timeout_does_not_hang` | Simulate a stuck loop (if feasible without excessive mocking — otherwise document as inspection-only per the plan's acknowledgment that the residual upstream-ordering window is out of this library's control). | `StopAsync` returns within the ~5s bound rather than hanging indefinitely. |

**Existing-suite regression bar:** full suite + `Category=multinode` (every Balanced multinode test's
teardown calls `StopAsync`).

### F13 — `saga_store_diagnostics.cs` (extend existing file)

Structural model: the file's own existing `DiagSaga`/`StartDiagSaga` pattern — add a second saga type keyed by `Guid` (or int/long) alongside the existing string-keyed `DiagSaga`.

| Test | Trigger | Assertion |
|---|---|---|
| `read_saga_finds_guid_keyed_instance_by_native_guid_identity` | A new `GuidDiagSaga : Saga { public Guid Id; ... }` (or similar), started and persisted. Call `diagnostics.ReadSagaAsync(typeName, guidValue, ...)` handing the **native `Guid`** as the identity argument. | Returns the saga state (regression guard — this already works today per the class doc's "identity used as-is"). |
| `read_saga_finds_guid_keyed_instance_by_string_identity` | Same saga. Call `ReadSagaAsync(typeName, guidValue.ToString(), ...)` — the JSON/URL round-trip scenario the `ISagaStoreDiagnostics` contract explicitly calls out (F1/plan-quoted `ISagaStoreDiagnostics.cs:42-46`: "the implementation is expected to coerce as needed"). | Also returns the saga state — coercion parses the string back to `Guid` before filtering. **FAILS today** — `Filter.Eq("_id", identity)` with a boxed `string` never matches a document whose `_id` is a native `Guid` BSON type; returns null today. |
| `read_saga_int_and_long_keyed_string_identity_coercion` | Analogous facts for `int`- and `long`-keyed sagas (mirrors Marten's three coercion branches, `MartenSagaStoreDiagnostics.cs:147-168`). | Same string→native coercion succeeds. |
| `read_saga_unparseable_string_identity_returns_null_not_throw` | `ReadSagaAsync(typeName, "not-a-guid", ...)` against the Guid-keyed saga. | Returns `null` ("not found" semantics upstream expects), does not throw `FormatException`. |

**Existing-suite regression bar:** all existing string-identity facts in this file unchanged.

### F14 / F15 / F16 / F17 — no new red-first tests (verification-by-inspection or suite-as-oracle, per the plan)

| Task | Verification method | Notes |
|---|---|---|
| F14 (NuGet ranges) | `dotnet pack` + inspect the produced `.nuspec` for `[6.16.0,7.0.0)` / `[3.9.0,4.0.0)`. No xUnit test — the plan's own Expected Output is a build artifact, not a fact. |
| F15 (docs truth sweep) | No test — a prose diff against `main`, verified sentence-by-sentence. |
| F16 (dedup cleanup) | Full suite + multinode green is the oracle (behavior-preserving refactor, plan explicitly says "no new tests"). |
| F17 (efficiency sweep) | Full suite + multinode green for items 2–4. **Item 1 (DLQ replay batching) needs one new focused test**, per the plan's own Expected Output: `replay_batch_falls_back_to_per_letter_handling_when_one_envelope_already_in_inbox` — replay a batch where one letter's envelope id already exists in `wolverine_incoming_envelopes` (simulating the F8-interaction crash window described in Risk R8); assert the batch-level `DuplicateIncomingEnvelopeException` is caught and replay falls back to per-letter handling so the *other* letters in the batch still succeed (mirrors `dead_letter_replay.cs`'s existing `replay_converges_when_incoming_doc_already_exists` fact, but through the newly-batched code path). File: extend `dead_letter_replay.cs` (same fixture, same idempotent-replay theme) rather than a new file. |

---

## 3. F18 demo design — non-`Id` identity-convention coverage

**Goal restated:** prove the Tier-1 fixes (F6+F7) end-to-end through the **packaged** nupkg (the demo
never project-references `src/Wolverine.MongoDB`), and leave a living reference implementation other
consumers can copy.

### 3.1 The entity

**`CustomerFeedback`** — a small, self-contained aggregate with **no `Id` member at all**, forcing the
`{TypeName}Id` convention end-to-end (the identity shape F6/F7 exist to fix). Lives beside
`OrderNote` as a structurally parallel but independent example — explicitly **not** a modification of
`OrderNote` (per the plan's "existing demo types unchanged" instruction).

```csharp
// demo/src/OrderDemo.Infrastructure/Persistence/CustomerFeedback.cs
namespace OrderDemo.Infrastructure.Persistence;

/// <summary>
/// Freeform post-delivery feedback for an order. Persisted via the Wolverine-generated entity
/// frames (Tier 1), same as <see cref="OrderNote"/> — but keyed by <c>CustomerFeedbackId</c>
/// (the <c>{TypeName}Id</c> convention) instead of a member literally named <c>Id</c>, to exercise
/// the non-`Id` identity-convention fix (2026-07-07 review findings F6/F7) through the packaged
/// library rather than only the library's own test project.
/// </summary>
public sealed class CustomerFeedback
{
    // {TypeName}Id convention — no member named Id/id/_id anywhere on this type. Wolverine's
    // SagaChain.DetermineSagaIdMember resolves this via tier 2 ("{TypeName}Id"); the driver's
    // NamedIdMemberConvention would find nothing without the library's EnsureIdMember bridge
    // (see MongoIdentityMapping in the library — this type only works post-F6/F7).
    public Guid CustomerFeedbackId { get; set; }

    public Guid OrderId { get; set; }
    public int Rating { get; set; }          // 1-5
    public string Comment { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset SubmittedAt { get; set; }
}
```

Design notes:
- **`Guid`, not `string`** (unlike `OrderNote.Id`) — deliberately the *harder* case: `OrderNote.cs`'s
  own doc comment explains it chose `string` specifically to dodge a `GuidRepresentation` gotcha with
  the app's globally-registered `GuidSerializer(GuidRepresentation.Standard)` when filtering through
  the driver's boxed-`object` `ObjectSerializer` path. `CustomerFeedback` should **filter through
  strongly-typed lambda builders** (`Builders<CustomerFeedback>.Filter.Eq(x => x.CustomerFeedbackId, id)`
  in the test, never `Filter.Eq("_id", (object)id)`) exactly like `OrderNote.OrderId` already does
  elsewhere in the demo — this sidesteps the gotcha while still proving the Guid-native `{TypeName}Id`
  path end-to-end. Document this choice in the new file's doc comment (drafted above) so it isn't
  mistaken for an oversight.
- **Rating/Comment/SubmittedAt** are filler fields with no bearing on the identity fix — kept minimal.

### 3.2 Handlers

Mirrors `OrderNoteHandler.cs`'s three-verb shape (`Insert`/`[Entity]`+`Update`... but per F5's Task
description, only `[Entity]`-load + `Insert<T>` are required — no `Update`/`Delete` needed to prove
the identity fix, since the failure mode is "insert and load never agree," not "update semantics").
Keeping it to two handlers (Insert + a load-and-read-back handler) is enough surface and matches the
plan's Task F18 scope ("`[Entity]`-load + `Insert<T>` handlers").

```csharp
// demo/src/OrderDemo.Application/Feedback/CustomerFeedbackHandler.cs
namespace OrderDemo.Application.Feedback;

public static class CustomerFeedbackHandler
{
    // Insert<CustomerFeedback> — same DetermineInsertFrame path OrderNoteHandler.Handle(AddOrderNoteCommand)
    // uses, but here the frame must resolve CustomerFeedbackId (not Id) to key the write.
    public static Insert<CustomerFeedback> Handle(SubmitCustomerFeedbackCommand cmd)
        => new(new CustomerFeedback
        {
            CustomerFeedbackId = Guid.NewGuid(),
            OrderId = cmd.OrderId,
            Rating = cmd.Rating,
            Comment = cmd.Comment,
            SubmittedAt = DateTimeOffset.UtcNow
        });

    // [Entity] load keyed by the {TypeName}Id convention — Wolverine resolves "FeedbackId" (the
    // parameter name hint) to the CustomerFeedbackId member via DetermineSagaIdMember tier 2.
    // Proves the READ side of the identity fix; the command itself is a no-op passthrough so the
    // safety-net test can assert the loaded instance is non-null and matches what Insert wrote.
    public static AcknowledgeCustomerFeedbackCommand.Result Handle(
        AcknowledgeCustomerFeedbackCommand cmd, [Entity("FeedbackId")] CustomerFeedback feedback)
        => new(feedback.CustomerFeedbackId, feedback.Comment);
}
```

(Command/result record shapes follow the existing `OrderDemo.Contracts.Commands.Notes` pattern —
`SubmitCustomerFeedbackCommand(Guid OrderId, int Rating, string Comment)`,
`AcknowledgeCustomerFeedbackCommand(Guid FeedbackId)` with a small result record, under a new
`OrderDemo.Contracts.Commands.Feedback` namespace mirroring `Commands.Notes`.)

### 3.3 Safety-net tests

New `demo/tests/OrderDemo.IntegrationTests/CustomerFeedbackFlowTests.cs`, styled exactly like
`OrderNoteFlowTests.cs` (same `[Collection("orders")]` fixture, same direct-Mongo-bypass verification
discipline):

| Test | Mirrors | Assertion |
|---|---|---|
| `Can_Submit_Customer_Feedback` | `Can_Add_Order_Note` | `SubmitCustomerFeedbackCommand` → direct Mongo read on the `customerfeedback` collection (lowercased type name, un-prefixed, per `MongoConstants.EntityCollectionName`) via `Builders<CustomerFeedback>.Filter.Eq(x => x.OrderId, orderId)` finds the document; **critically**, also assert the document's raw `_id` BSON field equals the `CustomerFeedbackId` value (`Filter.Eq("_id", feedback.CustomerFeedbackId)` — strongly-typed `Guid` overload, not boxed — must find the same document). This second assertion is the one that fails pre-F6/F7 (against an un-fixed nupkg) and is the entire point of this demo addition. |
| `Can_Acknowledge_Customer_Feedback_Via_Entity_Load` | `Can_Edit_Order_Note` | Submit, then invoke `AcknowledgeCustomerFeedbackCommand` with the returned `CustomerFeedbackId`; assert the handler's `[Entity("FeedbackId")]` parameter was non-null (via the result record echoing back the loaded `Comment`) — proves the load side resolves the same identity the insert wrote. |
| `Acknowledge_Missing_Feedback_Is_Skipped` | `Edit_Note_NotFound_Is_Skipped` | A random `Guid` with no backing document → handler skipped, no throw (the `[Entity]` `Required=true` default). |

### 3.4 Non-goals for F18 (explicit)

- **No `Update`/`Delete<CustomerFeedback>` handler** — out of scope per the plan's Task F18 wording;
  `OrderNoteHandler` already exercises those verbs for the `Id` convention, and F7's own
  `entity_identity_conventions.cs` (library-side) already proves `{TypeName}Id` load/insert/delete
  round-trips at the unit level. The demo's job is packaged end-to-end proof, not verb coverage.
- **No changes to `OrderNote`, `OrderFulfillmentSaga`, or any existing demo type** — confirmed
  non-negotiable by the plan; `CustomerFeedback` is fully additive.
- **`demo/CLAUDE.md`/`demo/README.md`** get a one-line mention in the Project Layout / Testing
  sections (mirroring how `OrderNoteFlowTests` is already listed) — not a new prose section.

---

## 4. Upstream-contribution candidates

Per **OQ5** (deferred — noted here for F6's PR per the plan's instruction, not acted on now):

- **`saga_identity_conventions.cs` (F6)** and **`entity_identity_conventions.cs` (F7)** are the
  strongest candidates: they are written compliance-style (parameterizable identity shape, direct
  persistence verification) precisely so they could become a `NonIdIdentifiedSagaComplianceSpecs`-
  style addition to `Wolverine.ComplianceTests` — every sibling provider (Marten, EF Core, RavenDb,
  Cosmos) has the *same* latent gap (F1 §1.4 confirms Marten/EF Core sidestep it via independent
  storage metadata rather than proving convention-agreement; RavenDb/Cosmos were not independently
  re-verified here but share Mongo's string/native-id storage shape closely enough to be worth a
  follow-up check). Flag as a `FOLLOWUPS.md` entry in F6's PR, not a blocking dependency.
- **F13's Guid/int/long diagnostics-coercion facts** are a weaker candidate — they test *this
  provider's* `MongoDbSagaStoreDiagnostics`, not shared Wolverine infrastructure, so there's nothing
  to contribute upstream; the *pattern* (mirroring Marten's `coerceIdentity`) is already public
  precedent, not new IP.
- **F8/F10/F11's store-level tests** are Mongo-specific (transaction semantics, `_id`-based claim
  scoping, two-tick release) — not portable to `Wolverine.ComplianceTests` since RDBMS/Marten don't
  share this provider's specific race shapes (RDBMS already solved F8/F11 with single-statement SQL;
  F10's claim scoping is Mongo's own `_inboxIdentity` construction). Not upstream candidates.

---

## 5. Assertions sensitive to F3/F4 — re-check when those PRs merge

This section exists because F3/F4 were still open when this document was written, per this task's
explicit instruction to flag anything sensitive to their decisions:

| Assertion above | Depends on | What to re-check |
|---|---|---|
| F6 `unresolvable_identity_throws_at_codegen_with_actionable_message` | F3 decision 3 (`UpdateSagaFrame` fallback → throw, exact message wording) | Confirm the thrown exception type/message matches F3's binding contract, not this document's placeholder wording. |
| F7 `delete_saga_from_plain_handler_throws_at_codegen` / `storage_action_saga_from_plain_handler_throws_at_codegen` | F3 LD4 (throw vs route; exact exception type/message) | Same — F3 is the source of truth for the exact wording; this document only states *that* a throw occurs. |
| F8 `batch_with_one_duplicate_persists_nothing` | F4 LD2 (Option A transaction vs Option B compensating delete) | If F4 chose Option B (compensating delete) instead of the recommended transaction wrap, the assertion "nothing persisted" still holds (compensating delete also ends at zero fresh docs) but the **mechanism** description in this table (transaction abort) would be wrong — update the "Trigger" framing, not the assertion. |
| F10 both tests | F4 decision 3 (claim-by-`_id` contract) | Confirm `ReassignIncomingAsync`'s exact filter shape matches F4's binding contract before writing the implementation-side test. |
| F11 all three tests | F4 LD3 (two-tick vs per-number recheck — OQ4) | If F4 somehow chose per-number recheck instead of two-tick (against the plan's stated recommendation), rewrite the two-call test shape entirely — the "first tick / second tick" assertion structure is two-tick-specific. |
| F12 all three tests | F4 decision 4 (await/timeout contract, exact bound) | Confirm the timeout value (5s in the plan's sketch) before asserting on it directly; prefer asserting "completed, bounded" over hard-coding the exact duration in the test. |

No other row in §2/§3 depends on F3/F4 — F9, F13, F14, F15, F16, F17, and the entire F18 demo design
are independent of both design gates (F9/F13/F14/F15/F16/F17 per the Task Table's own dependency
column; F18 depends on F5/F6/F7 being *merged*, not on F3/F4's exact wording, since it only exercises
the round-trip behavior, not the exception contracts).
