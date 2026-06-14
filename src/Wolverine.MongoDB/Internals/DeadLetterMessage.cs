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

    /// <summary>
    /// Builds a dead-letter document for an envelope whose body could not be serialized.
    /// Captures all available metadata and a safe empty body so the poison message can still
    /// be moved out of the inbox instead of being stranded. The serialization failure is
    /// recorded as the exception type/message when no handler exception is available.
    /// </summary>
    public static DeadLetterMessage ForUnserializableEnvelope(Envelope envelope, Exception? exception,
        Exception serializeFailure)
    {
        return new DeadLetterMessage
        {
            Id = envelope.Id,
            MessageType = envelope.MessageType,
            ReceivedAt = envelope.Destination?.ToString(),
            SentAt = envelope.SentAt,
            ScheduledTime = envelope.ScheduledTime,
            Source = envelope.Source,
            ExceptionType = (exception ?? serializeFailure).GetType().FullNameInCode(),
            ExceptionMessage = exception?.Message ?? $"Envelope body could not be serialized: {serializeFailure.Message}",
            Body = []
        };
    }

    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; set; }
    [BsonElement("messageType")] public string? MessageType { get; set; }
    [BsonElement("receivedAt")] public string? ReceivedAt { get; set; }
    [BsonElement("sentAt")] [BsonRepresentation(BsonType.DateTime)] public DateTimeOffset? SentAt { get; set; }
    [BsonElement("scheduledTime")] [BsonRepresentation(BsonType.DateTime)] public DateTimeOffset? ScheduledTime { get; set; }
    [BsonElement("source")] public string? Source { get; set; }
    [BsonElement("exceptionType")] public string? ExceptionType { get; set; }
    [BsonElement("exceptionMessage")] public string? ExceptionMessage { get; set; }
    [BsonElement("replayable")] public bool Replayable { get; set; }
    [BsonElement("body")] public byte[] Body { get; set; } = [];
    [BsonElement("expirationTime")]
    [BsonIgnoreIfNull]
    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset? ExpirationTime { get; set; }

    public DeadLetterEnvelope ToEnvelope()
    {
        // A body-less dead letter (an envelope that failed to serialize) reconstructs a minimal
        // envelope so the dead-letter admin/query surface still works for poison messages.
        var envelope = Body is { Length: > 0 }
            ? EnvelopeSerializer.Deserialize(Body)
            : new Envelope { Id = Id, MessageType = MessageType, Data = [] };
        return new DeadLetterEnvelope(Id, ScheduledTime, envelope, MessageType ?? "",
            ReceivedAt ?? "", Source ?? "", ExceptionType ?? "", ExceptionMessage ?? "",
            SentAt ?? DateTimeOffset.MinValue, Replayable);
    }
}
