namespace MCPHub.Core.Settings;

/// <summary>
/// Boolean environment flags as containers commonly spell them. Shared by every "setting with an
/// environment override" policy so they all accept the same spellings and report a pin the same way.
/// </summary>
public static class EnvironmentFlag
{
    /// <summary>
    /// Parses <c>true</c>/<c>false</c>, <c>1</c>/<c>0</c>, <c>yes</c>/<c>no</c>, <c>on</c>/<c>off</c>,
    /// <c>enabled</c>/<c>disabled</c> (any case, surrounding whitespace ignored). Unrecognised or blank →
    /// <see langword="null"/>, meaning "no override".
    /// </summary>
    public static bool? Parse(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        return value.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "enabled" => true,
            "0" or "false" or "no" or "off" or "disabled" => false,
            _ => null,
        };
    }

    /// <summary>
    /// <c>NAME=value</c> when <paramref name="raw"/> is a recognised flag — the text a UI shows beside a
    /// locked checkbox to say what is pinning it — else <see langword="null"/>.
    /// </summary>
    public static string? Describe(string variable, string? raw)
        => Parse(raw) is null ? null : $"{variable}={raw!.Trim()}";
}
