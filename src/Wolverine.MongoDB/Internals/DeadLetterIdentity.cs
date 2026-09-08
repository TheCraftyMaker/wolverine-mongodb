using System.Security.Cryptography;
using System.Text;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// Derives the dead-letter document key from the store's message-identity unit.
/// <para>
/// The key stays a <see cref="Guid"/> — i.e. a BSON Binary subtype-4 <c>_id</c> — deliberately.
/// Dead-letter documents have carried a Guid <c>_id</c> since the first release; re-typing the
/// member to a string would make the driver throw ("Cannot deserialize a 'String' from BsonType
/// 'Binary'") on every pre-existing document, and it throws inside cursor deserialization, so it
/// would fail whole result sets rather than one row — taking out <c>QueryAsync</c> and every
/// <c>ReplayDeadLettersAsync</c> tick of the durability agent.
/// </para>
/// </summary>
internal static class DeadLetterIdentity
{
    /// <summary>
    /// Folds an identity string — the <c>(envelope id, destination)</c> pair rendered by
    /// <c>MongoDbMessageStore.InboxIdentity</c> — into a stable, platform-independent
    /// <see cref="Guid"/>. SHA-256 truncated to 16 bytes with RFC 9562 version-8 / variant bits,
    /// read big-endian so the value is identical on every platform and driver version.
    /// </summary>
    internal static Guid Derive(string identity)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(identity), hash);

        Span<byte> id = stackalloc byte[16];
        hash[..16].CopyTo(id);
        id[6] = (byte)((id[6] & 0x0F) | 0x80); // version 8 (custom)
        id[8] = (byte)((id[8] & 0x3F) | 0x80); // RFC 9562 variant

        return new Guid(id, bigEndian: true);
    }
}
