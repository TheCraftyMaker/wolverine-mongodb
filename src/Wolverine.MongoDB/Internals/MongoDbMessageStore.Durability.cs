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
            .Limit(_options.Durability.RecoveryBatchSize)
            .ToListAsync(token);

        var toReplay = new List<(DeadLetterMessage Doc, Envelope Envelope)>();

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
            toReplay.Add((doc, envelope));
        }

        if (toReplay.Count == 0)
        {
            return;
        }

        try
        {
            // Batch store is all-or-nothing (F8): the common case (no duplicates in the batch)
            // persists every envelope and deletes every handled DLQ doc in two round trips
            // instead of one StoreIncomingAsync + DeleteOneAsync pair per letter.
            await StoreIncomingAsync(toReplay.Select(x => x.Envelope).ToList());
            await DeadLetterDocs.DeleteManyAsync(
                Builders<DeadLetterMessage>.Filter.In(x => x.Id, toReplay.Select(x => x.Doc.Id)), token);
        }
        catch (DuplicateIncomingEnvelopeException)
        {
            // The batch persisted nothing: at least one envelope in it already exists in the
            // inbox (the crash-window shape below). Fall back to the per-letter path so every
            // OTHER letter in the batch still replays, and the documented idempotent-replay
            // behavior (a duplicate converges by deleting its DLQ doc) survives.
            foreach (var (doc, envelope) in toReplay)
            {
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
            // but before enqueue leaves the doc Incoming owned by this node's number. The
            // orphan-recovery loop only matches OwnerId == AnyNode, so the doc is NOT re-picked
            // while owned; it is rescued at the next Solo-mode startup, which releases all
            // ownership (NodeAgentController.StartLocally). Balanced-mode recovery of this
            // window is part of the multinode plan.
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
            // we now own and enqueue exactly those to avoid double-processing. Key on the document
            // _id, the same identity unit ReassignIncomingAsync claims on: in IdAndDestination mode
            // the envelope Guid is not unique per destination, so it cannot map winners back
            // unambiguously. The page comes from distinct documents, so _id is injective here.
            var byId = envelopes.ToDictionary(InboxIdentity);
            var claimedIds = await Incoming.Distinct(x => x.Id,
                Builders<IncomingMessage>.Filter.And(
                    Builders<IncomingMessage>.Filter.In(x => x.Id, byId.Keys),
                    Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, nodeNumber)),
                cancellationToken: token).ToListAsync(token);

            var claimed = claimedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            if (claimed.Count > 0)
            {
                await circuit.EnqueueDirectlyAsync(claimed);
            }
        }
    }

    /// <summary>
    /// Node numbers observed as owned-but-unregistered on the previous recovery tick. Per-store
    /// state, not per-agent: <c>StartScheduledJobs</c> and <c>BuildAgent</c> each construct a new
    /// <c>MongoDbDurabilityAgent</c>, so "one agent per store" is not guaranteed by construction,
    /// while one dead-set per database is. Today the recovery loop is the only live caller (the
    /// scheduled-jobs agent is never started), so no locking is required; if a future Wolverine
    /// version starts a second agent, add an <c>Interlocked</c>/<c>lock</c> guard here rather than
    /// moving this field onto the agent.
    /// </summary>
    private HashSet<int>? _previousDeadOwners;

    /// <summary>
    /// Releases incoming/outgoing ownership held by node numbers that are confirmed dead: a number
    /// must be observed as owned-but-unregistered on TWO consecutive recovery ticks before its
    /// envelopes are released, and the release write names those numbers positively
    /// (<c>Filter.In(confirmed)</c>) rather than excluding a possibly-stale live snapshot
    /// (<c>Filter.Nin(live)</c>).
    ///
    /// Why this is sound (do not weaken to a single tick):
    ///  1. A number only enters the candidate set if some document already carries it as OwnerId.
    ///  2. A node cannot own anything before <c>INodeAgentPersistence.PersistAsync</c> has written
    ///     its node document (PersistAsync allocates the number and writes the doc before
    ///     returning, and the leader can only assign this store's durability agent from the
    ///     persisted node table).
    ///  3. The owned-set read happens BEFORE the live-set read, so "owned but not live" means the
    ///     node document existed at the earlier instant and was gone at the later one — i.e. it was
    ///     deleted on shutdown (NodeAgentController), never merely mid-registration.
    ///  4. Node numbers are monotonic and never reused (see the node-number-reuse decision in
    ///     CLAUDE.md, T4.6), so a number issued after the previous tick cannot appear in the
    ///     previous tick's dead set. Together with (3), a confirmed number was dead for the whole
    ///     interval between the two ticks, not merely at one instant.
    ///
    /// (3) and (4) are independent on purpose: even if a future change reused freed node numbers,
    /// (3) would still exclude a starting node and the In(confirmed) whitelist would still exclude
    /// any number unobserved at both reads.
    ///
    /// Cost: a crashed node's envelopes are rescued one recovery interval later than before.
    /// Graceful shutdown is unaffected — it releases ownership directly via ReleaseAllOwnershipAsync.
    /// Residual (out of scope, see FOLLOWUPS): if a *live* node's document is deleted by a reaper
    /// while it is still claiming, its work is released after two ticks — the same semantic the RDBMS
    /// single-statement release has; closing it requires an ownership fencing token.
    /// </summary>
    internal async Task ReleaseDeadNodeOwnershipAsync(CancellationToken token)
    {
        // OWNED FIRST, LIVE SECOND. This order is load-bearing (see fact 3 above): it makes the
        // liveness check strictly later than the evidence of ownership, so a node that is merely
        // mid-registration can never produce the "owned but not live" pattern.
        var owned = new HashSet<int>();
        owned.UnionWith(await Incoming
            .Distinct(x => x.OwnerId, FilterDefinition<IncomingMessage>.Empty, cancellationToken: token)
            .ToListAsync(token));
        owned.UnionWith(await Outgoing
            .Distinct(x => x.OwnerId, FilterDefinition<OutgoingMessage>.Empty, cancellationToken: token)
            .ToListAsync(token));

        var live = (await NodeDocs
            .Find(FilterDefinition<NodeDocument>.Empty)
            .Project(x => x.AssignedNodeNumber)
            .ToListAsync(token)).ToHashSet();

        // AnyNode (0) is by definition not "owned".
        live.Add(MongoConstants.AnyNode);

        var deadNow = owned.Where(n => !live.Contains(n)).ToHashSet();
        var confirmed = _previousDeadOwners is null
            ? []
            : deadNow.Intersect(_previousDeadOwners).ToList();

        // Assigned on EVERY path, including the early return below: a tick that finds nothing dead
        // must not leave a stale set behind for the next tick to confirm against.
        _previousDeadOwners = deadNow;

        if (confirmed.Count == 0)
        {
            return;
        }

        await Incoming.UpdateManyAsync(
            Builders<IncomingMessage>.Filter.In(x => x.OwnerId, confirmed),
            Builders<IncomingMessage>.Update.Set(x => x.OwnerId, MongoConstants.AnyNode),
            cancellationToken: token);

        await Outgoing.UpdateManyAsync(
            Builders<OutgoingMessage>.Filter.In(x => x.OwnerId, confirmed),
            Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, MongoConstants.AnyNode),
            cancellationToken: token);
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

            if (expired.Length > 0)
            {
                await DeleteOutgoingAsync(expired);
            }

            if (good.Length == 0)
            {
                continue;
            }

            // CAS claim: only flip envelopes still globally owned. A competing node that
            // claimed one of these ids between our load and this write keeps it.
            var nodeNumber = runtime.DurabilitySettings.AssignedNodeNumber;
            var ids = good.Select(e => e.Id).ToList();
            await Outgoing.UpdateManyAsync(
                Builders<OutgoingMessage>.Filter.And(
                    Builders<OutgoingMessage>.Filter.In(x => x.Id, ids),
                    Builders<OutgoingMessage>.Filter.Eq(x => x.OwnerId, MongoConstants.AnyNode)),
                Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, nodeNumber),
                cancellationToken: token);

            // Re-read which ids this node actually won and enqueue exactly those.
            var claimedIds = (await Outgoing.Distinct(x => x.Id,
                    Builders<OutgoingMessage>.Filter.And(
                        Builders<OutgoingMessage>.Filter.In(x => x.Id, ids),
                        Builders<OutgoingMessage>.Filter.Eq(x => x.OwnerId, nodeNumber)),
                    cancellationToken: token).ToListAsync(token))
                .ToHashSet();

            foreach (var envelope in good.Where(e => claimedIds.Contains(e.Id)))
            {
                await sendingAgent.EnqueueOutgoingAsync(envelope);
            }
        }
    }
}
