using MongoDB.Driver;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IMessageOutbox
{
    public async Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        var docs = await Outgoing
            .Find(Builders<OutgoingMessage>.Filter.Eq(x => x.Destination, destination.ToString()))
            .ToListAsync();
        return docs.Select(x => x.Read()).ToList();
    }

    public async Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        var doc = new OutgoingMessage(envelope) { OwnerId = ownerId };
        await Outgoing.ReplaceOneAsync(
            Builders<OutgoingMessage>.Filter.Eq(x => x.Id, doc.Id),
            doc, new ReplaceOptions { IsUpsert = true });
    }

    public Task DeleteOutgoingAsync(Envelope envelope)
        => Outgoing.DeleteOneAsync(Builders<OutgoingMessage>.Filter.Eq(x => x.Id, envelope.Id));

    public Task DeleteOutgoingAsync(Envelope[] envelopes)
        => Outgoing.DeleteManyAsync(Builders<OutgoingMessage>.Filter.In(x => x.Id, envelopes.Select(e => e.Id)));

    public async Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        if (discards.Length > 0)
        {
            await Outgoing.DeleteManyAsync(
                Builders<OutgoingMessage>.Filter.In(x => x.Id, discards.Select(e => e.Id)));
        }

        if (reassigned.Length > 0)
        {
            await Outgoing.UpdateManyAsync(
                Builders<OutgoingMessage>.Filter.In(x => x.Id, reassigned.Select(e => e.Id)),
                Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, nodeId));
        }
    }
}
