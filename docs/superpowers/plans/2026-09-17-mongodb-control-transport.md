# MongoDB Control Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `DurabilityMode.Balanced` a native MongoDB-backed control transport so a host that calls only `UseMongoDbPersistence()` coordinates its nodes without `UseTcpForControlEndpoint()`.

**Architecture:** A `mongocontrol://<node guid>` transport with one endpoint per node, a sender that inserts one document per control message into `wolverine_control_messages`, and a listener that polls that collection once a second for its own node id and deletes what it delivered. `MongoDbMessageStore.Initialize` registers the transport when the store is Main, the mode is Balanced and no control endpoint was configured, exactly as the RavenDb and Cosmos stores do. Template: `external/wolverine/src/Persistence/Wolverine.RavenDb/Internals/Transport/`.

**Tech Stack:** .NET 9/10, MongoDB.Driver 3.x, WolverineFx 6.38.0 (`external/wolverine` submodule at `V6.38.0`), JasperFx.Core `Cache`/`RetryBlock`, xUnit v3, Shouldly, Testcontainers MongoDB replica set.

**Spec:** `docs/superpowers/plans/2026-09-17-mongodb-control-transport-design.md` (the repo ignores `docs/superpowers/specs`, so the design sits beside this plan)

## Global Constraints

- Branch `feat/control-transport`, worktree `.claude/worktrees/control-transport`, one PR.
- Mirror `Wolverine.RavenDb/Internals/Transport` member for member unless the spec says otherwise. Do not invent extra configuration.
- Scheme is `mongocontrol`. Collection is `wolverine_control_messages`. Poll every 1 second, `DeliverWithin` 10 seconds, document `expires` 30 seconds, TTL index `ExpireAfter = TimeSpan.Zero`.
- Indexes are created only when `Durability.Mode == Balanced`. `ClearAllAsync` clears the collection unconditionally.
- Never disable NuGet auditing, never weaken `TreatWarningsAsErrors`. Every cancellation-token-accepting call in tests passes `TestContext.Current.CancellationToken` (analyzer xUnit1051 is an error).
- Two-host tests carry `[Trait("Category", "multinode")]` and `[Collection("mongodb")]`. Run `Category=multinode` serially.
- After any change under `src/Wolverine.MongoDB/Internals`, delete generated code before building: `Remove-Item -Recurse -Force src\Wolverine.MongoDB.Tests\Internal\Generated -ErrorAction SilentlyContinue`.
- Build both TFMs (`net9.0`, `net10.0`) before the PR. Docker Desktop must be running for every test step.
- No em-dashes anywhere in code comments or docs. Run the humanizer on new comments and on the README and CHANGELOG text.
- Commit after every task with the conventional message given in the task. Do not push and do not open the PR until Task 8 says so.

---

## Verified facts (2026-09-17, submodule at V6.38.0)

- `MongoDbMessageStore.Initialize(IWolverineRuntime runtime)` exists at `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs:153` and today only calls `WarnOnBalancedMode(runtime)`. `Role`, `_database` (pinned majority+journaled) and `_options` are available on the store.
- `RavenDbMessageStore.Initialize` registers its transport under `Role == MessageStoreRole.Main && runtime.Options.Transports.NodeControlEndpoint == null && runtime.Options.Durability.Mode == DurabilityMode.Balanced` and sets `runtime.Options.Transports.NodeControlEndpoint = transport.ControlEndpoint` (`Wolverine.RavenDb/Internals/RavenDbMessageStore.cs:79-91`). Cosmos does the same (`CosmosDbMessageStore.cs:81-88`).
- `WolverineNode.For` throws `ArgumentOutOfRangeException("ControlEndpoint cannot be null for this usage")` in Balanced mode without a control endpoint (`Wolverine/Runtime/Agents/WolverineNode.cs:31-34`). Wolverine calls the store's `Initialize` first.
- `TransportComplianceFixture` (`external/wolverine/src/Testing/Wolverine.ComplianceTests/Compliance/TransportCompliance.cs`) constructor is `(Uri destination, int defaultTimeInSeconds = 5)`; members used: `Mode`, `MustReset`, `ReceiverIs(Action<WolverineOptions>)`, `SenderIs(Action<WolverineOptions>)`, `Receiver`, `OutboundAddress { protected set; }`, `AfterDisposeAsync()`. `TransportCompliance<T>` requires `T : TransportComplianceFixture, new()` and carries 23 facts.
- Reserved system collection names live in `MongoCollectionNaming._reservedNames` (`src/Wolverine.MongoDB/Internals/MongoCollectionNaming.cs:96-109`). The clear-all sweep is `MongoDbMessageStore.Admin.cs:111-127`, index creation is `EnsureIndexesAsync` in the same file starting at line 18.
- `AppFixture` (`src/Wolverine.MongoDB.Tests/AppFixture.cs`) exposes `Client`, `DatabaseName`, `ClearAll()` and a public `InitializeAsync()` that starts the shared container; it is `new()`-constructible.
- Existing two-host tests that call `opts.UseTcpForControlEndpoint()`: `multinode_end_to_end.cs:50`, `leadership_election_compliance.cs:43`, `saga_multinode.cs:75`, `entity_multinode.cs:68`, `durability_mode_guard.cs:24`; `exclusive_listener_recovery_compliance.cs` (check its host builder while editing).

---

### Task 1: Collection constant, document type, indexes, sweep, reserved name

