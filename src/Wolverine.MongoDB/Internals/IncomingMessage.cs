using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MongoDB.Internals;

public class IncomingMessage
{
    public IncomingMessage() { }

    public IncomingMessage(Envelope envelope)
    {
        Id = $"{envelope.Id}|{envelope.Destination?.ToString().Replace(":/", "").TrimEnd('/')}";
        EnvelopeId = envelope.Id;
        Status = envelope.Status;
        OwnerId = envelope.OwnerId;
        ExecutionTime = envelope.ScheduledTime?.ToUniversalTime();
        Attempts = envelope.Attempts;
        Body = envelope.Status == EnvelopeStatus.Handled ? [] : EnvelopeSerializer.Serialize(envelope);
        MessageType = envelope.MessageType!;
        ReceivedAt = envelope.Destination?.ToString();
    }

    [BsonId] public string Id { get; set; } = string.Empty;
    [BsonElement("envelopeId")] [BsonGuidRepresentation(GuidRepresentation.Standard)] public Guid EnvelopeId { get; set; }
    [BsonElement("status")] [BsonRepresentation(BsonType.String)] public EnvelopeStatus Status { get; set; } = EnvelopeStatus.Incoming;
    [BsonElement("ownerId")] public int OwnerId { get; set; }
    [BsonElement("executionTime")] public DateTimeOffset? ExecutionTime { get; set; }
    [BsonElement("attempts")] public int Attempts { get; set; }
    [BsonElement("body")] public byte[] Body { get; set; } = [];
    [BsonElement("messageType")] public string MessageType { get; set; } = string.Empty;
    [BsonElement("receivedAt")] public string? ReceivedAt { get; set; }
    [BsonElement("keepUntil")] public DateTimeOffset? KeepUntil { get; set; }

    public Envelope Read()
    {
        Envelope envelope;
        if (Body is null || Body.Length == 0)
        {
            envelope = new Envelope
            {
                Id = EnvelopeId,
                MessageType = MessageType,
                Destination = ReceivedAt != null ? new Uri(ReceivedAt) : null,
                Data = []
            };
        }
        else
        {
            envelope = EnvelopeSerializer.Deserialize(Body);
        }

        envelope.Id = EnvelopeId;
        envelope.OwnerId = OwnerId;
        envelope.Status = Status;
        envelope.Attempts = Attempts;
        envelope.ScheduledTime = ExecutionTime;
        return envelope;
    }
}
