using OrderDemo.Contracts.Queries;
using OrderDemo.Infrastructure.Persistence;

namespace OrderDemo.Application.Orders;

/// <summary>
/// Handles <see cref="GetOrdersQuery"/> — reads from the denormalized
/// <see cref="OrderSummary"/> read model (write side is NOT queried).
/// </summary>
public static class GetOrdersHandler
{
    public static Task<IReadOnlyList<OrderSummary>> Handle(
        GetOrdersQuery _,
        OrderSummaryRepository summaries,
        CancellationToken ct)
        => summaries.GetAllAsync(ct);
}
