using System.Security.Cryptography;

namespace CxAgent.Helpers;

/// <summary>
/// Generates 26-character Crockford base32 ids: 80 bits of randomness followed by a 48-bit
/// millisecond timestamp.
///
/// <para>RANDOMNESS FIRST, WHICH IS BACKWARDS FROM A ULID, and deliberately so. A ULID leads with
/// its timestamp to sort lexicographically — but that means every id minted in the same few minutes
/// shares a leading prefix, and an id is something people READ and ABBREVIATE. Three sessions
/// started while driving the /sessions listing all rendered as <c>01KZXC</c>: an identifier that
/// identified nothing. The same collision showed in the log directory names.</para>
///
/// <para>WHAT WAS GIVEN UP is string-ordering by time, and nothing used it: every query orders by
/// <c>updated_at</c>, and the DAG orchestrator whose job ids were documented "sortable" is gone. The
/// timestamp is still IN the id and still exact to the millisecond — it is simply no longer the
/// part you read first.</para>
///
/// <para>Ids minted by earlier versions remain valid: they are the same length and alphabet, and
/// lookups that abbreviate accept either end.</para>
/// </summary>
public static class UlidGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32
    public static string NewId()
    {
        // A FRESH DRAW EVERY TIME, rather than one draw per millisecond nudged by one.
        //
        // The old scheme reused the previous randomness and incremented its last byte, which kept
        // ids monotonic inside a millisecond — and left their LEADING characters identical. That was
        // invisible while the timestamp came first and everything shared a prefix anyway; with the
        // randomness in front it is the whole problem, since two ids minted in the same millisecond
        // would abbreviate to the same six characters. 80 fresh bits collide at a rate nothing here
        // will ever meet, and no caller needs the monotonicity that reuse was buying.
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        return Encode(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), random);
    }

    private static string Encode(long ms, ReadOnlySpan<byte> random)
    {
        Span<char> chars = stackalloc char[26];

        // 16 chars of randomness (80 bits) FIRST — encode the 10 bytes as base32.
        int bit = 0, value = 0, ci = 0;
        for (int i = 0; i < 10; i++)
        {
            value = (value << 8) | random[i];
            bit += 8;
            while (bit >= 5)
            {
                bit -= 5;
                chars[ci++] = Alphabet[(value >> bit) & 0x1F];
            }
        }

        // ...then 10 chars of timestamp (48 bits).
        for (int i = 25; i >= 16; i--)
        {
            chars[i] = Alphabet[(int)(ms & 0x1F)];
            ms >>= 5;
        }

        return new string(chars);
    }
}
