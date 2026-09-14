using JasperFx.Core.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MongoDB.Internals;

public class DeadLetterMessage
{
    public DeadLetterMessage() { }

    /// <param name="envelope">The failed envelope.</param>
    /// <param name="exception">The handling exception, if any.</param>
    /// <param name="id">
    /// The document key, from <c>MongoDbMessageStore.DeadLetterKey</c>. Deliberately not defaulted
    /// to <c>envelope.Id</c>: that is only correct in <see cref="MessageIdentity.IdOnly"/> mode.
    /// </param>
    public DeadLetterMessage(Envelope envelope, Exception? exception, Guid id)
    {
        Id = id;
        EnvelopeId = envelope.Id;
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
        Exception serializeFailure, Guid id)
    {
        return new DeadLetterMessage
        {
            Id = id,
            EnvelopeId = envelope.Id,
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

    /// <summary>
    /// The document key — NOT necessarily the envelope id. Equal to the envelope Guid in the
    /// default <see cref="MessageIdentity.IdOnly"/> mode; derived from the
    /// <c>(envelope id, destination)</c> identity unit in
    /// <see cref="MessageIdentity.IdAndDestination"/> mode. Use <see cref="EnvelopeId"/> for
    /// anything Wolverine's dead-letter API addresses by Guid.
    /// </summary>
    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; set; }

    /// <summary>
    /// The envelope's own id, which one Guid-addressed dead-letter operation can legitimately
    /// match on several documents. Mirrors <see cref="IncomingMessage.EnvelopeId"/>.
    /// </summary>
    [BsonElement("envelopeId")]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid EnvelopeId { get; set; }

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

    /// <summary>
    /// The envelope Guid this document belongs to. Documents written before the identity split
    /// carry no <c>envelopeId</c> element; for those the <c>_id</c> WAS the envelope Guid, so fall
    /// back to it rather than reporting <see cref="Guid.Empty"/>.
    /// </summary>
    internal Guid ResolvedEnvelopeId => EnvelopeId == Guid.Empty ? Id : EnvelopeId;

    public DeadLetterEnvelope ToEnvelope()
    {
        var envelopeId = ResolvedEnvelopeId;

        // A body-less dead letter (an envelope that failed to serialize) reconstructs a minimal
        // envelope so the dead-letter admin/query surface still works for poison messages. The
        // destination is restored the same way IncomingMessage.Read() does: without it, an edited
        // poison letter would re-enter the inbox under the wrong identity key.
        var envelope = Body is { Length: > 0 }
            ? EnvelopeSerializer.Deserialize(Body)
            : new Envelope
            {
                Id = envelopeId,
                MessageType = MessageType,
                Destination = ReceivedAt != null ? new Uri(ReceivedAt) : null,
                Data = []
            };

        return new DeadLetterEnvelope(envelopeId, ScheduledTime, envelope, MessageType ?? "",
            ReceivedAt ?? "", Source ?? "", ExceptionType ?? "", ExceptionMessage ?? "",
            SentAt ?? DateTimeOffset.MinValue, Replayable);
    }
}
