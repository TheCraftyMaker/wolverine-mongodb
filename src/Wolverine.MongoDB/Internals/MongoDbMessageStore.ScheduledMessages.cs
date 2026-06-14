using JasperFx.Core;
using MongoDB.Driver;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IScheduledMessages
{
    private FilterDefinition<IncomingMessage> ScheduledFilter(ScheduledMessageQuery query)
    {
        var b = Builders<IncomingMessage>.Filter;
        var filter = b.Eq(x => x.Status, EnvelopeStatus.Scheduled);
        if (query.MessageIds.Length > 0) filter &= b.In(x => x.EnvelopeId, query.MessageIds);
        if (query.MessageType.IsNotEmpty()) filter &= b.Eq(x => x.MessageType, query.MessageType);
        if (query.ExecutionTimeFrom.HasValue) filter &= b.Gte(x => x.ExecutionTime, query.ExecutionTimeFrom.Value);
        if (query.ExecutionTimeTo.HasValue) filter &= b.Lte(x => x.ExecutionTime, query.ExecutionTimeTo.Value);
        return filter;
    }

    async Task<ScheduledMessageResults> IScheduledMessages.QueryAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        var filter = ScheduledFilter(query);
        var total = (int)await Incoming.CountDocumentsAsync(filter, cancellationToken: token);

        if (query.PageNumber <= 0) query.PageNumber = 1;
        var docs = await Incoming.Find(filter)
            .Sort(Builders<IncomingMessage>.Sort.Ascending(x => x.ExecutionTime))
            .Skip((query.PageNumber - 1) * query.PageSize)
            .Limit(query.PageSize)
            .ToListAsync(token);

        return new ScheduledMessageResults
        {
            PageNumber = query.PageNumber,
            TotalCount = total,
            DatabaseUri = Uri,
            Messages = docs.Select(m => new ScheduledMessageSummary
            {
                Id = m.EnvelopeId,
                MessageType = m.MessageType,
                ScheduledTime = m.ExecutionTime,
                Destination = m.ReceivedAt,
                Attempts = m.Attempts
            }).ToList()
        };
    }

    Task IScheduledMessages.CancelAsync(ScheduledMessageQuery query, CancellationToken token)
        => Incoming.DeleteManyAsync(ScheduledFilter(query), token);

    Task IScheduledMessages.RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime, CancellationToken token)
        => Incoming.UpdateManyAsync(
            Builders<IncomingMessage>.Filter.And(
                Builders<IncomingMessage>.Filter.Eq(x => x.Status, EnvelopeStatus.Scheduled),
                Builders<IncomingMessage>.Filter.Eq(x => x.EnvelopeId, envelopeId)),
            Builders<IncomingMessage>.Update.Set(x => x.ExecutionTime, newExecutionTime.ToUniversalTime()),
            cancellationToken: token);

    async Task<IReadOnlyList<ScheduledMessageCount>> IScheduledMessages.SummarizeAsync(string serviceName, CancellationToken token)
    {
        var grouped = await Incoming.Aggregate()
            .Match(Builders<IncomingMessage>.Filter.Eq(x => x.Status, EnvelopeStatus.Scheduled))
            .Group(x => x.MessageType, g => new { MessageType = g.Key, Count = g.Count() })
            .ToListAsync(token);

        return grouped
            .Select(g => new ScheduledMessageCount(serviceName, g.MessageType, Uri, g.Count))
            .ToList();
    }
}
