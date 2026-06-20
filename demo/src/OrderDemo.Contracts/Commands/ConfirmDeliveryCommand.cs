using Wolverine.Persistence.Sagas;

namespace OrderDemo.Contracts.Commands;

/// <summary>
/// Confirms delivery of a shipped order, completing the OrderFulfillmentSaga.
/// [SagaIdentity] tells Wolverine to load the saga keyed on OrderId.
/// </summary>
public sealed record ConfirmDeliveryCommand(
    [property: SagaIdentity] Guid OrderId,
    DateTimeOffset DeliveredAt);
