using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using MongoDB.Driver;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// Opens a MongoDB session + transaction, enlists the MessageContext in the
/// Wolverine outbox, and wraps the rest of the handler chain in a try/catch so
/// that any failure aborts the transaction. Implements <see cref="IFlushesMessages"/>
/// so Wolverine does NOT append a standalone <see cref="FlushOutgoingMessages"/>
/// postprocessor: flushing happens AFTER the commit in <see cref="CommitMongoTransactionFrame"/>
/// to avoid the flush-before-commit stranding bug.
/// </summary>
internal class TransactionalFrame : AsyncFrame, IFlushesMessages
{
    private readonly IChain _chain;
    private Variable? _cancellation;
    private Variable? _client;
    private Variable? _context;
    private Variable? _database;
    private Variable? _deduplicator;
    private DeduplicationClaim? _claim;

    public TransactionalFrame(IChain chain)
    {
        _chain = chain;

        // The MongoDB session this frame opens. Downstream frames (the commit
        // postprocessor) discover it through this created variable.
        Session = new Variable(typeof(IClientSessionHandle), "mongoSession", this);

        // The session-bound unit of work, constructed from the open session +
        // database. Handlers that take a MongoDbUnitOfWork parameter resolve it here.
        UnitOfWork = new Variable(typeof(MongoDbUnitOfWork), "mongoUnitOfWork", this);
    }

    public Variable Session { get; }

    public Variable UnitOfWork { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        // IMongoClient is registered by UseMongoDbPersistence
        _client = chain.FindVariable(typeof(IMongoClient));
        yield return _client;

        // IMongoDatabase is registered by UseMongoDbPersistence; the unit of work is
        // built from it together with the session this frame opens.
        _database = chain.FindVariable(typeof(IMongoDatabase));
        yield return _database;

        _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices);

        if (_context != null)
        {
            yield return _context;
        }

        // GH-4180: a logical-deduplication claim is woven in AHEAD of this frame (core inserts it at
        // Middleware[0], before any session exists) and core omits its compensating release frame for
        // transactional chains on the assumption that the claim lives inside the transaction. No upstream
        // hook lets a provider write the claim on its session, so here the claim is committed on its own
        // and this frame releases it when the transaction rolls back — otherwise the retry of a failed
        // handler would be refused as a duplicate of its own failed attempt.
        _claim = DeduplicationClaim.FindIn(_chain);
        if (_claim is not null)
        {
            _deduplicator = chain.FindVariable(typeof(IMessageDeduplicator));
            yield return _deduplicator;
            yield return _claim.IsDuplicate;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment("Open a MongoDB session and transaction for the outbox unit of work");
        writer.Write(
            $"using var {Session.Usage} = await {_client!.Usage}.{nameof(IMongoClient.StartSessionAsync)}(cancellationToken: {_cancellation!.Usage}).ConfigureAwait(false);");
        // The options are NOT optional: MongoDB discards the store's handle-level write and
        // read concern for in-transaction operations, so an option-less StartTransaction()
        // would commit at the consumer's MongoClient default. See MongoTransactionOptions.
        writer.Write(
            $"{Session.Usage}.{nameof(IClientSessionHandle.StartTransaction)}({typeof(MongoTransactionOptions).FullNameInCode()}.{nameof(MongoTransactionOptions.Durable)});");

        writer.WriteComment("Session-bound unit of work for handlers that take MongoDbUnitOfWork");
        writer.Write(
            $"var {UnitOfWork.Usage} = new {typeof(MongoDbUnitOfWork).FullNameInCode()}({Session.Usage}, {_database!.Usage});");

        if (_context != null)
        {
            writer.WriteComment("Enlist in the MongoDB outbox transaction");
            writer.Write(
                $"var mongoEnvelopeTransaction = new {typeof(MongoDbEnvelopeTransaction).FullNameInCode()}({Session.Usage}, {_context.Usage});");
            writer.Write(
                $"{_context.Usage}.{nameof(MessageContext.EnlistInOutbox)}(mongoEnvelopeTransaction);");

            // Wrap the remainder of the chain (including the commit postprocessor that is
            // part of Next) so that any exception aborts the transaction and rethrows.
            writer.Write("BLOCK:try");
            Next?.GenerateCode(method, writer);
            writer.FinishBlock();
            writer.Write($"BLOCK:catch ({typeof(Exception).FullNameInCode()})");
            writer.Write("await mongoEnvelopeTransaction.RollbackAsync().ConfigureAwait(false);");
            if (_claim is not null)
            {
                writer.WriteComment("The logical-deduplication claim was taken outside this (now rolled back) transaction: release it so the retry is not refused as a duplicate");
                writer.Write(
                    $"BLOCK:if (!{_claim.IsDuplicate.Usage} && !string.IsNullOrWhiteSpace({_claim.DeduplicationId.Usage}))");
                writer.Write(
                    $"await {_deduplicator!.Usage}.{nameof(IMessageDeduplicator.ReleaseAsync)}({_claim.DeduplicationId.Usage}, {_claim.AncillaryStoreMarkerCode}, {_cancellation!.Usage}).ConfigureAwait(false);");
                writer.FinishBlock();
            }

            writer.Write("throw;");
            writer.FinishBlock();
        }
        else
        {
            Next?.GenerateCode(method, writer);
        }
    }
}

