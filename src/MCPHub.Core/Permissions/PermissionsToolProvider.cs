using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MCPHub.Core.Users;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace MCPHub.Core.Permissions;

/// <summary>
/// Reading and editing which tools each user may use, as <c>permissions__*</c> tools — and, the one
/// worth having, <b>why a particular tool is not available to a particular user</b>.
///
/// <para>Identity is <c>users__*</c>'s business: creating a user and issuing its key happen there, so
/// that one caller has one key for every surface. This deals only in grants.</para>
///
/// <para>Exposed as MCP tools rather than a REST API because the hub already has exactly one
/// authenticated remote surface and this is it. A second listener would mean a second auth story to
/// get right, and a management client already speaks MCP to reach anything else here.</para>
/// </summary>
public sealed class PermissionsToolProvider : ILocalToolProvider
{
    /// <summary>Namespace key: tools appear as <c>permissions__*</c>.</summary>
    public const string ProviderKey = "permissions";

    /// <summary>Policy here cannot be edited because it is mounted read-only.</summary>
    public const string ReadOnlyCode = "permissions.read_only";

    /// <summary>The named user does not exist.</summary>
    public const string NoSuchUserCode = "permissions.no_such_user";

    /// <summary>A name matched more than one user, so the id is needed.</summary>
    public const string AmbiguousUserCode = "permissions.ambiguous_user";

    /// <summary>The stored document could not be loaded, so nothing is granted until it is repaired.</summary>
    public const string UnreadableCode = "permissions.unreadable";

    private const string UserArgument = """
        "user": { "type": "string", "description": "Which user: its id, or its name when that is unambiguous. See users__list." }
        """;

