using Wolverine.Persistence.Durability.ScheduledMessageManagement;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IScheduledMessages
{
    Task<ScheduledMessageResults> IScheduledMessages.QueryAsync(ScheduledMessageQuery query, CancellationToken token) => throw new NotImplementedException();
    Task IScheduledMessages.CancelAsync(ScheduledMessageQuery query, CancellationToken token) => throw new NotImplementedException();
    Task IScheduledMessages.RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime, CancellationToken token) => throw new NotImplementedException();
    Task<IReadOnlyList<ScheduledMessageCount>> IScheduledMessages.SummarizeAsync(string serviceName, CancellationToken token) => throw new NotImplementedException();
}
