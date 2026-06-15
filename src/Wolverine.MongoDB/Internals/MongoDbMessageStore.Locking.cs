using MongoDB.Driver;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore
{
    private IMongoCollection<LockDocument> Locks => _database.GetCollection<LockDocument>(MongoConstants.LockCollection);
    private LockDocument? _leaderLock;

    private Guid NodeId => _options.UniqueNodeId;

    private async Task<bool> TryAttainAsync(string lockId, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var b = Builders<LockDocument>.Filter;
        var filter = b.And(b.Eq(x => x.Id, lockId),
            b.Or(b.Lt(x => x.ExpiresAt, now), b.Eq(x => x.NodeId, NodeId)));
        var update = Builders<LockDocument>.Update
            .Set(x => x.NodeId, NodeId)
            .Set(x => x.ExpiresAt, now.Add(_persistenceOptions.LockLeaseDuration))
            .SetOnInsert(x => x.Id, lockId);

        try
        {
            var doc = await Locks.FindOneAndUpdateAsync(filter, update,
                new FindOneAndUpdateOptions<LockDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, token);
            if (lockId == MongoConstants.LeaderLockId)
            {
                _leaderLock = doc;
            }

            return true;
        }
        catch (MongoCommandException e) when (e.Code == 11000)
        {
            if (lockId == MongoConstants.LeaderLockId) _leaderLock = null;
            return false;
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            if (lockId == MongoConstants.LeaderLockId) _leaderLock = null;
            return false;
        }
    }

    private Task ReleaseAsync(string lockId)
        => Locks.DeleteOneAsync(Builders<LockDocument>.Filter.And(
            Builders<LockDocument>.Filter.Eq(x => x.Id, lockId),
            Builders<LockDocument>.Filter.Eq(x => x.NodeId, NodeId)));

    public bool HasLeadershipLock()
    {
        if (_leaderLock is null || _leaderLock.NodeId != NodeId) return false;

        // Report leadership only while comfortably inside the lease (75%): a paused or
        // clock-skewed node must stop acting as leader BEFORE another node can take over.
        var margin = TimeSpan.FromTicks(_persistenceOptions.LockLeaseDuration.Ticks / 4);
        return _leaderLock.ExpiresAt - margin > DateTime.UtcNow;
    }

    public Task<bool> TryAttainLeadershipLockAsync(CancellationToken token)
        => TryAttainAsync(MongoConstants.LeaderLockId, token);

    public async Task ReleaseLeadershipLockAsync()
    {
        await ReleaseAsync(MongoConstants.LeaderLockId);
        _leaderLock = null;
    }

    internal Task<bool> TryAttainScheduledJobLockAsync(CancellationToken token)
        => TryAttainAsync(MongoConstants.ScheduledLockId, token);

    internal Task ReleaseScheduledJobLockAsync() => ReleaseAsync(MongoConstants.ScheduledLockId);
}
