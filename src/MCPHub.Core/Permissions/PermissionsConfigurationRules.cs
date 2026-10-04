namespace MCPHub.Core.Permissions;

/// <summary>
/// What a valid grant document looks like, and how a grant is matched against a tool. Separate from
/// the model because both the desktop store and the deployment reader have to agree about validity.
///
/// <para>Key generation, hashing and matching used to live here; they moved to
/// <see cref="Users.UserKeys"/> when identity became its own concern.</para>
/// </summary>
public static class PermissionsConfigurationRules
{
    /// <summary>Every tool of every server, now and in future. See <see cref="PermissionsGrant.Tools"/>.</summary>
    public const string EverythingGrant = "*";

    /// <summary>The suffix that makes a grant cover one whole server, as in <c>kodi__*</c>.</summary>
    public const string ServerWildcardSuffix = "__*";

    /// <summary>
    /// Whether <paramref name="grant"/> covers <paramref name="exposedToolName"/>, the namespaced name
    /// the proxy advertises.
    ///
    /// <para>Ordinal and case-sensitive: MCP tool names are case-sensitive, so matching loosely would
    /// grant a tool nobody named. Says nothing about whether the user is enabled — that is the
    /// directory's answer, and asking one question in one place is what stopped the two surfaces
    /// disagreeing.</para>
    /// </summary>
    public static bool Covers(PermissionsGrant grant, string serverKey, string exposedToolName)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (string.IsNullOrEmpty(exposedToolName))
        {
            return false;
        }

        foreach (var pattern in grant.Tools)
        {
            if (pattern is EverythingGrant)
            {
                return true;
            }

            if (pattern.EndsWith(ServerWildcardSuffix, StringComparison.Ordinal))
            {
                // Compared against the server key rather than the tool's prefix, so a server whose key
                // contains the separator cannot be matched by halves.
                if (string.Equals(pattern[..^ServerWildcardSuffix.Length], serverKey, StringComparison.Ordinal))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(pattern, exposedToolName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Throws when the document could not be enforced as written.
    ///
    /// <para>Both checks are about something that would otherwise be a silent hole: two entries for one
    /// user means one of them is ignored and nobody can tell which, and a blank pattern matches nothing
    /// while reading like a grant.</para>
    /// </summary>
    public static void Validate(PermissionsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var users = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in configuration.Grants)
        {
            if (grant is null || string.IsNullOrWhiteSpace(grant.UserId))
            {
                throw new ArgumentException("Every grant must name a user.");
            }

            if (!users.Add(grant.UserId))
            {
                throw new ArgumentException($"User '{grant.UserId}' has more than one set of grants.");
            }

            foreach (var pattern in grant.Tools)
            {
                if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 256
                    || pattern.Any(char.IsControl) || pattern.Any(char.IsWhiteSpace))
                {
                    throw new ArgumentException("A tool grant must be a single printable name or pattern.");
                }
            }
        }
    }
}
