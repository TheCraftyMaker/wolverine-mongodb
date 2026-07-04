using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderDemo.Infrastructure.Persistence;

/// <summary>
/// A freeform note attached to an order. Persisted as a MongoDB document in the
/// <c>ordernote</c> collection via the Wolverine-generated entity frames (Tier 1).
/// No repository, no manual session — <c>Insert&lt;OrderNote&gt;</c>/<c>Update&lt;OrderNote&gt;</c>
/// /<c>Delete&lt;OrderNote&gt;</c> return values drive the write through the TransactionalFrame.
///
/// Lives alongside <see cref="OrderSummary"/> in Infrastructure (not Domain) because it carries
/// Mongo BSON attributes and the Domain project is intentionally persistence-agnostic.
///
/// <c>Id</c> is <c>string</c>, not <c>Guid</c>: the generated entity frames extract the id via
/// <c>BsonClassMap.IdMemberMap.Getter</c> as a boxed <c>object</c>, so ad-hoc <c>Filter.Eq("_id",
/// object)</c> calls resolve through the driver's <c>ObjectSerializer</c> rather than a strongly-typed
/// <c>Guid</c> filter — which does not honor this app's globally-registered
/// <c>GuidSerializer(GuidRepresentation.Standard)</c> and throws "GuidRepresentation is Unspecified".
/// This mirrors the library's own entity test fixtures (<c>Todo</c>, <c>NoteEntity</c>), which also
/// use <c>string</c> ids. <c>OrderId</c> stays <c>Guid</c> since it is only ever queried through
/// strongly-typed lambda filters, which are unaffected.
/// </summary>
public sealed class OrderNote
{
    // _id via the driver's default Id-member convention. No [BsonId] needed.
    public string Id { get; set; } = string.Empty;

    public Guid OrderId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset CreatedAt { get; set; }

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset? UpdatedAt { get; set; }
}
