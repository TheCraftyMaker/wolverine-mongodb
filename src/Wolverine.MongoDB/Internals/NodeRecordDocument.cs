using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Internals;

public class NodeRecordDocument
{
    [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString();
    [BsonElement("nodeNumber")] public int NodeNumber { get; set; }
    [BsonElement("recordType")] [BsonRepresentation(BsonType.String)] public NodeRecordType RecordType { get; set; }
    [BsonElement("timestamp")] [BsonRepresentation(BsonType.DateTime)] public DateTimeOffset Timestamp { get; set; }
    [BsonElement("description")] public string Description { get; set; } = string.Empty;
    [BsonElement("serviceName")] public string ServiceName { get; set; } = string.Empty;

    public static NodeRecordDocument FromRecord(NodeRecord r) => new()
    {
        NodeNumber = r.NodeNumber,
        RecordType = r.RecordType,
        Timestamp = r.Timestamp,
        Description = r.Description,
        ServiceName = r.ServiceName
    };

    public NodeRecord ToRecord() => new()
    {
        NodeNumber = NodeNumber,
        RecordType = RecordType,
        Timestamp = Timestamp,
        Description = Description,
        ServiceName = ServiceName
    };
}
