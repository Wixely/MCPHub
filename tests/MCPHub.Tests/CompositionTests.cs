using MCPHub.App;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Management;
using MCPHub.Core.Permissions;
using MCPHub.Core.Users;
using MCPHub.Core.Recipes;
using MCPHub.Core.Routing;
using MCPHub.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// The desktop composition root. <c>ValidateOnBuild</c> builds a call site for every registration without
/// running any constructor, so this catches a service added with a dependency nobody registered — which
/// otherwise only shows up as a crash on launch.
/// </summary>
public sealed class CompositionTests
{
    [Fact]
    public void Every_registered_service_can_be_constructed_from_what_is_registered()
    {
        var services = new ServiceCollection();
        Composition.ConfigureServices(services);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.NotNull(provider);
    }

    /// <summary>
    /// The permissions stack is actually enforced, not merely registered.
    ///
    /// <para>ValidateOnBuild above proves every service can be constructed, which would pass just as
    /// happily with the permissions policies registered and left out of the composite the proxy
    /// consults. That failure has no symptom: the hub starts, the tools are there, and every caller
    /// sees everything. So this resolves what the proxy will actually be given and looks inside it.</para>
    ///
    /// <para>Pointed at a temporary directory — the last <c>IAppPaths</c> registration wins — so
    /// resolving the stores reads a scratch settings folder rather than the machine's own.</para>
    /// </summary>
    [Fact]
    public void The_proxy_is_given_a_policy_that_includes_permissions()
    {
        using var directory = new TempDir();
        var services = new ServiceCollection();
        Composition.ConfigureServices(services);
        services.AddSingleton<IAppPaths>(new FakeAppPaths(directory.Path));

        using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<CompositeToolAuthorization>().Policies;

        Assert.Contains(policies, p => p is PermissionsToolAuthorization);
        Assert.Contains(policies, p => p is AdministrationPolicy);

        // The ones that were already there must still be, since the composite is an AND and dropping
        // one would widen access rather than narrow it.
        Assert.Contains(policies, p => p is RecipeAccessPolicy);
        Assert.Contains(policies, p => p is AgentManagementPolicy);
    }

    /// <summary>
    /// And the permissions tools are offered through the proxy, which is what makes any of it
    /// manageable from another process.
    /// </summary>
    [Fact]
    public async Task The_permissions_tools_are_among_the_proxys_local_providers()
    {
        using var directory = new TempDir();
        var services = new ServiceCollection();
        Composition.ConfigureServices(services);
        services.AddSingleton<IAppPaths>(new FakeAppPaths(directory.Path));

        // Async disposal: resolving every provider constructs the process host, which is
        // IAsyncDisposable only and throws if the container is torn down synchronously.
        await using var provider = services.BuildServiceProvider();
        var providers = provider.GetServices<ILocalToolProvider>().ToList();

        var permissions = Assert.Single(providers.OfType<PermissionsToolProvider>().ToList());
        Assert.Equal("permissions", permissions.Key);
        Assert.NotEmpty(permissions.Tools);

        // And identity with them: granting a user tools from another process is no use if the user
        // itself can only be created by somebody sitting at the desktop.
        var users = Assert.Single(providers.OfType<UserToolProvider>().ToList());
        Assert.Equal("users", users.Key);
        Assert.NotEmpty(users.Tools);
    }

    /// <summary>
    /// One directory, one key. Both pages and both tool providers have to be reading the same store —
    /// two instances would be two sets of users, which is the drift this whole split exists to end.
    /// </summary>
    [Fact]
    public void Everything_that_resolves_a_key_shares_one_user_directory()
    {
        using var directory = new TempDir();
        var services = new ServiceCollection();
        Composition.ConfigureServices(services);
        services.AddSingleton<IAppPaths>(new FakeAppPaths(directory.Path));

        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<UserStore>();
        Assert.Same(store, provider.GetRequiredService<IUserDirectory>());
        Assert.Same(store, provider.GetRequiredService<IWritableUsers>());

        var permissions = provider.GetRequiredService<PermissionsStore>();
        Assert.Same(permissions, provider.GetRequiredService<IPermissionsConfigurationSource>());
        Assert.Same(permissions, provider.GetRequiredService<IWritablePermissions>());

        var router = provider.GetRequiredService<RouterStore>();
        Assert.Same(router, provider.GetRequiredService<IRouterConfigurationSource>());
    }

    /// <summary>
    /// Deleting a user must leave nothing behind, and that only works if every layer keyed to a user is
    /// registered to be asked. A missing registration has no symptom at the time: the user goes, and a
    /// grant or a route stays, naming an id nobody holds.
    /// </summary>
    [Fact]
    public void Every_layer_keyed_to_a_user_is_asked_to_forget_it()
    {
        using var directory = new TempDir();
        var services = new ServiceCollection();
        Composition.ConfigureServices(services);
        services.AddSingleton<IAppPaths>(new FakeAppPaths(directory.Path));

        using var provider = services.BuildServiceProvider();
        var dependents = provider.GetServices<IUserDependent>().ToList();

        Assert.Contains(dependents, d => d is PermissionsStore);
        Assert.Contains(dependents, d => d is RouterStore);
    }
}
