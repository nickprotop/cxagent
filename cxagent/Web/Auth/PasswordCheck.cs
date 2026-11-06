using System.Security.Cryptography;
using System.Text;

namespace CxAgent.Web.Auth;

/// <summary>
/// Whether a password matches what config.json stores: the password itself, or a hash that begins <c>pbkdf2:</c>.
///
/// <para>PLAIN IS ALLOWED, ON PURPOSE. config.json already holds API keys in the clear, so a hash buys little for a
/// one-person machine — but a user who wants one gets one from <c>cxagent web passwd</c>.</para>
/// </summary>
public static class PasswordCheck
{
    private const string Prefix = "pbkdf2:";
    private const int Iterations = 600_000;

    /// <summary>Whether <paramref name="given"/> is the password <paramref name="stored"/> names. Constant time.</summary>
    public static bool Matches(string stored, string given)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // DIGESTS COMPARED, NOT THE STRINGS: equal lengths, so the time taken says nothing about how many leading
            // characters were right, or how long the real password is.
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(stored)), SHA256.HashData(Encoding.UTF8.GetBytes(given)));
        }

        var parts = stored.Split(':');
        if (parts.Length != 5 || parts[1] != "sha256"
            || !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var iterations)
            || iterations <= 0)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(given, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A hash to store instead of <paramref name="password"/>: <c>pbkdf2:sha256:600000:&lt;salt&gt;:&lt;hash&gt;</c>.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Prefix}sha256:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }
}
