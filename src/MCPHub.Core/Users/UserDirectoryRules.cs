namespace MCPHub.Core.Users;

/// <summary>
/// What a valid user document looks like. Separate from the model so the desktop store and the
/// deployment reader cannot disagree about validity.
/// </summary>
public static class UserDirectoryRules
{
    /// <summary>
    /// Throws when the document could not be used as written.
    ///
    /// <para>Each check is something that would otherwise become a silent hole: two users sharing a
    /// key means one caller is resolved arbitrarily to the other's identity, and a malformed hash can
    /// never match, so the user is quietly dead while looking configured.</para>
    /// </summary>
    public static void Validate(HubUsersConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in configuration.Users)
        {
            if (user is null)
            {
                throw new ArgumentException("Invalid user.");
            }

            if (string.IsNullOrWhiteSpace(user.Id) || !ids.Add(user.Id))
            {
                throw new ArgumentException("Each user needs its own non-empty id.");
            }

            if (user.Name.Length > 128 || user.Name.Any(char.IsControl))
            {
                throw new ArgumentException("A user name must be short and printable.");
            }

            if (user.KeyHash is null || user.KeyHash.Length != 64 || !user.KeyHash.All(Uri.IsHexDigit))
            {
                throw new ArgumentException("Invalid user key hash.");
            }
        }

        // Ordinal-ignore-case: two hashes differing only in case are the same key written twice, and
        // treating them as distinct would hide the collision.
        if (configuration.Users.Select(u => u.KeyHash)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != configuration.Users.Length)
        {
            throw new ArgumentException("Each user must have a unique key.");
        }
    }
}
