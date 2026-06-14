using JasperFx.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class datetime_serialization
{
    private readonly AppFixture _fixture;
    public datetime_serialization(AppFixture fixture) => _fixture = fixture;

    private IMongoCollection<BsonDocument> RawIncoming
        => _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<BsonDocument>(MongoConstants.IncomingCollection);

    [Fact]
    public async Task keepUntil_is_stored_as_bson_date_not_array()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("rabbitmq://queue/dt");
        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var doc = await RawIncoming
            .Find(Builders<BsonDocument>.Filter.Empty)
            .FirstOrDefaultAsync();

        doc.ShouldNotBeNull();
        doc.Contains("keepUntil").ShouldBeTrue();
        doc["keepUntil"].BsonType.ShouldBe(BsonType.DateTime);
    }

    [Fact]
    public async Task dead_letters_filter_and_order_by_sentAt()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Create three dead letters with distinct, ascending SentAt values.
        var envelopes = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var env = ObjectMother.Envelope();
            env.Destination = new Uri("rabbitmq://queue/dt-range");
            env.SentAt = baseTime.AddMinutes(i);
            await store.Inbox.StoreIncomingAsync(env);
            await store.Inbox.MoveToDeadLetterStorageAsync(env, new InvalidOperationException($"boom {i}"));
            envelopes.Add(env);
        }

        var range = new TimeRange(baseTime.AddMinutes(-10), baseTime.AddMinutes(10));
        var results = await store.DeadLetters.QueryAsync(
            new DeadLetterEnvelopeQuery { Range = range }, CancellationToken.None);

        results.TotalCount.ShouldBe(3);
        // Returned ordered by SentAt ascending — only possible if SentAt is a BSON Date.
        results.Envelopes.Select(x => x.SentAt).ShouldBe(envelopes.Select(e => e.SentAt));

        // A tight range that excludes the first one filters correctly via $gte.
        var tight = new TimeRange(baseTime.AddSeconds(30), baseTime.AddMinutes(10));
        var tightResults = await store.DeadLetters.QueryAsync(
            new DeadLetterEnvelopeQuery { Range = tight }, CancellationToken.None);
        tightResults.TotalCount.ShouldBe(2);
    }

    [Fact]
    public async Task scheduled_execution_time_stored_utc_and_compares_correctly()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("rabbitmq://queue/dt-sched");
        await store.Inbox.StoreIncomingAsync(envelope);

        // A non-UTC DateTimeOffset in the past (explicit +05:00 offset, one minute ago in that zone).
        envelope.ScheduledTime = new DateTimeOffset(DateTime.UtcNow.AddMinutes(-1).Ticks, TimeSpan.Zero)
            .ToOffset(TimeSpan.FromHours(5));
        await store.Inbox.ScheduleExecutionAsync(envelope);

        // Stored executionTime must be a BSON Date that compares correctly against UtcNow.
        var raw = await RawIncoming
            .Find(Builders<BsonDocument>.Filter.Empty)
            .FirstOrDefaultAsync();
        raw.ShouldNotBeNull();
        raw["executionTime"].BsonType.ShouldBe(BsonType.DateTime);

        // A $lte UtcNow scan must match the past-scheduled message.
        // DateTime maps natively to BSON Date; no global serializer required for raw filters.
        var dueFilter = Builders<BsonDocument>.Filter.Lte("executionTime", DateTime.UtcNow);
        var dueCount = await RawIncoming.CountDocumentsAsync(dueFilter);
        dueCount.ShouldBe(1);
    }
}
