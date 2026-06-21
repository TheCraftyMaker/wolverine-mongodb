# D5: Demo Flow Design + Cross-Tier Test Inventory

> **Task:** D5 from `docs/superpowers/plans/2026-06-21-persistence-suite-completion.md`  
> **Branch:** `docs/demo-and-test-inventory`  
> **Status:** ✅ Complete (design-only — no code changes)  
> **D6 status:** Not yet merged. Collection naming follows the plan's LD3 recommendation
> (`type.Name.ToLowerInvariant()`, un-prefixed). Refine section 5 once D6 lands and
> confirms or overrides that choice.

---

## Purpose

Produce a concrete, code-level design for every demo addition this plan introduces, and a
complete flow→test inventory mapping each capability to the library test (or compliance
subclass) and/or demo test that covers it. Downstream tasks T1.3, T4.1, T4.2 use this
document as their specification; T1.1 uses section 5 (collection naming) as an input to D6.

---

## 1. Tier-1 Demo: `OrderNote` Entity (`[Entity]` + `IStorageAction<T>`)

### 1.1 Motivation

The existing demo uses the **repository + `IClientSessionHandle`** pattern. The `OrderNote`
entity showcases the new `[Entity]`/`IStorageAction<T>` surface side-by-side with the
existing approach — demonstrating that both patterns coexist in the same Wolverine host and
commit atomically with the outbox.

`OrderNote` is chosen over `CustomerProfile` because:
- it naturally belongs to an order (Guid `OrderId` foreign key), so it fits the existing domain;
- its lifecycle has insert, update, and delete operations that each showcase a distinct return type;
- a text note has no domain invariants that would require a rich aggregate, keeping the handler trivially simple.

### 1.2 Entity Shape

```csharp
// demo/src/OrderDemo.Domain/Notes/OrderNote.cs
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderDemo.Domain.Notes;

/// <summary>
/// A freeform note attached to an order. Persisted as a MongoDB document in the
/// <c>ordernote</c> collection via the Wolverine-generated entity frames (Tier 1).
/// No repository, no manual session — <see cref="Insert{T}"/>/<see cref="Update{T}"/>
/// /<see cref="Delete{T}"/> return values drive the write through the TransactionalFrame.
/// </summary>
public sealed class OrderNote
{
    // _id via the driver's default Id-member convention. No [BsonId] needed.
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset CreatedAt { get; set; }

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

**Identity:** `Guid Id` — maps to `_id` via the driver's default `Id`-member convention.
No attribute required. This exercises the same Guid id path the saga uses, but without
`Saga.Version` OCC (entity writes are plain upserts — see LD2).

### 1.3 Commands

```csharp
// demo/src/OrderDemo.Contracts/Commands/Notes/AddOrderNoteCommand.cs
namespace OrderDemo.Contracts.Commands.Notes;

/// <summary>Adds a new note to an order. The handler creates the entity and returns
/// Insert&lt;OrderNote&gt; — no pre-existing document required.</summary>
public sealed record AddOrderNoteCommand(Guid OrderId, string Text, string Author);

// demo/src/OrderDemo.Contracts/Commands/Notes/EditOrderNoteCommand.cs
namespace OrderDemo.Contracts.Commands.Notes;

/// <summary>Edits an existing note. Wolverine loads the entity via [Entity("NoteId")]
/// before invoking the handler. Returns Update&lt;OrderNote&gt; to persist changes.</summary>
public sealed record EditOrderNoteCommand(Guid NoteId, string NewText);

// demo/src/OrderDemo.Contracts/Commands/Notes/DeleteOrderNoteCommand.cs
namespace OrderDemo.Contracts.Commands.Notes;

/// <summary>Deletes a note. Wolverine loads the entity via [Entity("NoteId")] and the
/// handler returns Delete&lt;OrderNote&gt;.</summary>
public sealed record DeleteOrderNoteCommand(Guid NoteId);
```

**Id resolution:** `[Entity("NoteId")]` binds the `NoteId` property on the command to the
`Id` member of `OrderNote`. This is the explicit `EntityAttribute` constructor arg
(`EntityAttribute.cs` optional `string` param — "names the id member on the message").
Using an explicit binding avoids requiring the command property to be named `OrderNoteId`
(which is the auto-convention fallback).

### 1.4 Handler

```csharp
// demo/src/OrderDemo.Application/Notes/OrderNoteHandler.cs
using OrderDemo.Contracts.Commands.Notes;
using OrderDemo.Domain.Notes;
using Wolverine;
using Wolverine.Persistence;

