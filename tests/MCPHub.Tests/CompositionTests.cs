using MCPHub.App;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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
}
