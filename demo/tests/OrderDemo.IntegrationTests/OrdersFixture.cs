using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using OrderDemo.Application.Orders;
using OrderDemo.Infrastructure;
using Testcontainers.MongoDb;
using Wolverine;
using Wolverine.MongoDB;
using Wolverine.Tracking;

namespace OrderDemo.IntegrationTests;

/// <summary>
/// Class-level fixture shared by all tests in the <c>[Collection("orders")]</c> group.
///
/// One MongoDB replica-set container is started per test run (expensive) and shared.
/// Each test gets its own database name (via <see cref="CreateDatabaseName"/>) so
/// that collection state never bleeds between tests.
///
/// Wolverine is booted without RabbitMQ: application events are routed to a named
/// local durable queue. This keeps tests fast and self-contained while still exercising
/// the transactional outbox path through MongoDB.
/// </summary>
public sealed class OrdersFixture : IAsyncLifetime
{
    private static MongoDbContainer? _container;
    private static string _connectionString = null!;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public IMongoClient MongoClient { get; private set; } = null!;

    // ── Container lifecycle (shared across all tests) ─────────────────────────

    private static async Task EnsureContainerRunning()
    {
        await _lock.WaitAsync();
        try
        {
            if (_container is not null) return;
            _container = new MongoDbBuilder("mongo:7").WithReplicaSet().Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task InitializeAsync()
    {
        await EnsureContainerRunning();
        // Register BSON serializers before any collection access
        InfrastructureBootstrap.ConfigureBsonSerializers();
        MongoClient = new MongoClient(_connectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask; // container is process-scoped; GC handles it

    // ── Per-test helpers ──────────────────────────────────────────────────────

    /// <summary>Returns a unique database name so tests don't share collection state.</summary>
    public static string CreateDatabaseName() => $"test_{Guid.NewGuid():N}";

    /// <summary>
    /// Builds and starts a Wolverine IHost wired to MongoDB for the given database.
    /// Application events are routed to a local durable queue (no RabbitMQ required).
    /// </summary>
    public async Task<IHost> CreateHostAsync(string databaseName)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseDefaultServiceProvider(opts =>
            {
                // IMessageBus is scoped; tests resolve it from host.Services (root provider).
                // Disable scope validation so this works identically in Rider, VS, and dotnet test.
                opts.ValidateScopes = false;
            })
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.IncludeAssembly(typeof(PlaceOrderHandler).Assembly);       // Application
                opts.Discovery.IncludeAssembly(typeof(InfrastructureBootstrap).Assembly); // Infrastructure (projectors)

                opts.Services.AddSingleton(MongoClient);
                opts.Services.AddScoped<Infrastructure.Persistence.IOrderRepository,
                    Infrastructure.Persistence.OrderRepository>();
                opts.Services.AddScoped<Infrastructure.Persistence.OrderSummaryRepository>();

                opts.UseMongoDbPersistence(databaseName);

                // Route all application events to a local durable queue so the outbox
                // path is exercised without needing a running RabbitMQ broker.
                opts.LocalQueueFor<Contracts.Events.OrderPlacedApplicationEvent>()
                    .UseDurableInbox();
                opts.LocalQueueFor<Contracts.Events.OrderShippedApplicationEvent>()
                    .UseDurableInbox();
                opts.LocalQueueFor<Contracts.Events.OrderCancelledApplicationEvent>()
                    .UseDurableInbox();
                opts.LocalQueueFor<Contracts.Events.DiscountAppliedApplicationEvent>()
                    .UseDurableInbox();

                opts.Policies.AutoApplyTransactions();
            })
            .StartAsync();

        return host;
    }
}

[CollectionDefinition("orders")]
public class OrdersCollection : ICollectionFixture<OrdersFixture>;
