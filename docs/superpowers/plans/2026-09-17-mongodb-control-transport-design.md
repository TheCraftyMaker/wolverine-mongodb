# MongoDB control transport for Balanced mode

**Status:** proposed, awaiting approval before implementation
**Date:** 2026-09-17
**Plan:** `docs/superpowers/plans/2026-09-17-mongodb-control-transport.md` (this design sits in the plans folder because the repo ignores `docs/superpowers/specs`)

## Problem

`DurabilityMode.Balanced` needs a control channel: the leader hands agents (the durability agent, exclusive listeners, scheduled jobs) to other nodes by sending `AssignAgent` and friends to each node's control URI. Every other Wolverine message store ships a native, store-backed control transport for this: the RDBMS stores and Oracle since long, Cosmos DB since 2026-03-16, RavenDb since 2026-07-02. Wolverine.MongoDB does not, so its README tells Balanced-mode users to call `UseTcpForControlEndpoint()`.

That call advertises `tcp://localhost:<port>`, which Wolverine's own XML doc describes as "probably mostly just for testing". On any platform where nodes are separate containers, the address is unreachable and agent assignment to a remote node times out forever. The first production consumer (the RMG Content Hub on Azure Container Apps, JIMMY-77) is pinned to one replica because of it. The alternative Wolverine offers, broker control queues, needs the runtime identity to create queues on the broker at startup, which that consumer's shared namespace does not allow.

## Goal

A Balanced-mode host that calls `opts.UseMongoDbPersistence(...)` and nothing else gets a working control channel through the same MongoDB database it already writes to. Hosts that configured another control endpoint (TCP, Azure Service Bus control queues) keep it. Solo hosts see no change at all.

## Design

The design mirrors Wolverine's RavenDb control transport member for member, translated to the MongoDB driver. Anything not listed here is deliberately the same as RavenDb's.

### Components

All internal, under `src/Wolverine.MongoDB/Internals/Transport/`:

| Type | Role |
|------|------|
| `MongoDbControlTransport : ITransport, IAsyncDisposable` | Scheme `mongocontrol`. Holds one `MongoDbControlEndpoint` per node id in a `Cache<Guid, MongoDbControlEndpoint>`, exposes `ControlEndpoint` for the current node, and owns a `RetryBlock<List<Envelope>>` that deletes delivered documents. |
| `MongoDbControlEndpoint : Endpoint` | URI `mongocontrol://<node guid>`, `EndpointRole.System`, `BufferedInMemory` locked through `supportsMode`, `MaxDegreeOfParallelism = 1`, telemetry off. Builds the listener and the sender. |
| `MongoDbControlSender : ISender` | Inserts one `ControlMessageDocument` per envelope addressed to the target node, through a `RetryBlock<Envelope>`. Stamps `DeliverWithin = 10 seconds` on the envelope and `expires = now + 30 seconds` on the document. A duplicate key on insert is ignored: the message was already posted. |
| `MongoDbControlListener : IListener` | Polls once a second for documents where `nodeId` is this node and `expires` is in the future, ordered by `posted`, deserialises the bodies with `EnvelopeSerializer`, hands them to the receiver, then deletes them through the transport's delete block. `CompleteAsync` deletes a single document; `DeferAsync` is a no-op. |
| `ControlMessageDocument` (in `Internals/`) | `_id` = envelope id (Guid, standard representation), `nodeId` (Guid), `messageType`, `body` (bytes), `expires` and `posted` (UTC dates). |

The collection is `wolverine_control_messages`, a new `MongoConstants.ControlMessagesCollection`. It joins the reserved names in `MongoCollectionNaming`, the `ClearAllAsync` sweep, and `EnsureIndexesAsync`.

### Registration

`MongoDbMessageStore.Initialize(IWolverineRuntime)` registers the transport when three conditions hold, exactly as the RavenDb and Cosmos stores do: the store's `Role` is `Main`, `runtime.Options.Transports.NodeControlEndpoint` is null, and `runtime.Options.Durability.Mode` is `Balanced`. It constructs the transport over the store's pinned `_database` handle (majority and journaled writes, majority reads), adds it to `runtime.Options.Transports`, and sets `NodeControlEndpoint` to the transport's `ControlEndpoint`. Wolverine calls `Initialize` before `WolverineNode.For`, which is the call that throws on a null control endpoint in Balanced mode; the RavenDb and Cosmos stores rely on the same ordering.

Registration is lazy, in `Initialize`, like Cosmos, not eager in `UseMongoDbPersistence` like RavenDb. RavenDb registers eagerly so its scheme resolves for publishing rules; nothing publishes application messages to a control queue in this library, so the simpler form wins.

The existing `WarnOnBalancedMode` message changes from "a control endpoint is required" to naming the control URI in use, and keeps the clock-synchronisation reminder.

### Provisioning

`EnsureIndexesAsync` creates two indexes on `wolverine_control_messages`, only when the durability mode is Balanced: a compound ascending index on `nodeId, posted` for the listener's query, and a TTL index on `expires` with `ExpireAfter = TimeSpan.Zero`, which reaps anything a dead node never collected. Gating on Balanced keeps a Solo deployment's collection set byte-identical to today, in the same way the deduplication indexes are gated on their flag. `ClearAllAsync` deletes the collection's documents unconditionally, alongside the other system collections.

### Data flow

1. The leader's `NodeAgentController` resolves a target node's `ControlUri` from `wolverine_nodes`. Because this transport set the node's control endpoint, that URI is `mongocontrol://<target guid>`.
2. `Transports.GetOrCreateEndpoint` returns the target's `MongoDbControlEndpoint`; its sender inserts the document.
3. The target's listener finds the document on its next one-second poll, delivers the envelope through the normal handler pipeline, and deletes it.
4. Replies (`AgentCommands` results) travel the same way in the other direction, because `ReplyEndpoint()` returns the current node's control endpoint and `IsUsedForReplies` semantics come from the `Endpoint` base.

