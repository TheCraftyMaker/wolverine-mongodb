# Task S5: Demo Saga Flow Design + Library/Demo Test Inventory

> **Task:** S5 from `docs/superpowers/plans/2026-06-18-saga-persistence.md`  
> **Branch:** `docs/saga-demo-and-test-inventory`  
> **Status:** ✅ Complete  
> **S4 alignment:** S4 has not yet merged. This document follows the plan's recommended Option B
> (native Guid id + `Saga.Version` OCC). When S4 lands, compare section 10 ("S4 Alignment Items")
> against S4's decisions and note any divergence as a follow-up edit before S12 begins.

---

## Purpose

Produce a complete, concrete design for the demo `OrderFulfillmentSaga` and an exhaustive
inventory of every library + demo test required by the saga persistence plan. Downstream
tasks (S9, S10, S11, S12, S13, S14) should be able to use this document as a spec —
no further design work needed.

---

## 1. Demo Saga: `OrderFulfillmentSaga`

### 1.1 Saga State Shape

```csharp
// demo/src/OrderDemo.Application/Sagas/OrderFulfillmentSaga.cs
using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Events;
using Wolverine;

namespace OrderDemo.Application.Sagas;

/// <summary>
/// Tracks the lifecycle of a placed order through to delivery confirmation.
/// Keyed on OrderId (Guid). Starts on OrderPlacedApplicationEvent, continues
/// on OrderShippedApplicationEvent, completes (and is deleted from MongoDB)
/// when ConfirmDeliveryCommand is processed.
///
/// The generated Wolverine frame opens a MongoDB session, loads this document
/// by _id (= Id = OrderId), runs the handler method, then persists the updated
/// document — all inside the existing TransactionalFrame transaction so that
/// saga state + any cascaded outbox entry commit atomically.
/// </summary>
public sealed class OrderFulfillmentSaga : Saga
{
    // Wolverine saga identity: "Id" is the standard convention member name.
    // Set to evt.OrderId by the Start handler on first insertion.
    // Maps to MongoDB _id via the driver's default Id-member convention.
    public Guid Id { get; set; }

    // Business state
    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }

    // Lifecycle flags — useful for saga-state assertions in tests
    public bool OrderPlaced { get; set; }
    public bool OrderShipped { get; set; }
    public bool DeliveryConfirmed { get; set; }

    // Timestamps
    public DateTimeOffset PlacedAt { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }

    // Inherited from Wolverine.Saga:
    //   public int Version { get; set; }       ← OCC token (S8)
    //   public bool IsCompleted()               ← returns true after MarkCompleted()
    //   protected void MarkCompleted()          ← signals Wolverine to delete the document
}
```

**Notes:**

- `Id` (type `Guid`) is the Wolverine saga identity member. Wolverine's naming convention
  resolves `Id` as the identity for the saga document; no `[SagaIdentity]` or `[BsonId]`
  attribute is required on the saga class itself.
- `Version` is inherited from `Wolverine.Saga`. The OCC-guarded update frame (S8) will filter
  on `(_id, Version)` and increment it on each save; insert (Start) sets `Version = 1`.
