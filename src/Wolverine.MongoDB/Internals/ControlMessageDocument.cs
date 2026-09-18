using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// One inter-node control message addressed to a single node. Written by the control sender, read
/// and deleted by the target node's control listener. The TTL index on <see cref="Expires"/> reaps
/// anything a dead node never collected.
/// </summary>
public class ControlMessageDocument
{
    [BsonId] [BsonGuidRepresentation(GuidRepresentation.Standard)] public Guid Id { get; set; }
    [BsonElement("nodeId")] [BsonGuidRepresentation(GuidRepresentation.Standard)] public Guid NodeId { get; set; }
    [BsonElement("messageType")] public string MessageType { get; set; } = string.Empty;
    [BsonElement("body")] public byte[] Body { get; set; } = [];
    [BsonElement("expires")] public DateTime Expires { get; set; }
    [BsonElement("posted")] public DateTime Posted { get; set; }

    public static ControlMessageDocument For(Envelope envelope, Guid nodeId, DateTime expires) => new()
    {
        Id = envelope.Id,
        NodeId = nodeId,
        MessageType = envelope.MessageType ?? string.Empty,
        Body = EnvelopeSerializer.Serialize(envelope),
        Expires = expires,
        Posted = DateTime.UtcNow
    };
}
