using JasperFx.Descriptors;
using MongoDB.Driver;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IMessageStoreWithAgentSupport
{
    private readonly IMongoClient _client;
    private readonly string _databaseName;
    private readonly WolverineOptions _options;
    private readonly IMongoDatabase _database;

    internal IMongoCollection<IncomingMessage> Incoming { get; }
    internal IMongoCollection<OutgoingMessage> Outgoing { get; }
    internal IMongoCollection<DeadLetterMessage> DeadLetterDocs { get; }

    public MongoDbMessageStore(IMongoClient client, string databaseName, WolverineOptions options)
    {
        _client = client;
        _databaseName = databaseName;
        _options = options;
        _database = client.GetDatabase(databaseName);

        Incoming = _database.GetCollection<IncomingMessage>(MongoConstants.IncomingCollection);
        Outgoing = _database.GetCollection<OutgoingMessage>(MongoConstants.OutgoingCollection);
        DeadLetterDocs = _database.GetCollection<DeadLetterMessage>(MongoConstants.DeadLetterCollection);
    }

    public MessageStoreRole Role { get; set; } = MessageStoreRole.Main;
    public List<string> TenantIds { get; } = new();
    public string Name => _databaseName;
    public Uri Uri => new($"{PersistenceConstants.AgentScheme}://mongodb/durability");
    public bool HasDisposed { get; set; }

    public IMessageInbox Inbox => this;
    public IMessageOutbox Outbox => this;
    public INodeAgentPersistence Nodes => this;
    public IListenerStore Listeners { get; protected set; } = NullListenerStore.Instance;
    public IMessageStoreAdmin Admin => this;
    public IDeadLetters DeadLetters => this;
    public IScheduledMessages ScheduledMessages => this;

    public void PromoteToMain(IWolverineRuntime runtime) => Role = MessageStoreRole.Main;
    public void DemoteToAncillary() => Role = MessageStoreRole.Ancillary;

    public void Initialize(IWolverineRuntime runtime) { }

    public DatabaseDescriptor Describe() => new(this) { Engine = "mongodb", DatabaseName = _databaseName };

    public Task DrainAsync() => Task.CompletedTask;

    public IAgent StartScheduledJobs(IWolverineRuntime runtime) => BuildAgent(runtime);

    public IAgent BuildAgent(IWolverineRuntime runtime) => new MongoDbDurabilityAgent(runtime, this);

    public IAgentFamily? BuildAgentFamily(IWolverineRuntime runtime) => null;

    public async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        var b = Builders<IncomingMessage>.Filter;
        var filter = b.And(
            b.Eq(x => x.OwnerId, MongoConstants.AnyNode),
            b.Eq(x => x.ReceivedAt, listenerAddress.ToString()),
            b.Eq(x => x.Status, EnvelopeStatus.Incoming));

        var docs = await Incoming.Find(filter)
            .Sort(Builders<IncomingMessage>.Sort.Ascending(x => x.EnvelopeId))
            .Limit(limit)
            .ToListAsync();

        return docs.Select(x => x.Read()).ToList();
    }

    public Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        if (incoming.Count == 0)
        {
            return Task.CompletedTask;
        }

        var ids = incoming.Select(x => x.Id).ToList();
        return Incoming.UpdateManyAsync(
            Builders<IncomingMessage>.Filter.In(x => x.EnvelopeId, ids),
            Builders<IncomingMessage>.Update.Set(x => x.OwnerId, ownerId));
    }

    public ValueTask DisposeAsync()
    {
        HasDisposed = true;
        return ValueTask.CompletedTask;
    }
}
