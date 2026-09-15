using System.Collections.Concurrent;
using System.Reflection;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// GH-3975 <c>AfterCommit</c> against the MongoDB transaction frame. Core owns the mechanism (a
/// separate <c>PostCommitPostprocessors</c> list concatenated after every postprocessor); what only
/// this provider can prove is where that lands relative to the frames it contributes from a
/// different code path: <c>CommitMongoTransactionFrame</c> (commit, then outbox flush) emitted by
/// <c>TransactionalFrame</c>. Asserted on the generated source, as every in-tree provider does, and
/// then behaviourally: the hook observes the committed write from OUTSIDE the transaction, runs after
/// the <c>After</c> convention, does not run when the chain fails before the commit, and a message it
/// cascades is delivered.
/// </summary>
[Collection("mongodb")]
public class after_commit_integration
{
    private readonly AppFixture _fixture;
    public after_commit_integration(AppFixture fixture) => _fixture = fixture;

    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    private async Task<IHost> buildHostAsync()
    {
        await _fixture.ClearAll();
        await Database.DropCollectionAsync(AfterCommitDoc.Collection, TestContext.Current.CancellationToken);
        AfterCommitOrderHandler.Calls.Clear();
        ThrowingPostProcessHandler.AfterCommitRan = false;
        ThrowingPostProcessHandler.Handled = false;
        AfterCommitSaga.Calls.Clear();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AfterCommitOrderHandler))
                    .IncludeType(typeof(ThrowingPostProcessHandler))
                    .IncludeType(typeof(AfterCommitSaga))
                    .IncludeType(typeof(NoticeHandler));
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();
                opts.OnException<InvalidOperationException>().Discard();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static string generatedSourceFor(IHost host, Type messageType)
    {
        var graph = host.Services.GetRequiredService<HandlerGraph>();
        graph.HandlerFor(messageType);
        var chain = graph.ChainFor(messageType)!;
        var property = typeof(HandlerChain).GetProperty("SourceCode",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        return (string)property.GetValue(chain)!;
    }

    private static void shouldBeEmittedAfterCommitAndFlush(string code, string afterCommitCall, string handlerTypeName)
    {
        var commit = code.IndexOf(".CommitTransactionAsync(", StringComparison.Ordinal);
        var flush = code.IndexOf(".FlushOutgoingMessagesAsync(", StringComparison.Ordinal);
        var afterCommit = code.IndexOf(afterCommitCall, StringComparison.Ordinal);
        var catchBlock = code.IndexOf("catch (System.Exception)", StringComparison.Ordinal);

        commit.ShouldBeGreaterThan(-1, "the MongoDB commit frame was never emitted, so this proves nothing");
        flush.ShouldBeGreaterThan(-1, "the outbox flush was never emitted");
        afterCommit.ShouldBeGreaterThan(-1, $"the AfterCommit method of {handlerTypeName} was never emitted");

        afterCommit.ShouldBeGreaterThan(commit,
            "AfterCommit must be emitted AFTER CommitTransactionAsync, or it observes a write that is not durable yet");
        afterCommit.ShouldBeGreaterThan(flush,
            "AfterCommit must be emitted AFTER the outbox flush too — its own cascades go through the end-of-pipeline flush");
        // Inside the TransactionalFrame try-block: a throwing commit unwinds straight past it.
        catchBlock.ShouldBeGreaterThan(afterCommit,
            "AfterCommit must sit inside the transaction frame's try-block so a failed commit skips it");
    }

    [Fact]
    public async Task handler_after_commit_is_emitted_after_the_commit_and_the_flush()
    {
        using var host = await buildHostAsync();
        var code = generatedSourceFor(host, typeof(AfterCommitOrderMessage));

        // The call form, not the bare name: the handler type is called AfterCommitOrderHandler, so a
        // bare "AfterCommit" search matches the type name first.
        shouldBeEmittedAfterCommitAndFlush(code, $".{nameof(AfterCommitOrderHandler.AfterCommit)}(", nameof(AfterCommitOrderHandler));

        // Compatibility guard: the After convention (PostProcess) still runs BEFORE the commit.
        var commit = code.IndexOf(".CommitTransactionAsync(", StringComparison.Ordinal);
        var after = code.IndexOf($".{nameof(AfterCommitOrderHandler.PostProcess)}(", StringComparison.Ordinal);
        after.ShouldBeGreaterThan(-1);
        after.ShouldBeLessThan(commit, "After methods must keep running BEFORE the commit");
    }

    [Fact]
    public async Task saga_after_commit_is_emitted_after_the_saga_commit_and_the_flush()
    {
        using var host = await buildHostAsync();
        var code = generatedSourceFor(host, typeof(StartAfterCommitSaga));

        shouldBeEmittedAfterCommitAndFlush(code, $".{nameof(AfterCommitSaga.AfterCommit)}(", nameof(AfterCommitSaga));
        code.IndexOf(".InsertSagaAsync<", StringComparison.Ordinal)
            .ShouldBeLessThan(code.IndexOf($".{nameof(AfterCommitSaga.AfterCommit)}(", StringComparison.Ordinal),
                "the saga write must be emitted before the hook");
    }

    [Fact]
    public async Task runs_after_handle_and_after_and_sees_the_committed_document_outside_the_transaction()
    {
        using var host = await buildHostAsync();
        var id = Guid.NewGuid().ToString();

        await host.InvokeMessageAndWaitAsync(new AfterCommitOrderMessage(id));

        AfterCommitOrderHandler.Calls.ToArray().ShouldBe(["Handle", "PostProcess", "AfterCommit"]);
        // The hook read the document through the raw IMongoDatabase (no session): only a committed
        // write is visible there, so this is a behavioural "after the commit", not just an ordering.
        AfterCommitOrderHandler.SeenOutsideTransaction[id].ShouldBeTrue();
    }

    [Fact]
    public async Task saga_after_commit_runs_and_the_saga_is_committed_first()
    {
        using var host = await buildHostAsync();
        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new StartAfterCommitSaga(id));

        AfterCommitSaga.Calls.ToArray().ShouldBe(["Start", "AfterCommit"]);
        AfterCommitSaga.SawCommittedSaga[id].ShouldBeTrue("the saga document must be visible outside the transaction when the hook runs");
    }

    [Fact]
    public async Task does_not_run_when_the_chain_fails_before_the_commit_and_nothing_is_committed()
    {
        using var host = await buildHostAsync();
        var id = Guid.NewGuid().ToString();

        // Send rather than invoke: the pipeline (not the caller) handles the exception, and the
        // Discard policy below makes it exactly one attempt.
        await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new ThrowingPostProcessMessage(id));
        ThrowingPostProcessHandler.Handled.ShouldBeTrue("the handler itself ran; the failure is in the After method");

        ThrowingPostProcessHandler.AfterCommitRan.ShouldBeFalse(
            "an after-commit method must not run when the chain threw before the commit");
        var stored = await Database.GetCollection<AfterCommitDoc>(AfterCommitDoc.Collection)
            .Find(x => x.Id == id).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        stored.ShouldBeNull("the transaction was rolled back, so the handler's write must not be visible either");
    }

    [Fact]
    public async Task a_message_cascaded_from_after_commit_is_delivered()
    {
        using var host = await buildHostAsync();
        var id = Guid.NewGuid().ToString();

        var session = await host.InvokeMessageAndWaitAsync(new AfterCommitOrderMessage(id));

        // Published AFTER the outbox flush, so it travels through the end-of-pipeline flush rather than
        // the committed transaction's outbox — but it is delivered and handled.
        session.Sent.SingleMessage<AfterCommitNotice>().Id.ShouldBe(id);
        session.Executed.SingleMessage<AfterCommitNotice>().Id.ShouldBe(id);
        NoticeHandler.Received.ShouldContain(id);
    }
}

