using MongoDB.Bson.Serialization.Attributes;

namespace Wolverine.MongoDB.Internals;

public class NodeCounterDocument
{
    [BsonId] public string Id { get; set; } = string.Empty;
    [BsonElement("count")] public int Count { get; set; }
}
