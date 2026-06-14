using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Internals;

public class NodeDocument
{
    [BsonId] [BsonGuidRepresentation(GuidRepresentation.Standard)] public Guid Id { get; set; }
    [BsonElement("assignedNodeNumber")] public int AssignedNodeNumber { get; set; }
    [BsonElement("description")] public string Description { get; set; } = string.Empty;
    [BsonElement("controlUri")] public string? ControlUri { get; set; }
    [BsonElement("started")] [BsonRepresentation(BsonType.DateTime)] public DateTimeOffset Started { get; set; }
    [BsonElement("lastHealthCheck")] public DateTime LastHealthCheck { get; set; }
    [BsonElement("capabilities")] public List<string> Capabilities { get; set; } = new();
    [BsonElement("version")] public string? Version { get; set; }

    public static NodeDocument FromWolverineNode(WolverineNode node) => new()
    {
        Id = node.NodeId,
        AssignedNodeNumber = node.AssignedNodeNumber,
        Description = node.Description,
        ControlUri = node.ControlUri?.ToString(),
        Started = node.Started,
        LastHealthCheck = node.LastHealthCheck.UtcDateTime,
        Capabilities = node.Capabilities.Select(x => x.ToString()).ToList(),
        Version = node.Version.ToString()
    };

    public WolverineNode ToWolverineNode()
    {
        var node = new WolverineNode
        {
            NodeId = Id,
            AssignedNodeNumber = AssignedNodeNumber,
            Description = Description,
            ControlUri = ControlUri != null ? new Uri(ControlUri) : null,
            Started = Started,
            LastHealthCheck = LastHealthCheck,
            Version = Version != null ? new Version(Version) : new Version(0, 0, 0, 0)
        };
        node.Capabilities.AddRange(Capabilities.Select(x => new Uri(x)));
        return node;
    }
}
