using System.Security.Cryptography;
using System.Text;

namespace MCPHub.Core.Users;

/// <summary>
/// Issuing, hashing and matching user keys — in one place so that every surface that authenticates a
/// caller does it identically.
///
/// <para>These moved here from the Router and the permissions layer, which had each grown their own
/// copy. Two implementations of "is this the right key" is one more than anybody should maintain.</para>
/// </summary>
public static class UserKeys
{
    /// <summary>
    /// A floor on OUR generator rather than a password policy — keys are generated, never chosen. A
    /// key short enough to guess is worse than none, because it reads as security in a config file.
    /// Comfortably below the length of every key this has ever issued, so migrated ones pass.
    /// </summary>
    public const int MinimumLength = 32;

    public const int MaximumLength = 256;

    /// <summary>
    /// A fresh key: 256 bits, URL-safe, and prefixed so one found in a log or a shell history is
    /// recognisably an MCPHub key and can be revoked rather than puzzled over.
    ///
    /// <para>Keys issued before users existed carry the older <c>mhrouter_</c> or <c>mcph_</c>
    /// prefixes and keep working — only the hash is ever compared, so a prefix is a label for people.</para>
    /// </summary>
    public static string New() => "mcphub_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Hashes a key for storage: SHA-256, lowercase hex — the format the Router has always written, so
    /// its stored hashes migrate as they are.
    ///
    /// <para>A plain hash rather than a KDF because a key is 256 bits of randomness from
    /// <see cref="New"/>; a KDF defends against guessing a human-chosen secret, and there is none here.</para>
    /// </summary>
    public static string Hash(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// The enabled user holding <paramref name="key"/>, or null.
    ///
    /// <para>Fixed-time comparison against every candidate with no early exit, so the time taken does
    /// not reveal how much of a hash was right or where a match sat in the list. A disabled user is
    /// skipped rather than matched-then-refused, which keeps "suspended" indistinguishable from
    /// "unknown" to whoever is presenting the key.</para>
    /// </summary>
    public static HubUser? Resolve(HubUsersConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(key) || key.Length is < MinimumLength or > MaximumLength)
        {
            return null;
        }

        var presented = Convert.FromHexString(Hash(key));
        HubUser? found = null;
        foreach (var user in configuration.Users)
        {
            if (!user.Enabled || user.KeyHash.Length != 64)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(presented, Convert.FromHexString(user.KeyHash)))
            {
                found ??= user;
            }
        }

        return found;
    }
}