public class AfterCommitDoc
{
    public const string Collection = "after_commit_docs";
    public string Id { get; set; } = string.Empty;
}

public record AfterCommitOrderMessage(string Id);
public record ThrowingPostProcessMessage(string Id);
public record AfterCommitNotice(string Id);
public record StartAfterCommitSaga(Guid Id);

[WolverineIgnore]
public static class AfterCommitOrderHandler
{
    public static readonly ConcurrentQueue<string> Calls = new();
    public static readonly ConcurrentDictionary<string, bool> SeenOutsideTransaction = new();

    public static Task Handle(AfterCommitOrderMessage message, MongoDbUnitOfWork uow, CancellationToken ct)
    {
        Calls.Enqueue("Handle");
        return uow.Collection<AfterCommitDoc>(AfterCommitDoc.Collection).InsertOneAsync(new AfterCommitDoc { Id = message.Id }, ct);
    }

    // "PostProcess" is one of the After convention names and, unlike "After", not a substring of
    // "AfterCommit" — these assertions are string index comparisons.
    public static void PostProcess() => Calls.Enqueue("PostProcess");

    // Reads through the raw database handle (no session): only a committed document is visible here.
    // Publishes its notice through the bus: the outbox was already flushed when this runs, so the
    // message rides the end-of-pipeline flush (not the committed transaction's outbox) — the contract
    // the attribute documents. A value RETURNED from an AfterCommit method is not a cascading message
    // in WolverineFx 6.38 (PostCommitPostprocessors are plain MethodCalls), hence the explicit publish.
    public static async Task AfterCommit(AfterCommitOrderMessage message, IMongoDatabase database, IMessageBus bus, CancellationToken ct)
    {
        Calls.Enqueue("AfterCommit");
        var doc = await database.GetCollection<AfterCommitDoc>(AfterCommitDoc.Collection)
            .Find(x => x.Id == message.Id).FirstOrDefaultAsync(ct);
        SeenOutsideTransaction[message.Id] = doc is not null;
        await bus.PublishAsync(new AfterCommitNotice(message.Id));
    }
}

