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
    private readonly Func<Envelope, Guid> _deadLetterKey;
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
        // configured their MongoClient.
        // This handle-level pin governs the SESSIONLESS writes only: MongoDB discards
        // collection/database concerns for anything run inside a transaction, so every
        // transaction this library opens restates it — see MongoTransactionOptions.Durable
        // and InTransactionAsync below.
        // The app-facing IMongoDatabase registered by UseMongoDbPersistence is still NOT
        // pinned: domain writes made OUTSIDE the Wolverine transaction remain the
        // application's choice. Domain writes ENLISTED in it commit at the store's concern,
        // because a transaction has exactly one write concern.
        _database = client.GetDatabase(databaseName)
            .WithWriteConcern(WriteConcern.WMajority.With(journal: true))
            .WithReadConcern(ReadConcern.Majority);

        _inboxIdentity = options.Durability.MessageIdentity == MessageIdentity.IdOnly
            ? e => e.Id.ToString()
            : e => $"{e.Id}|{e.Destination?.ToString().Replace(":/", "").TrimEnd('/')}";

        // The dead-letter document key follows the same identity unit as the inbox, but stays a
        // Guid so the BSON type of _id never changes. Capture the local rather than the field so
        // the dependency between the two assignments is explicit.
        var inboxIdentity = _inboxIdentity;
        _deadLetterKey = options.Durability.MessageIdentity == MessageIdentity.IdOnly
            ? e => e.Id
            : e => DeadLetterIdentity.Derive(inboxIdentity(e));

        Incoming = _database.GetCollection<IncomingMessage>(MongoConstants.IncomingCollection);
        Outgoing = _database.GetCollection<OutgoingMessage>(MongoConstants.OutgoingCollection);
        DeadLetterDocs = _database.GetCollection<DeadLetterMessage>(MongoConstants.DeadLetterCollection);
        NodeDocs = _database.GetCollection<NodeDocument>(MongoConstants.NodeCollection);
        AssignmentDocs = _database.GetCollection<AgentAssignmentDocument>(MongoConstants.NodeAssignmentCollection);
        RecordDocs = _database.GetCollection<NodeRecordDocument>(MongoConstants.NodeRecordCollection);
        RestrictionDocs = _database.GetCollection<AgentRestrictionDocument>(MongoConstants.AgentRestrictionCollection);
        Counters = _database.GetCollection<NodeCounterDocument>(MongoConstants.CounterCollection);
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
    private IDeduplicationStore? _deduplication;

    /// <summary>
    /// Logical message deduplication (GH-4180). Opt-in: with
    /// <c>DurabilitySettings.EnableMessageDeduplication</c> off this is <see cref="NullDeduplicationStore.Instance"/>
    /// and no collection or index is provisioned, so an upgrade is a no-op for hosts that have not asked
    /// for the feature. See <see cref="MongoDbDeduplicationStore"/> for the claim semantics.
    /// </summary>
    public IDeduplicationStore Deduplication => _deduplication ??= _options.Durability.EnableMessageDeduplication
        ? new MongoDbDeduplicationStore(_database)
        : NullDeduplicationStore.Instance;

    private MongoDbRecurringMessageStore? _recurring;

    /// <summary>
    /// Durable recurring-message tracking (cron schedules registered through <c>opts.Schedules</c>).
    /// Opt-in: real store only when <c>DurabilitySettings.EnableRecurringMessages</c> is on AND this is
    /// the <c>Main</c> store (tracking documents live in the main database only, as upstream), otherwise
    /// <see cref="NullRecurringMessageStore.Instance"/> and nothing is provisioned.
    /// </summary>
    public IRecurringMessageStore RecurringMessages
        => _options.Durability.EnableRecurringMessages && Role == MessageStoreRole.Main
            ? _recurring ??= new MongoDbRecurringMessageStore(this, _database)
            : NullRecurringMessageStore.Instance;

    public IMessageStoreAdmin Admin => this;
    public IDeadLetters DeadLetters => this;
    public IScheduledMessages ScheduledMessages => this;

    public void PromoteToMain(IWolverineRuntime runtime) => Role = MessageStoreRole.Main;
    public void DemoteToAncillary() => Role = MessageStoreRole.Ancillary;

    internal string InboxIdentity(Envelope envelope) => _inboxIdentity(envelope);

    /// <summary>
    /// The dead-letter document key. In the default <see cref="MessageIdentity.IdOnly"/> mode this
    /// is the envelope Guid itself, so the stored <c>_id</c> is byte-identical to every release
    /// before the identity split. In <see cref="MessageIdentity.IdAndDestination"/> mode the
    /// identity unit is the <c>(envelope id, destination)</c> pair — exactly as it already is for
    /// the inbox (<see cref="InboxIdentity"/>), and as it is for the RDBMS providers, whose
    /// dead-letter table adds <c>received_at</c> to its primary key in that mode
    /// (<c>Wolverine.Postgresql/Schema/DeadLettersTable.cs:19-26</c>). The framework-facing
    /// envelope Guid lives in <see cref="DeadLetterMessage.EnvelopeId"/>.
    /// </summary>
    internal Guid DeadLetterKey(Envelope envelope) => _deadLetterKey(envelope);

    /// <summary>
    /// The ONLY place this store opens a session + transaction. Centralised so
    /// <see cref="MongoTransactionOptions.Durable"/> can never be forgotten at a new call site:
    /// MongoDB discards the handle-level write/read concern pinned above for anything run inside a
    /// transaction, so an option-less transaction silently commits at the consumer's MongoClient
    /// default.
    /// <para>
    /// <c>WithTransactionAsync</c> transparently retries <c>TransientTransactionError</c> /
    /// <c>UnknownTransactionCommitResult</c> and aborts automatically if the body throws.
    /// </para>
    /// </summary>
    internal async Task InTransactionAsync(Func<IClientSessionHandle, CancellationToken, Task> body,
        CancellationToken cancellation = default)
    {
        using var session = await _client.StartSessionAsync(cancellationToken: cancellation);
        await session.WithTransactionAsync(async (s, ct) =>
        {
            await body(s, ct);
            return true;
        }, MongoTransactionOptions.Durable, cancellation);
    }

    public void Initialize(IWolverineRuntime runtime)
    {
        if (Role == MessageStoreRole.Main
            && runtime.Options.Transports.NodeControlEndpoint == null
            && runtime.Options.Durability.Mode == DurabilityMode.Balanced)
        {
            var transport = new Transport.MongoDbControlTransport(_database, runtime.Options);
            runtime.Options.Transports.Add(transport);
            runtime.Options.Transports.NodeControlEndpoint = transport.ControlEndpoint;
        }

        WarnOnBalancedMode(runtime);
    }

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
            "Wolverine.MongoDB is running in Balanced (multi-node) mode with node control endpoint {ControlUri}. " +
            "Node clocks must be synchronized to well within the lock lease ({Lease}).",
            runtime.Options.Transports.NodeControlEndpoint?.Uri,
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

        // Claim by document _id, not by envelope Guid: in MessageIdentity.IdAndDestination the
        // identity unit is (envelope id, destination), so one Guid can have a document per
        // destination and filtering on EnvelopeId would also claim siblings this caller never
        // loaded — stranding them (owned, but never enqueued for their own listener). In the
        // default IdOnly mode the _id IS the Guid string, so the filter values are unchanged.
        var ids = incoming.Select(InboxIdentity).ToList();

        // Compare-and-swap: only claim envelopes still owned by "any node". An envelope already
        // owned by another node (claimed between our read and this write) must not be stolen —
        // the OwnerId == AnyNode guard makes this an atomic claim per document.
        return Incoming.UpdateManyAsync(
            Builders<IncomingMessage>.Filter.And(
                Builders<IncomingMessage>.Filter.In(x => x.Id, ids),
                Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, MongoConstants.AnyNode)),
            Builders<IncomingMessage>.Update.Set(x => x.OwnerId, ownerId));
    }

    public ValueTask DisposeAsync()
    {
        HasDisposed = true;
        return ValueTask.CompletedTask;
    }
}
