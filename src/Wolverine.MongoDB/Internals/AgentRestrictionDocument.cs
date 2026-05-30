using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Internals;

public class AgentRestrictionDocument
{
    [BsonId] public string Id { get; set; } = string.Empty; // restriction|{guid}
    [BsonElement("agentUri")] public string AgentUri { get; set; } = string.Empty;
    [BsonElement("type")] [BsonRepresentation(BsonType.String)] public AgentRestrictionType Type { get; set; }
    [BsonElement("nodeNumber")] public int NodeNumber { get; set; }
}
