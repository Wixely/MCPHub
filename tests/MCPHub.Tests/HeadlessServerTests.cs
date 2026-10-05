using MCPHub.Core.Infrastructure;
using MCPHub.Core.Permissions;
using MCPHub.Core.Settings;
using MCPHub.Core.Users;
using MCPHub.Hub;
using MCPHub.Hub.Proxy;
using MCPHub.Proxy;
using MCPHub.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// MCPHub without a window.
///
/// <para>The hub is composed once and worn by two heads, so what is tested here is the seam: that
/// the headless head gets the same hub the desktop does — the same proxy, the same identity, the
/// same management tools — and that the few things a container decides for itself come from its
/// environment rather than from a user profile that does not exist in an image.</para>
/// </summary>
public sealed class HeadlessServerTests
{
    // ---- what a container decides for itself ----------------------------------------------------

    [Fact]
    public void Paths_come_from_the_environment_and_default_to_the_mounted_volumes()
    {
        var mounted = new ContainerPaths(_ => null);

        Assert.Equal(Path.GetFullPath("/config"), mounted.SettingsDirectory);
        Assert.Equal(Path.GetFullPath("/data"), mounted.DataDirectory);

        var told = new ContainerPaths(name => name switch
        {
            ContainerPaths.ConfigVariable => "/srv/conf",
            ContainerPaths.DataVariable => "/srv/data",
            _ => null,
        });

        Assert.Equal(Path.GetFullPath("/srv/conf"), told.SettingsDirectory);
        Assert.Equal(Path.GetFullPath("/srv/data"), told.DataDirectory);
        Assert.StartsWith(told.DataDirectory, told.DownloadsDirectory, StringComparison.Ordinal);
    }

    /// <summary>
    /// Loopback would be a hub reachable only from inside its own container, so the default is every
    /// interface — the opposite of the desktop's, and right for the opposite reason.
    /// </summary>
    [Fact]
    public void The_listener_defaults_to_every_interface_and_the_proxy_port()
    {
        var options = ServerOptions.FromEnvironment(_ => null);

        Assert.Equal("0.0.0.0", options.BindAddress);
        Assert.Equal(5800, options.Port);
        Assert.Null(options.Administration);
    }

    // ---- one, the other, or both ----------------------------------------------------------------

    /// <summary>
    /// Tools by default, models when asked for. The Router forwards to upstream providers, so a
    /// listener nobody has configured an output for would answer every request with a 503 — on by
    /// default it would look broken rather than unconfigured.
    /// </summary>
    [Fact]
    public void The_proxy_is_served_by_default_and_the_router_is_not()
    {
        var options = ServerOptions.FromEnvironment(_ => null);

        Assert.True(options.Proxy);
        Assert.False(options.Router);
        Assert.Equal("0.0.0.0", options.RouterBindAddress);
        Assert.Equal(5801, options.RouterPort);
    }

    [Fact]
    public void The_router_can_be_served_on_its_own()
    {
        var options = ServerOptions.FromEnvironment(name => name switch
        {
            ServerOptions.ProxyEnabledVariable => "false",
            ServerOptions.RouterEnabledVariable => "true",
            _ => null,
        });

        Assert.False(options.Proxy);
        Assert.True(options.Router);
    }

    [Fact]
    public void Both_can_be_served_from_one_container()
    {
        var options = ServerOptions.FromEnvironment(name => name switch
        {
            ServerOptions.RouterEnabledVariable => "on",
            ServerOptions.RouterPortVariable => "15801",
            ServerOptions.RouterBindVariable => "127.0.0.1",
            _ => null,
        });

        Assert.True(options.Proxy);
        Assert.True(options.Router);
        Assert.Equal("127.0.0.1", options.RouterBindAddress);
        Assert.Equal(15801, options.RouterPort);
    }

