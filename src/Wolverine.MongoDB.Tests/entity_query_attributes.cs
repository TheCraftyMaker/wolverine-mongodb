using System.Reflection;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// <c>[All]</c>, <c>[FirstOrDefault]</c> and <c>[Queryable]</c> (WolverineFx 6.38) against MongoDB. The
/// first five facts are the storage-agnostic handler shapes every upstream provider suite runs
/// (MartenTests / RavenDbTests / EfCoreTests). The rest are MongoDB-specific: the read honours an
/// explicit <c>MapEntityCollection</c> mapping, a saga type reads its <c>wolverine_saga_*</c>
/// collection, and the generated code threads the outbox session into the read exactly when the
/// handler is transactional (proven on the emitted source, the same way the saga/entity frames are).
/// </summary>
[Collection("mongodb")]
public class entity_query_attributes
{
    private readonly AppFixture _fixture;
    public entity_query_attributes(AppFixture fixture) => _fixture = fixture;

    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    /// <param name="mapped">
    /// Adds the <see cref="MappedColorHandler"/> and its <c>MapEntityCollection</c> mapping. The naming
    /// registry is process-global per database, so the mapped type is only ever resolved by hosts that
    /// carry the mapping — which is exactly the rule the collision guard documents.
    /// </param>
    private async Task<IHost> buildHostAsync(bool mapped = false)
    {
        await _fixture.ClearAll();
        foreach (var name in new[]
                 {
                     MongoConstants.EntityCollectionName(typeof(Color)),
                     MongoConstants.EntityCollectionName(typeof(AlertDefaults)),
                     MongoConstants.EntityCollectionName(typeof(MappedColor)),
                     "colors_mapped"
                 })
        {
            await Database.DropCollectionAsync(name, TestContext.Current.CancellationToken);
        }

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
                var discovery = opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ColorHandler))
                    .IncludeType(typeof(AlertDefaultsHandler))
                    .IncludeType(typeof(TransactionalColorHandler))
                    .IncludeType(typeof(ColorSaga))
                    .IncludeType(typeof(ColorSagaReader));
                if (mapped)
                {
                    discovery.IncludeType(typeof(MappedColorHandler));
                }

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName,
                    mapped ? o => o.MapEntityCollection<MappedColor>("colors_mapped") : null);
                opts.Policies.AutoApplyTransactions();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private Task seedAsync(string collection = "color") => Database.GetCollection<Color>(collection).InsertManyAsync(
    [
        new Color { Name = "red", Hits = 5 },
        new Color { Name = "green", Hits = 12 },
        new Color { Name = "blue", Hits = 3 }
    ], cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task all_gives_an_empty_list_rather_than_null_when_nothing_is_stored()
    {
        using var host = await buildHostAsync();
        var tracked = await host.InvokeMessageAndWaitAsync(new CountColors());
        tracked.Sent.SingleMessage<ColorsCounted>().Count.ShouldBe(0);
    }

    [Fact]
    public async Task all_supplies_every_document()
    {
        using var host = await buildHostAsync();
        await seedAsync();
        var tracked = await host.InvokeMessageAndWaitAsync(new CountColors());
        tracked.Sent.SingleMessage<ColorsCounted>().Count.ShouldBe(3);
    }

    [Fact]
    public async Task queryable_can_be_composed_against()
    {
        using var host = await buildHostAsync();
        await seedAsync();
        var tracked = await host.InvokeMessageAndWaitAsync(new FindPopularColors(4));
        tracked.Sent.SingleMessage<PopularColorsFound>().Names.ShouldBe(["green", "red"]);
    }

    [Fact]
    public async Task first_or_default_is_null_when_nothing_is_stored()
    {
        using var host = await buildHostAsync();
        var tracked = await host.InvokeMessageAndWaitAsync(new ReadAlertDefaults());
        tracked.Sent.SingleMessage<AlertDefaultsRead>().Threshold.ShouldBe(-1);
    }

    [Fact]
    public async Task first_or_default_supplies_the_first_document_when_one_exists()
    {
        using var host = await buildHostAsync();
        await Database.GetCollection<AlertDefaults>(MongoConstants.EntityCollectionName(typeof(AlertDefaults)))
            .InsertOneAsync(new AlertDefaults { Threshold = 42 }, cancellationToken: TestContext.Current.CancellationToken);

        var tracked = await host.InvokeMessageAndWaitAsync(new ReadAlertDefaults());
        tracked.Sent.SingleMessage<AlertDefaultsRead>().Threshold.ShouldBe(42);
    }

    [Fact]
    public async Task reads_honour_an_explicit_collection_mapping()
    {
        using var host = await buildHostAsync(mapped: true);
        await Database.GetCollection<MappedColor>("colors_mapped").InsertManyAsync(
            [new MappedColor { Name = "cyan" }, new MappedColor { Name = "magenta" }, new MappedColor { Name = "yellow" }],
            cancellationToken: TestContext.Current.CancellationToken);

        // The default-named collection stays empty, so a read that ignored the mapping would count 0.
        var tracked = await host.InvokeMessageAndWaitAsync(new CountMappedColors());
        tracked.Sent.SingleMessage<ColorsCounted>().Count.ShouldBe(3);
    }

    [Fact]
    public async Task a_saga_type_reads_its_saga_collection()
    {
        using var host = await buildHostAsync();
        await host.InvokeMessageAndWaitAsync(new StartColorSaga(Guid.NewGuid(), "teal"));
        await host.InvokeMessageAndWaitAsync(new StartColorSaga(Guid.NewGuid(), "mauve"));

        var tracked = await host.InvokeMessageAndWaitAsync(new ListColorSagas());
        tracked.Sent.SingleMessage<ColorSagasListed>().Names.OrderBy(x => x).ShouldBe(["mauve", "teal"]);
    }

    [Fact]
    public async Task a_transactional_handler_reads_on_the_outbox_session_and_a_read_only_one_does_not()
    {
        using var host = await buildHostAsync();

        var transactional = generatedSourceFor(host, typeof(RecolorAll));
        transactional.ShouldContain("MongoEntityOperations.LoadAllAsync<Wolverine.MongoDB.Tests.Color>(");
        transactional.Contains("mongoSession, ").ShouldBeTrue(
            "a transactional handler's [All] read must run on the outbox session so it sees the transaction's own writes");
        transactional.IndexOf(".LoadAllAsync<", StringComparison.Ordinal)
            .ShouldBeLessThan(transactional.IndexOf(".UpsertAsync<", StringComparison.Ordinal),
                "the read runs before the handler's write");

        var readOnly = generatedSourceFor(host, typeof(CountColors));
        readOnly.ShouldContain("MongoEntityOperations.LoadAllAsync<Wolverine.MongoDB.Tests.Color>(");
        readOnly.Contains(", null, ").ShouldBeTrue("a read-only handler has no outbox transaction and must not be forced to open one");
        readOnly.ShouldNotContain("StartSessionAsync");

        // And the transactional shape really works end to end: the write lands and the read saw the
        // pre-existing documents.
        await seedAsync();
        var tracked = await host.InvokeMessageAndWaitAsync(new RecolorAll("grey"));
        tracked.Sent.SingleMessage<ColorsCounted>().Count.ShouldBe(3);
        var stored = await Database.GetCollection<Color>("color").Find(x => x.Name == "grey")
            .ToListAsync(TestContext.Current.CancellationToken);
        stored.Count.ShouldBe(1);
    }

    private static string generatedSourceFor(IHost host, Type messageType)
    {
        var graph = host.Services.GetRequiredService<HandlerGraph>();
        graph.HandlerFor(messageType); // forces compilation
        var chain = graph.ChainFor(messageType)!;
        var property = typeof(HandlerChain).GetProperty("SourceCode",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        return (string)property.GetValue(chain)!;
    }
}

public class Color
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
    public int Hits { get; set; }
}

