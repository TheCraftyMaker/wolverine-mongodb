using MongoDB.Bson;
using MongoDB.Driver;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IMessageStoreAdmin
{
    public async Task RebuildAsync()
    {
        await ClearAllAsync();
        await EnsureIndexesAsync();
    }

    public async Task MigrateAsync() => await EnsureIndexesAsync();

    private async Task EnsureIndexesAsync()
    {
        await Incoming.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<IncomingMessage>(
                Builders<IncomingMessage>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.OwnerId)),
            new CreateIndexModel<IncomingMessage>(
                Builders<IncomingMessage>.IndexKeys.Ascending(x => x.ExecutionTime)),
            new CreateIndexModel<IncomingMessage>(
                Builders<IncomingMessage>.IndexKeys.Ascending(x => x.OwnerId).Ascending(x => x.ReceivedAt)),
            new CreateIndexModel<IncomingMessage>(
                Builders<IncomingMessage>.IndexKeys.Ascending(x => x.KeepUntil),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero })
        });

        await Outgoing.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<OutgoingMessage>(Builders<OutgoingMessage>.IndexKeys.Ascending(x => x.OwnerId)),
            new CreateIndexModel<OutgoingMessage>(Builders<OutgoingMessage>.IndexKeys.Ascending(x => x.Destination)),
            new CreateIndexModel<OutgoingMessage>(Builders<OutgoingMessage>.IndexKeys.Ascending(x => x.DeliverBy))
        });

        await DeadLetterDocs.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<DeadLetterMessage>(Builders<DeadLetterMessage>.IndexKeys.Ascending(x => x.SentAt)),
            new CreateIndexModel<DeadLetterMessage>(Builders<DeadLetterMessage>.IndexKeys.Ascending(x => x.MessageType)),
            new CreateIndexModel<DeadLetterMessage>(Builders<DeadLetterMessage>.IndexKeys.Ascending(x => x.ExceptionType)),
            new CreateIndexModel<DeadLetterMessage>(
                Builders<DeadLetterMessage>.IndexKeys.Ascending(x => x.ExpirationTime),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero })
        });
    }

    public async Task ClearAllAsync()
    {
        await Incoming.DeleteManyAsync(FilterDefinition<IncomingMessage>.Empty);
        await Outgoing.DeleteManyAsync(FilterDefinition<OutgoingMessage>.Empty);
        await DeadLetterDocs.DeleteManyAsync(FilterDefinition<DeadLetterMessage>.Empty);
        await _database.GetCollection<BsonDocument>(MongoConstants.NodeCollection).DeleteManyAsync(new BsonDocument());
        await _database.GetCollection<BsonDocument>(MongoConstants.NodeAssignmentCollection).DeleteManyAsync(new BsonDocument());
        await _database.GetCollection<BsonDocument>(MongoConstants.NodeRecordCollection).DeleteManyAsync(new BsonDocument());
        await _database.GetCollection<BsonDocument>(MongoConstants.AgentRestrictionCollection).DeleteManyAsync(new BsonDocument());
        await _database.GetCollection<BsonDocument>(MongoConstants.CounterCollection).DeleteManyAsync(new BsonDocument());
        await _database.GetCollection<BsonDocument>(MongoConstants.LockCollection).DeleteManyAsync(new BsonDocument());
    }

    public Task DeleteAllHandledAsync()
        => Incoming.DeleteManyAsync(Builders<IncomingMessage>.Filter.Eq(x => x.Status, EnvelopeStatus.Handled));

    public async Task<PersistedCounts> FetchCountsAsync()
    {
        return new PersistedCounts
        {
            Incoming = (int)await Incoming.CountDocumentsAsync(Builders<IncomingMessage>.Filter.Eq(x => x.Status, EnvelopeStatus.Incoming)),
            Scheduled = (int)await Incoming.CountDocumentsAsync(Builders<IncomingMessage>.Filter.Eq(x => x.Status, EnvelopeStatus.Scheduled)),
            Handled = (int)await Incoming.CountDocumentsAsync(Builders<IncomingMessage>.Filter.Eq(x => x.Status, EnvelopeStatus.Handled)),
            Outgoing = (int)await Outgoing.CountDocumentsAsync(FilterDefinition<OutgoingMessage>.Empty),
            DeadLetter = (int)await DeadLetterDocs.CountDocumentsAsync(FilterDefinition<DeadLetterMessage>.Empty)
        };
    }

    public async Task<IReadOnlyList<Envelope>> AllIncomingAsync()
        => (await Incoming.Find(FilterDefinition<IncomingMessage>.Empty).ToListAsync()).Select(x => x.Read()).ToList();

    public async Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
        => (await Outgoing.Find(FilterDefinition<OutgoingMessage>.Empty).ToListAsync()).Select(x => x.Read()).ToList();

    public async Task ReleaseAllOwnershipAsync()
    {
        await Incoming.UpdateManyAsync(Builders<IncomingMessage>.Filter.Ne(x => x.OwnerId, 0),
            Builders<IncomingMessage>.Update.Set(x => x.OwnerId, 0));
        await Outgoing.UpdateManyAsync(Builders<OutgoingMessage>.Filter.Ne(x => x.OwnerId, 0),
            Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, 0));
    }

    public async Task ReleaseAllOwnershipAsync(int ownerId)
    {
        await Incoming.UpdateManyAsync(Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, ownerId),
            Builders<IncomingMessage>.Update.Set(x => x.OwnerId, 0));
        await Outgoing.UpdateManyAsync(Builders<OutgoingMessage>.Filter.Eq(x => x.OwnerId, ownerId),
            Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, 0));
    }

    public async Task CheckConnectivityAsync(CancellationToken token)
        => await _database.RunCommandAsync((Command<BsonDocument>)new BsonDocument("ping", 1), cancellationToken: token);
}