    /// <summary>
    /// A container that starts, logs nothing wrong and answers nothing is indistinguishable from a
    /// broken one — and somebody would go looking for the fault in their network.
    /// </summary>
    [Fact]
    public void Serving_neither_is_refused_rather_than_started()
    {
        var thrown = Assert.Throws<ArgumentException>(() => ServerOptions.FromEnvironment(name => name switch
        {
            ServerOptions.ProxyEnabledVariable => "false",
            ServerOptions.RouterEnabledVariable => "false",
            _ => null,
        }));

        Assert.Contains(ServerOptions.ProxyEnabledVariable, thrown.Message, StringComparison.Ordinal);
        Assert.Contains(ServerOptions.RouterEnabledVariable, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kestrel's own answer to this is a bind error naming neither listener, on whichever happened to
    /// come up second. Named here instead, with the two variables that fix it.
    /// </summary>
    [Fact]
    public void Two_listeners_on_one_port_are_refused_by_name()
    {
        var thrown = Assert.Throws<ArgumentException>(() => ServerOptions.FromEnvironment(name => name switch
        {
            ServerOptions.RouterEnabledVariable => "true",
            ServerOptions.RouterPortVariable => "5800",
            _ => null,
        }));

        Assert.Contains("5800", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(ServerOptions.RouterPortVariable, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>Same port, different interfaces, is a real deployment and not a clash.</summary>
    [Fact]
    public void The_same_port_on_different_addresses_is_allowed()
    {
        var options = ServerOptions.FromEnvironment(name => name switch
        {
            ServerOptions.BindVariable => "127.0.0.1",
            ServerOptions.RouterEnabledVariable => "true",
            ServerOptions.RouterBindVariable => "0.0.0.0",
            ServerOptions.RouterPortVariable => "5800",
            _ => null,
        });

        Assert.Equal(options.Port, options.RouterPort);
        Assert.NotEqual(options.BindAddress, options.RouterBindAddress);
    }

    [Fact]
    public void A_router_address_that_could_never_be_bound_is_refused_at_startup()
    {
        Assert.Throws<ArgumentException>(() => ServerOptions.FromEnvironment(name => name switch
        {
            ServerOptions.RouterEnabledVariable => "true",
            ServerOptions.RouterBindVariable => "my-container",
            _ => null,
        }));
    }

    /// <summary>The names the headless Router sample already used, so a compose file moving onto this
    /// image keeps the lines it had.</summary>
    [Fact]
    public void The_router_reads_the_variables_it_always_did()
    {
        Assert.Equal("MCPHUB_ROUTER_BIND", ServerOptions.RouterBindVariable);
        Assert.Equal("MCPHUB_ROUTER_PORT", ServerOptions.RouterPortVariable);
    }

    [Fact]
    public void The_listener_can_be_told_where_to_bind()
    {
        var options = ServerOptions.FromEnvironment(name => name switch
        {
            ServerOptions.BindVariable => "127.0.0.1",
            ServerOptions.PortVariable => "15800",
            _ => null,
        });

        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.Equal(15800, options.Port);
    }

    /// <summary>
    /// A port that is not a port stops the process rather than quietly binding a different one: a
    /// container published on 5800 whose hub listened elsewhere would look like a network problem.
    /// </summary>
    [Theory]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    public void A_port_that_could_never_be_bound_is_refused_at_startup(string port)
    {
        Assert.Throws<ArgumentException>(
            () => ServerOptions.FromEnvironment(name => name == ServerOptions.PortVariable ? port : null));
    }

    /// <summary>
    /// The administration tools are the reason to run this headless — something else has to be able
    /// to add users and grant tools — but they still default off, and the variable is the same one
    /// the desktop reads so a compose file cannot mean two things.
    /// </summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    [InlineData("perhaps", null)]
    public void Administration_is_read_from_the_same_variable_the_desktop_uses(string value, bool? expected)
    {
        var options = ServerOptions.FromEnvironment(
            name => name == ServerOptions.AdministrationVariable ? value : null);

        Assert.Equal(expected, options.Administration);
        Assert.Equal(AdministrationPolicy.EnabledVariable, ServerOptions.AdministrationVariable);
    }

    // ---- the same hub, without a window ---------------------------------------------------------

    /// <summary>
    /// The seam this whole arrangement exists for: the headless head composes the hub and gets the
    /// proxy, identity, permissions and policies — not a second, thinner hub that would drift from
    /// the desktop's and quietly enforce something else.
    /// </summary>
    [Fact]
    public async Task A_headless_hub_serves_the_proxy_identity_and_permissions()
    {
        using var directory = new TempDir();
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(new FakeAppPaths(directory.Path));
        HubComposition.ConfigureHub(services);

        // Async disposal: resolving every provider constructs the process host, which is
        // IAsyncDisposable only and throws if the container is torn down synchronously.
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<ProxyCoordinator>());
        Assert.NotNull(provider.GetRequiredService<UserStore>());
        Assert.NotNull(provider.GetRequiredService<PermissionsStore>());

        var policies = provider.GetRequiredService<CompositeToolAuthorization>().Policies;
        Assert.Contains(policies, p => p is PermissionsToolAuthorization);
        Assert.Contains(policies, p => p is AdministrationPolicy);

        var tools = provider.GetServices<ILocalToolProvider>().ToList();
        Assert.Contains(tools, t => t.Key == UserToolProvider.ProviderKey);
        Assert.Contains(tools, t => t.Key == PermissionsToolProvider.ProviderKey);
    }

    /// <summary>
    /// And it composes without a desktop: nothing in the hub may reach for a window, a user profile
    /// or a place to run at sign-in. Registering an IAppPaths is the head's one obligation, so a hub
    /// built without one must fail loudly here rather than writing to somebody's home directory.
    /// </summary>
    [Fact]
    public void The_hub_asks_its_head_where_files_live_rather_than_assuming()
    {
        var services = new ServiceCollection();
        HubComposition.ConfigureHub(services);

        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<UserStore>());
    }

    /// <summary>
    /// A container is running because somebody started it, so the proxy comes up whatever a mounted
    /// settings file says — and on the port the deployment published, not one chosen on a desktop.
    /// </summary>
    [Fact]
    public async Task The_deployments_listener_wins_over_a_settings_file_carried_in()
    {
        using var directory = new TempDir();
        var paths = new FakeAppPaths(directory.Path);
        var stale = new SettingsStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance);
        stale.Current.ProxyBindAddress = "127.0.0.1";
        stale.Current.ProxyPort = 9999;
        stale.Current.StartProxyOnLaunch = false;
        await stale.SaveAsync();

        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(paths);
        HubComposition.ConfigureHub(services);
        services.AddSingleton(new ServerOptions(
            Proxy: true, BindAddress: "0.0.0.0", Port: 5801,
            Router: false, RouterBindAddress: "0.0.0.0", RouterPort: 5802,
            Administration: true));
        await using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<ISettingsStore>();
        var options = provider.GetRequiredService<ServerOptions>();

        // What HubLifetime applies before starting the coordinator.
        settings.Current.ProxyBindAddress = options.BindAddress;
        settings.Current.ProxyPort = options.Port;
        settings.Current.StartProxyOnLaunch = true;
        settings.Current.AdministrationEnabled = options.Administration!.Value;
        await settings.SaveAsync();

        var reopened = new SettingsStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance);
        Assert.Equal("0.0.0.0", reopened.Current.ProxyBindAddress);
        Assert.Equal(5801, reopened.Current.ProxyPort);
        Assert.True(reopened.Current.StartProxyOnLaunch);
        Assert.True(reopened.Current.AdministrationEnabled);
    }
}
