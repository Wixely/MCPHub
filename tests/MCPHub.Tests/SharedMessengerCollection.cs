using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// Test classes that listen on <c>WeakReferenceMessenger.Default</c>, which is one instance for the whole
/// process. Run in parallel, each hears the other's messages, and a test asserting it heard exactly one fails
/// at random (seen on Linux). Sharing a collection makes xunit run them one after another.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SharedMessengerCollection
{
    public const string Name = "Shared messenger";
}
