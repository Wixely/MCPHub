namespace MCPHub.Core.Users;

/// <summary>
/// One caller MCPHub knows: an agent, an application, a script. Holds the identity and nothing about
/// what it may do — the Router keeps its route and the proxy keeps its tool grants, both keyed by
/// <see cref="Id"/>.
///
/// <para><b>Why this exists at all.</b> The Router and the proxy each used to keep their own list of
/// callers with their own keys, on their own listeners. An agent wanting a model route and a tool
/// therefore needed two keys, had to be suspended twice, rotated twice, and could not be answered for
/// in one place. One identity, one key, usable on both.</para>
///
/// <para>Called users because that is what a person administering the hub calls them, even though most
/// are programs.</para>
/// </summary>
public sealed record HubUser
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>What an operator calls this caller — an agent's nickname, an application's name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Off refuses the key everywhere at once, keeping the user and everything keyed to it.
    ///
    /// <para>One switch rather than one per surface, deliberately: suspending a caller that has been
    /// compromised should be a single action, and three switches that can disagree would be another
    /// thing nobody can explain. Narrower control is still available by removing its route or its
    /// grants.</para>
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>SHA-256 of the key, as 64 lowercase hex characters. The key itself is never stored.</summary>
    public string KeyHash { get; init; } = string.Empty;
}

/// <summary>Every user the hub knows.</summary>
public sealed record HubUsersConfiguration
{
    public int SchemaVersion { get; init; } = 1;

    public HubUser[] Users { get; init; } = [];
}