**Files:**
- Modify: `src/Wolverine.MongoDB/Internals/MongoConstants.cs`
- Create: `src/Wolverine.MongoDB/Internals/ControlMessageDocument.cs`
- Modify: `src/Wolverine.MongoDB/Internals/MongoCollectionNaming.cs:96-109`
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Admin.cs` (`EnsureIndexesAsync`, `ClearAllAsync`)
- Test: `src/Wolverine.MongoDB.Tests/control_collection.cs`

**Interfaces:**
- Produces: `MongoConstants.ControlMessagesCollection = "wolverine_control_messages"`; `public class ControlMessageDocument { Guid Id; Guid NodeId; string MessageType; byte[] Body; DateTime Expires; DateTime Posted; static ControlMessageDocument For(Envelope, Guid nodeId, DateTime expires) }`.

- [ ] **Step 1: Write the failing tests**

```csharp
// src/Wolverine.MongoDB.Tests/control_collection.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;
using Wolverine.Transports.Tcp;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class control_collection
{
    private readonly AppFixture _fixture;
    public control_collection(AppFixture fixture) => _fixture = fixture;

    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    [Fact]
    public async Task balanced_host_provisions_the_control_collection_indexes()
    {
        await _fixture.ClearAll();
        await Database.DropCollectionAsync(MongoConstants.ControlMessagesCollection,
            TestContext.Current.CancellationToken);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                // Task 1 has no control transport yet, so an explicit endpoint keeps the host starting.
                opts.UseTcpForControlEndpoint();
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(TestContext.Current.CancellationToken);

        var indexes = await (await Database.GetCollection<BsonDocument>(MongoConstants.ControlMessagesCollection)
            .Indexes.ListAsync(TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken);

        indexes.ShouldContain(i => i["key"].AsBsonDocument.Contains("nodeId") && i["key"].AsBsonDocument.Contains("posted"));
        indexes.ShouldContain(i => i["key"].AsBsonDocument.Contains("expires") && i.Contains("expireAfterSeconds")
                                   && i["expireAfterSeconds"].ToInt64() == 0);
    }

    [Fact]
    public async Task solo_host_creates_no_control_collection()
    {
        await _fixture.ClearAll();
        await Database.DropCollectionAsync(MongoConstants.ControlMessagesCollection,
            TestContext.Current.CancellationToken);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(TestContext.Current.CancellationToken);

        var names = await (await Database.ListCollectionNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);
        names.ShouldNotContain(MongoConstants.ControlMessagesCollection);
    }

    [Fact]
    public void the_control_collection_name_is_reserved()
    {
        var options = new MongoDbPersistenceOptions();
        Should.Throw<InvalidOperationException>(
            () => options.MapEntityCollection<ReservedProbe>(MongoConstants.ControlMessagesCollection));
    }

    public class ReservedProbe
    {
        public Guid Id { get; set; }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```powershell
Remove-Item -Recurse -Force src\Wolverine.MongoDB.Tests\Internal\Generated -ErrorAction SilentlyContinue
dotnet test src/Wolverine.MongoDB.Tests -f net10.0 --filter "FullyQualifiedName~control_collection"
```
Expected: compile error, `MongoConstants.ControlMessagesCollection` does not exist.

- [ ] **Step 3: Add the constant**

In `src/Wolverine.MongoDB/Internals/MongoConstants.cs`, after `RecurringMessagesCollection`:

```csharp
    public const string ControlMessagesCollection = "wolverine_control_messages";
```

- [ ] **Step 4: Add the document type**

```csharp
// src/Wolverine.MongoDB/Internals/ControlMessageDocument.cs
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// One inter-node control message addressed to a single node. Written by the control sender, read
/// and deleted by the target node's control listener. The TTL index on <see cref="Expires"/> reaps
/// anything a dead node never collected.
/// </summary>
public class ControlMessageDocument
{
    [BsonId] [BsonGuidRepresentation(GuidRepresentation.Standard)] public Guid Id { get; set; }
    [BsonElement("nodeId")] [BsonGuidRepresentation(GuidRepresentation.Standard)] public Guid NodeId { get; set; }
    [BsonElement("messageType")] public string MessageType { get; set; } = string.Empty;
    [BsonElement("body")] public byte[] Body { get; set; } = [];
    [BsonElement("expires")] public DateTime Expires { get; set; }
    [BsonElement("posted")] public DateTime Posted { get; set; }

    public static ControlMessageDocument For(Envelope envelope, Guid nodeId, DateTime expires) => new()
    {
        Id = envelope.Id,
        NodeId = nodeId,
        MessageType = envelope.MessageType ?? string.Empty,
        Body = EnvelopeSerializer.Serialize(envelope),
        Expires = expires,
        Posted = DateTime.UtcNow
    };
}
```

- [ ] **Step 5: Reserve the name**

In `MongoCollectionNaming._reservedNames`, add after `MongoConstants.RecurringMessagesCollection`:

```csharp
        MongoConstants.RecurringMessagesCollection,
        MongoConstants.ControlMessagesCollection
```

- [ ] **Step 6: Provision the indexes and extend the sweep**

In `MongoDbMessageStore.Admin.cs`, inside `EnsureIndexesAsync`, after the deduplication block:

```csharp
        // The control collection only exists for Balanced hosts: Solo has no peers to coordinate, and
        // gating here keeps a Solo deployment's collection set unchanged. Sweeping it below is
        // unconditional so a rebuild always clears everything.
        if (_options.Durability.Mode == DurabilityMode.Balanced)
        {
            var control = _database.GetCollection<ControlMessageDocument>(MongoConstants.ControlMessagesCollection);
            await control.Indexes.CreateManyAsync(new[]
            {
                new CreateIndexModel<ControlMessageDocument>(Builders<ControlMessageDocument>.IndexKeys
                    .Ascending(x => x.NodeId).Ascending(x => x.Posted)),
                new CreateIndexModel<ControlMessageDocument>(
                    Builders<ControlMessageDocument>.IndexKeys.Ascending(x => x.Expires),
                    new CreateIndexOptions { ExpireAfter = TimeSpan.Zero })
            });
        }
```

In `ClearAllAsync`, after the `RecurringMessagesCollection` line:

```csharp
        await _database.GetCollection<BsonDocument>(MongoConstants.ControlMessagesCollection).DeleteManyAsync(new BsonDocument());
```

- [ ] **Step 7: Run the tests to verify they pass**

Run the same command as Step 2. Expected: 3 passed.

- [ ] **Step 8: Run the naming suite, which pins the reserved list**

```powershell
dotnet test src/Wolverine.MongoDB.Tests -f net10.0 --filter "FullyQualifiedName~collection_naming|FullyQualifiedName~collection_name_collision_guard"
```
Expected: all pass. If a fact enumerates the reserved names explicitly, add `wolverine_control_messages` to it.

- [ ] **Step 9: Commit**

```powershell
git add src/Wolverine.MongoDB/Internals/MongoConstants.cs src/Wolverine.MongoDB/Internals/ControlMessageDocument.cs src/Wolverine.MongoDB/Internals/MongoCollectionNaming.cs src/Wolverine.MongoDB/Internals/MongoDbMessageStore.Admin.cs src/Wolverine.MongoDB.Tests/control_collection.cs
git commit -m "feat(control): provision the wolverine_control_messages collection for Balanced hosts"
```

---

### Task 2: Transport, endpoint, sender, listener, and registration in the store

**Files:**
- Create: `src/Wolverine.MongoDB/Internals/Transport/MongoDbControlTransport.cs`
- Create: `src/Wolverine.MongoDB/Internals/Transport/MongoDbControlEndpoint.cs`
- Create: `src/Wolverine.MongoDB/Internals/Transport/MongoDbControlSender.cs`
- Create: `src/Wolverine.MongoDB/Internals/Transport/MongoDbControlListener.cs`
- Modify: `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs:153` (`Initialize`) and `WarnOnBalancedMode`
- Test: `src/Wolverine.MongoDB.Tests/control_queue_tests.cs`

**Interfaces:**
- Consumes: `ControlMessageDocument`, `MongoConstants.ControlMessagesCollection` from Task 1.
- Produces: `internal class MongoDbControlTransport(IMongoDatabase database, WolverineOptions options)` with `const string ProtocolName = "mongocontrol"`, `MongoDbControlEndpoint ControlEndpoint`, `IMongoCollection<ControlMessageDocument> Messages`, `Task DeleteEnvelopesAsync(List<Envelope>)`; `internal class MongoDbControlEndpoint(MongoDbControlTransport parent, Guid nodeId) : Endpoint` with `Guid NodeId`.

- [ ] **Step 1: Write the failing two-node tests**

```csharp
// src/Wolverine.MongoDB.Tests/control_queue_tests.cs
using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.MongoDB.Tests;

// Two Balanced hosts on one database: control messages written by one node must be read by the other.
[Trait("Category", "multinode")]
[Collection("mongodb")]
public class control_queue_tests : IAsyncLifetime
{
    private readonly AppFixture _fixture;
    private IHost _sender = null!;
    private IHost _receiver = null!;
    private Uri _receiverUri = null!;

    public control_queue_tests(AppFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ClearAll();

        _sender = await StartNode("Sender");
        _receiver = await StartNode("Receiver");

        var nodeId = _receiver.GetRuntime().Options.UniqueNodeId;
        _receiverUri = new Uri($"mongocontrol://{nodeId}");
    }

    private Task<IHost> StartNode(string serviceName) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.ServiceName = serviceName;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Discovery.IncludeType(typeof(ControlQueueMessageHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync(TestContext.Current.CancellationToken);
        _sender.Dispose();
        await _receiver.StopAsync(TestContext.Current.CancellationToken);
        _receiver.Dispose();
    }

    [Fact]
    public void control_endpoint_is_wired_up_in_balanced_mode()
    {
        // Regression: in Balanced mode a null NodeControlEndpoint makes WolverineNode.For throw
        // "ControlEndpoint cannot be null for this usage".
        var endpoint = _sender.GetRuntime().Options.Transports.NodeControlEndpoint;
        endpoint.ShouldNotBeNull();
        endpoint.Uri.Scheme.ShouldBe("mongocontrol");
    }

    [Fact]
    public async Task send_message_from_one_to_another()
    {
        var tracked = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(m => m.EndpointFor(_receiverUri).SendAsync(new ControlCommand(10)));

        tracked.Sent.RecordsInOrder().Single(x => x.Envelope!.Message?.GetType() == typeof(ControlCommand))
            .ServiceName!.ShouldBe("Sender");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope!.Message?.GetType() == typeof(ControlCommand))
            .ServiceName!.ShouldBe("Receiver");
    }

    [Fact]
    public async Task request_reply_message_from_one_to_another()
    {
        var (tracked, result) = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(60.Seconds())
            .InvokeAndWaitAsync<ControlResult>(new ControlQuery(13), _receiverUri);

        result!.Number.ShouldBe(13);

        tracked.Sent.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(ControlQuery))
            .ServiceName!.ShouldBe("Sender");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(ControlQuery))
            .ServiceName!.ShouldBe("Receiver");
        tracked.Sent.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(ControlResult))
            .ServiceName!.ShouldBe("Receiver");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(ControlResult))
            .ServiceName!.ShouldBe("Sender");
    }
}

public record ControlQuery(int Number);
public record ControlResult(int Number);
public record ControlCommand(int Number);

public static class ControlQueueMessageHandler
{
    public static ControlResult Handle(ControlQuery query) => new(query.Number);

    public static void Handle(ControlCommand command) => Debug.WriteLine($"Got command {command.Number}");

    public static void Handle(ControlResult result) => Debug.WriteLine($"Got result {result.Number}");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
Remove-Item -Recurse -Force src\Wolverine.MongoDB.Tests\Internal\Generated -ErrorAction SilentlyContinue
dotnet test src/Wolverine.MongoDB.Tests -f net10.0 --filter "FullyQualifiedName~control_queue_tests"
```
Expected: `InitializeAsync` fails with `ArgumentOutOfRangeException: ControlEndpoint cannot be null for this usage`.

- [ ] **Step 3: Write the endpoint**

```csharp
// src/Wolverine.MongoDB/Internals/Transport/MongoDbControlEndpoint.cs
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.MongoDB.Internals.Transport;

internal class MongoDbControlEndpoint : Endpoint
{
    private readonly MongoDbControlTransport _parent;

    public MongoDbControlEndpoint(MongoDbControlTransport parent, Guid nodeId)
        : base(new Uri($"{MongoDbControlTransport.ProtocolName}://{nodeId}"), EndpointRole.System)
    {
        _parent = parent;
        NodeId = nodeId;
        Mode = EndpointMode.BufferedInMemory;
        MaxDegreeOfParallelism = 1;
        BrokerRole = "queue";

        // Node coordination traffic is not application telemetry.
        TelemetryEnabled = false;
    }

    public Guid NodeId { get; }

    // Durable would route every control envelope through the inbox and outbox the durability agent
    // itself owns, which deadlocks agent assignment; Inline would skip the batched poll. Locked to
    // BufferedInMemory whatever a global endpoint policy says.
    protected override bool supportsMode(EndpointMode mode) => mode == EndpointMode.BufferedInMemory;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        => new(new MongoDbControlListener(_parent, this, receiver,
            runtime.LoggerFactory.CreateLogger<MongoDbControlListener>(), runtime.Options.Durability.Cancellation));

    protected override ISender CreateSender(IWolverineRuntime runtime)
        => new MongoDbControlSender(this, _parent, runtime.LoggerFactory.CreateLogger<MongoDbControlSender>(),
            runtime.Options.Durability.Cancellation);
}
```

- [ ] **Step 4: Write the transport**

```csharp
// src/Wolverine.MongoDB/Internals/Transport/MongoDbControlTransport.cs
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.MongoDB.Internals.Transport;

/// <summary>
/// Node control channel over the message store's own database: one <c>mongocontrol://&lt;node id&gt;</c>
/// endpoint per node, one document per control message in <c>wolverine_control_messages</c>.
/// Registered by <see cref="MongoDbMessageStore.Initialize"/> for Balanced hosts that configured no
/// other control endpoint.
/// </summary>
internal class MongoDbControlTransport : ITransport, IAsyncDisposable
{
    public const string ProtocolName = "mongocontrol";

    private readonly Cache<Guid, MongoDbControlEndpoint> _endpoints;
    private RetryBlock<List<Envelope>>? _deleteBlock;

    public MongoDbControlTransport(IMongoDatabase database, WolverineOptions options)
    {
        Options = options;
        Messages = database.GetCollection<ControlMessageDocument>(MongoConstants.ControlMessagesCollection);
        _endpoints = new Cache<Guid, MongoDbControlEndpoint>(nodeId => new MongoDbControlEndpoint(this, nodeId));
        ControlEndpoint = _endpoints[options.UniqueNodeId];
    }

    public WolverineOptions Options { get; }
    internal IMongoCollection<ControlMessageDocument> Messages { get; }
    public MongoDbControlEndpoint ControlEndpoint { get; }

    public string Protocol => ProtocolName;
    public string Name => "MongoDB control message transport for Wolverine node coordination";

    public Endpoint ReplyEndpoint() => ControlEndpoint;

    public Endpoint GetOrCreateEndpoint(Uri uri) => _endpoints[Guid.Parse(uri.Host)];

    public Endpoint? TryGetEndpoint(Uri uri)
        => _endpoints.TryFind(Guid.Parse(uri.Host), out var endpoint) ? endpoint : null;

    public IEnumerable<Endpoint> Endpoints() => _endpoints;

    public ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        foreach (var endpoint in Endpoints()) endpoint.Compile(runtime);

        _deleteBlock = new RetryBlock<List<Envelope>>(deleteAsync,
            runtime.LoggerFactory.CreateLogger<MongoDbControlTransport>(), runtime.Options.Durability.Cancellation);
        return ValueTask.CompletedTask;
    }

    public bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = default!;
        return false;
    }

    public bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = default;
        return false;
    }

    public Task DeleteEnvelopesAsync(List<Envelope> envelopes)
        => _deleteBlock?.PostAsync(envelopes)
           ?? throw new InvalidOperationException("The MongoDbControlTransport has not been initialized");

    private Task deleteAsync(List<Envelope> envelopes, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested || envelopes.Count == 0) return Task.CompletedTask;

        var ids = envelopes.Select(x => x.Id);
        return Messages.DeleteManyAsync(Builders<ControlMessageDocument>.Filter.In(x => x.Id, ids), cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_deleteBlock == null) return;

        try
        {
            await _deleteBlock.DrainAsync();
        }
        catch (TaskCanceledException)
        {
        }

        _deleteBlock.SafeDispose();
    }
}
```

- [ ] **Step 5: Write the sender**

```csharp
// src/Wolverine.MongoDB/Internals/Transport/MongoDbControlSender.cs
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Wolverine.Transports.Sending;

namespace Wolverine.MongoDB.Internals.Transport;

internal class MongoDbControlSender : ISender, IAsyncDisposable
{
    private readonly MongoDbControlEndpoint _endpoint;
    private readonly MongoDbControlTransport _transport;
    private readonly RetryBlock<Envelope> _retryBlock;

    public MongoDbControlSender(MongoDbControlEndpoint endpoint, MongoDbControlTransport transport,
        ILogger logger, CancellationToken cancellation)
    {
        _endpoint = endpoint;
        _transport = transport;
        Destination = endpoint.Uri;
        _retryBlock = new RetryBlock<Envelope>(sendAsync, logger, cancellation);
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        try
        {
            await _transport.Messages.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        envelope.DeliverWithin = 10.Seconds();
        await _retryBlock.PostAsync(envelope);
    }

    private async Task sendAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested) return;

        var document = ControlMessageDocument.For(envelope, _endpoint.NodeId, DateTime.UtcNow.AddSeconds(30));

        try
        {
            await _transport.Messages.InsertOneAsync(document, cancellationToken: cancellation);
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The retry block re-posted an envelope whose first insert did land. Nothing to do.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _retryBlock.DrainAsync();
        _retryBlock.Dispose();
    }
}
```

- [ ] **Step 6: Write the listener**

```csharp
// src/Wolverine.MongoDB/Internals/Transport/MongoDbControlListener.cs
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.MongoDB.Internals.Transport;

internal class MongoDbControlListener : IListener
{
    private readonly CancellationTokenSource _cancellation;
    private readonly IReceiver _receiver;
    private readonly MongoDbControlTransport _transport;
    private readonly RetryBlock<Envelope> _completeBlock;
    private readonly Task _receivingLoop;

    public MongoDbControlListener(MongoDbControlTransport transport, MongoDbControlEndpoint endpoint,
        IReceiver receiver, ILogger<MongoDbControlListener> logger, CancellationToken cancellation)
    {
        _transport = transport;
        _receiver = receiver;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Address = endpoint.Uri;

        _completeBlock = new RetryBlock<Envelope>(deleteAsync, logger, cancellation);

        _receivingLoop = Task.Run(async () =>
        {
            // Spread the first poll so two nodes started together do not hit the collection in lockstep.
            await Task.Delay(Random.Shared.Next(100, 1000).Milliseconds(), _cancellation.Token);

            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await pollAsync();
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error polling the MongoDB control queue for node {NodeId}",
                        transport.Options.UniqueNodeId);
                }

                await Task.Delay(1.Seconds(), _cancellation.Token);
            }
        }, _cancellation.Token);
    }

    public Uri Address { get; }
    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public async ValueTask CompleteAsync(Envelope envelope) => await _completeBlock.PostAsync(envelope);

    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

    public async ValueTask StopAsync() => await _cancellation.CancelAsync();

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _receivingLoop.SafeDispose();
        _completeBlock.SafeDispose();
    }

    private async Task pollAsync()
    {
        // The expiry predicate closes the window between a message's expiry and the TTL monitor's
        // next sweep (up to a minute), so a stale agent command is never delivered late.
        var filter = Builders<ControlMessageDocument>.Filter.And(
            Builders<ControlMessageDocument>.Filter.Eq(x => x.NodeId, _transport.Options.UniqueNodeId),
            Builders<ControlMessageDocument>.Filter.Gt(x => x.Expires, DateTime.UtcNow));

        var documents = await _transport.Messages.Find(filter)
            .SortBy(x => x.Posted)
            .ToListAsync(_cancellation.Token);

        if (documents.Count == 0) return;

        var envelopes = documents.Select(d => EnvelopeSerializer.Deserialize(d.Body)).ToArray();

        await _receiver.ReceivedAsync(this, envelopes);
        await _transport.DeleteEnvelopesAsync(envelopes.ToList());
    }

    private Task deleteAsync(Envelope envelope, CancellationToken cancellation)
        => _transport.Messages.DeleteOneAsync(
            Builders<ControlMessageDocument>.Filter.Eq(x => x.Id, envelope.Id), cancellation);
}
```

- [ ] **Step 7: Register the transport in the store and reword the warning**

In `src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs`, replace the one-line `Initialize` and the body of `WarnOnBalancedMode`:

```csharp
    public void Initialize(IWolverineRuntime runtime)
    {
        // Balanced hosts need a control endpoint before WolverineNode.For runs, or it throws
        // "ControlEndpoint cannot be null for this usage". Register the native one unless the host
        // already supplied its own (TCP, a broker's control queues): a configured endpoint always wins.
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
```

```csharp
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
```

- [ ] **Step 8: Run the tests to verify they pass**

Run the Step 2 command. Expected: 3 passed. If `send_message_from_one_to_another` times out, check that `EnsureIndexesAsync` ran for a Balanced host (Task 1) and that both hosts share `AppFixture.DatabaseName`.

- [ ] **Step 9: Commit**

```powershell
git add src/Wolverine.MongoDB/Internals/Transport src/Wolverine.MongoDB/Internals/MongoDbMessageStore.cs src/Wolverine.MongoDB.Tests/control_queue_tests.cs
git commit -m "feat(control): native mongocontrol transport registered for Balanced hosts"
```

---

### Task 3: Guard facts: no explicit endpoint needed, explicit endpoint kept, expired messages skipped

**Files:**
- Modify: `src/Wolverine.MongoDB.Tests/durability_mode_guard.cs`
- Modify: `src/Wolverine.MongoDB.Tests/control_collection.cs`

**Interfaces:**
- Consumes: `MongoDbControlTransport.ProtocolName`, `ControlMessageDocument`, `MongoConstants.ControlMessagesCollection`.

- [ ] **Step 1: Write the failing facts**

In `durability_mode_guard.cs`, add `using Wolverine.Runtime;` and these facts (keep the two existing ones):

```csharp
    [Fact]
    public async Task balanced_mode_starts_without_an_explicit_control_endpoint()
    {
        await _fixture.ClearAll();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(TestContext.Current.CancellationToken);

        var endpoint = host.GetRuntime().Options.Transports.NodeControlEndpoint;
        endpoint.ShouldNotBeNull();
        endpoint.Uri.Scheme.ShouldBe("mongocontrol");
        endpoint.Uri.Host.ShouldBe(host.GetRuntime().Options.UniqueNodeId.ToString());
    }

    [Fact]
    public async Task an_explicitly_configured_control_endpoint_is_kept()
    {
        await _fixture.ClearAll();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.UseTcpForControlEndpoint();
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(TestContext.Current.CancellationToken);

        host.GetRuntime().Options.Transports.NodeControlEndpoint!.Uri.Scheme.ShouldBe("tcp");
    }
```

In `control_collection.cs`, add `using Wolverine.Runtime;` and `using Wolverine.MongoDB.Internals.Transport;` is not needed (the type is internal); add this fact:

```csharp
    [Fact]
    public async Task an_expired_control_message_is_never_delivered()
    {
        await _fixture.ClearAll();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Discovery.IncludeType(typeof(ExpiryProbeHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var envelope = new Envelope(new ExpiryProbe()) { Id = Guid.NewGuid(), MessageType = typeof(ExpiryProbe).FullName };
        var document = ControlMessageDocument.For(envelope, runtime.Options.UniqueNodeId,
            DateTime.UtcNow.AddSeconds(-5));

        await Database.GetCollection<ControlMessageDocument>(MongoConstants.ControlMessagesCollection)
            .InsertOneAsync(document, cancellationToken: TestContext.Current.CancellationToken);

        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        ExpiryProbeHandler.Received.ShouldBe(0);
    }

    public record ExpiryProbe;

    public static class ExpiryProbeHandler
    {
        public static int Received;
        public static void Handle(ExpiryProbe probe) => Interlocked.Increment(ref Received);
    }
```

Add `using JasperFx.Core;` to `control_collection.cs` for `3.Seconds()`. `Envelope(object message)` is Wolverine's public constructor; `MessageType` is settable.

- [ ] **Step 2: Run the facts to verify the new ones fail or pass for the right reason**

```powershell
dotnet test src/Wolverine.MongoDB.Tests -f net10.0 --filter "FullyQualifiedName~durability_mode_guard|FullyQualifiedName~control_collection"
```
Expected: all pass, because Task 2 already registers the transport. These facts exist to fail later if the registration condition or the expiry filter regresses. If `an_expired_control_message_is_never_delivered` fails with a serialization error, the `Envelope` built in the test lacks a serializer: set `envelope.Data = runtime.Options.DefaultSerializer.Write(envelope)` before `For(...)` and re-run.

- [ ] **Step 3: Commit**

```powershell
git add src/Wolverine.MongoDB.Tests/durability_mode_guard.cs src/Wolverine.MongoDB.Tests/control_collection.cs
git commit -m "test(control): guard the registration condition and the expiry filter"
```

---

### Task 4: Upstream transport compliance suite over the control transport

**Files:**
- Create: `src/Wolverine.MongoDB.Tests/control_transport_compliance.cs`

**Interfaces:**
- Consumes: `TransportComplianceFixture`, `TransportCompliance<T>` from `Wolverine.ComplianceTests.Compliance`; `AppFixture`.

- [ ] **Step 1: Write the fixture and suite**

```csharp
// src/Wolverine.MongoDB.Tests/control_transport_compliance.cs
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;

namespace Wolverine.MongoDB.Tests;

// The upstream transport contract (send by destination, request/reply, listener stop/restart,
// correlation, scheduling) run over the mongocontrol transport. Same shape as
// RavenDbTests/control_transport_compliance.cs.
public class MongoDbControlTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    private readonly AppFixture _mongo = new();

    public MongoDbControlTransportFixture() : base(new Uri("mongocontrol://placeholder"), 30)
    {
        Mode = DurabilityMode.Balanced;
        MustReset = false;
    }

    public async ValueTask InitializeAsync()
    {
        await _mongo.InitializeAsync();
        await _mongo.ClearAll();

        await ReceiverIs(opts =>
        {
            opts.Services.AddSingleton<IMongoClient>(_mongo.Client);
            opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            tightenClusterCadence(opts);
        });

        var receiverNodeId = Receiver.Services.GetRequiredService<IWolverineRuntime>().Options.UniqueNodeId;
        OutboundAddress = new Uri($"mongocontrol://{receiverNodeId}");

        await SenderIs(opts =>
        {
            opts.Services.AddSingleton<IMongoClient>(_mongo.Client);
            opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            tightenClusterCadence(opts);
        });
    }

    private static void tightenClusterCadence(WolverineOptions opts)
    {
        opts.Durability.CheckAssignmentPeriod = 1.Seconds();
        opts.Durability.HealthCheckPollingTime = 1.Seconds();
        opts.Durability.ScheduledJobPollingTime = 1.Seconds();
        opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
    }

    public new async ValueTask DisposeAsync() => await ValueTask.CompletedTask;
}

[Trait("Category", "multinode")]
[Collection("mongodb")]
public class control_transport_compliance : TransportCompliance<MongoDbControlTransportFixture>;
```

- [ ] **Step 2: Run the suite**

```powershell
dotnet test src/Wolverine.MongoDB.Tests -f net10.0 --filter "FullyQualifiedName~control_transport_compliance"
```
Expected: 23 passed. Two facts are worth knowing about if they fail: `schedule_send` needs the tightened `ScheduledJobPollingTime` above; `can_stop_and_restart_listeners` needs `MongoDbControlListener.StopAsync` to cancel the loop (Task 2, Step 6). If a fact fails only because two hosts collide on `AppFixture.DatabaseName` with another running class, re-run with `--filter "FullyQualifiedName~control_transport_compliance"` alone; the suite must be green on its own before Task 8.

- [ ] **Step 3: Commit**

```powershell
git add src/Wolverine.MongoDB.Tests/control_transport_compliance.cs
git commit -m "test(control): run the upstream TransportCompliance suite over mongocontrol"
```

---

### Task 5: Existing two-host suites run on the native transport

**Files:**
- Modify: `src/Wolverine.MongoDB.Tests/multinode_end_to_end.cs:38-50`
- Modify: `src/Wolverine.MongoDB.Tests/leadership_election_compliance.cs:41-43`
- Modify: `src/Wolverine.MongoDB.Tests/saga_multinode.cs:65-75`
- Modify: `src/Wolverine.MongoDB.Tests/entity_multinode.cs:57-68`
- Modify: `src/Wolverine.MongoDB.Tests/exclusive_listener_recovery_compliance.cs` (if it calls `UseTcpForControlEndpoint`)

- [ ] **Step 1: Remove the TCP lines and their comments**

In each file, delete the `opts.UseTcpForControlEndpoint();` call and the comment block above it that explains the OS-assigned port or says MongoDB has no native control transport. Remove the now-unused `using Wolverine.Transports.Tcp;`. Do not touch `durability_mode_guard.cs`: its `an_explicitly_configured_control_endpoint_is_kept` fact is the one place TCP stays on purpose.

- [ ] **Step 2: Run the multinode category on both TFMs, serially**

```powershell
Remove-Item -Recurse -Force src\Wolverine.MongoDB.Tests\Internal\Generated -ErrorAction SilentlyContinue
dotnet test src/Wolverine.MongoDB.Tests -f net10.0 --filter "Category=multinode"
dotnet test src/Wolverine.MongoDB.Tests -f net9.0 --filter "Category=multinode"
```
Expected: all pass, including the 17 leadership facts, `scheduled_message_executes_exactly_once_across_two_nodes` and the dead-node rescue fact. These now prove leader election and agent handoff over the real control channel.

- [ ] **Step 3: Commit**

```powershell
git add src/Wolverine.MongoDB.Tests
git commit -m "test(multinode): coordinate the two-host suites over the native control transport"
```

---

### Task 6: Documentation

**Files:**
- Modify: `CHANGELOG.md` (under `## [Unreleased]`)
- Modify: `README.md` (section `## Multinode support`, lines 507 to 545)
- Modify: `CLAUDE.md` (repository layout, MongoDB Collections table, the Balanced-mode startup warning bullet, the last constraint line)
- Modify: `FOLLOWUPS.md`

- [ ] **Step 1: CHANGELOG**

Under `## [Unreleased]` add:

```markdown
### Added
- **Native control transport for `DurabilityMode.Balanced`.** A Balanced host that configures no other
  node control endpoint now gets one over its own MongoDB database: `mongocontrol://<node id>` per node,
  one document per control message in the new `wolverine_control_messages` collection (indexed on
  `nodeId, posted`, TTL on `expires`), a one-second poll per node, thirty-second message expiry. Mirrors
  Wolverine's RavenDb and Cosmos control transports. `opts.UseTcpForControlEndpoint()` is no longer
  needed; a host that still calls it, or that enables a broker's control queues, keeps that endpoint.
  Solo hosts are unchanged: no transport, no collection, no indexes.

### Changed
- The Balanced-mode startup log line names the control endpoint in use instead of asking for one.
```

- [ ] **Step 2: README**

Replace the `## Multinode support` intro and the `### Multinode requirements` first bullet with:

```markdown
## Multinode support

`DurabilityMode.Balanced` is supported out of the box. Nodes coordinate (leader election, agent
assignment, exclusive listeners) over a control channel that this library provides on the same
MongoDB database: each node listens on `mongocontrol://<its node id>`, and control messages are
documents in `wolverine_control_messages`, indexed on `nodeId, posted` with a TTL index on
`expires`. Nothing to configure:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.Durability.Mode = DurabilityMode.Balanced;
    opts.UseMongoDbPersistence("my_database");
});
```

To use another control channel instead (Wolverine's TCP endpoint, or a broker's control queues
such as `EnableWolverineControlQueues()` on Azure Service Bus), configure it before
`UseMongoDbPersistence`: an already configured control endpoint is always kept.

At startup, when `DurabilityMode.Balanced` is detected, the store logs an `Information` message
naming the control endpoint in use and reminding you that synchronized clocks are required.

### Multinode requirements

- **Synchronized node clocks**: the leader lock uses a time-based lease
  (`LockLeaseDuration`, default 1 minute), and control messages expire thirty seconds after they
  are posted. Node clocks must be synchronized to well within the lease. Standard NTP keeps
  typical server clocks within a few milliseconds, which is safe for the defaults.
```

Keep the rest of the section (`### Multinode semantics` onward) as it is.

- [ ] **Step 3: CLAUDE.md**

Four edits:
1. Repository layout: add `Internals/Transport/` with one line: `mongocontrol transport: endpoint, sender, listener (Balanced-mode node control)`, and `ControlMessageDocument.cs`.
2. MongoDB Collections table: add `| wolverine_control_messages | Inter-node control messages (Balanced only): _id = envelope id, nodeId, body, expires (TTL) |`.
3. Replace the **Balanced-mode startup warning** bullet with: `**Native control transport (Balanced only):** Initialize registers MongoDbControlTransport when the store is Main, the mode is Balanced and Transports.NodeControlEndpoint is null, and sets it as the node control endpoint, exactly as the RavenDb and Cosmos stores do. A configured endpoint (TCP, broker control queues) always wins. One document per control message in wolverine_control_messages, one-second poll per node, thirty-second expiry, TTL index as the reaper. The startup Information line names the endpoint and reminds about clock synchronisation.`
4. Replace the last constraint line (`DurabilityMode.Balanced is supported. It requires opts.UseTcpForControlEndpoint()...`) with: `DurabilityMode.Balanced is supported with no extra configuration; the native mongocontrol transport is registered unless another control endpoint was configured. Node clocks must be synchronized.`

- [ ] **Step 4: FOLLOWUPS**

Add a section:

```markdown
## Control transport

- **Change-stream listener, deferred.** The `mongocontrol` listener polls `wolverine_control_messages`
  once a second, like every sibling provider. A change stream on the collection would push control
  messages with sub-second latency and the library already requires a replica set, but it adds a
  long-lived cursor per node and resume-token handling. Revisit if a consumer needs faster agent
  handoff than one second; the durability timers it coordinates run at seconds to minutes today.
```

- [ ] **Step 5: Humanize and check for dashes**

Run the humanizer on the new README, CHANGELOG and FOLLOWUPS text and on every new code comment. Then:

```powershell
Select-String -Path CHANGELOG.md,README.md,CLAUDE.md,FOLLOWUPS.md -Pattern ([string][char]0x2014) | Measure-Object | Select-Object -ExpandProperty Count
Get-ChildItem src -Recurse -Filter *.cs | Select-String -Pattern ([string][char]0x2014) | Where-Object { $_.Path -like '*Control*' } | Measure-Object | Select-Object -ExpandProperty Count
```
Expected: 0 and 0 (pre-existing dashes elsewhere in the docs are out of scope).

- [ ] **Step 6: Commit**

```powershell
git add CHANGELOG.md README.md CLAUDE.md FOLLOWUPS.md
git commit -m "docs(control): document the native control transport and drop the TCP requirement"
```

---

### Task 7: Full verification on both TFMs

- [ ] **Step 1: Clean build**

```powershell
Remove-Item -Recurse -Force src\Wolverine.MongoDB.Tests\Internal\Generated -ErrorAction SilentlyContinue
dotnet build -c Release
```
Expected: 0 warnings, 0 errors (warnings are errors in this repo).

- [ ] **Step 2: Single-node suite, both TFMs**

```powershell
dotnet test src/Wolverine.MongoDB.Tests -f net10.0 --filter "Category!=multinode"
dotnet test src/Wolverine.MongoDB.Tests -f net9.0 --filter "Category!=multinode"
```
Expected: all pass.

- [ ] **Step 3: Multinode suite, both TFMs, serially**

```powershell
dotnet test src/Wolverine.MongoDB.Tests -f net10.0 --filter "Category=multinode"
dotnet test src/Wolverine.MongoDB.Tests -f net9.0 --filter "Category=multinode"
```
Expected: all pass. Run the multinode suite a second time on net10.0 to catch a flaky poll timing.

- [ ] **Step 4: Pack**

```powershell
dotnet pack src/Wolverine.MongoDB/Wolverine.MongoDB.csproj -c Release -p:UseWolverineSource=false
```
Expected: a nupkg in `src/Wolverine.MongoDB/bin/Release/`.

---

### Task 8: Pull request

- [ ] **Step 1: Review the diff against the spec**

Read `docs/superpowers/plans/2026-09-17-mongodb-control-transport-design.md` once more and confirm every item under Design, Testing and Documentation has a commit. Confirm `git grep UseTcpForControlEndpoint src/` returns only `durability_mode_guard.cs`.

- [ ] **Step 2: Push and open the PR**

```powershell
git push -u origin feat/control-transport
gh pr create --title "feat(control): native MongoDB control transport for Balanced mode" --body-file docs/superpowers/plans/2026-09-17-mongodb-control-transport-design.md
```

- [ ] **Step 3: After merge**

Invoke the `release` agent with "release 1.1.0" (additive feature, minor bump). The consumer-side changes (delete `UseTcpForControlEndpoint()`, bump the package) follow in the consumer's repository.