### Failure handling

A control message the target never reads is deleted by the TTL index within about a minute of its expiry (the TTL monitor runs every sixty seconds), and Wolverine's own `DeliverWithin` discards a late one on receipt. The listener additionally filters `expires > now` so a message that survived past its expiry but before the TTL sweep is never delivered; this is one predicate and it removes a stale-command window that the RavenDb version leaves to the envelope check alone.

The poll loop logs and continues on any exception, including a transient MongoDB outage; the durability agent's own recovery loops already tolerate the same outages. Startup failure modes are unchanged: if the database is unreachable, the storage migration fails first.

### Compatibility

- A host that calls `UseTcpForControlEndpoint()` or `EnableWolverineControlQueues()` before the store initialises keeps that endpoint, because `NodeControlEndpoint` is no longer null. The TCP call becomes unnecessary, not harmful.
- Solo, Serverless and MediatorOnly hosts get no transport, no collection, no indexes.
- Ancillary stores never register a control transport.
- No public API changes. The change is additive: version 1.1.0.

### Consumers

Wolverine.MongoDB consumers running Balanced mode delete their `UseTcpForControlEndpoint()` line after upgrading. For the RMG Content Hub that is one line in `IntegrationsBindings` plus the package bump; nothing changes on its Service Bus namespace.

## Testing

Two suites port from `external/wolverine/src/Persistence/RavenDbTests/`, both tagged `[Trait("Category", "multinode")]` because each starts two Balanced hosts on one database:

- `control_transport_compliance.cs`: the upstream `TransportCompliance<T>` suite (23 facts) over a `MongoDbControlTransportFixture` whose `OutboundAddress` is the receiver node's `mongocontrol://` URI. This proves the transport contract: send by destination, request and reply, stop and restart listeners, correlation, scheduling.
- `control_queue_tests.cs`: the two-node facts that prove the registration, the scheme, one-way send and request-reply between separately started hosts.

New facts in existing files:

- `durability_mode_guard.cs`: a Balanced host starts with no explicit control endpoint and ends up with a `mongocontrol` one; a Balanced host that calls `UseTcpForControlEndpoint()` keeps `tcp`.
- `control_collection.cs` (new, single-node): a Balanced host provisions the `nodeId, posted` and TTL indexes; a Solo host creates no `wolverine_control_messages` collection; an expired document addressed to a live node is never delivered.

The existing two-host suites (`multinode_end_to_end.cs`, `saga_multinode.cs`, `entity_multinode.cs`, `leadership_election_compliance.cs`, `exclusive_listener_recovery_compliance.cs`) drop their `UseTcpForControlEndpoint()` lines and run on the new transport, which is the strongest regression check available: leader election, exactly-once scheduling and dead-node rescue over the real control channel.

## Documentation

`CHANGELOG.md` under Unreleased. `README.md` Multinode section rewritten: no TCP requirement, the collection and its indexes, how to keep an external control endpoint. `CLAUDE.md`: layout table, collections table, the Balanced-mode bullet, the constraint line at the end. `FOLLOWUPS.md`: a change-stream listener as a possible latency improvement over the one-second poll, deferred until a consumer needs sub-second agent handoff.

## Out of scope

- A change-stream-based listener. MongoDB change streams would push control messages instead of polling, and the library already requires a replica set, but they add resume-token handling and a long-lived cursor per node. The one-second poll matches every sibling provider and is enough for agent coordination, whose other timers run at seconds to minutes.
- Any change to leader election, agent assignment or the durability agent. They consume the control channel; they do not change.
- Making the poll interval or expiry configurable. Neither RavenDb nor Cosmos exposes them; add a knob when a consumer asks.

## Decisions

- **Scheme `mongocontrol`, not `mongodb`.** `mongodb://` is the driver's connection string scheme; reusing it for endpoint URIs invites confusion in logs and configuration. Cosmos made the same choice with `cosmoscontrol`.
- **Collection `wolverine_control_messages`.** Follows the `wolverine_<plural noun>` pattern of every system collection and reads as what it holds.
- **Indexes gated on Balanced, sweep unconditional.** Solo deployments stay byte-identical; a rebuild always clears everything.
- **Expiry filter in the listener query.** One predicate, closes the window between expiry and the TTL sweep.
- **One PR.** The change is cohesive and small; docs travel with the code.

## As built

- The Testing section above lists `exclusive_listener_recovery_compliance.cs` among the suites
  that drop their `UseTcpForControlEndpoint()` line. That suite starts one Solo host per fact and
  never called it, so nothing changed there.
- The Testing section says the upstream `TransportCompliance<T>` suite has 23 facts. At the
  pinned `V6.38.0` submodule it declares 23 `[Fact]` attributes, one of them commented out
  upstream, so 22 actually run.
- The Registration section argues for lazy registration on the grounds that nothing in this
  library publishes application messages to a control queue. The Testing section's own
  `MongoDbControlTransportFixture` contradicts that: the upstream compliance harness's
  `PublishAllMessages().To(OutboundAddress)` rule resolves the `mongocontrol` scheme at host
  build time, before `Initialize` runs. Resolution: lazy registration stayed,
  `MongoDbMessageStore.Initialize` now reuses an already-registered `MongoDbControlTransport`
  instead of constructing a second one, the way `RavenDbMessageStore.Initialize` does, and the
  compliance fixture registers the transport itself on the sender host so the scheme resolves
  for the harness's publishing rule.
