using MongoDB.Driver;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore
{
    /// <summary>
    /// Moves dead-letter documents that have been flagged <c>Replayable</c> back into
    /// <c>wolverine_incoming_envelopes</c> as globally-owned Incoming envelopes, then removes
    /// them from the dead-letter queue. Mirrors the RDBMS
    /// <c>MoveReplayableErrorMessagesToIncomingOperation</c>; called from the durability agent's
    /// recovery loop so flagging a dead letter replayable actually re-delivers it.
    /// </summary>
    internal async Task ReplayDeadLettersAsync(CancellationToken token)
    {
        var replayable = await DeadLetterDocs
            .Find(Builders<DeadLetterMessage>.Filter.Eq(x => x.Replayable, true))
            .ToListAsync(token);

        foreach (var doc in replayable)
        {
            if (doc.Body is not { Length: > 0 })
            {
                // A poison dead letter without a serialized body cannot be replayed.
                // Unflag it (instead of failing every tick) and leave it queryable.
                await DeadLetterDocs.UpdateOneAsync(
                    Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, doc.Id),
                    Builders<DeadLetterMessage>.Update.Set(x => x.Replayable, false),
                    cancellationToken: token);
                continue;
            }

            var envelope = EnvelopeSerializer.Deserialize(doc.Body);
            envelope.Status = EnvelopeStatus.Incoming;
            envelope.OwnerId = MongoConstants.AnyNode;

            try
            {
                await StoreIncomingAsync(envelope);
            }
            catch (DuplicateIncomingEnvelopeException)
            {
                // A previous pass (or a competing node) already re-inserted this envelope
                // and crashed before deleting the DLQ doc. Fall through: removing the
                // DLQ doc below is what converges the replay.
            }

            await DeadLetterDocs.DeleteOneAsync(
                Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, doc.Id), token);
        }
    }

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
            // Atomically claim each due message by flipping Scheduled -> Incoming only if it is
            // still Scheduled. The Status==Scheduled guard means a competing node (or a prior
            // pass) cannot also claim it, so a due message is published exactly once. If the
            // claim returns null another node already took it, so skip. A crash after the flip
            // but before enqueue leaves the doc Incoming owned by this node, which the incoming
            // orphan-recovery loop re-picks — it is never silently stranded.
            var claimed = await Incoming.FindOneAndUpdateAsync(
                Builders<IncomingMessage>.Filter.And(
                    Builders<IncomingMessage>.Filter.Eq(x => x.Id, message.Id),
                    Builders<IncomingMessage>.Filter.Eq(x => x.Status, EnvelopeStatus.Scheduled)),
                Builders<IncomingMessage>.Update
                    .Set(x => x.Status, EnvelopeStatus.Incoming)
                    .Set(x => x.OwnerId, ownerId),
                new FindOneAndUpdateOptions<IncomingMessage> { ReturnDocument = ReturnDocument.After },
                token);

            if (claimed is null)
            {
                continue;
            }

            var envelope = claimed.Read();
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

            var nodeNumber = runtime.DurabilitySettings.AssignedNodeNumber;
            var envelopes = await LoadPageOfGloballyOwnedIncomingAsync(listener, runtime.DurabilitySettings.RecoveryBatchSize);
            await ReassignIncomingAsync(nodeNumber, envelopes);

            // Only enqueue the envelopes this node actually won. The CAS in ReassignIncomingAsync
            // skips any that another node claimed between our read and write, so re-read which ids
            // we now own and enqueue exactly those to avoid double-processing.
            var ids = envelopes.Select(x => x.Id).ToList();
            var claimedIds = await Incoming.Distinct(x => x.EnvelopeId,
                Builders<IncomingMessage>.Filter.And(
                    Builders<IncomingMessage>.Filter.In(x => x.EnvelopeId, ids),
                    Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, nodeNumber)),
                cancellationToken: token).ToListAsync(token);

            var claimedSet = claimedIds.ToHashSet();
            var claimed = envelopes.Where(e => claimedSet.Contains(e.Id)).ToList();
            if (claimed.Count > 0)
            {
                await circuit.EnqueueDirectlyAsync(claimed);
            }
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
