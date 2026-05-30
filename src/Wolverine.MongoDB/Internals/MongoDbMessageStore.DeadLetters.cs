using JasperFx.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IDeadLetters
{
    public Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null) => throw new NotImplementedException();
    public Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range, CancellationToken token) => throw new NotImplementedException();
    public Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token) => throw new NotImplementedException();
    public Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token) => throw new NotImplementedException();
    public Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token) => throw new NotImplementedException();
    public Task EditAndReplayAsync(Guid envelopeId, byte[] newBody, CancellationToken token) => throw new NotImplementedException();
}
