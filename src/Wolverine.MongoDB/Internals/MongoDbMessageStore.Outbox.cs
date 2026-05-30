using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IMessageOutbox
{
    public Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination) => throw new NotImplementedException();
    public Task StoreOutgoingAsync(Envelope envelope, int ownerId) => throw new NotImplementedException();
    public Task DeleteOutgoingAsync(Envelope[] envelopes) => throw new NotImplementedException();
    public Task DeleteOutgoingAsync(Envelope envelope) => throw new NotImplementedException();
    public Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId) => throw new NotImplementedException();
}
