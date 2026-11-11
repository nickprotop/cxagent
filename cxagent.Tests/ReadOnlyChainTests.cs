using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A CHAIN OF READS IS A READ (user, 2026-11-11): a colleague exploring the codebase it knows wrote `cd X && echo … && ls
/// … | grep …` and was asked for every one. Each command of a chain is judged on its own; one that is not on the list,
/// a redirect anywhere but /dev/null, a second cd or a background `&` makes the whole line ask. `sed` prints line ranges
/// and nothing else.
/// </summary>
public sealed class ReadOnlyChainTests
{
    [Theory]
    [InlineData("cd src && echo \"== A ==\" && ls && grep -n 'a|b' f.cs | head", "src")]
    [InlineData("ls 2>/dev/null | head", null)]
    [InlineData("echo '=== x ===' ; ls Controls/ ; wc -l f.cs", null)]
    [InlineData("cat f.cs | grep -v '^\\s*///' | head -7", null)]
    [InlineData("sed -n '1,50p' f.cs", null)]
    [InlineData("cd SharpConsoleUI && sed -n '114,170p' Layout/DockRootLayout.cs", "SharpConsoleUI")]
    [InlineData("tail -n 5 f.log && sed -n '$p' f.log", null)]
    [InlineData("sed -n '330,350p;415,445p' f.cs", null)]
    public void A_chain_of_reads_is_read_only(string command, string? cd)
    {
        Assert.True(ReadOnlyCommands.IsReadOnly(command, out var changesTo), command);
        Assert.Equal(cd, changesTo);
    }

    [Theory]
    [InlineData("cat f && rm f")]
    [InlineData("echo x > f")]
    [InlineData("ls >> out.txt")]
    [InlineData("cd a && cd b && ls")]
    [InlineData("ls & rm -rf x")]
    [InlineData("ls $(rm -rf x)")]
    [InlineData("ls `rm x`")]
    [InlineData("git log --oneline -3")]
    [InlineData("sed -i 's/a/b/' f")]
    [InlineData("sed -ni '1p' f")]
    [InlineData("sed -n '1w out' f")]
    [InlineData("sed -n '1p;2w out' f")]
    [InlineData("sed 's/a/b/' f")]
    [InlineData("sed -n 's/a/b/e' f")]
    [InlineData("sed -n '1,5p' f --in-place")]
    [InlineData("ls | sh")]
    [InlineData("tail -f app.log && ls")]
    [InlineData("grep 'abc f.cs")]
    [InlineData("cat <<EOF\nx\nEOF")]
    public void Anything_else_in_the_chain_makes_it_ask(string command) =>
        Assert.False(ReadOnlyCommands.IsReadOnly(command, out _), command);
}