    private static readonly IReadOnlyList<Tool> ToolDefinitions =
    [
        new Tool
        {
            Name = "status",
            Description = "How this hub's tool policy is configured: whether unauthenticated callers are allowed, "
                          + "whether policy can be edited here or is mounted read-only, how many users exist and "
                          + "how many have grants, and whether anything is self-defeating. Start here when a tool "
                          + "is missing and you do not know why.",
            InputSchema = Schema("""{ "type": "object", "properties": {} }"""),
        },
        new Tool
        {
            Name = "list_grants",
            Description = "Every user's tool grants, with whether that user still exists and is enabled — a grant "
                          + "on a suspended or deleted user applies to nothing, which is worth seeing.",
            InputSchema = Schema("""{ "type": "object", "properties": {} }"""),
        },
        new Tool
        {
            Name = "explain",
            Description = "Why a tool is or is not available to a user. Reports EVERY reason it is denied, not "
                          + "just the first — a tool can be withheld by a missing grant and a feature switch at "
                          + "the same time, and fixing one changes nothing. Each reason carries a stable code, "
                          + "what to change, and the environment variable pinning it where one is.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": {
                    {{UserArgument}},
                    "tool": { "type": "string", "description": "The namespaced tool name, e.g. 'mcphub__install'." }
                  },
                  "required": ["user", "tool"]
                }
                """),
        },
        new Tool
        {
            Name = "set_grants",
            Description = "Replace a user's tool grants wholesale. Grants may be exact tool names "
                          + "('kodi__play_pause'), a whole server ('kodi__*'), or everything ('*'). Takes effect on "
                          + "that user's next call, including one already connected.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": {
                    {{UserArgument}},
                    "tools": { "type": "array", "items": { "type": "string" }, "description": "The complete new set; an empty array revokes everything." }
                  },
                  "required": ["user", "tools"]
                }
                """),
        },
        new Tool
        {
            Name = "clear_grants",
            Description = "Remove a user's grant entry entirely. Use this to tidy an entry left behind by a "
                          + "deleted user; for a user that still exists, set_grants with an empty array has the "
                          + "same effect on what it may do.",
            InputSchema = Schema($$"""
                { "type": "object", "properties": { {{UserArgument}} }, "required": ["user"] }
                """),
        },
        new Tool
        {
            Name = "set_allow_unauthenticated",
            Description = "Whether a caller presenting no recognised key may use every tool. True is the "
                          + "single-user default; a hub that issues keys should set it false, after which an "
                          + "unauthenticated caller gets nothing rather than everything.",
            InputSchema = Schema("""
                { "type": "object", "properties": { "allowed": { "type": "boolean" } }, "required": ["allowed"] }
                """),
        },
    ];

    private readonly IUserDirectory _users;
    private readonly IPermissionsConfigurationSource _source;
    private readonly IToolAuthorization _effective;
    private readonly ILogger<PermissionsToolProvider> _logger;

    /// <param name="effective">The whole authorization stack, so <c>explain</c> answers the question an
    /// operator actually asked — "why can this caller not use this tool" — across every policy rather
    /// than the grant half.</param>
    public PermissionsToolProvider(
        IUserDirectory users,
        IPermissionsConfigurationSource source,
        IToolAuthorization effective,
        ILogger<PermissionsToolProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(logger);
        _users = users;
        _source = source;
        _effective = effective;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => ProviderKey;

    /// <inheritdoc />
    public string DisplayName => "Permissions";

    /// <inheritdoc />
    public IReadOnlyList<Tool> Tools => ToolDefinitions;

    /// <inheritdoc />
    public ValueTask<CallToolResult> CallAsync(
        string toolName, IReadOnlyDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        try
        {
            return new ValueTask<CallToolResult>(toolName switch
            {
                "status" => Status(),
                "list_grants" => ListGrants(),
                "explain" => Explain(arguments),
                "set_grants" => SetGrants(arguments),
                "clear_grants" => ClearGrants(arguments),
                "set_allow_unauthenticated" => SetAllowUnauthenticated(arguments),
                _ => Failed("permissions.unknown_tool", $"Unknown permissions tool '{toolName}'."),
            });
        }
        catch (PermissionsArgumentException ex)
        {
            return new ValueTask<CallToolResult>(Failed(ex.Code, ex.Message, ex.Remedy));
        }
        catch (ArgumentException ex)
        {
            // Validation from the rules class: a grant that matches nothing, say. Written for a person
            // and carrying no secret.
            return new ValueTask<CallToolResult>(Failed("permissions.invalid", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // The store refusing to write over a document it could not read.
            return new ValueTask<CallToolResult>(Failed(UnreadableCode, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "permissions__{Tool} failed.", toolName);
            return new ValueTask<CallToolResult>(
                Failed("permissions.failed", $"permissions__{toolName} failed: {ex.Message}"));
        }
    }

    // ---- reading --------------------------------------------------------------------------------

    private CallToolResult Status()
    {
        var policy = _source.Snapshot;
        var users = _users.Snapshot.Users;
        var granted = policy.Grants.Count(g => users.Any(u => string.Equals(u.Id, g.UserId, StringComparison.Ordinal)));

        return Json(new PermissionsStatus
        {
            AllowUnauthenticated = policy.AllowUnauthenticated,
            KeysEnforced = !policy.AllowUnauthenticated,
            Editable = _source is IWritablePermissions,
            UserCount = users.Length,
            EnabledUserCount = users.Count(u => u.Enabled),
            GrantedUserCount = granted,
            LoadError = (_source as PermissionsStore)?.LoadError,
            ReloadError = (_source as PermissionsDeploymentSource)?.ReloadError,
            Warning = policy is { AllowUnauthenticated: true } && users.Length > 0
                ? $"{users.Length} user(s) exist, but unauthenticated callers are allowed — so every caller "
                  + "is served as the single user and no grant has any effect. Call "
                  + "permissions__set_allow_unauthenticated with allowed=false to start enforcing keys."
                : null,
        }, PermissionsResultsJsonContext.Default.PermissionsStatus);
    }

    private CallToolResult ListGrants() => Json(
        new PermissionsGrantList { Grants = [.. _source.Snapshot.Grants.Select(Describe)] },
        PermissionsResultsJsonContext.Default.PermissionsGrantList);

    /// <summary>
    /// The question an operator is actually asking, answered across the whole stack.
    ///
    /// <para>Deliberately not limited to grants. A tool can be withheld by a missing grant and a
    /// feature switch simultaneously, so this reports every reason: grant it and the answer still lists
    /// the switch, which is how somebody knows to keep going rather than conclude the grant failed.</para>
    /// </summary>
    private CallToolResult Explain(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var reference = Text(arguments, "user");
        var tool = Text(arguments, "tool");
        var user = FindUser(reference);
        var serverKey = ServerKeyOf(tool);
        var tenant = new TenantContext(user.Id);

        var denials = _effective is CompositeToolAuthorization composite
            ? composite.ExplainAll(tenant, serverKey, tool)
            : Single(_effective, tenant, serverKey, tool);

        return Json(new PermissionsExplanation
        {
            UserId = user.Id,
            UserName = user.Name,
            Tool = tool,
            ServerKey = serverKey,
            Available = denials.Count == 0,
            Denials =
            [
                .. denials.Select(d => new PermissionsDenial
                {
                    Code = d.Code, Reason = d.Reason, Remedy = d.Remedy, PinnedBy = d.PinnedBy,
                }),
            ],
        }, PermissionsResultsJsonContext.Default.PermissionsExplanation);
    }

    private static IReadOnlyList<ToolDenial> Single(
        IToolAuthorization policy, TenantContext tenant, string serverKey, string tool)
    {
        if (policy.IsToolVisible(tenant, serverKey, tool) && policy.IsCallAllowed(tenant, serverKey, tool))
        {
            return [];
        }

        return
        [
            (policy as IToolAuthorizationDiagnostics)?.Explain(tenant, serverKey, tool)
            ?? new ToolDenial
            {
                Code = CompositeToolAuthorization.UndiagnosedCode,
                Reason = $"{policy.GetType().Name} denies this tool and cannot explain why.",
            },
        ];
    }

    // ---- editing --------------------------------------------------------------------------------

    private CallToolResult SetGrants(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();

        // Every argument read before anything is looked up, so a malformed call is reported as
        // malformed. Resolving first would answer "no such user" to a caller who also forgot an
        // argument, sending them after the wrong mistake.
        var reference = Text(arguments, "user");
        var tools = Grants(arguments, "tools");
        var user = FindUser(reference);
        store.SetGrants(user.Id, tools);
        return Changed($"'{user.Name}' now holds {tools.Count} grant(s).", store, user.Id);
    }

    private CallToolResult ClearGrants(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();
        var reference = Text(arguments, "user");

        // Resolved against the grants as well as the directory, because the point of this tool is to
        // tidy an entry whose user has gone — insisting the user exist would refuse exactly that.
        var userId = _users.Snapshot.Users
                         .FirstOrDefault(u => string.Equals(u.Id, reference, StringComparison.Ordinal)
                                              || string.Equals(u.Name, reference, StringComparison.OrdinalIgnoreCase))?.Id
                     ?? _source.Snapshot.Grants
                         .FirstOrDefault(g => string.Equals(g.UserId, reference, StringComparison.Ordinal))?.UserId
                     ?? throw new PermissionsArgumentException(
                         NoSuchUserCode,
                         $"No user or grant entry matches '{reference}'.",
                         "Call permissions__list_grants to see what exists.");

        store.ForgetUser(userId);
        return Changed($"Grants for '{reference}' removed.", store, forUser: null);
    }

    private CallToolResult SetAllowUnauthenticated(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();
        var allowed = Flag(arguments, "allowed");
        store.SetAllowUnauthenticated(allowed);
        return Changed(
            allowed
                ? "Callers with no recognised key may now use every tool."
                : "Callers with no recognised key now get nothing.",
            store, forUser: null);
    }

    // ---- plumbing -------------------------------------------------------------------------------

    /// <summary>
    /// The store, or a named refusal when policy is mounted read-only.
    ///
    /// <para>This is the failure most likely to waste somebody's afternoon: a client issues a grant
    /// against a container whose policy is a mounted file, the call appears to work, and nothing
    /// changes. Saying so by code lets a UI grey the controls instead.</para>
    /// </summary>
    private IWritablePermissions Writable() =>
        _source as IWritablePermissions
        ?? throw new PermissionsArgumentException(
            ReadOnlyCode,
            "This hub's permissions are mounted read-only, so they cannot be edited here.",
            "Edit the permissions document the hub was given, or run it with an editable store.");

    /// <summary>
    /// A user by id, or by name when that is unambiguous. Ambiguity is refused rather than resolved:
    /// granting the wrong one of two users called "agent" and reporting success is worse than refusing.
    /// </summary>
    private HubUser FindUser(string reference)
    {
        var users = _users.Snapshot.Users;
        if (users.FirstOrDefault(u => string.Equals(u.Id, reference, StringComparison.Ordinal)) is { } byId)
        {
            return byId;
        }

        var byName = users.Where(u => string.Equals(u.Name, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            1 => byName[0],
            0 => throw new PermissionsArgumentException(
                NoSuchUserCode,
                $"No user has the id or name '{reference}'.",
                "Call users__list to see what exists."),
            _ => throw new PermissionsArgumentException(
                AmbiguousUserCode,
                $"{byName.Count} users are called '{reference}'.",
                "Use the user's id instead of its name."),
        };
    }

    /// <summary>The server key a namespaced tool belongs to. A name with no separator is its own key,
    /// which is what an operator typing a bare tool name means.</summary>
    private static string ServerKeyOf(string exposedToolName)
    {
        var at = exposedToolName.IndexOf(ProxyConstants.NamespaceSeparator, StringComparison.Ordinal);
        return at > 0 ? exposedToolName[..at] : exposedToolName;
    }

    private PermissionsGrantSummary Describe(PermissionsGrant grant)
    {
        var user = _users.Snapshot.Users
            .FirstOrDefault(u => string.Equals(u.Id, grant.UserId, StringComparison.Ordinal));
        return new PermissionsGrantSummary
        {
            UserId = grant.UserId,
            UserName = user?.Name ?? string.Empty,
            UserEnabled = user?.Enabled ?? false,
            UserExists = user is not null,
            Tools = [.. grant.Tools],
        };
    }

    private CallToolResult Changed(string message, IWritablePermissions store, string? forUser)
    {
        var grant = forUser is null ? null : store.GrantsFor(forUser);
        return Json(
            new PermissionsChange { Message = message, Grant = grant is null ? null : Describe(grant) },
            PermissionsResultsJsonContext.Default.PermissionsChange);
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new PermissionsArgumentException("permissions.bad_argument", $"'{name}' is required.");
        }

        return value.GetString()!;
    }

    private static bool Flag(IReadOnlyDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new PermissionsArgumentException("permissions.bad_argument", $"'{name}' must be true or false.");
        }

        return value.GetBoolean();
    }

    private static IReadOnlyList<string> Grants(IReadOnlyDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new PermissionsArgumentException("permissions.bad_argument", $"'{name}' must be an array of strings.");
        }

        var grants = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new PermissionsArgumentException(
                    "permissions.bad_argument", $"'{name}' must contain only non-empty strings.");
            }

            grants.Add(item.GetString()!);
        }

        return grants;
    }

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static CallToolResult Json<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, typeInfo) }] };

    /// <summary>
    /// A failure a caller can act on: a stable code, what is wrong, and what to change. JSON rather
    /// than a bare sentence so a client can branch on the code instead of matching prose.
    /// </summary>
    private static CallToolResult Failed(string code, string reason, string? remedy = null) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(
                    new PermissionsDenial { Code = code, Reason = reason, Remedy = remedy },
                    PermissionsResultsJsonContext.Default.PermissionsDenial),
            },
        ],
    };
}

/// <summary>A bad argument or a refusal, carrying the code a caller should branch on.</summary>
internal sealed class PermissionsArgumentException(string code, string message, string? remedy = null)
    : Exception(message)
{
    public string Code { get; } = code;

    public string? Remedy { get; } = remedy;
}
