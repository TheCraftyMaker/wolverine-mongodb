using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using MongoDB.Driver;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// Shared plumbing for the three entity-query frames behind <c>[All]</c>, <c>[FirstOrDefault]</c> and
/// <c>[Queryable]</c>. Each resolves the DI-registered <see cref="IMongoDatabase"/> and, exactly like
/// <see cref="LoadEntityFrame"/>, the Wolverine-managed <see cref="IClientSessionHandle"/>
/// <b>non-forcingly</b>: when the chain carries an outbox transaction (a write-back storage action,
/// a <c>MongoDbUnitOfWork</c> parameter, …) the read runs on that session and therefore sees the
/// transaction's own writes; a read-only handler has no session and reads straight off the database.
/// The session is never injected just to satisfy a read.
/// </summary>
internal abstract class MongoEntityQueryFrame : AsyncFrame
{
    protected readonly Type ElementType;
    protected Variable? Database;
    protected Variable? Session;
    protected Variable? Cancellation;

    protected MongoEntityQueryFrame(Type elementType)
    {
        // Codegen-time class-map alignment, same reason as every other entity frame: the collection
        // name and _id member are resolved for this type before any document is read.
        MongoIdentityMapping.EnsureIdMember(elementType);
        ElementType = elementType;
    }

    public Variable Result { get; protected init; } = null!;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        Database = chain.FindVariable(typeof(IMongoDatabase));
        yield return Database;

        Cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return Cancellation;

        Session = chain.TryFindVariable(typeof(IClientSessionHandle), VariableSource.NotServices);
        if (Session is not null)
        {
            yield return Session;
        }
    }

    /// <summary>The session argument for the helper call: the chain's session, or <c>null</c> for a session-less read.</summary>
    protected string SessionArgument => Session?.Usage ?? "null";
}

/// <summary>
/// <c>[All] IReadOnlyList&lt;T&gt;</c>: every document of the type's collection, never null.
/// </summary>
internal class MongoAllFrame : MongoEntityQueryFrame
{
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType closes IReadOnlyList<> over the element type at CODEGEN time only; AOT consumers run pre-generated code in TypeLoadMode.Static.")]
    public MongoAllFrame(Type elementType) : base(elementType)
    {
        Result = new Variable(typeof(IReadOnlyList<>).MakeGenericType(elementType), $"all_{elementType.Name}", this);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment($"Read every {ElementType.NameInCode()} document (on the MongoDB session when inside a transaction)");
        writer.Write(
            $"var {Result.Usage} = await {typeof(MongoEntityOperations).FullNameInCode()}.{nameof(MongoEntityOperations.LoadAllAsync)}<{ElementType.FullNameInCode()}>({Database!.Usage}, {SessionArgument}, {Cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// <c>[FirstOrDefault] T?</c>: the first document of the type's collection, or <c>null</c>. There is
/// deliberately no "required" branch — core owns that decision and chose not to have one for this
/// attribute.
/// </summary>
internal class MongoFirstOrDefaultFrame : MongoEntityQueryFrame
{
    public MongoFirstOrDefaultFrame(Type elementType) : base(elementType)
    {
        Result = new Variable(elementType, $"firstOrDefault_{elementType.Name}", this);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment($"Read the first {ElementType.NameInCode()} document, if any (on the MongoDB session when inside a transaction)");
        writer.Write(
            $"var {Result.Usage} = await {typeof(MongoEntityOperations).FullNameInCode()}.{nameof(MongoEntityOperations.FirstOrDefaultAsync)}<{ElementType.FullNameInCode()}>({Database!.Usage}, {SessionArgument}, {Cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// <c>[Queryable] IQueryable&lt;T&gt;</c>: the driver's LINQ provider over the type's collection — the
/// sharp escape hatch. Synchronous, so it is a <see cref="SyncFrame"/>; it still resolves the session
/// the same non-forcing way so the queryable is transaction-consistent when one is open.
/// </summary>
internal class MongoQueryableFrame : SyncFrame
{
    private readonly Type _elementType;
    private Variable? _database;
    private Variable? _session;

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType closes IQueryable<> over the element type at CODEGEN time only; AOT consumers run pre-generated code in TypeLoadMode.Static.")]
    public MongoQueryableFrame(Type elementType)
    {
        MongoIdentityMapping.EnsureIdMember(elementType);
        _elementType = elementType;
        Result = new Variable(typeof(IQueryable<>).MakeGenericType(elementType), $"queryable_{elementType.Name}", this);
    }

    public Variable Result { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _database = chain.FindVariable(typeof(IMongoDatabase));
        yield return _database;

        _session = chain.TryFindVariable(typeof(IClientSessionHandle), VariableSource.NotServices);
        if (_session is not null)
        {
            yield return _session;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment($"The raw MongoDB IQueryable for {_elementType.NameInCode()} (on the MongoDB session when inside a transaction)");
        writer.Write(
            $"{Result.VariableType.FullNameInCode()} {Result.Usage} = {typeof(MongoEntityOperations).FullNameInCode()}.{nameof(MongoEntityOperations.Queryable)}<{_elementType.FullNameInCode()}>({_database!.Usage}, {_session?.Usage ?? "null"});");
        Next?.GenerateCode(method, writer);
    }
}
