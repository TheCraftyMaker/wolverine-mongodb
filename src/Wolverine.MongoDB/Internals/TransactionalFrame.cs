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

    public TransactionalFrame(IChain chain)
    {
        _chain = chain;

        // The MongoDB session this frame opens. Downstream frames (the commit
        // postprocessor) discover it through this created variable.
        Session = new Variable(typeof(IClientSessionHandle), "mongoSession", this);
    }

    public Variable Session { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        // IMongoClient is registered by UseMongoDbPersistence
        _client = chain.FindVariable(typeof(IMongoClient));
        yield return _client;

        _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices);

        if (_context != null)
        {
            yield return _context;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment("Open a MongoDB session and transaction for the outbox unit of work");
        writer.Write(
            $"using var {Session.Usage} = await {_client!.Usage}.{nameof(IMongoClient.StartSessionAsync)}(cancellationToken: {_cancellation!.Usage}).ConfigureAwait(false);");
        writer.Write($"{Session.Usage}.{nameof(IClientSessionHandle.StartTransaction)}();");

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
