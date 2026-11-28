using System.Text.RegularExpressions;
using JasperFx.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IDeadLetters
{
    /// <summary>
    /// Matches every dead-letter document belonging to the given envelope ids.
    /// <para>
    /// Current documents carry the envelope Guid in <c>envelopeId</c> and an identity-derived
    /// <c>_id</c>, so one Guid can legitimately match several documents — which is the shape the
    /// Guid-addressed <see cref="IDeadLetters"/> surface expects, and what the RDBMS providers
    /// return. Documents written before the identity split have no <c>envelopeId</c> and stored
    /// the envelope Guid directly in <c>_id</c>; the second branch keeps those addressable until
    /// <c>MigrateAsync</c> backfills them. That branch cannot capture a current document, because
    /// a current document's <c>envelopeId</c> is present and non-empty.
    /// </para>
    /// </summary>
    private static FilterDefinition<DeadLetterMessage> ForEnvelopeIds(IEnumerable<Guid> ids)
    {
        var b = Builders<DeadLetterMessage>.Filter;
        var list = ids as IReadOnlyCollection<Guid> ?? ids.ToList();
        var preSplit = b.Or(b.Exists(x => x.EnvelopeId, false), b.Eq(x => x.EnvelopeId, Guid.Empty));
        return b.Or(b.In(x => x.EnvelopeId, list), b.And(b.In(x => x.Id, list), preSplit));
    }

    private FilterDefinition<DeadLetterMessage> DlqFilter(DeadLetterEnvelopeQuery query)
    {
        var b = Builders<DeadLetterMessage>.Filter;
        var filter = b.Empty;
        // MessageIds takes precedence over every other option (DeadLetterEnvelopeQuery.cs:32-35),
        // and matches all documents for those envelope ids — so DiscardAsync deletes all of them
        // and ReplayAsync flags all of them, matching MessageDatabase.DeadLetterAdminService.
        if (query.MessageIds is { Length: > 0 }) return ForEnvelopeIds(query.MessageIds);
        if (query.Range?.From.HasValue == true) filter &= b.Gte(x => x.SentAt, query.Range.From!.Value);
        if (query.Range?.To.HasValue == true) filter &= b.Lte(x => x.SentAt, query.Range.To!.Value);
        if (query.ExceptionType.IsNotEmpty()) filter &= b.Eq(x => x.ExceptionType, query.ExceptionType);
        if (query.ExceptionMessage.IsNotEmpty())
            filter &= b.Regex(x => x.ExceptionMessage, new BsonRegularExpression("^" + Regex.Escape(query.ExceptionMessage!)));
        if (query.MessageType.IsNotEmpty()) filter &= b.Eq(x => x.MessageType, query.MessageType);
        if (query.ReceivedAt.IsNotEmpty()) filter &= b.Eq(x => x.ReceivedAt, query.ReceivedAt);
        // Tri-state (6.22+): null = no predicate, true = only letters already flagged for replay,
        // false = only stuck ones. Server-side so QueryAsync's TotalCount stays coherent with its page,
        // and so DiscardAsync/ReplayAsync act on exactly the group the caller named.
        if (query.Replayable.HasValue) filter &= b.Eq(x => x.Replayable, query.Replayable.Value);
        return filter;
    }

    /// <summary>
    /// Returns one dead letter for the envelope id. The signature admits only one, and the RDBMS
    /// reference reads the first matching row (<c>MessageDatabase.DeadLetters.cs:9-26</c>); when an
    /// envelope has a dead letter per destination the sort makes which one deterministic. Use
    /// <see cref="QueryAsync"/> to see them all.
    /// </summary>
    public async Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        var doc = await DeadLetterDocs.Find(ForEnvelopeIds([id]))
            .Sort(Builders<DeadLetterMessage>.Sort.Ascending(x => x.ReceivedAt))
            .FirstOrDefaultAsync();
        return doc?.ToEnvelope();
    }

    public async Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range, CancellationToken token)
    {
        var b = Builders<DeadLetterMessage>.Filter;
        var filter = b.Empty;
        if (range.From.HasValue) filter &= b.Gte(x => x.SentAt, range.From.Value);
        if (range.To.HasValue) filter &= b.Lte(x => x.SentAt, range.To.Value);

        var grouped = await DeadLetterDocs.Aggregate()
            .Match(filter)
            .Group(x => new { x.ReceivedAt, x.MessageType, x.ExceptionType },
                g => new { g.Key.ReceivedAt, g.Key.MessageType, g.Key.ExceptionType, Count = g.Count() })
            .ToListAsync(token);

        return grouped
            .Select(g => new DeadLetterQueueCount(
                serviceName,
                g.ReceivedAt.IsNotEmpty() ? new Uri(g.ReceivedAt!) : Uri,
                g.MessageType ?? "",
                g.ExceptionType ?? "",
                Uri,
                g.Count))
            .ToList();
    }

    // The _id tie-break keeps the order stable, so consecutive pages neither repeat nor skip a letter
    private static readonly SortDefinition<DeadLetterMessage> DlqOrder =
        Builders<DeadLetterMessage>.Sort.Ascending(x => x.SentAt).Ascending(x => x.Id);

    private const int DlqIdBatchSize = 1000;

    public async Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.PageNumber <= 0) query.PageNumber = 1;
        if (query.HasHeaderFilter()) return await QueryByHeadersAsync(query, token);

        var filter = DlqFilter(query);
        var total = (int)await DeadLetterDocs.CountDocumentsAsync(filter, cancellationToken: token);

        var docs = await DeadLetterDocs.Find(filter)
            .Sort(DlqOrder)
            .Skip((query.PageNumber - 1) * query.PageSize)
            .Limit(query.PageSize)
            .ToListAsync(token);

        return new DeadLetterEnvelopeResults
        {
            PageNumber = query.PageNumber,
            TotalCount = total,
            Envelopes = docs.Select(m => m.ToEnvelope()).ToList(),
            DatabaseUri = Uri
        };
    }

    /// <summary>
    /// Headers are stored only inside the serialized envelope <c>body</c>, so a header filter cannot
    /// run on the server. Every document that matches the rest of the query is read in order and
    /// checked with <see cref="DeadLetterEnvelopeQuery.MatchesHeaders"/>. Only the requested page is
    /// kept, and <c>TotalCount</c> counts the documents that match the headers. A body-less dead letter
    /// carries no headers, so it never matches a header filter.
    /// </summary>
    private async Task<DeadLetterEnvelopeResults> QueryByHeadersAsync(DeadLetterEnvelopeQuery query,
        CancellationToken token)
    {
        // A page size of zero or less reads every match, as Limit(0) does on the unfiltered path
        var pageSize = query.PageSize > 0 ? query.PageSize : int.MaxValue;
        var skip = query.PageSize > 0 ? (query.PageNumber - 1) * query.PageSize : 0;
        var page = new List<DeadLetterEnvelope>();
        var total = 0;

        using var cursor = await DeadLetterDocs.Find(DlqFilter(query)).Sort(DlqOrder).ToCursorAsync(token);
        while (await cursor.MoveNextAsync(token))
        {
            foreach (var doc in cursor.Current)
            {
                var envelope = doc.ToEnvelope();
                if (!query.MatchesHeaders(envelope.Envelope)) continue;

                if (total >= skip && page.Count < pageSize) page.Add(envelope);
                total++;
            }
        }

        return new DeadLetterEnvelopeResults
        {
            PageNumber = query.PageNumber,
            TotalCount = total,
            Envelopes = page,
            DatabaseUri = Uri
        };
    }

    public async Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (!query.HasHeaderFilter())
        {
            await DeadLetterDocs.DeleteManyAsync(DlqFilter(query), cancellationToken: token);
            return;
        }

        foreach (var batch in await HeaderMatchedDlqFiltersAsync(query, token))
        {
            await DeadLetterDocs.DeleteManyAsync(batch, cancellationToken: token);
        }
    }

    public async Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var update = Builders<DeadLetterMessage>.Update.Set(x => x.Replayable, true);
        if (!query.HasHeaderFilter())
        {
            await DeadLetterDocs.UpdateManyAsync(DlqFilter(query), update, cancellationToken: token);
            return;
        }

        foreach (var batch in await HeaderMatchedDlqFiltersAsync(query, token))
        {
            await DeadLetterDocs.UpdateManyAsync(batch, update, cancellationToken: token);
        }
    }

    /// <summary>
    /// The bulk discard and replay with a header filter: reads the documents that match the rest of
    /// the query, keeps the <c>_id</c> of each one whose headers match, and returns filters over those
    /// ids in batches. Each filter also repeats the rest of the query, so a document that stopped
    /// matching after it was read (flagged for replay in the meantime, for example) is left alone.
    /// No match gives no filters, so nothing is discarded or replayed.
    /// </summary>
    private async Task<List<FilterDefinition<DeadLetterMessage>>> HeaderMatchedDlqFiltersAsync(
        DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var filter = DlqFilter(query);
        var ids = new List<Guid>();

        using var cursor = await DeadLetterDocs.Find(filter).ToCursorAsync(token);
        while (await cursor.MoveNextAsync(token))
        {
            ids.AddRange(cursor.Current
                .Where(doc => query.MatchesHeaders(doc.ToEnvelope().Envelope))
                .Select(doc => doc.Id));
        }

        var b = Builders<DeadLetterMessage>.Filter;
        return ids.Chunk(DlqIdBatchSize).Select(batch => b.And(b.In(x => x.Id, batch), filter)).ToList();
    }

    /// <summary>
    /// Applies the new body to every dead letter for the envelope id and flags them replayable,
    /// matching the RDBMS reference, which updates every row sharing the id
    /// (<c>MessageDatabase.DeadLetterAdminService.cs:201-221</c>).
    /// <para>
    /// The body is re-serialized per document rather than copying one blob across all of them: a
    /// MongoDB dead letter's body carries its own <c>Destination</c>, so a shared blob would make
    /// every replayed envelope resolve to the same inbox identity and collapse into one document.
    /// </para>
    /// </summary>
    public async Task EditAndReplayAsync(Guid envelopeId, byte[] newBody, CancellationToken token)
    {
        var docs = await DeadLetterDocs.Find(ForEnvelopeIds([envelopeId])).ToListAsync(token);

        foreach (var doc in docs)
        {
            var envelope = doc.ToEnvelope().Envelope;
            envelope.Data = newBody;
            doc.Body = EnvelopeSerializer.Serialize(envelope);
            doc.Replayable = true;
            // The whole document is rewritten anyway, so bring a pre-split one up to the current
            // shape while we are here instead of writing an empty envelopeId back.
            doc.EnvelopeId = doc.ResolvedEnvelopeId;
            // Keyed on the document _id, which round-trips correctly for pre-split documents too.
            await DeadLetterDocs.ReplaceOneAsync(
                Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, doc.Id), doc, cancellationToken: token);
        }
    }
}
