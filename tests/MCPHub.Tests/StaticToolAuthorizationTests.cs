using MCPHub.Proxy;
using Xunit;

namespace MCPHub.Tests;

public class StaticToolAuthorizationTests
{
    private static StaticToolAuthorization Auth(params (string Tenant, string[] Patterns)[] grants)
        => new(new StaticToolAuthorizationOptions
        {
            Grants = grants.ToDictionary(g => g.Tenant, g => (IReadOnlyList<string>)g.Patterns),
        });

    private static readonly TenantContext Alice = new("alice");
    private static readonly TenantContext Bob = new("bob");

    [Fact]
    public void Server_key_grant_covers_every_tool_of_that_server()
    {
        var auth = Auth(("alice", ["noteworthy"]));

        Assert.True(auth.IsToolVisible(Alice, "noteworthy", "noteworthy__list_notes"));
        Assert.True(auth.IsCallAllowed(Alice, "noteworthy", "noteworthy__delete_note"));
        Assert.False(auth.IsToolVisible(Alice, "azuredevops", "azuredevops__azdo_get_project"));
    }

    [Fact]
    public void Original_name_wildcard_matches_under_any_server_key()
    {
        var auth = Auth(("alice", ["azdo_*"]));

        Assert.True(auth.IsToolVisible(Alice, "azuredevops", "azuredevops__azdo_get_project"));
        Assert.True(auth.IsCallAllowed(Alice, "azuredevops", "azuredevops__azdo_list_repositories"));
        Assert.False(auth.IsToolVisible(Alice, "azuredevops", "azuredevops__gh_get_repository"));
    }

    [Fact]
    public void Exact_namespaced_tool_grant_is_tool_scoped()
    {
        var auth = Auth(("alice", ["noteworthy__list_notes"]));

        Assert.True(auth.IsToolVisible(Alice, "noteworthy", "noteworthy__list_notes"));
        Assert.False(auth.IsToolVisible(Alice, "noteworthy", "noteworthy__delete_note"));
    }

    [Fact]
    public void Star_grants_everything()
    {
        var auth = Auth(("alice", ["*"]));

        Assert.True(auth.IsToolVisible(Alice, "anything", "anything__at_all"));
        Assert.True(auth.IsCallAllowed(Alice, "other", "other__tool"));
    }

    [Fact]
    public void Tenant_without_grants_sees_and_calls_nothing()
    {
        var auth = Auth(("alice", ["*"]));

        Assert.False(auth.IsToolVisible(Bob, "noteworthy", "noteworthy__list_notes"));
        Assert.False(auth.IsCallAllowed(Bob, "noteworthy", "noteworthy__list_notes"));
    }

    [Fact]
    public void Patterns_are_anchored_not_substring_matches()
    {
        var auth = Auth(("alice", ["azdo", "gh_*"]));

        // "azdo" is a server-key grant, not a prefix of tool names.
        Assert.False(auth.IsToolVisible(Alice, "azuredevops", "azuredevops__azdo_get_project"));
        Assert.True(auth.IsToolVisible(Alice, "azdo", "azdo__whatever"));

        // "gh_*" must match from the start of a candidate, not mid-string.
        Assert.False(auth.IsToolVisible(Alice, "github", "github__xgh_tool"));
        Assert.True(auth.IsToolVisible(Alice, "github", "github__gh_get_repository"));
    }
}
