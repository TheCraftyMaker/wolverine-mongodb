using JasperFx.Core.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MongoDB.Internals;

public class DeadLetterMessage
{
    public DeadLetterMessage() { }

    public DeadLetterMessage(Envelope envelope, Exception? exception)
    {
        Id = envelope.Id;
        MessageType = envelope.MessageType;
        ReceivedAt = envelope.Destination?.ToString();
        SentAt = envelope.SentAt;
        ScheduledTime = envelope.ScheduledTime;
        Source = envelope.Source;
        ExceptionType = exception?.GetType().FullNameInCode();
        ExceptionMessage = exception?.Message;
        Body = EnvelopeSerializer.Serialize(envelope);
    }

    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; set; }
    [BsonElement("messageType")] public string? MessageType { get; set; }
    [BsonElement("receivedAt")] public string? ReceivedAt { get; set; }
    [BsonElement("sentAt")] public DateTimeOffset? SentAt { get; set; }
    [BsonElement("scheduledTime")] public DateTimeOffset? ScheduledTime { get; set; }
    [BsonElement("source")] public string? Source { get; set; }
    [BsonElement("exceptionType")] public string? ExceptionType { get; set; }
    [BsonElement("exceptionMessage")] public string? ExceptionMessage { get; set; }
    [BsonElement("replayable")] public bool Replayable { get; set; }
    [BsonElement("body")] public byte[] Body { get; set; } = [];
    [BsonElement("expirationTime")] public DateTimeOffset ExpirationTime { get; set; }

    public DeadLetterEnvelope ToEnvelope()
    {
        var envelope = EnvelopeSerializer.Deserialize(Body);
        return new DeadLetterEnvelope(Id, ScheduledTime, envelope, MessageType ?? "",
            ReceivedAt ?? "", Source ?? "", ExceptionType ?? "", ExceptionMessage ?? "",
            SentAt ?? DateTimeOffset.MinValue, Replayable);
    }
}