/// <summary>
/// Commits the MongoDB outbox transaction and then flushes the MessageContext's
/// outgoing messages. Emitted as a postprocessor (part of <see cref="TransactionalFrame"/>'s
/// try-block via Next) so the commit happens before the outgoing messages are relayed
/// to the broker — committing AFTER the handler writes and flushing AFTER the commit.
/// </summary>
internal class CommitMongoTransactionFrame : AsyncFrame
{
    private Variable? _cancellation;
    private Variable? _session;
    private Variable? _context;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IClientSessionHandle));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices);
        if (_context != null)
        {
            yield return _context;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment("Commit the MongoDB outbox transaction, then flush outgoing messages");
        writer.Write($"BLOCK:if ({_session!.Usage}.IsInTransaction)");
        writer.Write(
            $"await {_session.Usage}.{nameof(IClientSessionHandle.CommitTransactionAsync)}({_cancellation!.Usage}).ConfigureAwait(false);");
        writer.FinishBlock();

        if (_context != null)
        {
            writer.Write(
                $"await {_context.Usage}.{nameof(MessageContext.FlushOutgoingMessagesAsync)}().ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// The logical-deduplication claim core wove into a chain, as <see cref="TransactionalFrame"/> needs it
/// for the rollback release: the <c>isDuplicateMessage</c> flag, the deduplication-id variable and the
/// ancillary-store marker. Core's <c>ClaimDeduplicationIdFrame</c> is <c>internal</c> and exposes only the
/// flag publicly, so the other two are read reflectively — and a mismatch fails the host build loudly
/// (<see cref="InvalidOperationException"/>) rather than silently dropping the release, which would
/// re-introduce the poisoned-id failure this exists to prevent. TODO(upstream): a public hook on the
/// claim frame (or a provider-side "claim inside my transaction" seam) would remove the reflection.
/// </summary>
internal sealed class DeduplicationClaim
{
    private DeduplicationClaim(Variable isDuplicate, Variable deduplicationId, Type? ancillaryStoreMarker)
    {
        IsDuplicate = isDuplicate;
        DeduplicationId = deduplicationId;
        AncillaryStoreMarker = ancillaryStoreMarker;
    }

    public Variable IsDuplicate { get; }
    public Variable DeduplicationId { get; }
    public Type? AncillaryStoreMarker { get; }

    public string AncillaryStoreMarkerCode
        => AncillaryStoreMarker is null ? "null" : $"typeof({AncillaryStoreMarker.FullNameInCode()})";

    public static DeduplicationClaim? FindIn(IChain chain)
    {
        var frame = chain.Middleware.FirstOrDefault(x => x.GetType().Name == "ClaimDeduplicationIdFrame");
        if (frame is null) return null;

        var type = frame.GetType();
        var isDuplicate = type.GetProperty("Variable")?.GetValue(frame) as Variable;
        var idField = type.GetField("_deduplicationId", BindingFlags.Instance | BindingFlags.NonPublic);
        var markerField = type.GetField("_ancillaryStoreMarker", BindingFlags.Instance | BindingFlags.NonPublic);

        if (isDuplicate is null || idField?.GetValue(frame) is not Variable deduplicationId)
        {
            throw new InvalidOperationException(
                $"Wolverine.MongoDB cannot read the logical-deduplication claim frame ({type.FullName}) it needs to " +
                "release the claim when a MongoDB transaction rolls back. The WolverineFx version in use has changed " +
                "ClaimDeduplicationIdFrame's shape; update Wolverine.MongoDB before enabling message deduplication.");
        }

        return new DeduplicationClaim(isDuplicate, deduplicationId, markerField?.GetValue(frame) as Type);
    }
}
