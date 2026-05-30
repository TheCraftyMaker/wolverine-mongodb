using Wolverine.Logging;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IMessageStoreAdmin
{
    public Task DeleteAllHandledAsync() => throw new NotImplementedException();
    public Task ClearAllAsync() => throw new NotImplementedException();
    public Task RebuildAsync() => throw new NotImplementedException();
    public Task<PersistedCounts> FetchCountsAsync() => throw new NotImplementedException();
    public Task<IReadOnlyList<Envelope>> AllIncomingAsync() => throw new NotImplementedException();
    public Task<IReadOnlyList<Envelope>> AllOutgoingAsync() => throw new NotImplementedException();
    public Task ReleaseAllOwnershipAsync() => throw new NotImplementedException();
    public Task ReleaseAllOwnershipAsync(int ownerId) => throw new NotImplementedException();
    public Task CheckConnectivityAsync(CancellationToken token) => throw new NotImplementedException();
    public Task MigrateAsync() => throw new NotImplementedException();
}
