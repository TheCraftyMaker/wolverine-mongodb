using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Wolverine.MongoDB.Internals;

public class LockDocument
{
    [BsonId] public string Id { get; set; } = string.Empty;
    [BsonElement("nodeId")] [BsonGuidRepresentation(GuidRepresentation.Standard)] public Guid NodeId { get; set; }
    [BsonElement("expiresAt")] public DateTime ExpiresAt { get; set; }
}
