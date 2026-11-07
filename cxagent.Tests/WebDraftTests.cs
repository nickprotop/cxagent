using System.Text.Json.Nodes;
using CxAgent.Core.Llm;
using CxAgent.Core.Web;
using CxAgent.UI.Config;
using CxAgent.Web.Auth;
using Xunit;

namespace CxAgent.Tests;

/// <summary>F8 › Web: what it reads from config.json's web block, what a save writes back, and what it refuses.</summary>
public sealed class WebDraftTests
{
    private static readonly Dictionary<string, string> NoEnv = new();
    private const string Providers = """ "providers": { "local": { "kind": "ollama", "model": "m" } }, "defaultProvider": "local" """;

    private static WebDraft Draft(string extra = "") =>
        WebDraft.From(ProviderConfigLoader.Parse($$"""{ {{Providers}} {{extra}} }""", NoEnv));

    private static JsonObject Saved(WebDraft draft, string extra = "")
    {
        var root = JsonNode.Parse($$"""{ {{Providers}} {{extra}} }""")!.AsObject();
        draft.Apply(root);
        return root;
    }

    [Fact]
    public void An_untouched_draft_is_clean_and_writes_nothing()
    {
        var draft = Draft();
        Assert.False(draft.IsDirty);
        Assert.Null(Saved(draft)["web"]);
        Assert.Equal("5180", draft.PortText);
        Assert.Equal("127.0.0.1", draft.Bind);
        Assert.Equal(LoginMode.Auto, draft.Login);
    }

    [Fact]
    public void Every_field_round_trips()
    {
        var draft = Draft();
        draft.PortText = "6001";
        draft.Bind = "0.0.0.0";
        draft.SelfSigned = true;
        draft.Login = LoginMode.Always;
        draft.RootsText = "~/source\n/srv/a,b";
        draft.IdleText = "0";
        draft.AddUser("nick", "secret");
        var root = Saved(draft);

        var back = WebDraft.From(ProviderConfigLoader.Parse(root.ToJsonString(), NoEnv));
        Assert.Equal("6001", back.PortText);
        Assert.Equal("0.0.0.0", back.Bind);
        Assert.True(back.SelfSigned);
        Assert.Equal(LoginMode.Always, back.Login);
        Assert.Equal("~/source\n/srv/a,b", back.RootsText);
        Assert.Equal("0", back.IdleText);
        Assert.Equal(["nick"], back.Users.Select(u => u.Name));
        Assert.False(back.IsDirty);
    }

    [Fact]
    public void A_new_password_is_stored_as_a_hash_that_matches()
    {
        var draft = Draft();
        draft.AddUser("nick", "secret");
        var stored = Saved(draft)["web"]!["users"]!["nick"]!.GetValue<string>();
        Assert.StartsWith("pbkdf2:", stored, StringComparison.Ordinal);
        Assert.True(PasswordCheck.Matches(stored, "secret"));
    }

    [Fact]
    public void A_plain_password_already_there_is_kept_and_shown_as_plain()
    {
        var draft = Draft(""", "web": { "users": { "ana": "pw" } } """);
        Assert.True(draft.Users.Single().Plain);
        draft.AddUser("nick", "x");
        Assert.Equal("pw", Saved(draft, """, "web": { "users": { "ana": "pw" } } """)["web"]!["users"]!["ana"]!.GetValue<string>());
    }

    [Fact]
    public void Setting_a_password_replaces_it_with_a_hash()
    {
        var extra = """, "web": { "users": { "ana": "pw" } } """;
        var draft = Draft(extra);
        draft.SetPassword("ana", "new");
        var stored = Saved(draft, extra)["web"]!["users"]!["ana"]!.GetValue<string>();
        Assert.True(PasswordCheck.Matches(stored, "new"));
        Assert.False(PasswordCheck.Matches(stored, "pw"));
    }

