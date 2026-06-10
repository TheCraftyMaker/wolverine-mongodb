using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class dead_letter_expiration
{
    private readonly AppFixture _fixture;
    public dead_letter_expiration(AppFixture fixture) => _fixture = fixture;

    private IMongoCollection<DeadLetterMessage> Dlq
        => _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<DeadLetterMessage>(MongoConstants.DeadLetterCollection);

    [Fact]
    public async Task expiration_disabled_by_default_leaves_dead_letters_unexpirable()
    {
        await _fixture.ClearAll();
        // Default WolverineOptions: DeadLetterQueueExpirationEnabled == false
        var store = _fixture.BuildMessageStore();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("local://dlq-default");
        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("boom"));

        var doc = await Dlq.Find(Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, envelope.Id)).SingleAsync();
        doc.ExpirationTime.ShouldBeNull(
            "with expiration disabled (Wolverine's default) the TTL index must never remove a dead letter");
    }

    [Fact]
    public async Task expiration_enabled_stamps_expiration_time()
    {
        await _fixture.ClearAll();
        var options = new WolverineOptions();
        options.Durability.DeadLetterQueueExpirationEnabled = true;
        options.Durability.DeadLetterQueueExpiration = TimeSpan.FromDays(3);
        var store = new MongoDbMessageStore(_fixture.Client, AppFixture.DatabaseName, options);

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("local://dlq-enabled");
        envelope.DeliverBy = null; // ObjectMother sets DeliverBy ~28h out; null it so the code uses DeadLetterQueueExpiration
        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("boom"));

        var doc = await Dlq.Find(Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, envelope.Id)).SingleAsync();
        doc.ExpirationTime.ShouldNotBeNull();
        doc.ExpirationTime!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(2));
    }
}