namespace OrderDemo.Application.Notes;

/// <summary>
/// Demonstrates the Tier-1 generic entity persistence surface:
///   Insert&lt;T&gt;  — create a new entity (no prior document required)
///   Update&lt;T&gt;  — mutate a loaded entity (entity loaded via [Entity])
///   Delete&lt;T&gt;  — remove a loaded entity (entity loaded via [Entity])
///
/// Wolverine's generated frame opens the TransactionalFrame session, runs the handler,
/// then persists the returned storage action inside the same transaction — the entity
/// write and any cascaded outbox entries commit atomically. No manual session needed.
/// </summary>
public static class OrderNoteHandler
{
    // ── Insert ────────────────────────────────────────────────────────────────
    // Returning Insert<OrderNote> causes Wolverine to call DetermineInsertFrame:
    // the entity is upserted into the "ordernote" collection inside the transaction.
    public static Insert<OrderNote> Handle(AddOrderNoteCommand cmd)
        => Insert.For(new OrderNote
        {
            Id = Guid.NewGuid(),
            OrderId = cmd.OrderId,
            Text = cmd.Text,
            Author = cmd.Author,
            CreatedAt = DateTimeOffset.UtcNow
        });

    // ── Update ────────────────────────────────────────────────────────────────
    // [Entity("NoteId")] tells Wolverine to load the OrderNote whose _id == cmd.NoteId.
    // If not found (Required=true by default) the handler is skipped (404 behaviour).
    // The loaded note is mutated in-place; returning Update<OrderNote> persists it.
    public static Update<OrderNote> Handle(EditOrderNoteCommand cmd, [Entity("NoteId")] OrderNote note)
    {
        note.Text = cmd.NewText;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        return Update.For(note);
    }

    // ── Delete ────────────────────────────────────────────────────────────────
    // [Entity("NoteId")] loads the note; returning Delete<OrderNote> removes it.
    public static Delete<OrderNote> Handle(DeleteOrderNoteCommand cmd, [Entity("NoteId")] OrderNote note)
        => Delete.For(note);
}
```

**Notes:**
- `Insert.For(entity)` / `Update.For(entity)` / `Delete.For(entity)` are the static factory
  helpers on the concrete Wolverine types. The exact factory API will be confirmed in T1.1
  against the submodule source; adapt if the constructors take the entity directly
  (`new Insert<OrderNote>(entity)`).
- No `IClientSessionHandle` parameter — the generated `TransactionalFrame` resolves it. This
  is the key contrast with the repository-based handlers (`PlaceOrderHandler`, etc.) that
  accept `IClientSessionHandle` explicitly.
- `AutoApplyTransactions()` in the fixture/program wires the transactional frame automatically.
- The handler is intentionally `static` to match Wolverine convention and remove any DI
  dependency — demonstrating that a purely functional entity handler needs zero infrastructure.

### 1.5 Discovery + Routing

```csharp
// In OrdersFixture.CreateHostAsync (and Program.cs):

// Include the new Application.Notes handlers in discovery
opts.Discovery.IncludeAssembly(typeof(PlaceOrderHandler).Assembly); // already includes Application.*

// Route note commands through a durable queue so the outbox path is exercised.
// NoteId-bearing commands don't need their own event route — they are fire-and-forget
// commands invoked inline in tests (not via the outbox fan-out path).
// No LocalQueueFor<T>().UseDurableInbox() needed unless the commands flow through RabbitMQ.
// (For demo API: POST /orders/{id}/notes → sends AddOrderNoteCommand inline.)
```

No new routing entries are needed in `OrdersFixture` for the `AddOrderNoteCommand`/
`EditOrderNoteCommand`/`DeleteOrderNoteCommand` — these are invoked via `TrackActivity()
.InvokeMessageAndWaitAsync(...)` directly from tests (fire-and-return). The entity write
still exercises the full outbox transaction because `AutoApplyTransactions()` wraps the
handler and `UseDurableInbox()` on the overall host confirms commit.

### 1.6 Demo Test File and Assertions

```
demo/tests/OrderDemo.IntegrationTests/OrderNoteFlowTests.cs
```

```csharp
// [Collection("orders")] — shares OrdersFixture, one DB per test.
// Reads the "ordernote" collection directly to verify persistence.
private const string NoteCollection = "ordernote"; // MongoConstants.EntityCollectionName(typeof(OrderNote))

