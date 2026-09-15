using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// <see cref="DeadLetterEnvelopeQuery.Replayable"/> (WolverineFx 6.22+) is tri-state: <c>null</c>
/// filters nothing, <c>true</c> selects only letters already flagged for replay, <c>false</c> only the
/// stuck ones. It flows through the single <c>DlqFilter</c> builder, so <c>QueryAsync</c> (page AND
/// <c>TotalCount</c>), <c>DiscardAsync</c> and <c>ReplayAsync</c> honour it identically — and
/// <c>MessageIds</c> keeps its documented precedence over every other option, this one included.
/// The upstream <c>DeadLetterAdminCompliance.query_by_replayable_flag</c> covers the query surface
/// end to end; these facts add discard/replay and the id-precedence rule against the store directly.
/// </summary>
[Collection("mongodb")]
public class dead_letter_replayable_filter
{
    private readonly AppFixture _fixture;
    public dead_letter_replayable_filter(AppFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<Guid>> deadLetterAsync(MongoDbMessageStore store, int count, Exception exception)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Destination = new Uri("local://replayable-filter");
            await store.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);
            ids.Add(envelope.Id);
        }

        return ids;
    }

    /// <summary>9 BadImageFormatException letters flagged replayable, 6 DivideByZeroException letters left stuck.</summary>
    private async Task<(MongoDbMessageStore Store, List<Guid> Replayable, List<Guid> Stuck)> seedAsync()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var replayable = await deadLetterAsync(store, 9, new BadImageFormatException("bad"));
        var stuck = await deadLetterAsync(store, 6, new DivideByZeroException("zero"));

        await store.DeadLetters.ReplayAsync(
            new DeadLetterEnvelopeQuery { ExceptionType = typeof(BadImageFormatException).FullName }, Ct);

        return (store, replayable, stuck);
    }

    private static async Task<DeadLetterEnvelopeResults> queryAsync(MongoDbMessageStore store, bool? replayable, string? exceptionType = null)
        => await store.DeadLetters.QueryAsync(
            new DeadLetterEnvelopeQuery { Replayable = replayable, ExceptionType = exceptionType, PageSize = 100 }, Ct);

    [Fact]
    public async Task query_honours_true_false_and_null_with_a_coherent_total_count()
    {
        var (store, _, _) = await seedAsync();

        var onlyReplayable = await queryAsync(store, true);
        onlyReplayable.TotalCount.ShouldBe(9);
        onlyReplayable.Envelopes.Count.ShouldBe(9);
        onlyReplayable.Envelopes.ShouldAllBe(e => e.Replayable);

        var onlyStuck = await queryAsync(store, false);
        onlyStuck.TotalCount.ShouldBe(6);
        onlyStuck.Envelopes.Count.ShouldBe(6);
        onlyStuck.Envelopes.ShouldAllBe(e => !e.Replayable);

        var all = await queryAsync(store, null);
        all.TotalCount.ShouldBe(15);
        all.Envelopes.Count.ShouldBe(15);
    }

    [Fact]
    public async Task the_replayable_predicate_composes_with_the_other_filters()
    {
        var (store, _, _) = await seedAsync();

        var replayableButStuck = await queryAsync(store, false, typeof(BadImageFormatException).FullName);
        replayableButStuck.TotalCount.ShouldBe(0);
        replayableButStuck.Envelopes.ShouldBeEmpty();

        var stuckByType = await queryAsync(store, false, typeof(DivideByZeroException).FullName);
        stuckByType.TotalCount.ShouldBe(6);
    }

    [Fact]
    public async Task discarding_the_stuck_group_cannot_remove_the_replayable_group()
    {
        var (store, replayable, _) = await seedAsync();

        await store.DeadLetters.DiscardAsync(new DeadLetterEnvelopeQuery { Replayable = false }, Ct);

        var remaining = await queryAsync(store, null);
        remaining.TotalCount.ShouldBe(9);
        remaining.Envelopes.Select(e => e.Id).OrderBy(x => x).ShouldBe(replayable.OrderBy(x => x));
        remaining.Envelopes.ShouldAllBe(e => e.Replayable);
    }

    [Fact]
    public async Task discarding_the_replayable_group_cannot_remove_the_stuck_group()
    {
        var (store, _, stuck) = await seedAsync();

        await store.DeadLetters.DiscardAsync(new DeadLetterEnvelopeQuery { Replayable = true }, Ct);

        var remaining = await queryAsync(store, null);
        remaining.TotalCount.ShouldBe(6);
        remaining.Envelopes.Select(e => e.Id).OrderBy(x => x).ShouldBe(stuck.OrderBy(x => x));
        remaining.Envelopes.ShouldAllBe(e => !e.Replayable);
    }

    [Fact]
    public async Task replaying_only_the_stuck_group_flags_exactly_that_group()
    {
        var (store, _, stuck) = await seedAsync();

        // Flag the not-yet-flagged letters of ONE exception type; the other stuck type stays stuck.
        var extraStuck = await deadLetterAsync(store, 3, new TimeoutException("slow"));

        await store.DeadLetters.ReplayAsync(
            new DeadLetterEnvelopeQuery { Replayable = false, ExceptionType = typeof(DivideByZeroException).FullName }, Ct);

        var replayable = await queryAsync(store, true);
        replayable.TotalCount.ShouldBe(15);
        replayable.Envelopes.Select(e => e.Id).ShouldContain(stuck[0]);

        var stillStuck = await queryAsync(store, false);
        stillStuck.TotalCount.ShouldBe(3);
        stillStuck.Envelopes.Select(e => e.Id).OrderBy(x => x).ShouldBe(extraStuck.OrderBy(x => x));
    }

    [Fact]
    public async Task explicit_message_ids_take_precedence_over_the_replayable_filter()
    {
        var (store, replayable, stuck) = await seedAsync();
        var ids = new[] { replayable[0], stuck[0] };

        // The ids name one letter of each group; the contradicting Replayable value is ignored.
        var byIds = await store.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(ids) { Replayable = false }, Ct);
        byIds.TotalCount.ShouldBe(2);
        byIds.Envelopes.Select(e => e.Id).OrderBy(x => x).ShouldBe(ids.OrderBy(x => x));

        await store.DeadLetters.DiscardAsync(new DeadLetterEnvelopeQuery(ids) { Replayable = true }, Ct);
        (await queryAsync(store, null)).TotalCount.ShouldBe(13);
    }
}
