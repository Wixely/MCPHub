using MCPHub.App;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Management;
using MCPHub.Core.Permissions;
using MCPHub.Core.Recipes;
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
        Assert.Contains(policies, p => p is PermissionsManagementPolicy);

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
    }
}
