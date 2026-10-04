using MCPHub.Core.Management;
using MCPHub.Core.Permissions;
using MCPHub.Core.Settings;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// Saying which configuration is wrong.
///
/// <para>Tool authorization is an AND over independent policies, so a missing tool can have several
/// causes at once — no grant, a capability switch off, and an environment variable pinning that
/// switch. An operator told only "denied" has two files and a checkbox to check and no idea which
/// one is lying to them. These pin that the hub can say.</para>
/// </summary>
public sealed class ToolDenialDiagnosticsTests : IDisposable
{
    private const string Install = "mcphub__install";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static PermissionsPrincipal Principal(string id, params string[] tools) => new()
    {
        Id = id,
        Name = id,
        KeyHash = PermissionsConfigurationRules.HashKey($"key-for-{id}-0123456789abcdef"),
        Tools = tools,
    };

    private static PermissionsToolAuthorization Permissions(params PermissionsPrincipal[] principals) =>
        new(new StaticPermissionsSource(new PermissionsConfiguration
        {
            AllowUnauthenticated = false,
            Principals = principals,
        }));

    /// <summary>
    /// A policy whose switches come only from the environment. Settings are left at their defaults —
    /// every test here drives the environment, which is what wins anyway and is the case that confuses
    /// operators.
    /// </summary>
    private AgentManagementPolicy Management(params (string Variable, string Value)[] environment)
    {
        var map = environment.ToDictionary(e => e.Variable, e => e.Value, StringComparer.Ordinal);
        var settings = new SettingsStore(new FakeAppPaths(_dir.Path), NullLogger<SettingsStore>.Instance);
        return new AgentManagementPolicy(settings, name => map.GetValueOrDefault(name));
    }

    // ---- an explanation never disagrees with enforcement ---------------------------------------

    [Fact]
    public void An_allowed_tool_explains_nothing()
    {
        var permissions = Permissions(Principal("banter", "*"));

        Assert.Null(permissions.Explain(new TenantContext("banter"), "kodi", "kodi__play_pause"));
    }

    [Fact]
    public void Every_denial_this_policy_reports_comes_with_a_reason()
    {
        var permissions = Permissions(Principal("banter", "kodi__*"));
        var tenant = new TenantContext("banter");

        Assert.False(permissions.IsToolVisible(tenant, "redis", "redis__get"));
        Assert.NotNull(permissions.Explain(tenant, "redis", "redis__get"));
    }

    // ---- which thing is wrong -------------------------------------------------------------------

