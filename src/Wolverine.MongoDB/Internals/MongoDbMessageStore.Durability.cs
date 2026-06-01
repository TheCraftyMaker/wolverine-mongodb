using MongoDB.Driver;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore
{
    /// <summary>
    /// Translation of the Cosmos durability agent's runScheduledJobs body: find scheduled
    /// envelopes that are due, flip them to Incoming owned by this node, and enqueue them
    /// to the local scheduled queue for immediate execution.
    /// </summary>
    internal async Task PublishDueScheduledMessagesAsync(IWolverineRuntime runtime, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var b = Builders<IncomingMessage>.Filter;
        var filter = b.And(
            b.Eq(x => x.Status, EnvelopeStatus.Scheduled),
            b.Lte(x => x.ExecutionTime, now));

        var due = await Incoming.Find(filter)
            .Sort(Builders<IncomingMessage>.Sort.Ascending(x => x.ExecutionTime))
            .Limit(runtime.DurabilitySettings.RecoveryBatchSize)
            .ToListAsync(token);

        if (due.Count == 0)
        {
            return;
        }

        var localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(TransportConstants.Scheduled);
        var ownerId = runtime.DurabilitySettings.AssignedNodeNumber;

        foreach (var message in due)
        {
            await Incoming.UpdateOneAsync(
                Builders<IncomingMessage>.Filter.Eq(x => x.Id, message.Id),
                Builders<IncomingMessage>.Update
                    .Set(x => x.Status, EnvelopeStatus.Incoming)
                    .Set(x => x.OwnerId, ownerId),
                cancellationToken: token);

            var envelope = message.Read();
            envelope.Status = EnvelopeStatus.Incoming;
            envelope.OwnerId = ownerId;
            await localQueue.EnqueueAsync(envelope);
        }
    }

    /// <summary>
    /// Best-effort orphan recovery: reassign globally-owned incoming envelopes to listeners
    /// that are currently accepting. Mirrors the Cosmos durability agent's incoming recovery.
    /// </summary>
    internal async Task RecoverOrphanedIncomingAsync(IWolverineRuntime runtime, CancellationToken token)
    {
        var b = Builders<IncomingMessage>.Filter;
        var filter = b.And(
            b.Eq(x => x.OwnerId, MongoConstants.AnyNode),
            b.Eq(x => x.Status, EnvelopeStatus.Incoming),
            b.Ne(x => x.ReceivedAt, null));

        var listeners = await Incoming.Distinct(x => x.ReceivedAt, filter, cancellationToken: token).ToListAsync(token);

        foreach (var listenerStr in listeners)
        {
            if (listenerStr is null)
            {
                continue;
            }

            var listener = new Uri(listenerStr);
            var circuit = runtime.Endpoints.FindListenerCircuit(listener);
            if (circuit is null || circuit.Status != ListeningStatus.Accepting)
            {
                continue;
            }

            var envelopes = await LoadPageOfGloballyOwnedIncomingAsync(listener, runtime.DurabilitySettings.RecoveryBatchSize);
            await ReassignIncomingAsync(runtime.DurabilitySettings.AssignedNodeNumber, envelopes);
            await circuit.EnqueueDirectlyAsync(envelopes);
        }
    }

    /// <summary>
    /// Best-effort orphan recovery for outgoing envelopes: reassign globally-owned (OwnerId == 0)
    /// outgoing messages to this node and hand them back to the sending agent for delivery,
    /// discarding any that have already expired. Mirrors the Cosmos durability agent's outgoing recovery.
    /// </summary>
    internal async Task RecoverOrphanedOutgoingAsync(IWolverineRuntime runtime, CancellationToken token)
    {
        var b = Builders<OutgoingMessage>.Filter;
        var filter = b.And(
            b.Eq(x => x.OwnerId, MongoConstants.AnyNode),
            b.Ne(x => x.Destination, null));

        var destinations = await Outgoing.Distinct(x => x.Destination, filter, cancellationToken: token).ToListAsync(token);

        foreach (var destinationStr in destinations)
        {
            if (destinationStr is null)
            {
                continue;
            }

            var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(new Uri(destinationStr));
            if (sendingAgent.Latched)
            {
                continue;
            }

            var outgoing = await LoadOutgoingAsync(sendingAgent.Destination);
            var expired = outgoing.Where(x => x.IsExpired()).ToArray();
            var good = outgoing.Where(x => !x.IsExpired()).ToArray();

            await DiscardAndReassignOutgoingAsync(expired, good, runtime.DurabilitySettings.AssignedNodeNumber);

            foreach (var envelope in good)
            {
                await sendingAgent.EnqueueOutgoingAsync(envelope);
            }
        }
    }
}
