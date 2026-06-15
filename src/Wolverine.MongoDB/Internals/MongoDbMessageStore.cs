using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
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
    private readonly Func<Envelope, string> _inboxIdentity;
    private readonly MongoDbPersistenceOptions _persistenceOptions;

    internal IMongoCollection<IncomingMessage> Incoming { get; }
    internal IMongoCollection<OutgoingMessage> Outgoing { get; }
    internal IMongoCollection<DeadLetterMessage> DeadLetterDocs { get; }

    public MongoDbMessageStore(IMongoClient client, string databaseName, WolverineOptions options)
        : this(client, databaseName, options, new MongoDbPersistenceOptions())
    {
    }

    public MongoDbMessageStore(IMongoClient client, string databaseName, WolverineOptions options,
        MongoDbPersistenceOptions persistenceOptions)
    {
        _persistenceOptions = persistenceOptions;
        _client = client;
        _databaseName = databaseName;
        _options = options;
        // The message store's writes ARE the durability guarantee: pin majority +
        // journaled acknowledgement and majority reads regardless of how the consumer
        // configured their MongoClient. The app-facing IMongoDatabase registered by
        // UseMongoDbPersistence is intentionally NOT pinned — domain write concerns
        // belong to the application.
        _database = client.GetDatabase(databaseName)
            .WithWriteConcern(WriteConcern.WMajority.With(journal: true))
            .WithReadConcern(ReadConcern.Majority);

        _inboxIdentity = options.Durability.MessageIdentity == MessageIdentity.IdOnly
            ? e => e.Id.ToString()
            : e => $"{e.Id}|{e.Destination?.ToString().Replace(":/", "").TrimEnd('/')}";

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

    internal string InboxIdentity(Envelope envelope) => _inboxIdentity(envelope);

    public void Initialize(IWolverineRuntime runtime) => WarnOnBalancedMode(runtime);

    public DatabaseDescriptor Describe() => new(this) { Engine = "mongodb", DatabaseName = _databaseName };

    public Task DrainAsync() => Task.CompletedTask;

    public IAgent StartScheduledJobs(IWolverineRuntime runtime) => BuildAgent(runtime);

    public IAgent BuildAgent(IWolverineRuntime runtime)
    {
        WarnOnBalancedMode(runtime);
        return new MongoDbDurabilityAgent(runtime, this);
    }

    private bool _warnedOnBalanced;

    private void WarnOnBalancedMode(IWolverineRuntime runtime)
    {
        if (runtime.Options.Durability.Mode != DurabilityMode.Balanced || _warnedOnBalanced) return;
        _warnedOnBalanced = true;
        runtime.LoggerFactory.CreateLogger<MongoDbMessageStore>().LogInformation(
            "Wolverine.MongoDB is running in Balanced (multi-node) mode. " +
            "A control endpoint is required (e.g. opts.UseTcpForControlEndpoint()) and node clocks " +
            "must be synchronized to well within the lock lease ({Lease}).",
            _persistenceOptions.LockLeaseDuration);
    }

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

        // Compare-and-swap: only claim envelopes still owned by "any node". An envelope already
        // owned by another node (claimed between our read and this write) must not be stolen —
        // the OwnerId == AnyNode guard makes this an atomic claim per document.
        return Incoming.UpdateManyAsync(
            Builders<IncomingMessage>.Filter.And(
                Builders<IncomingMessage>.Filter.In(x => x.EnvelopeId, ids),
                Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, MongoConstants.AnyNode)),
            Builders<IncomingMessage>.Update.Set(x => x.OwnerId, ownerId));
    }

    public ValueTask DisposeAsync()
    {
        HasDisposed = true;
        return ValueTask.CompletedTask;
    }
}
