using System.Security.Cryptography;

namespace CxAgent.Helpers;

/// <summary>
/// Generates ULID-style ids: 48-bit millisecond timestamp + 80 bits randomness,
/// Crockford base32, 26 chars, lexicographically sortable. Monotonic within a
/// process: ids generated in the same millisecond still increase.
/// </summary>
public static class UlidGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32
    private static readonly object Lock = new();
    private static long _lastMs = -1;
    private static readonly byte[] _lastRandom = new byte[10];

    public static string NewId()
    {
        lock (Lock)
        {
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (ms == _lastMs)
            {
                // Same ms: increment the random component to stay monotonic.
                for (int i = 9; i >= 0; i--)
                {
                    if (++_lastRandom[i] != 0) break;
                }
            }
            else
            {
                _lastMs = ms;
                RandomNumberGenerator.Fill(_lastRandom);
            }

            return Encode(ms, _lastRandom);
        }
    }

    private static string Encode(long ms, byte[] random)
    {
        Span<char> chars = stackalloc char[26];
        // 10 chars of timestamp (48 bits)
        for (int i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(ms & 0x1F)];
            ms >>= 5;
        }
        // 16 chars of randomness (80 bits) — encode the 10 bytes as base32.
        int bit = 0, value = 0, ci = 10;
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
        return new string(chars);
    }
}