    [Fact]
    public void A_missing_grant_says_so_and_offers_the_grant_to_add()
    {
        var denial = Permissions(Principal("banter", "kodi__*"))
            .Explain(new TenantContext("banter"), "redis", "redis__get")!;

        Assert.Equal(PermissionsToolAuthorization.NoGrantCode, denial.Code);
        Assert.Contains("redis__get", denial.Remedy!, StringComparison.Ordinal);
        Assert.Contains("redis__*", denial.Remedy!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_principal_is_distinguished_from_a_missing_grant()
    {
        var denial = Permissions(Principal("banter", "*") with { Enabled = false })
            .Explain(new TenantContext("banter"), "kodi", "kodi__play_pause")!;

        Assert.Equal(PermissionsToolAuthorization.PrincipalDisabledCode, denial.Code);
    }

    [Fact]
    public void A_principal_deleted_mid_session_is_distinguished_from_one_never_authenticated()
    {
        var permissions = Permissions(Principal("banter", "*"));

        Assert.Equal(
            PermissionsToolAuthorization.UnknownPrincipalCode,
            permissions.Explain(new TenantContext("deleted"), "kodi", "kodi__x")!.Code);
        Assert.Equal(
            PermissionsToolAuthorization.UnauthenticatedCode,
            permissions.Explain(TenantContext.Default, "kodi", "kodi__x")!.Code);
    }

    [Fact]
    public void Management_being_off_altogether_is_distinguished_from_one_capability_being_off()
    {
        var off = Management();
        var onButNoInstall = Management(
            (AgentManagementPolicy.EnabledVariable, "true"),
            (AgentManagementPolicy.InstallVariable, "false"));

        Assert.Equal(
            AgentManagementPolicy.ManagementOffCode,
            off.Explain(TenantContext.Default, "mcphub", Install)!.Code);
        Assert.Equal(
            AgentManagementPolicy.CapabilityOffCode,
            onButNoInstall.Explain(TenantContext.Default, "mcphub", Install)!.Code);
    }

    /// <summary>
    /// The single most confusing failure the hub has: a checkbox that will not stay where it is put,
    /// because an environment variable is winning. The denial has to name the variable.
    /// </summary>
    [Fact]
    public void A_switch_pinned_by_the_environment_names_the_variable_doing_it()
    {
        var denial = Management(
                (AgentManagementPolicy.EnabledVariable, "true"),
                (AgentManagementPolicy.InstallVariable, "false"))
            .Explain(TenantContext.Default, "mcphub", Install)!;

        // The source names the variable AND the value it is set to, which is what an operator needs:
        // "=false" is the half that explains why their checkbox will not stay on.
        Assert.Contains(AgentManagementPolicy.InstallVariable, denial.PinnedBy!, StringComparison.Ordinal);
        Assert.Contains("false", denial.PinnedBy!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AgentManagementPolicy.InstallVariable, denial.Remedy!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Off because nobody turned it on is not the same as off because something is forcing it, and
    /// only the second sends an operator to a container's environment. Management itself is the case
    /// to test: it is the one switch that defaults off, the three capability switches under it
    /// defaulting on.
    /// </summary>
    [Fact]
    public void A_switch_that_is_merely_off_is_not_reported_as_pinned()
    {
        var denial = Management().Explain(TenantContext.Default, "mcphub", Install)!;

        Assert.Equal(AgentManagementPolicy.ManagementOffCode, denial.Code);
        Assert.Null(denial.PinnedBy);
    }

    /// <summary>
    /// Turning management on is enough on its own: the capability switches default on beneath it, so
    /// an operator who flips the master gets install as well. Worth pinning, because the denial codes
    /// above would otherwise imply two steps where there is one.
    /// </summary>
    [Fact]
    public void Turning_management_on_grants_the_capabilities_beneath_it_by_default()
    {
        var policy = Management((AgentManagementPolicy.EnabledVariable, "true"));

        Assert.Null(policy.Explain(TenantContext.Default, "mcphub", Install));
        Assert.True(policy.IsToolVisible(TenantContext.Default, "mcphub", Install));
    }

    // ---- two things wrong at once ---------------------------------------------------------------

    /// <summary>
    /// The case the whole facility exists for. Grant the tool and nothing happens, because a switch
    /// was off too; turn the switch on and nothing happens, because the grant was missing. Reporting
    /// one denial at a time makes each fix look like it failed.
    /// </summary>
    [Fact]
    public void Both_causes_are_reported_when_both_are_wrong()
    {
        var composite = new CompositeToolAuthorization(
            Permissions(Principal("banter", "kodi__*")),
            Management((AgentManagementPolicy.EnabledVariable, "false")));

        var denials = composite.ExplainAll(new TenantContext("banter"), "mcphub", Install);

        Assert.Equal(2, denials.Count);
        Assert.Contains(denials, d => d.Code == PermissionsToolAuthorization.NoGrantCode);
        Assert.Contains(denials, d => d.Code == AgentManagementPolicy.ManagementOffCode);
    }

    /// <summary>
    /// And fixing one of the two leaves exactly the other, which is how an operator knows they are
    /// making progress rather than guessing.
    /// </summary>
    [Fact]
    public void Fixing_one_cause_leaves_exactly_the_other()
    {
        var granted = Principal("banter", "*");

        var afterGrant = new CompositeToolAuthorization(
            Permissions(granted),
            Management((AgentManagementPolicy.EnabledVariable, "false")))
            .ExplainAll(new TenantContext("banter"), "mcphub", Install);

        Assert.Single(afterGrant);
        Assert.Equal(AgentManagementPolicy.ManagementOffCode, afterGrant[0].Code);

        var afterBoth = new CompositeToolAuthorization(
            Permissions(granted),
            Management(
                (AgentManagementPolicy.EnabledVariable, "true"),
                (AgentManagementPolicy.InstallVariable, "true")))
            .ExplainAll(new TenantContext("banter"), "mcphub", Install);

        Assert.Empty(afterBoth);
    }

    [Fact]
    public void An_available_tool_has_no_denials_at_all()
    {
        var composite = new CompositeToolAuthorization(
            Permissions(Principal("banter", "kodi__*")),
            Management((AgentManagementPolicy.EnabledVariable, "true")));

        Assert.Empty(composite.ExplainAll(new TenantContext("banter"), "kodi", "kodi__play_pause"));
    }

    /// <summary>
    /// A policy that denies without diagnostics still produces a denial naming its type. "Denied and
    /// nothing knows why" is the one answer an operator can do nothing with.
    /// </summary>
    [Fact]
    public void A_policy_that_cannot_explain_itself_is_still_named()
    {
        var composite = new CompositeToolAuthorization(new DenyEverything());

        var denials = composite.ExplainAll(TenantContext.Default, "kodi", "kodi__play_pause");

        Assert.Single(denials);
        Assert.Equal(CompositeToolAuthorization.UndiagnosedCode, denials[0].Code);
        Assert.Contains(nameof(DenyEverything), denials[0].Reason, StringComparison.Ordinal);
    }

    private sealed class DenyEverything : IToolAuthorization
    {
        public bool IsToolVisible(TenantContext tenant, string serverKey, string exposedToolName) => false;

        public bool IsCallAllowed(TenantContext tenant, string serverKey, string exposedToolName) => false;
    }
}