private static Task<OrderNote?> LoadNoteAsync(IMongoDatabase db, Guid id) => ...
```

Scenarios:

| Test method | What it proves |
|---|---|
| `can_add_order_note` | `Insert<OrderNote>` persists to `ordernote`; entity doc readable |
| `can_edit_order_note` | `Update<OrderNote>` overwrites the doc; `UpdatedAt` set |
| `can_delete_order_note` | `Delete<OrderNote>` removes the doc |
| `edit_note_not_found_is_skipped` | `Required=true` — handler not executed when entity missing |
| `delete_note_not_found_is_skipped` | same, for delete |
| `add_note_commits_atomically_with_outbox` | entity write + outbox entry committed together (force rollback → neither persists) — optional, light version of `entity_atomicity.cs` |

The last test is **optional in T1.3** (the full rollback proof is in `entity_atomicity.cs`);
a simpler "entity persists AND at least one outbox envelope was committed" assertion is sufficient
for the demo safety-net.

---

## 2. Tier-4 Demo: `MongoDbUnitOfWork` Example Handler

### 2.1 Motivation

`FOLLOWUPS.md` records "demo has no `MongoDbUnitOfWork` example." This handler closes that
gap and shows the UoW surface as an alternative to the repository + `IClientSessionHandle`
pattern for handlers that write to arbitrary collections directly.

`RecordOrderAuditCommand` is chosen because:
- it is additive (insert-only), so it cannot conflict with existing order state;
- audit logging is a canonical UoW use case (write a document directly, no domain aggregate);
- it demonstrates the collection-name-as-string flexibility of `MongoDbUnitOfWork.Collection<T>(name)`.

### 2.2 Entity + Command

```csharp
// demo/src/OrderDemo.Domain/Audit/OrderAuditEntry.cs
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderDemo.Domain.Audit;

public sealed class OrderAuditEntry
{
    public Guid Id { get; set; }          // generated by the handler
    public Guid OrderId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset OccurredAt { get; set; }
}

// demo/src/OrderDemo.Contracts/Commands/Audit/RecordOrderAuditCommand.cs
namespace OrderDemo.Contracts.Commands.Audit;

public sealed record RecordOrderAuditCommand(Guid OrderId, string Action, string PerformedBy);
```

### 2.3 Handler

```csharp
// demo/src/OrderDemo.Application/Audit/RecordOrderAuditHandler.cs
using MongoDB.Driver;
using OrderDemo.Contracts.Commands.Audit;
using OrderDemo.Domain.Audit;
using Wolverine.MongoDB;

namespace OrderDemo.Application.Audit;

/// <summary>
/// Demonstrates <see cref="MongoDbUnitOfWork"/> as the recommended write surface for
/// handlers that write directly to a collection without a repository layer.
///
/// MongoDbUnitOfWork is injected by Wolverine's generated frame (constructed from the
/// open IClientSessionHandle). Every write through Collection&lt;T&gt;(name) automatically
/// participates in the TransactionalFrame transaction — it is impossible to forget the session.
/// </summary>
public static class RecordOrderAuditHandler
{
    public static async Task Handle(
        RecordOrderAuditCommand cmd,
        MongoDbUnitOfWork uow,
        CancellationToken ct)
    {
        var entry = new OrderAuditEntry
        {
            Id = Guid.NewGuid(),
            OrderId = cmd.OrderId,
            Action = cmd.Action,
            PerformedBy = cmd.PerformedBy,
            OccurredAt = DateTimeOffset.UtcNow
        };

        // Collection<T>(name) returns a session-bound write surface — the session is
        // threaded automatically so the write commits with the outbox in one transaction.
        await uow.Collection<OrderAuditEntry>("order_audit_entries").InsertOneAsync(entry, ct);
    }
}
```

**Key contrasts with existing handlers:**
- No `IClientSessionHandle` parameter — the UoW wraps it
- No repository interface — writes directly to a named collection
- No domain aggregate — purely infrastructure-facing logic

The `"order_audit_entries"` collection name is specified by the caller (not derived from the
type name) — demonstrating the `Collection<T>(name)` flexibility. The UoW does NOT use the
entity-collection naming convention; it is a free-form write surface.

### 2.4 Discovery + Routing

```csharp
// In OrdersFixture.CreateHostAsync and Program.cs:

