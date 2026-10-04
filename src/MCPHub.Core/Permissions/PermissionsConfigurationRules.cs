using System.Security.Cryptography;
using System.Text;

namespace MCPHub.Core.Permissions;

/// <summary>
/// What a valid permissions document looks like, and how a grant is matched against a tool.
///
/// <para>Separate from the model for the same reason <see cref="Routing.RouterConfigurationRules"/>
/// is: both the desktop store and the deployment reader have to agree about validity, and a rule
/// that lived in one of them would be a rule the other could violate.</para>
/// </summary>
public static class PermissionsConfigurationRules
{
    /// <summary>Every tool of every server, now and in future. See <see cref="PermissionsPrincipal.Tools"/>.</summary>
    public const string EverythingGrant = "*";

    /// <summary>The suffix that makes a grant cover one whole server, as in <c>kodi__*</c>.</summary>
    public const string ServerWildcardSuffix = "__*";

    /// <summary>Keys are generated, not chosen, so this is a floor on OUR generator rather than a
    /// password policy. A key short enough to be guessed is worse than no key, because it reads as
    /// security in the configuration file.</summary>
    public const int MinimumKeyLength = 32;

    public const int MaximumKeyLength = 256;

    /// <summary>Hashes a key for storage: SHA-256, lowercase hex, the same as the router's inputs.
    /// A plain hash rather than a KDF because keys are 256 bits of randomness from
    /// <see cref="NewKey"/> — a KDF defends against guessing a human-chosen secret, and there is no
    /// human-chosen secret here.</summary>
    public static string HashKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>A fresh key: 256 bits, URL-safe, prefixed so one found in a log or a shell history is
    /// recognisably an MCPHub key and can be revoked rather than puzzled over.</summary>
    public static string NewKey() => "mcph_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Whether <paramref name="principal"/> is granted <paramref name="exposedToolName"/>, which is
    /// the namespaced name the proxy advertises (<c>{serverKey}__{tool}</c>).
    ///
    /// <para>Ordinal and case-sensitive: MCP tool names are case-sensitive, so matching loosely here
    /// would grant a tool nobody named.</para>
    /// </summary>
    public static bool Grants(PermissionsPrincipal principal, string serverKey, string exposedToolName)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!principal.Enabled || string.IsNullOrEmpty(exposedToolName))
        {
            return false;
        }

        foreach (var grant in principal.Tools)
        {
            if (grant is EverythingGrant)
            {
                return true;
            }

            if (grant.EndsWith(ServerWildcardSuffix, StringComparison.Ordinal))
            {
                // "kodi__*" covers the server it names, whatever the tool is called. Compared against
                // the server key rather than the tool's prefix so a server whose key contains "__"
                // cannot be matched by halves.
                if (string.Equals(
                        grant[..^ServerWildcardSuffix.Length], serverKey, StringComparison.Ordinal))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(grant, exposedToolName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Throws when the document could not be enforced as written.
    ///
    /// <para>Every check here is something that would otherwise become a silent policy hole: two
    /// principals sharing a key means one caller is resolved arbitrarily to the other's grants; a
    /// malformed hash can never match, so the principal is quietly dead; a blank grant matches
    /// nothing and reads like a grant.</para>
    /// </summary>
    public static void Validate(PermissionsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var principal in configuration.Principals)
        {
            if (principal is null)
            {
                throw new ArgumentException("Invalid principal.");
            }

            if (string.IsNullOrWhiteSpace(principal.Id) || !ids.Add(principal.Id))
            {
                throw new ArgumentException("Each principal needs its own non-empty id.");
            }

            if (principal.Name.Length > 128 || principal.Name.Any(char.IsControl))
            {
                throw new ArgumentException("A principal name must be short and printable.");
            }

            if (principal.KeyHash is null || principal.KeyHash.Length != 64
                || !principal.KeyHash.All(Uri.IsHexDigit))
            {
                throw new ArgumentException("Invalid principal key hash.");
            }

            foreach (var grant in principal.Tools)
            {
                if (string.IsNullOrWhiteSpace(grant) || grant.Length > 256 || grant.Any(char.IsControl)
                    || grant.Any(char.IsWhiteSpace))
                {
                    throw new ArgumentException("A tool grant must be a single printable name or pattern.");
                }
            }
        }

        // Ordinal-ignore-case, like the router: two hashes differing only in case are the same key
        // written twice, and treating them as distinct would hide the collision.
        if (configuration.Principals.Select(p => p.KeyHash)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != configuration.Principals.Length)
        {
            throw new ArgumentException("Each principal must have a unique key.");
        }
    }
}
