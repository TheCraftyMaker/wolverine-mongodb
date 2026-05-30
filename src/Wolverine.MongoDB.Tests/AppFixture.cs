using MongoDB.Driver;
using Testcontainers.MongoDb;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB.Tests;

public class AppFixture : IAsyncLifetime
{
    public const string DatabaseName = "wolverine_tests";

    private static MongoDbContainer? _sharedContainer;
    private static string _connectionString = null!;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public IMongoClient Client { get; private set; } = null!;

    private static async Task EnsureContainerStarted()
    {
        await _lock.WaitAsync();
        try
        {
            if (_sharedContainer != null) return;
            _sharedContainer = new MongoDbBuilder("mongo:7")
                .WithReplicaSet()
                .Build();
            await _sharedContainer.StartAsync();
            _connectionString = _sharedContainer.GetConnectionString();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task InitializeAsync()
    {
        await EnsureContainerStarted();
        Client = new MongoClient(_connectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask; // container shared; process exit cleans up

    public string ConnectionString => _connectionString;

    public MongoDbMessageStore BuildMessageStore()
        => new(Client, DatabaseName, new WolverineOptions());

    public async Task ClearAll()
    {
        var store = BuildMessageStore();
        await store.Admin.RebuildAsync();
    }
}

[CollectionDefinition("mongodb")]
public class MongoDbCollection : ICollectionFixture<AppFixture>;