// opts.Discovery.IncludeAssembly(typeof(PlaceOrderHandler).Assembly) already includes
// Application.Audit.RecordOrderAuditHandler — no new discovery entry needed.

// RecordOrderAuditCommand is an inline command (invoked directly by the test/API).
// No LocalQueueFor<T> route needed unless the command flows through RabbitMQ.
```

### 2.5 Demo Test

```
demo/tests/OrderDemo.IntegrationTests/OrderAuditTests.cs
```

| Test method | What it proves |
|---|---|
| `can_record_order_audit_entry` | audit doc appears in `order_audit_entries` collection |
| `audit_write_commits_atomically_with_outbox` | force post-write throw → neither audit doc nor outbox envelope persists (proves session enlistment) |

For the rollback test: add a middleware that throws after the UoW write but before commit,
using the same deterministic pattern as `saga_atomicity.cs` (an in-test fake message that
a marker handler inspects to conditionally throw).

---

## 3. Tier-4 Demo: Fulfillment Read-Model Projector (Saga Cascade Consumer)

### 3.1 Motivation

`FOLLOWUPS.md` records "demo saga cascade events have no consumer." The `OrderFulfillmentSaga`
cascades `FulfillmentShippedEvent` and `FulfillmentCompletedEvent` from its handlers, but
nothing consumes them. This projector closes that gap, demonstrating the full path:

```
saga handler → outbox entry → local durable queue → projector → read model
```

The `FulfillmentDeliveryStatus` read model records shipping + delivery timestamps that
`OrderSummary` does not track (orthogonal data — no duplication, no conflict).

### 3.2 Read Model Shape

```csharp
// demo/src/OrderDemo.Infrastructure/Persistence/FulfillmentDeliveryStatus.cs
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderDemo.Infrastructure.Persistence;

/// <summary>
/// Read model projected from <see cref="FulfillmentShippedEvent"/> and
/// <see cref="FulfillmentCompletedEvent"/> saga cascade events.
/// Tracks the delivery timeline orthogonally to <see cref="OrderSummary"/>.
/// </summary>
public sealed class FulfillmentDeliveryStatus
{
    // OrderId is the _id so Find-by-OrderId is a primary key lookup.
    public Guid OrderId { get; set; }

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset ShippedAt { get; set; }

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset? DeliveredAt { get; set; }

    public string Status { get; set; } = "Shipped";  // "Shipped" | "Delivered"
}
```

**Collection name:** `fulfillment_delivery_statuses`
(caller-defined, not entity-framework; analogous to `order_summaries`).

### 3.3 Projector Handler

```csharp
// demo/src/OrderDemo.Infrastructure/Projectors/FulfillmentStatusProjector.cs
using OrderDemo.Contracts.Events;
using OrderDemo.Infrastructure.Persistence;
using MongoDB.Driver;

namespace OrderDemo.Infrastructure.Projectors;

/// <summary>
/// Projects FulfillmentShippedEvent and FulfillmentCompletedEvent (cascaded from
/// OrderFulfillmentSaga) onto the FulfillmentDeliveryStatus read model.
///
/// Runs on a durable local queue (with inbox persistence) so the projection survives
/// process crashes between saga commit and handler invocation.
/// Uses $setOnInsert / absolute assignment for idempotent handling.
/// </summary>
[WolverineHandler]
public static class FulfillmentStatusProjector
{
    private const string Collection = "fulfillment_delivery_statuses";

