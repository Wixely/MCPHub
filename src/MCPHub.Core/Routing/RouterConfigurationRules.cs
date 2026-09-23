using System.Net;

namespace MCPHub.Core.Routing;

/// <summary>Shared validation for every router configuration source.</summary>
public static class RouterConfigurationRules
{
    /// <summary>IPv4 loopback — the default bind, reachable only from this machine.</summary>
    public const string Loopback = "127.0.0.1";

    /// <summary>IPv4 wildcard — every interface on this machine, including the local network.</summary>
    public const string AnyIPv4 = "0.0.0.0";

    /// <summary>IPv6 wildcard. Kestrel accepts IPv4 connections on it too when the OS is dual-stack.</summary>
    public const string AnyIPv6 = "::";

    public static void Validate(RouterConfiguration c)
    {
        if (c.SchemaVersion != 1) throw new ArgumentException("Unsupported router configuration version.");
        if (c.Port is < 1024 or > 65535) throw new ArgumentException("Choose a router port between 1024 and 65535.");
        CoerceBindAddress(c.BindAddress);
        if (c.Inputs is null || c.Outputs is null || c.Inputs.Length > 256 || c.Outputs.Length > 256)
            throw new ArgumentException("Router supports up to 256 inputs and outputs.");
        if (c.Inputs.Any(i => i is null) || c.Outputs.Any(o => o is null)) throw new ArgumentException("Invalid router entries.");
        var ids = c.Outputs.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != c.Outputs.Length || c.Inputs.Select(i => i.Id).Distinct().Count() != c.Inputs.Length)
            throw new ArgumentException("Router IDs must be unique.");
        if (c.DefaultOutputId is not null && !ids.Contains(c.DefaultOutputId)) throw new ArgumentException("Choose an existing default output.");
        foreach (var o in c.Outputs)
        {
            ValidateName(o.Name);
            if (string.IsNullOrWhiteSpace(o.Id)) throw new ArgumentException("Missing output ID.");
            NormalizeBaseUrl(o.BaseUrl);
            if (o.Model?.Length > 256) throw new ArgumentException("Model name is too long.");
        }
        foreach (var i in c.Inputs)
        {
            ValidateName(i.Name);
            if (string.IsNullOrWhiteSpace(i.Id) || i.KeyHash is null || i.KeyHash.Length != 64 || !i.KeyHash.All(Uri.IsHexDigit))
                throw new ArgumentException("Invalid input key hash.");
            if (i.OutputId is not null && !ids.Contains(i.OutputId)) throw new ArgumentException("Choose an existing output or the global default.");
        }
        if (c.Inputs.Select(i => i.KeyHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.Inputs.Length)
            throw new ArgumentException("Each input must have a unique key.");
    }

    public static string NormalizeBaseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use an HTTP(S) API base URL without credentials, query, or fragment, e.g. http://localhost:8000/v1.");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    /// <summary>
    /// Parses a bind address, rejecting host names: Kestrel binds an address, and resolving a name here
    /// would silently pick one of several answers. Returns the canonical literal to store and display.
    /// </summary>
    public static IPAddress ParseBindAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value.Trim(), out var address))
            throw new ArgumentException($"Enter an IP address to bind, e.g. {Loopback} for this machine only or {AnyIPv4} for every network interface.");
        return address;
    }

    /// <inheritdoc cref="ParseBindAddress"/>
    public static string NormalizeBindAddress(string value) => ParseBindAddress(value).ToString();

    /// <summary>
    /// The bind address to use for a <em>stored</em> configuration, where absence is not a mistake: every
    /// <c>router.json</c> written before this setting existed has no bind address at all, and refusing to
    /// load one would cost the user their whole routing table over a field they never chose. Missing and
    /// blank both mean loopback, which is what those files were doing. A value that is present but is not an
    /// address is still an error — that is a real mistake, and silently ignoring it would bind somewhere the
    /// operator did not ask for.
    /// </summary>
    public static string CoerceBindAddress(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Loopback : NormalizeBindAddress(value);

    /// <summary>Whether <paramref name="address"/> is a wildcard, i.e. every interface rather than one.</summary>
    public static bool IsWildcard(string address) =>
        IPAddress.TryParse((address ?? string.Empty).Trim(), out var parsed) &&
        (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any));

    /// <summary>
    /// Host an agent on this machine should dial for a listener bound to <paramref name="bindAddress"/>.
    /// A wildcard bind has no dialable form of its own, so loopback stands in; an IPv6 literal is bracketed
    /// so it can be pasted into a URL.
    /// </summary>
    public static string ClientHost(string bindAddress)
    {
        if (IsWildcard(bindAddress)) return Loopback;
        var address = ParseBindAddress(bindAddress);
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new ArgumentException("Enter a name of 1–100 characters.");
    }
}
