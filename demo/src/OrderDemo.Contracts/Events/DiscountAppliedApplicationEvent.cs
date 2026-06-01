namespace OrderDemo.Contracts.Events;

/// <summary>
/// Application event published to RabbitMQ (via Wolverine outbox) when a discount is applied.
/// Consumed by the read-side projector to update the order total in the read model.
/// <para>
/// <b>Idempotency:</b> <see cref="CumulativeDiscountPercent"/> is the total discount after this
/// operation and must be used for absolute assignment in projectors. Do NOT use
/// <see cref="DiscountPercent"/> (the incremental value) with <c>+=</c> — that would double
/// on message redelivery.
/// </para>
/// </summary>
public sealed record DiscountAppliedApplicationEvent(
    Guid OrderId,
    decimal DiscountPercent,
    decimal NewTotal,
    decimal CumulativeDiscountPercent);
