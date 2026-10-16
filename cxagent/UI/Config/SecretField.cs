namespace CxAgent.UI.Config;

/// <summary>
/// How a stored secret — an API key, a token in an environment variable or a header — is shown.
///
/// <para>NEVER THE VALUE. "set" and the last four characters are enough to tell two keys apart and to
/// see that one is there; the stored value is never put back into a field the user can read or copy.</para>
/// </summary>
public static class SecretField
{
    /// <summary>"none", or "set · …" and the last four characters.</summary>
    public static string Describe(string? stored) =>
        string.IsNullOrEmpty(stored) ? "none" : $"set · …{(stored.Length > 4 ? stored[^4..] : "")}";
}
