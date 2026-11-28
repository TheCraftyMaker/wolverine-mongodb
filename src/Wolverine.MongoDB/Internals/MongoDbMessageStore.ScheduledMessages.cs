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

    /// <summary>
    /// Lists scheduled messages. The tenant and saga ids live only in the serialized envelope, so each
    /// summary reads them from its body. A query that names a saga id goes through core's
    /// <see cref="ScheduledMessageSagaFilter"/>, which reads every match of the other criteria
    /// unpaged (a page size of zero) and then filters and pages in memory. A document without a body
    /// has neither id, so it never matches a saga id.
    /// </summary>
    async Task<ScheduledMessageResults> IScheduledMessages.QueryAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        if (query.SagaId.IsNotEmpty())
        {
            return await ScheduledMessageSagaFilter.QueryAsync(this, query, token);
        }

        var filter = ScheduledFilter(query);
        var total = (int)await Incoming.CountDocumentsAsync(filter, cancellationToken: token);

        if (query.PageNumber <= 0) query.PageNumber = 1;
        var find = Incoming.Find(filter)
            .Sort(Builders<IncomingMessage>.Sort.Ascending(x => x.ExecutionTime));

        if (query.PageSize > 0)
        {
            find = find.Skip((query.PageNumber - 1) * query.PageSize).Limit(query.PageSize);
        }

        var docs = await find.ToListAsync(token);

        return new ScheduledMessageResults
        {
            PageNumber = query.PageNumber,
            TotalCount = total,
            DatabaseUri = Uri,
            Messages = docs.Select(m =>
            {
                var summary = new ScheduledMessageSummary
                {
                    Id = m.EnvelopeId,
                    MessageType = m.MessageType,
                    ScheduledTime = m.ExecutionTime,
                    Destination = m.ReceivedAt,
                    Attempts = m.Attempts
                };

                summary.ReadEnvelopeIdentity(m.Body);
                return summary;
            }).ToList()
        };
    }

    async Task IScheduledMessages.CancelAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        if (query.SagaId.IsNotEmpty())
        {
            // Null when the saga has nothing scheduled. An empty id list would mean "no id filter".
            var bySagaIds = await ScheduledMessageSagaFilter.ResolveCancellationAsync(this, query, token);
            if (bySagaIds == null) return;
            query = bySagaIds;
        }

        await Incoming.DeleteManyAsync(ScheduledFilter(query), token);
    }

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
