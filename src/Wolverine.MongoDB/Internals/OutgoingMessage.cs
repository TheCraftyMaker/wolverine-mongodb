using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MongoDB.Internals;

public class OutgoingMessage
{
    public OutgoingMessage() { }

    public OutgoingMessage(Envelope envelope)
    {
        Id = envelope.Id;
        OwnerId = envelope.OwnerId;
        Attempts = envelope.Attempts;
        Body = EnvelopeSerializer.Serialize(envelope);
        MessageType = envelope.MessageType!;
        Destination = envelope.Destination?.ToString();
        DeliverBy = envelope.DeliverBy?.ToUniversalTime();
    }

    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; set; }
    [BsonElement("ownerId")] public int OwnerId { get; set; }
    [BsonElement("destination")] public string? Destination { get; set; }
    [BsonElement("deliverBy")] [BsonRepresentation(BsonType.DateTime)] public DateTimeOffset? DeliverBy { get; set; }
    [BsonElement("body")] public byte[] Body { get; set; } = [];
    [BsonElement("attempts")] public int Attempts { get; set; }
    [BsonElement("messageType")] public string MessageType { get; set; } = string.Empty;

    public Envelope Read()
    {
        var envelope = EnvelopeSerializer.Deserialize(Body);
        envelope.OwnerId = OwnerId;
        envelope.Attempts = Attempts;
        return envelope;
    }
}