public class MappedColor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
}

public class AlertDefaults
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Threshold { get; set; }
}

public record CountColors;
public record CountMappedColors;
public record FindPopularColors(int Minimum);
public record ColorsCounted(int Count);
public record PopularColorsFound(string[] Names);
public record ReadAlertDefaults;
public record AlertDefaultsRead(int Threshold);
public record RecolorAll(string Name);
public record StartColorSaga(Guid Id, string Name);
public record ListColorSagas;
public record ColorSagasListed(string[] Names);

[WolverineIgnore]
public static class ColorHandler
{
    public static ColorsCounted Handle(CountColors command, [All] IReadOnlyList<Color> colors)
        => new(colors.Count);

    // The escape hatch: composing against the driver's own LINQ provider. Deliberately not portable.
    public static async Task<PopularColorsFound> Handle(FindPopularColors command,
        [Queryable] IQueryable<Color> colors, CancellationToken token)
    {
        var names = await colors.Where(x => x.Hits >= command.Minimum)
            .OrderByDescending(x => x.Hits)
            .Select(x => x.Name)
            .ToListAsync(token);
        return new PopularColorsFound(names.ToArray());
    }

    public static void Handle(ColorsCounted msg) { }
    public static void Handle(PopularColorsFound msg) { }
}

[WolverineIgnore]
public static class MappedColorHandler
{
    public static ColorsCounted Handle(CountMappedColors command, [All] IReadOnlyList<MappedColor> colors)
        => new(colors.Count);
}

[WolverineIgnore]
public static class AlertDefaultsHandler
{
    // No identity anywhere in the message — the singleton-configuration shape [Entity] cannot express.
    public static AlertDefaultsRead Handle(ReadAlertDefaults command, [FirstOrDefault] AlertDefaults? defaults)
        => new(defaults?.Threshold ?? -1);

    public static void Handle(AlertDefaultsRead msg) { }
}

[WolverineIgnore]
public static class TransactionalColorHandler
{
    // Returns Insert<Color>, so the chain is transactional and the [All] read must run on the session.
    public static (Insert<Color>, ColorsCounted) Handle(RecolorAll command, [All] IReadOnlyList<Color> existing)
        => (Storage.Insert(new Color { Name = command.Name, Hits = existing.Count }), new ColorsCounted(existing.Count));
}

public class ColorSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public void Start(StartColorSaga cmd)
    {
        Id = cmd.Id;
        Name = cmd.Name;
    }
}

[WolverineIgnore]
public static class ColorSagaReader
{
    public static ColorSagasListed Handle(ListColorSagas command, [All] IReadOnlyList<ColorSaga> sagas)
        => new(sagas.Select(x => x.Name).ToArray());

    public static void Handle(ColorSagasListed msg) { }
}
