using System.Collections.Concurrent;
using System.Net.Http.Json;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.Http;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// GH-3975 <c>AfterCommit</c> on a Wolverine.Http endpoint whose transaction is the MongoDB frame.
/// <c>HttpChain.Codegen</c> yields <c>PostCommitPostprocessors</c> after every postprocessor, including
/// the commit frame this provider adds through <c>ApplyTransactionSupport</c>; the source assertion
/// proves that landed on the right side of <c>CommitTransactionAsync</c>, and the request proves the
/// hook observed the committed document from outside the transaction.
/// </summary>
[Collection("mongodb")]
public class after_commit_http
{
    private readonly AppFixture _fixture;
    public after_commit_http(AppFixture fixture) => _fixture = fixture;

    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    private async Task<WebApplication> buildAppAsync()
    {
        await _fixture.ClearAll();
        await Database.DropCollectionAsync(AfterCommitDoc.Collection, TestContext.Current.CancellationToken);
        AfterCommitEndpoint.Calls.Clear();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
            opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            opts.Policies.AutoApplyTransactions();
        });
        builder.Services.AddWolverineHttp();

        var app = builder.Build();
        // Default discovery scans this (the application) assembly; AfterCommitEndpoint is its only endpoint.
        app.MapWolverineEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpClient clientFor(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    [Fact]
    public async Task http_after_commit_runs_after_the_mongodb_commit_and_sees_the_committed_document()
    {
        await using var app = await buildAppAsync();
        using var client = clientFor(app);
        var id = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync("/after-commit", new AfterCommitHttpRequest(id), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        AfterCommitEndpoint.Calls.ToArray().ShouldBe(["Post", "AfterCommit"]);
        AfterCommitEndpoint.SeenOutsideTransaction[id].ShouldBeTrue(
            "the hook must observe the committed document from outside the transaction");

        // Generated source: the hook sits after the MongoDB commit and the outbox flush, inside the
        // transaction frame's try-block.
        // HttpGraph is not a registered service; it hangs off WolverineHttpOptions.Endpoints (internal).
        var httpOptions = app.Services.GetRequiredService<WolverineHttpOptions>();
        var graph = (HttpGraph)typeof(WolverineHttpOptions)
            .GetProperty("Endpoints", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .GetValue(httpOptions)!;
        var chain = graph.ChainFor("POST", "/after-commit");
        chain.ShouldNotBeNull();
        var code = chain.SourceCode;
        code.ShouldNotBeNull();

        var commit = code.IndexOf(".CommitTransactionAsync(", StringComparison.Ordinal);
        var flush = code.IndexOf(".FlushOutgoingMessagesAsync(", StringComparison.Ordinal);
        var afterCommit = code.IndexOf($".{nameof(AfterCommitEndpoint.AfterCommit)}(", StringComparison.Ordinal);
        var catchBlock = code.IndexOf("catch (System.Exception)", StringComparison.Ordinal);
        commit.ShouldBeGreaterThan(-1, "the MongoDB commit frame was never emitted for the endpoint");
        afterCommit.ShouldBeGreaterThan(commit, "AfterCommit must be emitted after CommitTransactionAsync");
        afterCommit.ShouldBeGreaterThan(flush, "AfterCommit must be emitted after the outbox flush");
        catchBlock.ShouldBeGreaterThan(afterCommit, "AfterCommit must sit inside the transaction frame's try-block");
    }
}

public record AfterCommitHttpRequest(string Id);

public static class AfterCommitEndpoint
{
    public static readonly ConcurrentQueue<string> Calls = new();
    public static readonly ConcurrentDictionary<string, bool> SeenOutsideTransaction = new();

    [WolverinePost("/after-commit")]
    public static Task Post(AfterCommitHttpRequest request, MongoDbUnitOfWork uow, CancellationToken ct)
    {
        Calls.Enqueue("Post");
        return uow.Collection<AfterCommitDoc>(AfterCommitDoc.Collection).InsertOneAsync(new AfterCommitDoc { Id = request.Id }, ct);
    }

    public static async Task AfterCommit(AfterCommitHttpRequest request, IMongoDatabase database, CancellationToken ct)
    {
        Calls.Enqueue("AfterCommit");
        var doc = await database.GetCollection<AfterCommitDoc>(AfterCommitDoc.Collection)
            .Find(x => x.Id == request.Id).FirstOrDefaultAsync(ct);
        SeenOutsideTransaction[request.Id] = doc is not null;
    }
}
