using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;
using Wolverine.Runtime;

namespace Wolverine.MongoDB.Tests;

/// <summary>
/// Phase 0 regression guard for the CONTESTED review finding:
///
/// Claim: a duplicate-key insert in <see cref="MongoDbEnvelopeTransaction.TryMakeEagerIdempotencyCheckAsync"/>
/// aborts the open Mongo session transaction, so a subsequent <c>PersistOutgoingAsync</c>
/// on the SAME session throws "Cannot run command ... aborted transaction".
///
/// This test exercises the exact sequence on a real replica-set transaction:
///   1. open a real session + transaction
///   2. eager idempotency check for an envelope (succeeds, inserts the handled marker)
///   3. eager idempotency check AGAIN for the SAME envelope id (forces a duplicate-key)
///   4. PersistOutgoingAsync on the same session
/// then asserts whether step 4 throws an aborted-transaction error.
/// </summary>
[Collection("mongodb")]
public class eager_idempotency_transaction
{
    private readonly AppFixture _fixture;
    public eager_idempotency_transaction(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task duplicate_eager_check_does_not_strand_subsequent_outgoing_write()
    {
        await _fixture.ClearAll();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.LocalQueue("things").UseDurableInbox();
            }).StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Storage.ShouldBeOfType<MongoDbMessageStore>();

        var context = new MessageContext(runtime);
        var settings = runtime.Options.Durability;

        // A fixed-id incoming envelope, plus an outgoing envelope to persist after the duplicate.
        var incoming = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "thing",
            Data = [1, 2, 3]
        };

        var outgoing = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "thing-out",
            Destination = new Uri("local://things"),
            Data = [4, 5, 6]
        };

        var client = _fixture.Client;
        using var session = await client.StartSessionAsync();
        session.StartTransaction();

        var tx = new MongoDbEnvelopeTransaction(session, context);

        // First eager check: inserts the "handled" marker for this id -> should succeed.
        var first = await tx.TryMakeEagerIdempotencyCheckAsync(incoming, settings, CancellationToken.None);
        first.ShouldBeTrue();

        // Second eager check for the SAME id: duplicate-key on the unique _id -> should return false.
        var second = await tx.TryMakeEagerIdempotencyCheckAsync(incoming, settings, CancellationToken.None);
        second.ShouldBeFalse("a duplicate envelope must be detected as already handled");

        // The contested behavior: after the duplicate-key error, can we still write on the same session?
        // If the duplicate aborted the transaction, this throws an aborted-transaction MongoCommandException.
        await tx.PersistOutgoingAsync(outgoing);

        await session.CommitTransactionAsync();

        // Verify the outgoing write actually committed.
        var outgoingCollection = client
            .GetDatabase(AppFixture.DatabaseName)
            .GetCollection<OutgoingMessage>(MongoConstants.OutgoingCollection);
        var stored = await outgoingCollection
            .Find(Builders<OutgoingMessage>.Filter.Eq(x => x.Id, outgoing.Id))
            .FirstOrDefaultAsync();
        stored.ShouldNotBeNull();
    }
}
