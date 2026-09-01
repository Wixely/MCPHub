using MCPHub.Core.Settings;
using MCPHub.Proxy;

namespace MCPHub.Core.Recipes;

/// <summary>
/// Decides what agents may do with recipes: nothing (recipes off), read only, or read and write.
/// Two switches, each with a persisted setting and an environment-variable override for headless /
/// container deployments (<c>MCPHUB_RECIPES_ENABLED</c>, <c>MCPHUB_RECIPES_AGENT_EDIT</c>). When an
/// override is present it wins and the UI shows the checkbox as locked. The desktop user's own Recipes
/// page is never affected — these gate the <c>recipes__*</c> tools, not the knowledge base.
/// </summary>
public interface IRecipeAccessPolicy
{
    /// <summary>Whether agents can see and call any <c>recipes__*</c> tool.</summary>
    bool RecipesEnabled { get; }

    /// <summary>Whether agents may call <c>recipes__add</c> / <c>update</c> / <c>remove</c> (requires <see cref="RecipesEnabled"/>).</summary>
    bool AgentEditEnabled { get; }

    /// <summary>
    /// The edit switch on its own (environment override, else the setting), ignoring <see cref="RecipesEnabled"/> —
    /// what a checkbox should display while recipes are off, so the choice is kept for when they come back.
    /// </summary>
    bool AgentEditSwitch { get; }

    /// <summary>Set when <see cref="RecipesEnabled"/> comes from the environment rather than settings.</summary>
    string? RecipesEnabledOverrideSource { get; }

    /// <summary>Set when <see cref="AgentEditEnabled"/> comes from the environment rather than settings.</summary>
    string? AgentEditOverrideSource { get; }

    /// <summary>The MCP server instructions to advertise for the current policy; <see langword="null"/> when recipes are off.</summary>
    string? ServerInstructions { get; }
}

/// <summary>
/// <see cref="IRecipeAccessPolicy"/> backed by <see cref="MCPHubSettings"/> with environment overrides, and the
/// <see cref="IToolAuthorization"/> that enforces it on the proxy: recipe tools an agent may not use are
/// filtered out of <c>tools/list</c> and refused on call, exactly like an ungranted tenant's tools. Every
/// other tool is allowed — this is the single-user desktop policy, not a tenancy scheme.
/// </summary>
public sealed class RecipeAccessPolicy : IRecipeAccessPolicy, IToolAuthorization
{
    /// <summary>Environment variable that forces recipes on/off (<c>true</c>/<c>false</c>, <c>1</c>/<c>0</c>, <c>yes</c>/<c>no</c>, <c>on</c>/<c>off</c>).</summary>
    public const string EnabledVariable = "MCPHUB_RECIPES_ENABLED";

    /// <summary>Environment variable that forces agent editing on/off.</summary>
    public const string AgentEditVariable = "MCPHUB_RECIPES_AGENT_EDIT";

    /// <summary>Recipe tools that only read.</summary>
    public static readonly IReadOnlySet<string> ReadOnlyTools = new HashSet<string>(StringComparer.Ordinal) { "list", "get" };

    private readonly ISettingsStore _settings;
    private readonly Func<string, string?> _environment;

    public RecipeAccessPolicy(ISettingsStore settings)
        : this(settings, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test seam: <paramref name="environment"/> stands in for <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    public RecipeAccessPolicy(ISettingsStore settings, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);
        _settings = settings;
        _environment = environment;
    }

    /// <inheritdoc />
    public bool RecipesEnabled => Override(EnabledVariable) ?? _settings.Current.RecipesEnabled;

    /// <inheritdoc />
    public bool AgentEditEnabled => RecipesEnabled && AgentEditSwitch;

    /// <inheritdoc />
    public bool AgentEditSwitch => Override(AgentEditVariable) ?? _settings.Current.RecipesAgentEditEnabled;

    /// <inheritdoc />
    public string? RecipesEnabledOverrideSource => OverrideSource(EnabledVariable);

    /// <inheritdoc />
    public string? AgentEditOverrideSource => OverrideSource(AgentEditVariable);

    /// <inheritdoc />
    public string? ServerInstructions => !RecipesEnabled
        ? null
        : AgentEditEnabled
            ? RecipeToolProvider.ServerInstructions
            : RecipeToolProvider.ReadOnlyServerInstructions;

    /// <inheritdoc />
    public bool IsToolVisible(TenantContext tenant, string serverKey, string exposedToolName)
        => IsAllowed(serverKey, exposedToolName);

    /// <inheritdoc />
    public bool IsCallAllowed(TenantContext tenant, string serverKey, string exposedToolName)
        => IsAllowed(serverKey, exposedToolName);

    private bool IsAllowed(string serverKey, string exposedToolName)
    {
        if (!string.Equals(serverKey, RecipeToolProvider.ProviderKey, StringComparison.Ordinal))
            return true;
        if (!RecipesEnabled)
            return false;
        if (AgentEditEnabled)
            return true;

        var prefix = RecipeToolProvider.ProviderKey + ProxyConstants.NamespaceSeparator;
        var original = exposedToolName.StartsWith(prefix, StringComparison.Ordinal) ? exposedToolName[prefix.Length..] : exposedToolName;
        return ReadOnlyTools.Contains(original);
    }

    private bool? Override(string variable) => ParseFlag(_environment(variable));

    private string? OverrideSource(string variable)
    {
        var raw = _environment(variable);
        return ParseFlag(raw) is null ? null : $"{variable}={raw!.Trim()}";
    }

    /// <summary>Parses a boolean flag as containers commonly spell it; unrecognised or blank → <see langword="null"/> (no override).</summary>
    public static bool? ParseFlag(string? raw)
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
}
