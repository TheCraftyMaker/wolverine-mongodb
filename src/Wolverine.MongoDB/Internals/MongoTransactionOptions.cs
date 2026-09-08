using MongoDB.Driver;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// The transaction options that EVERY transaction this library opens must carry.
/// <para>
/// MongoDB ignores collection/database-level write concern for operations inside a transaction:
/// the individual writes are never acknowledged on their own, only <c>commitTransaction</c> is, and
/// that command's concern resolves transaction options → the session's default transaction options →
/// the consumer's <see cref="MongoClientSettings"/>. The store's durability pin
/// (<see cref="MongoDbMessageStore"/> ctor: <c>w:majority</c> + <c>j:true</c> and majority reads on
/// the database handle) therefore does NOT survive into a transaction — without these options it
/// would silently degrade to whatever the consumer's <c>MongoClient</c> is configured with (often
/// <c>w:1</c>). <see cref="Durable"/> restates the pin at transaction scope.
/// </para>
/// <para>
/// RULE: no <c>StartTransaction</c> / <c>WithTransactionAsync</c> call anywhere in this library may
/// omit options. There are exactly two places a transaction is opened, and both are pinned:
/// <list type="bullet">
///   <item>store-side code goes through <c>MongoDbMessageStore.InTransactionAsync</c>, the single
///   funnel that owns the store's session/transaction lifetime;</item>
///   <item>the code-generated handler/outbox transaction is emitted by <c>TransactionalFrame</c>,
///   which writes a reference to <see cref="Durable"/> into the generated source.</item>
/// </list>
/// A new transaction site must use one of those two. <c>transaction_write_concern.cs</c> asserts on
/// the emitted <c>commitTransaction</c> / <c>startTransaction</c> commands that every transaction
/// opened during a live host's lifetime carries this concern, so an unpinned fourth site fails
/// loudly rather than silently downgrading durability.
/// </para>
/// <para>
/// PUBLIC because the code-generated handler references <see cref="Durable"/> by name, and that
/// code compiles into the APPLICATION's assembly, which is not covered by this assembly's
/// <c>InternalsVisibleTo</c>. Same reason <see cref="MongoConstants"/>,
/// <see cref="MongoSagaOperations"/> and <see cref="MongoEntityOperations"/> are public.
/// </para>
/// </summary>
public static class MongoTransactionOptions
{
    /// <summary>
    /// The store's durability pin, restated at transaction scope: majority + journaled writes,
    /// majority reads.
    /// <para>
    /// This governs the handler/outbox transaction too, which is shared between Wolverine's
    /// inbox/outbox envelopes and the application's own enlisted writes (<c>MongoDbUnitOfWork</c>,
    /// a raw <see cref="IClientSessionHandle"/>, saga and entity documents). A transaction has
    /// exactly one write concern, so the store's promise necessarily governs those too — a handler
    /// that enlists in the outbox transaction opts into its commit semantics. Writes the
    /// application makes OUTSIDE that transaction, through the unpinned app-facing
    /// <see cref="IMongoDatabase"/>, are untouched.
    /// </para>
    /// <para>
    /// Read concern is majority for the same reason the sessionless
    /// <c>MongoDbMessageStore.ExistsAsync(Envelope, CancellationToken)</c> idempotency probe already
    /// reads at majority through the pinned database handle: the in-session eager idempotency probe
    /// must not be the one path that can observe a write which later rolls back.
    /// </para>
    /// <para>
    /// Deliberately a single non-configurable constant. <c>MongoDbPersistenceOptions</c> cannot
    /// reach the frame (<c>MongoDbPersistenceFrameProvider</c> is <c>new()</c>-constrained by
    /// Wolverine's <c>InsertFirstPersistenceStrategy&lt;T&gt;</c> and the options object is never
    /// registered in DI), and the pin already governs every non-transactional store write, so a
    /// per-host knob would buy no availability that the store does not already require. Tracked in
    /// <c>FOLLOWUPS.md</c>.
    /// </para>
    /// </summary>
    public static readonly TransactionOptions Durable = new(
        readConcern: ReadConcern.Majority,
        writeConcern: WriteConcern.WMajority.With(journal: true));
}
