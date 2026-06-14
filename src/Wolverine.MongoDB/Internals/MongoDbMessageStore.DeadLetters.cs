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
    private FilterDefinition<DeadLetterMessage> DlqFilter(DeadLetterEnvelopeQuery query)
    {
        var b = Builders<DeadLetterMessage>.Filter;
        var filter = b.Empty;
        if (query.MessageIds is { Length: > 0 }) return b.In(x => x.Id, query.MessageIds);
        if (query.Range?.From.HasValue == true) filter &= b.Gte(x => x.SentAt, query.Range.From!.Value);
        if (query.Range?.To.HasValue == true) filter &= b.Lte(x => x.SentAt, query.Range.To!.Value);
        if (query.ExceptionType.IsNotEmpty()) filter &= b.Eq(x => x.ExceptionType, query.ExceptionType);
        if (query.ExceptionMessage.IsNotEmpty())
            filter &= b.Regex(x => x.ExceptionMessage, new BsonRegularExpression("^" + Regex.Escape(query.ExceptionMessage!)));
        if (query.MessageType.IsNotEmpty()) filter &= b.Eq(x => x.MessageType, query.MessageType);
        if (query.ReceivedAt.IsNotEmpty()) filter &= b.Eq(x => x.ReceivedAt, query.ReceivedAt);
        return filter;
    }

    public async Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        var doc = await DeadLetterDocs.Find(Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, id)).FirstOrDefaultAsync();
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

    public async Task EditAndReplayAsync(Guid envelopeId, byte[] newBody, CancellationToken token)
    {
        var doc = await DeadLetterDocs.Find(Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, envelopeId)).FirstOrDefaultAsync(token);
        if (doc is null) return;

        var envelope = EnvelopeSerializer.Deserialize(doc.Body);
        envelope.Data = newBody;
        doc.Body = EnvelopeSerializer.Serialize(envelope);
        doc.Replayable = true;
        await DeadLetterDocs.ReplaceOneAsync(Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, envelopeId), doc, cancellationToken: token);
    }
}