- MongoDB collection (per S4's per-saga-type decision):
  `wolverine_saga_order_fulfillment_saga` — derived by camel-to-snake-case from the
  type name. The helper in `MongoConstants` will determine the exact convention; this
  document uses the camel-to-snake form throughout.

### 1.2 Identity and Collection Summary

| Property | Value |
|---|---|
| Saga type | `OrderFulfillmentSaga` |
| Identity member | `Guid Id` (set to `OrderId` by Start handler) |
| Id type | `Guid` (Option B native) |
| MongoDB `_id` | Maps via default `Id` driver convention |
| Collection name | `wolverine_saga_order_fulfillment_saga` (pending S4 naming helper) |
| OCC token | `Saga.Version` (int) — guarded update in S8 |
| Completion | `MarkCompleted()` in `Handle(ConfirmDeliveryCommand)` → document deleted |

---

## 2. Message Contracts

### 2.1 Existing Messages — Reused As-Is for Saga Trigger

| Message | Assembly | Existing role | Saga role | Change required |
|---|---|---|---|---|
| `OrderPlacedApplicationEvent(Guid OrderId, Guid CustomerId, decimal TotalAmount, DateTimeOffset PlacedAt)` | `OrderDemo.Contracts` | Cascaded from `PlaceOrderHandler`; consumed by `OrderSummaryProjector` | **Starts** `OrderFulfillmentSaga` | **None** — start message creates a new saga; no routing attribute needed |
| `OrderShippedApplicationEvent(Guid OrderId, DateTimeOffset ShippedAt)` | `OrderDemo.Contracts` | Cascaded from `ShipOrderHandler`; consumed by `OrderSummaryProjector` | **Continues** `OrderFulfillmentSaga` | **Add `[SagaIdentity]` to `OrderId`** (see section 2.3) |

### 2.2 New Contracts

All new types live in `OrderDemo.Contracts`.

#### `ConfirmDeliveryCommand`
```csharp
// demo/src/OrderDemo.Contracts/Commands/ConfirmDeliveryCommand.cs
using Wolverine.Attributes;

namespace OrderDemo.Contracts.Commands;

/// <summary>
/// Confirms delivery of a shipped order, completing the OrderFulfillmentSaga.
/// OrderId is the saga identity for routing; [SagaIdentity] tells Wolverine
/// to load the saga keyed on this value.
/// </summary>
public sealed record ConfirmDeliveryCommand(
    [property: SagaIdentity] Guid OrderId,
    DateTimeOffset DeliveredAt);
```

#### `FulfillmentCompletedEvent`
```csharp
// demo/src/OrderDemo.Contracts/Events/FulfillmentCompletedEvent.cs
namespace OrderDemo.Contracts.Events;

/// <summary>
/// Application event cascaded from OrderFulfillmentSaga when delivery is confirmed.
/// Routed through the Wolverine outbox (same transaction as the saga completion),
/// demonstrating that the outbox and saga deletion commit atomically.
/// Consumed by a local durable queue in tests; in production this would go to RabbitMQ.
/// </summary>
public sealed record FulfillmentCompletedEvent(Guid OrderId, DateTimeOffset DeliveredAt);
```

#### `FulfillmentTimedOut` (stretch/optional)
```csharp
// demo/src/OrderDemo.Contracts/Events/FulfillmentTimedOut.cs
namespace OrderDemo.Contracts.Events;

/// <summary>
/// STRETCH — scheduled by the saga on start (e.g. 7 days) as a timeout guard.
/// If fired, the saga logs/alerts that the order was never delivered.
/// Exercises saga + scheduled-message + multi-node together (S14 stretch).
/// Defer to FOLLOWUPS if it complicates S12/S13.
/// </summary>
public sealed record FulfillmentTimedOut(Guid OrderId);
```

### 2.3 Contract Modifications Required

#### Add `[SagaIdentity]` to `OrderShippedApplicationEvent.OrderId`

`OrderShippedApplicationEvent` is a `sealed record` in `OrderDemo.Contracts`. Wolverine's
identity convention resolves `[SagaIdentity]` → `{SagaTypeName}Id` → `SagaId` → `Id`.
Since `OrderId` matches none of those, `[SagaIdentity]` must be added.

Current:
```csharp
public sealed record OrderShippedApplicationEvent(Guid OrderId, DateTimeOffset ShippedAt);
```

Required for S12:
```csharp
using Wolverine.Attributes;

public sealed record OrderShippedApplicationEvent(
    [property: SagaIdentity] Guid OrderId,
    DateTimeOffset ShippedAt);
```

**Dependency implication:** This adds `WolverineFx` (or a slim `Wolverine.Attributes`
assembly) to `OrderDemo.Contracts`. `ConfirmDeliveryCommand` (new) already requires this.
Add `WolverineFx` to `OrderDemo.Contracts.csproj` once in S12 (covers both).

> **S4 alignment item OQ-A** — if S4 picks a different identity resolution approach
> (e.g., always use a `SagaId` property convention and let S12 add `SagaId` aliases),
> update this section before S12 begins.

---

## 3. Handler Signatures

Full `OrderFulfillmentSaga` class with handler methods:

```csharp
// demo/src/OrderDemo.Application/Sagas/OrderFulfillmentSaga.cs
using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Events;
using Wolverine;

namespace OrderDemo.Application.Sagas;

public sealed class OrderFulfillmentSaga : Saga
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }
    public bool OrderPlaced { get; set; }
    public bool OrderShipped { get; set; }
    public bool DeliveryConfirmed { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }

    // ── Start ─────────────────────────────────────────────────────────────────
    // Wolverine recognises "Start" or "Starts" as a saga-start method name.
    // Called on a freshly-created OrderFulfillmentSaga instance (Id not yet set).
    // Must assign Id; the frame then inserts the document into MongoDB.
    public void Start(OrderPlacedApplicationEvent evt)
    {
        Id = evt.OrderId;
        CustomerId = evt.CustomerId;
        TotalAmount = evt.TotalAmount;
        PlacedAt = evt.PlacedAt;
        OrderPlaced = true;
    }

    // ── Continue: order shipped ───────────────────────────────────────────────
    // Wolverine resolves the saga Id from OrderShippedApplicationEvent.OrderId
    // (via [SagaIdentity]); loads the document; calls this method; updates MongoDB.
    // Returns FulfillmentShippedEvent to demonstrate outbox-in-saga path.
    // If the saga is not found: Wolverine throws UnknownSagaException.
    public FulfillmentShippedEvent Handle(OrderShippedApplicationEvent evt)
    {
        OrderShipped = true;
        ShippedAt = evt.ShippedAt;
        return new FulfillmentShippedEvent(Id, evt.ShippedAt);
    }

    // ── Complete: delivery confirmed ──────────────────────────────────────────
    // Wolverine resolves Id from ConfirmDeliveryCommand.OrderId ([SagaIdentity]).
    // MarkCompleted() signals Wolverine to delete the saga document from MongoDB.
    // The delete and FulfillmentCompletedEvent outbox entry commit atomically.
    public FulfillmentCompletedEvent Handle(ConfirmDeliveryCommand cmd)
    {
        DeliveryConfirmed = true;
        DeliveredAt = cmd.DeliveredAt;
        MarkCompleted();
        return new FulfillmentCompletedEvent(Id, cmd.DeliveredAt);
    }

    // ── Stretch: timeout guard ────────────────────────────────────────────────
    // Schedule in Start(): Wolverine.ScheduleMessage(new FulfillmentTimedOut(Id), delay);
    // If delivered before timeout, saga is already completed (Wolverine ignores it).
    // public void Handle(FulfillmentTimedOut evt) { /* log/alert */ MarkCompleted(); }
}
```

#### `FulfillmentShippedEvent` (internal cascade, new)
```csharp
// demo/src/OrderDemo.Contracts/Events/FulfillmentShippedEvent.cs
namespace OrderDemo.Contracts.Events;

/// <summary>
/// Cascaded by OrderFulfillmentSaga when an order is shipped.
/// Demonstrates that saga handlers can write to the outbox; consumed by a
/// local durable queue in tests to prove inbox/outbox interaction.
/// </summary>
public sealed record FulfillmentShippedEvent(Guid OrderId, DateTimeOffset ShippedAt);
```

#### Handler signatures summary

| Message | Saga method | Returns | Routing resolved by |
|---|---|---|---|
| `OrderPlacedApplicationEvent` | `Start(OrderPlacedApplicationEvent)` | `void` | New saga — no routing needed |
| `OrderShippedApplicationEvent` | `Handle(OrderShippedApplicationEvent)` | `FulfillmentShippedEvent` | `[SagaIdentity] Guid OrderId` |
| `ConfirmDeliveryCommand` | `Handle(ConfirmDeliveryCommand)` | `FulfillmentCompletedEvent` | `[SagaIdentity] Guid OrderId` |
| `FulfillmentTimedOut` *(stretch)* | `Handle(FulfillmentTimedOut)` | `void` | `[SagaIdentity] Guid OrderId` |

---

## 4. Message Flow

```
HTTP POST /orders  (PlaceOrderCommand)
  └─► PlaceOrderHandler
        └─► returns OrderPlacedApplicationEvent  (cascaded to outbox, durable inbox)
              ├─► OrderSummaryProjector.Handle(...)   [existing — updates read model]
              └─► OrderFulfillmentSaga.Start(...)     [NEW — inserts saga document]

HTTP POST /orders/{id}/ship  (ShipOrderCommand)
  └─► ShipOrderHandler
        └─► returns OrderShippedApplicationEvent  (cascaded to outbox, durable inbox)
              ├─► OrderSummaryProjector.Handle(...)   [existing]
              └─► OrderFulfillmentSaga.Handle(...)    [NEW — updates saga; cascades FulfillmentShippedEvent]

HTTP POST /orders/{id}/confirm-delivery  (ConfirmDeliveryCommand)
  └─► OrderFulfillmentSaga.Handle(...)   [NEW — MarkCompleted() → saga doc deleted; cascades FulfillmentCompletedEvent]
```

**Multi-handler note:** Wolverine supports multiple handlers for the same message type.
Both `OrderSummaryProjector.Handle(OrderPlacedApplicationEvent)` and
`OrderFulfillmentSaga.Start(OrderPlacedApplicationEvent)` run when the event is dispatched.
The saga handler runs inside the generated saga-aware transactional frame (session + OCC);
the projector runs in its own (non-saga) transaction.

---

## 5. Flow → Test Mapping Table

Each row identifies the flow, the concrete trigger/assertion, the test location (library or demo),
and the task that delivers it.

| # | Flow | Trigger | Assert | Test | Task |
|---|---|---|---|---|---|
| F1 | **Start** — saga created on OrderPlaced | `OrderPlacedApplicationEvent` with new `OrderId` | `LoadState<OrderFulfillmentSaga>(orderId)` → not null; `OrderPlaced == true` | `SagaFlowTests.PlaceOrder_StartsSaga` | S13 |
| F2 | **Continue** — saga advanced on OrderShipped | `OrderShippedApplicationEvent` after start | `LoadState` → `OrderShipped == true`, `ShippedAt != null` | `SagaFlowTests.ShipOrder_AdvancesSaga` | S13 |
| F3 | **Complete** — saga deleted on delivery confirmed | `ConfirmDeliveryCommand` after ship | `LoadState` → null (document deleted) | `SagaFlowTests.ConfirmDelivery_CompletesSaga_SagaDocumentDeleted` | S13 |
| F4 | **Missing state** — continue on unknown id | `ConfirmDeliveryCommand` for a `Guid.NewGuid()` never placed | Wolverine throws `UnknownSagaException` | `SagaFlowTests.ConfirmDelivery_UnknownOrder_ThrowsUnknownSagaException` | S13 |
| F4b | **Unknown state — library** | `StringCompleteThree { SagaId = "unknown" }` | `UnknownSagaException` | `string_saga_storage_compliance.unknown_state` | S9 |
| F5 | **Duplicate / repeated message** — inbox idempotency | Re-deliver same `OrderPlacedApplicationEvent` envelope (same `EnvelopeId`) | Saga doc exists once; `OrderPlaced == true`; no duplicate insert | `SagaFlowTests.DuplicateOrderPlacedEnvelope_SagaStartedOnce` | S13 |
| F5b | **Inbox idempotency — library** | Same-envelope re-delivery to `StringBasicWorkflow` | State applied once | `saga_atomicity.SameEnvelope_Redelivered_IsIdempotent` | S11 |
| F6 | **Across restarts** — saga persists across host disposal | Start saga, `await host.DisposeAsync()`, rebuild host on same DB name, send `OrderShippedApplicationEvent` | `OrderShipped == true` on reloaded state | `SagaFlowTests.SagaState_SurvivesHostRestart` | S13 |
| F7 | **Inbox/outbox interaction** — saga cascades event through outbox | `OrderShippedApplicationEvent` → saga returns `FulfillmentShippedEvent` | `FulfillmentShippedEvent` arrives at the local durable queue; saga state updated; both atomic | `SagaFlowTests.SagaHandler_CascadedEvent_FlowsThroughOutbox` | S13 |
| F8 | **Atomicity — saga + outbox** | Saga handler writes state and cascades outbox message; force post-handler exception | Neither the saga document nor the outbox envelope persists (rolled back together) | `saga_atomicity.SagaAndOutbox_RollbackTogether_OnPostHandlerFailure` | S11 |
| F8b | **Atomicity — success path** | Same saga handler without forced exception | Both saga document and cascaded outbox envelope persist | `saga_atomicity.SagaAndOutbox_BothPersist_OnSuccess` | S11 |
| F9 | **OCC conflict** — stale version | Two loads of the same saga; first updates; second attempts stale update | Second update throws `SagaConcurrencyException`; document unchanged | `saga_atomicity.StaleSagaVersion_ThrowsSagaConcurrencyException` | S11 (after S8) |
| F10 | **Single-node compliance — string id** | All `StringIdentifiedSagaComplianceSpecs` facts | All 8 facts green | `string_saga_storage_compliance` (8 facts) | S9 |
| F11 | **Single-node compliance — Guid id** | All `GuidIdentifiedSagaComplianceSpecs` facts | All 8 facts green (+ Guid `_id` round-trip) | `guid_saga_storage_compliance` (8 facts) | S10 |
| F12 | **Single-node compliance — int id** | All `IntIdentifiedSagaComplianceSpecs` facts | All facts green | `int_saga_storage_compliance` | S10 (if in scope) |
| F13 | **Single-node compliance — long id** | All `LongIdentifiedSagaComplianceSpecs` facts | All facts green | `long_saga_storage_compliance` | S10 (if in scope) |
| F14 | **Multi-node — exactly-once saga progression** | Two Balanced hosts; drive `OrderFulfillmentSaga` start + multiple continues across nodes | Saga state advanced exactly once; no double-apply; completion deletes doc | `saga_multinode.CrossNode_SagaAdvancesExactlyOnce` | S14 |
| F15 | **Multi-node — dead-node saga rescue** (stretch) | Crash node 1 mid-saga; node 2 takes over | Saga completes correctly via node 2 | `saga_multinode.DeadNode_SagaRescuedByAliveNode` | S14 (stretch) |

---

## 6. Library Test Inventory

All library tests live in `src/Wolverine.MongoDB.Tests/`.

### 6.1 Compliance Subclasses

#### `MongoDbSagaHost` (S9)

New file: `src/Wolverine.MongoDB.Tests/MongoDbSagaHost.cs`

```csharp
// Mirror CosmosDbSagaHost (external/wolverine/src/Persistence/CosmosDbTests/saga_storage_compliance.cs)
// Key requirements:
//   - Implements ISagaHost
//   - BuildHostAsync<TSaga>():
//       Host.CreateDefaultBuilder().UseWolverine(opts => {
//           opts.Durability.Mode = DurabilityMode.Solo;
//           opts.CodeGeneration.GeneratedCodeOutputPath = ...;  // for codeFor<T>() dumps
//           opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
//           opts.Discovery.IncludeType<TSaga>();
//           opts.Discovery.IncludeAssembly(typeof(StringBasicWorkflow).Assembly);
//           opts.Services.AddSingleton(_fixture.Client);
//           opts.UseMongoDbPersistence(AppFixture.DatabaseName);
//       }).StartAsync()
//       // Then clear the fixture DB before returning:
//       // var store = host.Services.GetRequiredService<IMessageStore>() as MongoDbMessageStore;
//       // await store.Admin.RebuildAsync();   ← drops system + saga collections
//   - LoadState<T>(Guid id): _fixture.Client.GetDatabase(AppFixture.DatabaseName)
//       .GetCollection<T>(MongoConstants.SagaCollectionName(typeof(T)))
//       .Find(Builders<T>.Filter.Eq("_id", id)).FirstOrDefaultAsync()
//   - LoadState<T>(string id): same with string _id
//   - LoadState<T>(int id): same with int _id  [NotSupportedException until S10]
//   - LoadState<T>(long id): same with long _id [NotSupportedException until S10]
```

**Critical:** `RebuildAsync()` in `MongoDbMessageStore.Admin` must drop per-saga-type
collections (e.g. `wolverine_saga_*`), not just the five system collections, or
compliance facts bleed between test runs on the shared `[Collection("mongodb")]` fixture DB.
This is a **mandatory** S6 fix (see plan S4 #2 and `MongoDbMessageStore.Admin.cs:67-78`).

#### `string_saga_storage_compliance` (S9)

New file: `src/Wolverine.MongoDB.Tests/string_saga_storage_compliance.cs`

```csharp
[Collection("mongodb")]
public class string_saga_storage_compliance
    : StringIdentifiedSagaComplianceSpecs<MongoDbSagaHost>;
```

Facts covered (8):
1. `complete` — `FinishItAll` → saga doc null
2. `handle_a_saga_message_with_cascading_messages_passes_along_the_saga_id_in_header`
3. `start_1` — direct start by id
4. `start_2` — wildcard start
5. `straight_up_update_with_the_saga_id_on_the_message`
6. `unknown_state` — throws `UnknownSagaException`
7. `update_expecting_the_saga_id_to_be_on_the_envelope`
8. `update_with_message_that_uses_saga_identity_attributed_property`
9. `update_with_no_saga_id_to_be_on_the_envelope` → `IndeterminateSagaStateIdException`
10. `update_with_no_saga_id_to_be_on_the_envelope_or_message` → `IndeterminateSagaStateIdException`

*(10 facts total in StringIdentifiedSagaComplianceSpecs — recount against the source)*

#### `guid_saga_storage_compliance` (S10)

New file: `src/Wolverine.MongoDB.Tests/guid_saga_storage_compliance.cs`

```csharp
[Collection("mongodb")]
public class guid_saga_storage_compliance
    : GuidIdentifiedSagaComplianceSpecs<MongoDbSagaHost>;
```

Requires: `LoadState<T>(Guid id)` implemented on `MongoDbSagaHost`.
Also requires: `_id` round-trip for `Guid` in BSON — add a focused assertion to
confirm the Guid `_id` stored and retrieved correctly (the demo's `Order._id` is
already Guid and works, so this should just confirm parity).

#### `int_saga_storage_compliance` and `long_saga_storage_compliance` (S10, if in scope)

```csharp
[Collection("mongodb")]
public class int_saga_storage_compliance
    : IntIdentifiedSagaComplianceSpecs<MongoDbSagaHost>;

[Collection("mongodb")]
public class long_saga_storage_compliance
    : LongIdentifiedSagaComplianceSpecs<MongoDbSagaHost>;
```

Defer to FOLLOWUPS if int/long BSON mapping proves non-trivial; Guid + string are the
primary requirement.

### 6.2 Custom Library Tests: `saga_atomicity.cs` (S11)

New file: `src/Wolverine.MongoDB.Tests/saga_atomicity.cs`

All tests are `[Collection("mongodb")]`, use `AppFixture`, and a small inline saga type
defined in the test file (so they don't depend on the demo project):

```csharp
// Inline test saga (defined inside the test file, not exported)
public class AtomicityTestSaga : Saga
{
    public Guid Id { get; set; }
    public int HandleCount { get; set; }

    public AtomicityTestMessage Handle(AtomicityTestMessage msg)
    {
        Id = msg.SagaId;
        HandleCount++;
        // The test can configure this saga to throw after state mutation
        // to prove rollback. For that, a separate "ThrowingSaga" subtype is used.
        return new AtomicityTestMessage(msg.SagaId); // cascade
    }

    public void Handle(FinishAtomicityTestSaga msg) => MarkCompleted();
}

public record AtomicityTestMessage([property: SagaIdentity] Guid SagaId);
public record FinishAtomicityTestSaga([property: SagaIdentity] Guid SagaId);
```

#### Test list

| Test method | Assertion | Depends on |
|---|---|---|
| `SagaAndOutbox_BothPersist_OnSuccess` | After a successful saga+outbox handler, saga doc exists AND outbox envelope arrived at the durable queue | S6 |
| `SagaAndOutbox_RollbackTogether_OnPostHandlerFailure` | When the handler succeeds but a forced post-handler failure occurs before commit, neither the saga doc nor the outbox envelope persists | S6 |
| `SagaCompletion_DeletesSagaDocument` | After `MarkCompleted()`, `LoadState` returns null | S6 |
| `SameEnvelope_Redelivered_IsIdempotent` | Re-deliver the identical envelope (same `EnvelopeId`) twice; saga `HandleCount == 1` (inbox dedup); no double-apply | S6 |
| `StaleSagaVersion_ThrowsSagaConcurrencyException` | Load saga twice in the same test scope; first update succeeds (version incremented); second update with the stale snapshot throws `SagaConcurrencyException` | S8 |

**Atomicity test strategy** (mirrors `OutboxAtomicityTests` in the demo):
- Success path: `host.TrackActivity().InvokeMessageAndWaitAsync(startMsg)` → assert saga doc
  + a tracked `AtomicityTestMessage` reached the durable queue.
- Rollback path: use a `ThrowingSaga` that writes saga state then throws before the commit
  frame; assert `LoadState` → null AND outbox collection has no record for the test message.
- Idempotency: use `host.InvokeMessageAndWaitAsync` with the same `Envelope` (manually
  create one with a fixed `Id`); the durable inbox's unique-`_id` constraint stops the
  second delivery; `HandleCount` stays `1`.
- OCC: build two host-scoped `IMessageBus` instances against the same DB; invoke a
  start on node-1; simultaneously invoke an update on node-2 with the pre-start-version
  snapshot; assert `SagaConcurrencyException`.

### 6.3 Multinode Library Test: `saga_multinode.cs` (S14)

New file: `src/Wolverine.MongoDB.Tests/saga_multinode.cs`

```csharp
[Trait("Category", "multinode")]
[Collection("mongodb")]
public class saga_multinode
{
    // Two in-proc Balanced hosts share one MongoDB — mirrors multinode_end_to_end.cs.
    // Uses a static shared counter (same pattern as MultinodeCounterHandler) to detect
    // double-application across both nodes.
    //
    // StartNode() helper: same as multinode_end_to_end.StartNode() but also includes:
    //   opts.Discovery.IncludeType<MultinodesTestSaga>();
    //
    // MultinodesTestSaga:
    //   - Guid Id; int ApplyCount; Start(StartSagaMsg); Handle(AdvanceSagaMsg); Handle(CompleteSagaMsg → MarkCompleted())
    //   - Each Handle increments a shared static counter in the test class
    //
    // Five-in-a-row green bar on both net9.0 + net10.0 before merging (S14 rule).
    // R11 applies: if S8 OCC is in, the retry policy for SagaConcurrencyException must be
    // wired so that the losing node retries rather than failing (that is correct production
    // behavior; do NOT weaken the assertion — see plan R11 and the retry-policy prerequisite
    // noted before S14).
}
```

#### Tests in `saga_multinode`

| Test method | Scenario | Assertion |
|---|---|---|
| `CrossNode_SagaAdvancesExactlyOnce` | Start saga on node 1; send multiple advance messages alternating nodes | `ApplyCount == N` (N messages sent); saga doc updated exactly N times; no double-apply |
| `CrossNode_SagaCompletes_DocumentDeleted` | Full start → advance → complete across two nodes | After completion, `LoadState` → null on both nodes |
| `DeadNode_SagaRescuedByAliveNode` *(stretch)* | Start + advance on node 1; stop node 1; send complete on node 2 | Saga completes correctly; doc deleted |

---

## 7. Demo Test Inventory: `SagaFlowTests.cs` (S13)

New file: `demo/tests/OrderDemo.IntegrationTests/SagaFlowTests.cs`

Uses `OrdersFixture` (same Testcontainers harness as existing tests). Each test calls
`OrdersFixture.CreateDatabaseName()` for isolation; `CreateHostAsync(db)` includes the
new saga-assembly discovery and `FulfillmentCompletedEvent` + `FulfillmentShippedEvent`
local queue routing.

The `CreateHostAsync` method in `OrdersFixture` will need a small addition for S13:
```csharp
// Add to CreateHostAsync:
opts.LocalQueueFor<FulfillmentCompletedEvent>().UseDurableInbox();
opts.LocalQueueFor<FulfillmentShippedEvent>().UseDurableInbox();
```

The demo also needs a `POST /orders/{id}/confirm-delivery` endpoint for the full live path,
though tests can invoke the command directly via `IMessageBus`.

### Test list

| Method | Flow | Key steps | Assertions |
|---|---|---|---|
| `PlaceOrder_StartsSaga` | F1: Start | `InvokeMessageAndWaitAsync(PlaceOrderCommand)` | `LoadState` → not null; `OrderPlaced == true`; `Id == orderId`; `CustomerId` correct |
| `ShipOrder_AdvancesSaga` | F2: Continue | Place → Ship | `OrderShipped == true`; `ShippedAt != null` |
| `ConfirmDelivery_CompletesSaga_SagaDocumentDeleted` | F3: Complete | Place → Ship → `InvokeMessageAndWaitAsync(ConfirmDeliveryCommand)` | `LoadState` → null; `FulfillmentCompletedEvent` arrived in local queue (proves outbox) |
| `ConfirmDelivery_UnknownOrder_ThrowsUnknownSagaException` | F4: Missing state | `InvokeAsync(new ConfirmDeliveryCommand(Guid.NewGuid(), now))` (no prior place) | Throws `UnknownSagaException` (via `act.Should().ThrowAsync<UnknownSagaException>()`) |
| `DuplicateOrderPlacedEnvelope_SagaStartedOnce` | F5: Duplicate/idempotency | Send `OrderPlacedApplicationEvent` twice with the same `Envelope.Id` (re-deliver same envelope via the durable inbox) | Saga doc exists once; `HandleCount` equivalent is `1`; no `DuplicateKeyException` surfaced |
| `SagaState_SurvivesHostRestart` | F6: Across restarts | Place → `DisposeAsync` host → `CreateHostAsync(same db)` → Ship | Reloaded host can advance the saga; `OrderShipped == true` |
| `SagaHandler_CascadedEvent_FlowsThroughOutbox` | F7: Inbox/outbox | Place → Ship (which cascades `FulfillmentShippedEvent`) | `FulfillmentShippedEvent` arrives at durable local queue; `OrderSummaryProjector` also received `OrderShippedApplicationEvent` (read model updated); both atomic with saga state |
| `SagaAndExistingOrderFlow_NoRegression` | Regression | Full place → ship → cancel flow (no saga interaction) | All existing `OutboxAtomicityTests` flows still pass; saga collection untouched for orders that don't complete delivery |

### Helper: `LoadSagaState<T>(IMongoDatabase, Guid)`

Add a private helper (or extend `OrdersFixture`) to read the saga doc directly:

```csharp
private static Task<OrderFulfillmentSaga?> LoadSagaStateAsync(IMongoDatabase db, Guid orderId)
    => db.GetCollection<OrderFulfillmentSaga>("wolverine_saga_order_fulfillment_saga")
         .Find(Builders<OrderFulfillmentSaga>.Filter.Eq(s => s.Id, orderId))
         .FirstOrDefaultAsync()!;
```

This reads directly from MongoDB (bypasses Wolverine) to independently verify persistence —
mirrors the pattern used by `ISagaHost.LoadState<T>` in the compliance suite.

---

## 8. New HTTP Endpoint for Demo (S12)

To make the demo runnable end-to-end (not just via tests):

```csharp
// In OrdersEndpoints.MapOrderEndpoints():
orders.MapPost("/{id:guid}/confirm-delivery", async (Guid id, ConfirmBody body, IMessageBus bus) =>
{
    await bus.InvokeAsync(new ConfirmDeliveryCommand(id, body.DeliveredAt));
    return Results.Accepted();
})
.WithSummary("Confirm delivery of a shipped order (completes the OrderFulfillmentSaga)");

// record ConfirmBody(DateTimeOffset DeliveredAt);
```

---

## 9. Demo Wiring Changes (S12)

Summary of all changes needed in S12:

| File | Change |
|---|---|
| `OrderDemo.Contracts.csproj` | Add `WolverineFx` package reference (for `[SagaIdentity]` attribute) |
| `OrderDemo.Contracts/Commands/ConfirmDeliveryCommand.cs` | New record with `[SagaIdentity] Guid OrderId` |
| `OrderDemo.Contracts/Events/FulfillmentCompletedEvent.cs` | New record |
| `OrderDemo.Contracts/Events/FulfillmentShippedEvent.cs` | New record |
| `OrderDemo.Contracts/Events/OrderShippedApplicationEvent.cs` | Add `[property: SagaIdentity]` to `OrderId` |
| `OrderDemo.Application/Sagas/OrderFulfillmentSaga.cs` | New saga class |
| `OrderDemo.Api/Endpoints/OrdersEndpoints.cs` | Add `POST /orders/{id}/confirm-delivery` endpoint |
| `OrderDemo.Api/Program.cs` | Add `opts.LocalQueueFor<FulfillmentCompletedEvent>().UseDurableInbox()` and `FulfillmentShippedEvent` equivalent; `opts.PublishMessage<FulfillmentCompletedEvent>().ToRabbitExchange(appEventsExchange)` for production path |
| `OrderDemo.IntegrationTests/OrdersFixture.cs` | Add local durable queues for the two new events |
| `OrderDemo.IntegrationTests/SagaFlowTests.cs` | New test class (S13) |

**No changes required** to existing command handlers (`PlaceOrderHandler`, `ShipOrderHandler`),
the `OrderSummaryProjector`, or the `OrderRepository`. The saga runs independently alongside
the existing flows.

---

## 10. S4 Alignment Items

The following decisions are flagged for alignment once `docs/saga-document-model-design.md` (S4) lands.
If S4's choices match this document, no update is needed. If they differ, update the relevant section
before handing off to S12.

| # | This document assumes | S4 may decide differently |
|---|---|---|
| OQ-A | Saga routing: add `[SagaIdentity]` to `OrderShippedApplicationEvent.OrderId`; add `WolverineFx` dep to Contracts | S4 may prefer `SagaId` convention alias or a different routing approach |
| OQ-B | Collection name: `wolverine_saga_order_fulfillment_saga` (camel-to-snake of type name) | S4 defines the naming helper; update collection name here and in test helpers |
| OQ-C | Option B (native Guid id + `Saga.Version` OCC) | If S4 picks Option A (string-only), remove Guid compliance rows from the test table and note the `Id` type change |
| OQ-D | Insert sets `Version = 1`; update uses `(id, oldVersion)` guard; delete unguarded | S4/S8 may choose a different initial version or guarded delete |
| OQ-E | `FulfillmentShippedEvent` returned from the shipped handler | S4 may prefer a simpler `void` handler; drop the event if not needed for the outbox test |
| OQ-F | int/long compliance in scope (S10) | If S4 defers, remove F12/F13 rows |

---

## 11. File Creation Checklist for Downstream Tasks

| Task | File to create | Notes |
|---|---|---|
| S9 | `src/Wolverine.MongoDB.Tests/MongoDbSagaHost.cs` | `ISagaHost` implementation |
| S9 | `src/Wolverine.MongoDB.Tests/string_saga_storage_compliance.cs` | 10 compliance facts |
| S10 | `src/Wolverine.MongoDB.Tests/guid_saga_storage_compliance.cs` | 8+ Guid compliance facts |
| S10 | `src/Wolverine.MongoDB.Tests/int_saga_storage_compliance.cs` | If in scope |
| S10 | `src/Wolverine.MongoDB.Tests/long_saga_storage_compliance.cs` | If in scope |
| S11 | `src/Wolverine.MongoDB.Tests/saga_atomicity.cs` | 5 custom tests (4 after S6, +OCC after S8) |
| S12 | `demo/src/OrderDemo.Contracts/Commands/ConfirmDeliveryCommand.cs` | New contract |
| S12 | `demo/src/OrderDemo.Contracts/Events/FulfillmentCompletedEvent.cs` | New event |
| S12 | `demo/src/OrderDemo.Contracts/Events/FulfillmentShippedEvent.cs` | New event |
| S12 | `demo/src/OrderDemo.Application/Sagas/OrderFulfillmentSaga.cs` | New saga class |
| S13 | `demo/tests/OrderDemo.IntegrationTests/SagaFlowTests.cs` | 8 integration tests |
| S14 | `src/Wolverine.MongoDB.Tests/saga_multinode.cs` | `[Category=multinode]` tests |
