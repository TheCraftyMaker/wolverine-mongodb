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

    public async Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var filter = DlqFilter(query);
        var total = (int)await DeadLetterDocs.CountDocumentsAsync(filter, cancellationToken: token);
        if (query.PageNumber <= 0) query.PageNumber = 1;

        var docs = await DeadLetterDocs.Find(filter)
            .Sort(Builders<DeadLetterMessage>.Sort.Ascending(x => x.SentAt))
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

    public Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
        => DeadLetterDocs.DeleteManyAsync(DlqFilter(query), cancellationToken: token);

    public Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
        => DeadLetterDocs.UpdateManyAsync(DlqFilter(query),
            Builders<DeadLetterMessage>.Update.Set(x => x.Replayable, true), cancellationToken: token);

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