[WolverineIgnore]
public static class ThrowingPostProcessHandler
{
    public static bool AfterCommitRan;
    public static bool Handled;

    public static Task Handle(ThrowingPostProcessMessage message, MongoDbUnitOfWork uow, CancellationToken ct)
    {
        Handled = true;
        return uow.Collection<AfterCommitDoc>(AfterCommitDoc.Collection).InsertOneAsync(new AfterCommitDoc { Id = message.Id }, ct);
    }

    public static void PostProcess() => throw new InvalidOperationException("the commit never happens");

    public static void AfterCommit() => AfterCommitRan = true;
}

[WolverineIgnore]
public static class NoticeHandler
{
    public static readonly ConcurrentBag<string> Received = new();
    public static void Handle(AfterCommitNotice notice) => Received.Add(notice.Id);
}

public class AfterCommitSaga : Saga
{
    public static readonly ConcurrentQueue<string> Calls = new();
    public static readonly ConcurrentDictionary<Guid, bool> SawCommittedSaga = new();

    public Guid Id { get; set; }

    public void Start(StartAfterCommitSaga cmd)
    {
        Id = cmd.Id;
        Calls.Enqueue("Start");
    }

    public static async Task AfterCommit(StartAfterCommitSaga cmd, IMongoDatabase database, CancellationToken ct)
    {
        Calls.Enqueue("AfterCommit");
        var doc = await database.GetCollection<AfterCommitSaga>(Internals.MongoConstants.SagaCollectionName(typeof(AfterCommitSaga)))
            .Find(x => x.Id == cmd.Id).FirstOrDefaultAsync(ct);
        SawCommittedSaga[cmd.Id] = doc is not null;
    }
}
