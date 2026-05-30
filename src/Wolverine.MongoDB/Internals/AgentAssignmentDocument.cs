using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Wolverine.MongoDB.Internals;

public class AgentAssignmentDocument
{
    [BsonId] public string Id { get; set; } = string.Empty; // agent uri
    [BsonElement("nodeId")] [BsonGuidRepresentation(GuidRepresentation.Standard)] public Guid NodeId { get; set; }
    [BsonElement("agentUri")] public string AgentUri { get; set; } = string.Empty;
}