    public static async Task Handle(
        FulfillmentShippedEvent evt,
        IMongoDatabase db,
        CancellationToken ct)
    {
        var collection = db.GetCollection<FulfillmentDeliveryStatus>(Collection);
        var filter = Builders<FulfillmentDeliveryStatus>.Filter.Eq(s => s.OrderId, evt.OrderId);
        var update = Builders<FulfillmentDeliveryStatus>.Update
            .SetOnInsert(s => s.OrderId, evt.OrderId)
            .Set(s => s.ShippedAt, evt.ShippedAt)
            .Set(s => s.Status, "Shipped");
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public static async Task Handle(
        FulfillmentCompletedEvent evt,
        IMongoDatabase db,
        CancellationToken ct)
    {
        var collection = db.GetCollection<FulfillmentDeliveryStatus>(Collection);
        var filter = Builders<FulfillmentDeliveryStatus>.Filter.Eq(s => s.OrderId, evt.OrderId);
        var update = Builders<FulfillmentDeliveryStatus>.Update
            .Set(s => s.DeliveredAt, evt.DeliveredAt)
            .Set(s => s.Status, "Delivered");
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }
}
```

**Notes:**
- `IMongoDatabase db` — **not** `MongoDbUnitOfWork`; the projector runs outside the saga's
  transaction (it receives the cascade event from the outbox queue). The inbox-delivery path
  has its own transaction; projector writes are fine outside the saga session.
- `$setOnInsert` for `ShippedAt` on FulfillmentShippedEvent prevents a late/retried event
  from overwriting a `Delivered` status with an earlier `ShippedAt` (idempotent upsert).
- The `FulfillmentCompletedEvent` handler uses `IsUpsert = true` defensively; it should
  always find the shipped record first, but idempotency is guaranteed either way.
- `[WolverineHandler]` attribute is required (matching `OrderSummaryProjector`) because the
  class lives in the Infrastructure assembly and is not a conventional `*Handler`.

### 3.4 Queue Routes + OrdersFixture Changes

```csharp
// In OrdersFixture.CreateHostAsync — add routes for the saga cascade events:

opts.LocalQueueFor<Contracts.Events.FulfillmentShippedEvent>().UseDurableInbox();
opts.LocalQueueFor<Contracts.Events.FulfillmentCompletedEvent>().UseDurableInbox();
```

These entries are also needed in `Program.cs` (for the production app) and in the
`OrdersFixture` (for integration tests). Without them the cascade events are dropped (they
have no handler route in the current config).

`MultipleHandlerBehavior.Separated` is already set — the saga and the new projector both
handle `OrderShippedApplicationEvent`/`OrderPlacedApplicationEvent` independently.
`FulfillmentShippedEvent` and `FulfillmentCompletedEvent` have **only one handler each**
(the new projector), so no `Separated` concern for those message types.

### 3.5 Test Assertions

Extend `SagaFlowTests` (or a new `FulfillmentProjectorTests.cs`):

```
demo/tests/OrderDemo.IntegrationTests/FulfillmentProjectorTests.cs
```

Or as an extension to the existing `ship_order_advances_saga_state` / `confirm_delivery_completes_saga` facts:

| Test method | What it proves |
|---|---|
| `fulfillment_shipped_event_creates_delivery_status` | After ship: `fulfillment_delivery_statuses` has a doc with `Status="Shipped"`, `ShippedAt` set |
| `fulfillment_completed_event_updates_delivery_status` | After confirm: same doc has `Status="Delivered"`, `DeliveredAt` set |
| `fulfillment_projection_is_idempotent` | Replay of `FulfillmentShippedEvent` → same doc, no duplication |

**Preferred approach:** extend `SagaFlowTests` with `saga_cascade_events_are_projected` — a
single end-to-end test that ships + confirms, then reads the `fulfillment_delivery_statuses`
collection and asserts both timestamps are set. Keeps the saga-cascade path in one file.

---

## 4. Entity Collection-Naming Convention (Input to D6)

D6 resolves LD3. This section states what D5 recommends as input.

### 4.1 Recommended Convention

```csharp
// In MongoConstants.cs (T1.1):
public static string EntityCollectionName(Type entityType)
    => entityType.Name.ToLowerInvariant();
```

**Examples:**

| Entity type | Collection name |
|---|---|
| `Todo` (StorageActionCompliance entity) | `todo` |
| `OrderNote` (demo entity) | `ordernote` |
| `CustomerProfile` (hypothetical) | `customerprofile` |

**Rationale:**
- Un-prefixed: entity collections are application-owned, not Wolverine system collections.
  Prefixing (like `wolverine_entity_todo`) would be misleading and non-idiomatic MongoDB.
- Lowercased type name: minimal, collision-free per type, mirrors the saga convention
  (`wolverine_saga_orderfulfillmentsaga = "wolverine_saga_" + type.Name.ToLowerInvariant()`).
- The caller controls collection names for `MongoDbUnitOfWork.Collection<T>(name)` writes —
  those are free-form and NOT derived from this convention.

### 4.2 Implications for the Compliance Host

`StorageActionCompliance` declares `Todo { string Id; string? Name; bool IsComplete; }`.
The compliance host must expose `Load`/`Persist` against the **same** collection the generated
frames write to. Specifically:

```csharp
// storage_action_compliance.cs — Load and Persist MUST use EntityCollectionName:
private static IMongoCollection<Todo> TodoCollection(AppFixture f)
    => f.Client.GetDatabase(AppFixture.DatabaseName)
        .GetCollection<Todo>(MongoConstants.EntityCollectionName(typeof(Todo)));  // "todo"

public override Task<Todo?> Load(string id)
    => TodoCollection(_fixture).Find(Builders<Todo>.Filter.Eq("_id", id)).FirstOrDefaultAsync()!;

public override Task Persist(Todo todo)
    => TodoCollection(_fixture).ReplaceOneAsync(
        Builders<Todo>.Filter.Eq("_id", todo.Id), todo, new ReplaceOptions { IsUpsert = true });
```

If D6 changes the naming convention, both the frame factory and the compliance host
`Load`/`Persist` must be updated together to match. They are a coupled pair.

### 4.3 `ClearAllAsync` — No Cleanup of Entity Collections

Entity collections (`ordernote`, `todo`, etc.) are **not** Wolverine system collections.
`ClearAllAsync` (via `IMessageStoreAdmin.ClearAllAsync`) clears system collections and drops
`wolverine_saga_*` collections. It must **not** drop entity collections — those are
application state. The compliance host clears its `todo` collection manually in its
`InitializeAsync`/`DisposeAsync` lifecycle (or via `configureWolverine` before each test).

---

## 5. Cross-Tier Flow → Test Inventory

### 5.1 Tier 1: Generic Entity / `IStorageAction<T>` Persistence

**Library tests** — `src/Wolverine.MongoDB.Tests/storage_action_compliance.cs`
(single file, `: StorageActionCompliance`):

| Compliance fact | What it exercises |
|---|---|
| `use_insert_as_return_value` | `Insert<Todo>` return → frame upserts into `todo` |
| `use_entity_attribute_with_id` | `[Entity] Todo` load by id from message |
| `use_entity_attribute_with_entity_id` | same, via `entity_id` message property |
| `use_entity_attribute_with_explicit_id` | same, via explicit ctor arg `[Entity("id")]` |
| `use_delete_as_return_value` | `Delete<Todo>` return → frame removes from `todo` |
| `use_generic_action_as_insert` | `IStorageAction<Todo>` with `StorageAction.Insert` |
| `use_generic_action_as_update` | `IStorageAction<Todo>` with `StorageAction.Update` |
| `use_generic_action_as_store` | `IStorageAction<Todo>` with `StorageAction.Store` |
| `use_generic_action_as_delete` | `IStorageAction<Todo>` with `StorageAction.Delete` |
| `do_nothing_as_generic_action` | `StorageAction.Nothing` — no-op, entity unchanged |
| `do_nothing_if_storage_action_is_null` | null `IStorageAction<Todo>` — no-op |
| `do_nothing_if_generic_storage_action_is_null` | null generic action — no-op |
| `do_not_execute_the_handler_if_the_entity_is_not_found` | Required=true, missing entity → handler skipped |
| `handler_not_required_entity_attributes` | Required=false, missing entity → handler executes with null |
| `entity_can_be_used_in_before_methods_...` | `[Entity]` in before-middleware method |
| `can_use_attribute_on_before_methods` | same, explicit attribute on before method |
| `use_unit_of_work_as_return_value` | `UnitOfWork<Todo>` list return → all actions applied |

All ~17 facts target the `todo` collection via `MongoConstants.EntityCollectionName(typeof(Todo))`.

**Library tests** — `src/Wolverine.MongoDB.Tests/entity_atomicity.cs`:

| Test method | What it proves |
|---|---|
| `entity_write_and_outbox_commit_together` | `Insert<T>` + cascade message → both persist on success |
| `entity_write_rolls_back_with_outbox` | post-write throw → neither entity doc nor outbox envelope persists |
| `saga_and_entity_coexist_in_same_handler` | handler returns `(SagaUpdate, entity Insert<T>)` — saga OCC untouched, entity persisted |
| `entity_not_found_does_not_execute_handler` | end-to-end: required `[Entity]` missing → handler body never runs |

**Demo tests** — `demo/tests/OrderDemo.IntegrationTests/OrderNoteFlowTests.cs`:

| Test method | What it proves |
|---|---|
| `can_add_order_note` | `Insert<OrderNote>` → doc in `ordernote` collection |
| `can_edit_order_note` | `Update<OrderNote>` → doc updated; `UpdatedAt` populated |
| `can_delete_order_note` | `Delete<OrderNote>` → doc removed |
| `edit_note_not_found_is_skipped` | `Required=true` — no entity → command silently skipped |
| `delete_note_not_found_is_skipped` | same for delete |

---

### 5.2 Tier 2: `ISagaStoreDiagnostics`

**Library tests** — `src/Wolverine.MongoDB.Tests/saga_store_diagnostics.cs`
(per-provider integration test; no unified compliance spec exists):

| Test method | What it proves |
|---|---|
| `get_registered_sagas_includes_mongo_sagas` | `GetRegisteredSagasAsync` returns saga types owned by MongoDb, tagged `"MongoDb"` |
| `read_saga_by_full_name_returns_instance` | `ReadSagaAsync(typeFQN, id)` → loaded saga instance |
| `read_saga_by_short_name_returns_instance` | `ReadSagaAsync(typeName, id)` → same instance (both index keys work) |
| `list_saga_instances_returns_up_to_count` | `ListSagaInstancesAsync(typeName, 5)` → up to 5 docs |
| `list_saga_instances_clamps_to_1000` | count > 1000 is clamped |
| `unknown_type_read_returns_null` | unregistered type name → `null` |
| `unknown_type_list_returns_empty` | unregistered type name → empty list |
| `registration_does_not_break_startup` | existing library suite still green with diagnostics registered |

**Demo tests:** none. The diagnostics surface is UI/tooling-facing; the library tests are sufficient.

---

### 5.3 Tier 3: Parity Capabilities (Document-as-Non-Goal)

No library tests or demo tests — these are documentation-only decisions.

| Capability | Recommendation | Library test | Demo test |
|---|---|---|---|
| Multi-tenancy | DEFER — non-goal (matches Cosmos/Raven) | None | None |
| Durable listeners | DEFER — keep `NullListenerStore` (matches Cosmos/Raven) | None | None |
| Query-spec frames | DEFER — non-goal (Marten/EF only) | None | None |
| Soft-delete | DEFER — non-goal (Marten only) | None | None |

The existing `ClearAllAsync`, startup, and admin tests implicitly confirm the non-goal
defaults are inert (no-op implementations don't break anything). No new test files.

---

### 5.4 Tier 4: Hardening + Tracked Follow-ups

**Library tests — existing or new per task:**

| Task | Library test | Notes |
|---|---|---|
| T4.3 unkeyed `IMongoDatabase` | Existing startup tests remain green | The fix is a DI-registration change; existing tests cover the regression |
| T4.4 `ClearAllAsync` scope | New or existing admin test (scope decision) | T4.4 decides scope; test confirms it |
| T4.5 multinode leadership | `leadership_election_compliance.cs` (un-gated) | 5× green on net9.0 + net10.0 required before un-gating |
| T4.6 hardening docs | None (doc only) | Pre-1.0 backlog items documented, not implemented |

**Demo tests — `MongoDbUnitOfWork` example (T4.1):**

New file: `demo/tests/OrderDemo.IntegrationTests/OrderAuditTests.cs`

| Test method | What it proves |
|---|---|
| `can_record_order_audit_entry` | `Handle(RecordOrderAuditCommand, MongoDbUnitOfWork)` → doc in `order_audit_entries` |
| `audit_entry_commits_atomically_with_outbox` | post-write throw → neither audit doc nor outbox envelope persists (session enrolled) |

**Demo tests — fulfillment projector (T4.2):**

Extension to `SagaFlowTests.cs` (single new fact) or new `FulfillmentProjectorTests.cs`:

| Test method | What it proves |
|---|---|
| `saga_cascade_events_are_projected` | Ship + confirm order → `fulfillment_delivery_statuses` has both timestamps; `Status = "Delivered"` |
| `fulfillment_projection_is_idempotent` | (optional) replay `FulfillmentShippedEvent` → same doc, no duplication |

---

## 6. Full File Inventory

### New files per task

| Task | File | Type |
|---|---|---|
| T1.1 | `src/Wolverine.MongoDB.Tests/storage_action_compliance.cs` | Library test (compliance subclass) |
| T1.2 | `src/Wolverine.MongoDB.Tests/entity_atomicity.cs` | Library test (custom atomicity) |
| T1.3 | `demo/src/OrderDemo.Domain/Notes/OrderNote.cs` | Entity POCO |
| T1.3 | `demo/src/OrderDemo.Contracts/Commands/Notes/AddOrderNoteCommand.cs` | Command record |
| T1.3 | `demo/src/OrderDemo.Contracts/Commands/Notes/EditOrderNoteCommand.cs` | Command record |
| T1.3 | `demo/src/OrderDemo.Contracts/Commands/Notes/DeleteOrderNoteCommand.cs` | Command record |
| T1.3 | `demo/src/OrderDemo.Application/Notes/OrderNoteHandler.cs` | Handler |
| T1.3 | `demo/tests/OrderDemo.IntegrationTests/OrderNoteFlowTests.cs` | Demo test |
| T2.2 | `src/Wolverine.MongoDB.Tests/saga_store_diagnostics.cs` | Library test |
| T4.1 | `demo/src/OrderDemo.Domain/Audit/OrderAuditEntry.cs` | Entity POCO |
| T4.1 | `demo/src/OrderDemo.Contracts/Commands/Audit/RecordOrderAuditCommand.cs` | Command record |
| T4.1 | `demo/src/OrderDemo.Application/Audit/RecordOrderAuditHandler.cs` | Handler |
| T4.1 | `demo/tests/OrderDemo.IntegrationTests/OrderAuditTests.cs` | Demo test |
| T4.2 | `demo/src/OrderDemo.Infrastructure/Persistence/FulfillmentDeliveryStatus.cs` | Read model POCO |
| T4.2 | `demo/src/OrderDemo.Infrastructure/Projectors/FulfillmentStatusProjector.cs` | Projector handler |

### Changed files per task

| Task | File | Change |
|---|---|---|
| T1.3 | `demo/tests/OrderDemo.IntegrationTests/OrdersFixture.cs` | No new routes (commands are inline); confirm no change needed |
| T1.3 | `demo/src/OrderDemo.Api/Program.cs` | Add POST `/orders/{id}/notes` endpoint; add note command discovery if not in existing assembly scan |
| T4.2 | `demo/tests/OrderDemo.IntegrationTests/OrdersFixture.cs` | Add `LocalQueueFor<FulfillmentShippedEvent>().UseDurableInbox()` + `LocalQueueFor<FulfillmentCompletedEvent>().UseDurableInbox()` |
| T4.2 | `demo/src/OrderDemo.Api/Program.cs` | Same queue routes for the cascade events |
| T4.2 | `demo/tests/OrderDemo.IntegrationTests/SagaFlowTests.cs` | Add `saga_cascade_events_are_projected` (or new file) |

---

## 7. Summary of Decisions

| Decision | Recommendation | Feeds |
|---|---|---|
| Tier-1 demo entity | `OrderNote` (Guid Id, OrderId FK, Text, Author, CreatedAt, UpdatedAt) | T1.3 |
| Entity id type | `Guid` | T1.3, T1.1 (frame branching), D6 |
| Entity collection naming | `MongoConstants.EntityCollectionName(T) = type.Name.ToLowerInvariant()` | D6, T1.1 |
| Compliance `Load`/`Persist` collection | `MongoConstants.EntityCollectionName(typeof(Todo))` = `"todo"` | T1.1 |
| `ClearAllAsync` entity scope | No cleanup of entity collections (app-owned, not Wolverine system) | D6, T1.1 |
| Tier-4 UoW handler | `RecordOrderAuditHandler(RecordOrderAuditCommand, MongoDbUnitOfWork)` | T4.1 |
| UoW collection naming | Caller-specified string (`"order_audit_entries"`) — free-form | T4.1 |
| Tier-4 fulfillment projector | `FulfillmentStatusProjector`: handles `FulfillmentShippedEvent` + `FulfillmentCompletedEvent` | T4.2 |
| Projector write surface | `IMongoDatabase` (not UoW — projector runs outside saga session) | T4.2 |
| Fulfillment collection | `"fulfillment_delivery_statuses"` (caller-specified) | T4.2 |
| Saga cascade queue routes | Add `LocalQueueFor<FulfillmentShipped/Completed>().UseDurableInbox()` | T4.2 |