    [Fact]
    public void Removing_the_last_user_removes_the_users_key()
    {
        var extra = """, "web": { "login": "always", "users": { "ana": "pw" } } """;
        var draft = Draft(extra);
        draft.RemoveUser("ana");
        var web = Saved(draft, extra)["web"]!;
        Assert.Null(web["users"]);
        Assert.Equal("always", web["login"]!.GetValue<string>());
    }

    [Fact]
    public void Back_to_the_defaults_removes_the_block()
    {
        var extra = """, "web": { "port": 6000 } """;
        var draft = Draft(extra);
        draft.PortText = "5180";
        Assert.Null(Saved(draft, extra)["web"]);
    }

    [Fact]
    public void Unknown_keys_in_the_block_are_kept()
    {
        var extra = """, "web": { "future": 1 } """;
        var draft = Draft(extra);
        draft.SelfSigned = true;
        var web = Saved(draft, extra)["web"]!;
        Assert.Equal(1, web["future"]!.GetValue<int>());
        Assert.True(web["selfSigned"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("port", "0", "port")]
    [InlineData("port", "x", "port")]
    [InlineData("bind", "localhost", "IP address")]
    [InlineData("roots", "projects", "start with / or ~")]
    [InlineData("idle", "-1", "idle")]
    public void Bad_values_cannot_be_saved(string field, string value, string says)
    {
        var draft = Draft();
        switch (field)
        {
            case "port": draft.PortText = value; break;
            case "bind": draft.Bind = value; break;
            case "roots": draft.RootsText = value; break;
            case "idle": draft.IdleText = value; break;
        }
        Assert.Contains(says, draft.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "pw", "name")]
    [InlineData("nick", "", "password")]
    [InlineData("a b", "pw", "name")]
    public void A_user_needs_a_name_and_a_password(string name, string password, string says) =>
        Assert.Contains(says, Draft().AddUser(name, password), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void A_name_already_there_cannot_be_added_again()
    {
        var draft = Draft(""", "web": { "users": { "ana": "pw" } } """);
        Assert.Contains("already", draft.AddUser("ana", "x"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Login_required_with_no_users_is_named_as_a_problem()
    {
        var draft = Draft();
        draft.Login = LoginMode.Always;
        Assert.Contains("user", draft.Problem, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Browser access: the four switches -----------------------------------------------------------------------

    [Fact]
    public void The_switches_show_their_defaults_and_writing_nothing_keeps_them()
    {
        var draft = Draft();
        Assert.False(draft.AllowShell);
        Assert.False(draft.AllowConfig);
        Assert.True(draft.AllowPlugins);
        Assert.False(draft.AllowOpenAnywhere);
        Assert.Null(Saved(draft)["web"]);
    }

    [Fact]
    public void A_switch_turned_on_is_written_and_read_back()
    {
        var draft = Draft();
        draft.AllowShell = true;
        draft.AllowOpenAnywhere = true;
        Assert.True(draft.IsDirty);
        var root = Saved(draft);
        Assert.True(root["web"]!["allow"]!["shell"]!.GetValue<bool>());
        Assert.Null(root["web"]!["allow"]!["config"]);
        var back = WebDraft.From(ProviderConfigLoader.Parse(root.ToJsonString(), NoEnv));
        Assert.True(back.AllowShell);
        Assert.True(back.AllowOpenAnywhere);
        Assert.False(back.AllowConfig);
    }

    [Fact]
    public void Plugins_turned_off_is_written_and_turned_back_on_leaves_no_allow_block()
    {
        var draft = Draft();
        draft.AllowPlugins = false;
        var root = Saved(draft);
        Assert.False(root["web"]!["allow"]!["plugins"]!.GetValue<bool>());

        var again = WebDraft.From(ProviderConfigLoader.Parse(root.ToJsonString(), NoEnv));
        again.AllowPlugins = true;
        again.Apply(root);
        Assert.Null(root["web"]);
    }
}
