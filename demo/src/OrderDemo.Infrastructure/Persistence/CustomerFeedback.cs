using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderDemo.Infrastructure.Persistence;

/// <summary>
/// Freeform post-delivery feedback for an order. Persisted as a MongoDB document in the
/// <c>customerfeedback</c> collection via the Wolverine-generated entity frames (Tier 1), same
/// as <see cref="OrderNote"/> — but keyed by <see cref="CustomerFeedbackId"/> (the
/// <c>{TypeName}Id</c> convention) rather than a member literally named <c>Id</c>, exercising the
/// non-<c>Id</c> identity-convention fix through the packaged library rather than only the
/// library's own test project.
///
/// Unlike <see cref="OrderNote"/>'s <c>string</c> id (chosen there to dodge a boxed-<c>object</c>
/// <c>GuidRepresentation</c> gotcha), <see cref="CustomerFeedbackId"/> is a native <c>Guid</c> —
/// the harder case. Callers must filter through strongly-typed lambda builders
/// (<c>Builders&lt;CustomerFeedback&gt;.Filter.Eq(x => x.CustomerFeedbackId, id)</c>), never a
/// boxed <c>Filter.Eq("_id", (object)id)</c>, exactly like <see cref="OrderNote.OrderId"/> is
/// already queried elsewhere in the demo.
/// </summary>
public sealed class CustomerFeedback
{
    // {TypeName}Id convention — no member named Id/id/_id anywhere on this type.
    public Guid CustomerFeedbackId { get; set; }

    public Guid OrderId { get; set; }
    public int Rating { get; set; }
    public string Comment { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset SubmittedAt { get; set; }
}
