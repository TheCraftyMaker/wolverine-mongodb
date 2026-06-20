using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Events;
using Wolverine;

namespace OrderDemo.Application.Sagas;

/// <summary>
/// Tracks the lifecycle of a placed order through to delivery confirmation.
///
/// Keyed on OrderId (Guid). Starts on OrderPlacedApplicationEvent (already cascaded by
/// PlaceOrderHandler), continues on OrderShippedApplicationEvent, and completes when
/// ConfirmDeliveryCommand is processed — at which point MarkCompleted() signals Wolverine
/// to delete the saga document from MongoDB.
///
/// The generated Wolverine saga frame opens a MongoDB session, loads this document by
/// _id (= Id = OrderId), runs the handler method, and persists the result — all inside
/// the existing TransactionalFrame transaction so saga state + outbox entries commit
/// atomically. No manual session handling is required in these methods.
/// </summary>
public sealed class OrderFulfillmentSaga : Saga
{
    // Wolverine saga identity — "Id" is the standard convention member.
    // Set to evt.OrderId by the Start handler on first insertion.
    // Maps to MongoDB _id via the driver's default Id-member convention.
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }

    // Lifecycle flags — useful for saga-state assertions in tests
    public bool OrderPlaced { get; set; }
    public bool OrderShipped { get; set; }
    public bool DeliveryConfirmed { get; set; }

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset PlacedAt { get; set; }
    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset? ShippedAt { get; set; }
    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset? DeliveredAt { get; set; }

    // ── Start ─────────────────────────────────────────────────────────────────
    // Wolverine recognises "Start"/"Starts" as a saga-start method name.
    // Called on a freshly-created OrderFulfillmentSaga instance (Id not yet set).
    // Assigning Id here causes the frame to insert the new document into MongoDB.
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
    // via [SagaIdentity]; loads the document; calls this method; updates MongoDB.
    // Returns FulfillmentShippedEvent to demonstrate outbox-in-saga atomicity.
    // If the saga document is not found: Wolverine throws UnknownSagaException.
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
}
