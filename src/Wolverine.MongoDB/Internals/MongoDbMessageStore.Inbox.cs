using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IMessageInbox
{
    public Task ScheduleExecutionAsync(Envelope envelope) => throw new NotImplementedException();
    public Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception) => throw new NotImplementedException();
    public Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope) => throw new NotImplementedException();
    public Task StoreIncomingAsync(Envelope envelope) => throw new NotImplementedException();
    public Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes) => throw new NotImplementedException();
    public Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation) => throw new NotImplementedException();
    public Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope) => throw new NotImplementedException();
    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope) => throw new NotImplementedException();
    public Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes) => throw new NotImplementedException();
    public Task ReleaseIncomingAsync(int ownerId, Uri receivedAt) => throw new NotImplementedException();
}
