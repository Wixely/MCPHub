using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace MCPHub.Core.Permissions;

/// <summary>
/// Reading and editing hub policy through the proxy, as <c>permissions__*</c> tools: who holds a key,
/// what each may use, and — the one worth having — <b>why a particular tool is not available to a
/// particular caller</b>.
///
/// <para>Exposed as MCP tools rather than as a REST API because the hub already has exactly one
/// authenticated remote surface and this is it. A second listener would mean a second auth story to
/// get right, and a management client already speaks MCP to reach anything else here.</para>
///
/// <para>Whether a caller gets these at all is <see cref="PermissionsManagementPolicy"/>'s decision;
/// which of them it may call is an ordinary grant. This class only does the work.</para>
/// </summary>
/// <remarks>
/// A key is returned exactly once, by <c>create_principal</c> and <c>rotate_key</c>. Nothing else ever
/// yields one, because nothing else can: only hashes are stored. Principal listings carry a short
/// fingerprint of the hash instead, which is enough to tell two keys apart in a support conversation
/// and useless for authenticating.
/// </remarks>
public sealed class PermissionsToolProvider : ILocalToolProvider
{
    /// <summary>Namespace key: tools appear as <c>permissions__*</c>.</summary>
    public const string ProviderKey = "permissions";

    private const string PrincipalArgument = """
        "principal": { "type": "string", "description": "Which principal: its id, or its name when that is unambiguous. See permissions__list_principals." }
        """;

    private static readonly IReadOnlyList<Tool> ToolDefinitions =
    [
        new Tool
        {
            Name = "status",
            Description = "How this hub's tool policy is configured: whether unauthenticated callers are allowed, "
                          + "whether policy can be edited here or is mounted read-only, how many principals exist, "
                          + "and whether any switch is being pinned by an environment variable. Start here when a "
                          + "tool is missing and you do not know why.",
            InputSchema = Schema("""{ "type": "object", "properties": {} }"""),
        },
        new Tool
        {
            Name = "list_principals",
            Description = "Every principal: id, name, whether it is enabled, its tool grants, and a short "
                          + "fingerprint of its key. Keys themselves are not stored and cannot be listed.",
            InputSchema = Schema("""{ "type": "object", "properties": {} }"""),
        },
        new Tool
        {
            Name = "explain",
            Description = "Why a tool is or is not available to a principal. Reports EVERY reason it is denied, "
                          + "not just the first — a tool can be withheld by a missing grant and a feature switch at "
                          + "the same time, and fixing one changes nothing. Each reason carries a stable code, what "
                          + "to change, and the environment variable pinning it where one is.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": {
                    {{PrincipalArgument}},
                    "tool": { "type": "string", "description": "The namespaced tool name, e.g. 'mcphub__install' or 'kodi__play_pause'." }
                  },
                  "required": ["principal", "tool"]
                }
                """),
        },
        new Tool
        {
            Name = "create_principal",
            Description = "Add a principal and issue its key. THE KEY IS RETURNED ONCE and cannot be recovered — "
                          + "only its hash is stored. Grants may be exact tool names ('kodi__play_pause'), a whole "
                          + "server ('kodi__*'), or everything ('*').",
            InputSchema = Schema("""
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "What an operator will call this caller, e.g. an agent's nickname." },
                    "tools": { "type": "array", "items": { "type": "string" }, "description": "Tool grants. Omit for a principal that may use nothing yet." }
                  },
                  "required": ["name"]
                }
                """),
        },
        new Tool
        {
            Name = "rotate_key",
            Description = "Issue a new key for a principal, retiring the old one immediately. Returned once.",
            InputSchema = Schema($$"""
                { "type": "object", "properties": { {{PrincipalArgument}} }, "required": ["principal"] }
                """),
        },
        new Tool
        {
            Name = "set_grants",
            Description = "Replace a principal's tool grants wholesale. Takes effect on the principal's next call, "
                          + "including one already connected.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": {
                    {{PrincipalArgument}},
                    "tools": { "type": "array", "items": { "type": "string" }, "description": "The complete new set of grants; an empty array revokes everything." }
                  },
                  "required": ["principal", "tools"]
                }
                """),
        },
        new Tool
        {
            Name = "set_enabled",
            Description = "Suspend or restore a principal, keeping its grants either way. A suspended principal's "
                          + "key stops working at once and is indistinguishable from an unknown one.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": { {{PrincipalArgument}}, "enabled": { "type": "boolean" } },
                  "required": ["principal", "enabled"]
                }
                """),
        },
        new Tool
        {
            Name = "delete_principal",
            Description = "Remove a principal and retire its key. Use set_enabled to suspend one you may want back.",
            InputSchema = Schema($$"""
                { "type": "object", "properties": { {{PrincipalArgument}} }, "required": ["principal"] }
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

    private readonly IPermissionsConfigurationSource _source;
    private readonly IToolAuthorization _effective;
    private readonly ILogger<PermissionsToolProvider> _logger;

    /// <param name="source">Hub policy. A source that is not <see cref="IWritablePermissions"/> — a
    /// mounted deployment file — makes the editing tools report <see cref="ReadOnlyCode"/> rather than
    /// appear to succeed.</param>
    /// <param name="effective">The whole authorization stack, so <c>explain</c> answers the question an
    /// operator actually asked: "why can this caller not use this tool", across every policy, not just
    /// the grant half.</param>
    public PermissionsToolProvider(
        IPermissionsConfigurationSource source,
        IToolAuthorization effective,
        ILogger<PermissionsToolProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _effective = effective;
        _logger = logger;
    }

    /// <summary>Policy here cannot be edited because it is mounted read-only.</summary>
    public const string ReadOnlyCode = "permissions.read_only";

    /// <summary>The named principal does not exist.</summary>
    public const string NoSuchPrincipalCode = "permissions.no_such_principal";

    /// <summary>A name matched more than one principal, so the id is needed.</summary>
    public const string AmbiguousPrincipalCode = "permissions.ambiguous_principal";

    /// <summary>The stored document could not be loaded, so nothing is recognised until it is repaired.</summary>
    public const string UnreadableCode = "permissions.unreadable";

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
                "list_principals" => ListPrincipals(),
                "explain" => Explain(arguments),
                "create_principal" => CreatePrincipal(arguments),
                "rotate_key" => RotateKey(arguments),
                "set_grants" => SetGrants(arguments),
                "set_enabled" => SetEnabled(arguments),
                "delete_principal" => DeletePrincipal(arguments),
                "set_allow_unauthenticated" => SetAllowUnauthenticated(arguments),
                _ => Failed("permissions.unknown_tool", $"Unknown permissions tool '{toolName}'."),
            });
        }
        catch (PermissionsArgumentException ex)
        {
            return new ValueTask<CallToolResult>(Failed(ex.Code, ex.Message, ex.Remedy));
        }
        catch (PermissionsNotFoundException ex)
        {
            return new ValueTask<CallToolResult>(Failed(NoSuchPrincipalCode, ex.Message));
        }
        catch (ArgumentException ex)
        {
            // Validation from the rules class: a grant that matches nothing, a name too long. The
            // message is written for a person and carries no secret.
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
        var snapshot = _source.Snapshot;
        return Json(new PermissionsStatus
        {
            AllowUnauthenticated = snapshot.AllowUnauthenticated,
            Editable = _source is IWritablePermissions,
            PrincipalCount = snapshot.Principals.Length,
            EnabledPrincipalCount = snapshot.Principals.Count(p => p.Enabled),
            LoadError = (_source as PermissionsStore)?.LoadError,
            ReloadError = (_source as PermissionsDeploymentSource)?.ReloadError,
        }, PermissionsResultsJsonContext.Default.PermissionsStatus);
    }

    private CallToolResult ListPrincipals() => Json(
        new PermissionsPrincipalList
        {
            Principals = [.. _source.Snapshot.Principals.Select(Describe)],
        },
        PermissionsResultsJsonContext.Default.PermissionsPrincipalList);

    /// <summary>
    /// The question an operator is actually asking, answered across the whole stack.
    ///
    /// <para>Deliberately not limited to grants. A tool can be withheld by a missing grant and a
    /// feature switch simultaneously, so this reports every reason: grant it and the answer still
    /// lists the switch, which is how somebody knows to keep going rather than conclude the grant
    /// failed.</para>
    /// </summary>
    private CallToolResult Explain(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var reference = Text(arguments, "principal");
        var tool = Text(arguments, "tool");
        var principal = FindPrincipal(reference);
        var serverKey = ServerKeyOf(tool);
        var tenant = new TenantContext(principal.Id);

        var denials = _effective is CompositeToolAuthorization composite
            ? composite.ExplainAll(tenant, serverKey, tool)
            : Single(_effective, tenant, serverKey, tool);

        return Json(new PermissionsExplanation
        {
            Principal = Describe(principal),
            Tool = tool,
            ServerKey = serverKey,
            Available = denials.Count == 0,
            Denials = [.. denials.Select(d => new PermissionsDenial
            {
                Code = d.Code,
                Reason = d.Reason,
                Remedy = d.Remedy,
                PinnedBy = d.PinnedBy,
            })],
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

    private CallToolResult CreatePrincipal(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();
        var (principal, key) = store.CreatePrincipal(Text(arguments, "name"), Grants(arguments, "tools", required: false));
        return Json(new PermissionsKeyIssued
        {
            Principal = Describe(principal),
            Key = key,
            Notice = "This key is shown once and is not recoverable. Store it now; a lost key is rotated, not looked up.",
        }, PermissionsResultsJsonContext.Default.PermissionsKeyIssued);
    }

    private CallToolResult RotateKey(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();
        var principal = FindPrincipal(Text(arguments, "principal"));
        var key = store.RotateKey(principal.Id);
        return Json(new PermissionsKeyIssued
        {
            Principal = Describe(principal),
            Key = key,
            Notice = "The previous key stopped working the moment this one was issued.",
        }, PermissionsResultsJsonContext.Default.PermissionsKeyIssued);
    }

    private CallToolResult SetGrants(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();

        // Every argument read before anything is looked up, so a malformed call is reported as
        // malformed. Resolving first would answer "no such principal" to a caller who also forgot an
        // argument, sending them after the wrong mistake.
        var reference = Text(arguments, "principal");
        var tools = Grants(arguments, "tools", required: true);
        var principal = FindPrincipal(reference);
        store.SetGrants(principal.Id, tools);
        return Changed($"'{principal.Name}' now holds {tools.Count} grant(s).", store, principal.Id);
    }

    private CallToolResult SetEnabled(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();
        var reference = Text(arguments, "principal");
        var enabled = Flag(arguments, "enabled");
        var principal = FindPrincipal(reference);
        store.SetEnabled(principal.Id, enabled);
        return Changed(
            enabled ? $"'{principal.Name}' is enabled again." : $"'{principal.Name}' is suspended; its key no longer works.",
            store, principal.Id);
    }

    private CallToolResult DeletePrincipal(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();
        var principal = FindPrincipal(Text(arguments, "principal"));
        store.DeletePrincipal(principal.Id);
        return Changed($"'{principal.Name}' is gone and its key is retired.", store, principal: null);
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
            store, principal: null);
    }

    // ---- plumbing -------------------------------------------------------------------------------

    /// <summary>
    /// The store, or a named refusal when policy is mounted read-only.
    ///
    /// <para>This is the failure most likely to waste somebody's afternoon: a management UI issues a
    /// grant against a container whose policy is a mounted file, the call appears to work, and nothing
    /// changes. Saying so by code lets the UI grey the controls instead.</para>
    /// </summary>
    private IWritablePermissions Writable() =>
        _source as IWritablePermissions
        ?? throw new PermissionsArgumentException(
            ReadOnlyCode,
            "This hub's permissions are mounted read-only, so they cannot be edited here.",
            "Edit the permissions document the hub was given, or run it with an editable store.");

    /// <summary>
    /// A principal by id, or by name when that is unambiguous. Ambiguity is refused rather than
    /// resolved arbitrarily: picking one of two principals called "agent" would edit the wrong policy
    /// and report success.
    /// </summary>
    private PermissionsPrincipal FindPrincipal(string reference)
    {
        var principals = _source.Snapshot.Principals;
        var byId = principals.FirstOrDefault(p => string.Equals(p.Id, reference, StringComparison.Ordinal));
        if (byId is not null)
        {
            return byId;
        }

        var byName = principals
            .Where(p => string.Equals(p.Name, reference, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return byName.Count switch
        {
            1 => byName[0],
            0 => throw new PermissionsArgumentException(
                NoSuchPrincipalCode,
                $"No principal has the id or name '{reference}'.",
                "Call permissions__list_principals to see what exists."),
            _ => throw new PermissionsArgumentException(
                AmbiguousPrincipalCode,
                $"{byName.Count} principals are called '{reference}'.",
                "Use the principal's id instead of its name."),
        };
    }

    /// <summary>The server key a namespaced tool belongs to — what the authorization stack is asked
    /// about. A name with no separator is its own key, which is what an operator typing a bare tool
    /// name means.</summary>
    private static string ServerKeyOf(string exposedToolName)
    {
        var at = exposedToolName.IndexOf(ProxyConstants.NamespaceSeparator, StringComparison.Ordinal);
        return at > 0 ? exposedToolName[..at] : exposedToolName;
    }

    private static PermissionsPrincipalSummary Describe(PermissionsPrincipal principal) => new()
    {
        Id = principal.Id,
        Name = principal.Name,
        Enabled = principal.Enabled,
        Tools = [.. principal.Tools],

        // Enough to tell two keys apart when somebody says "the key I gave Banter", and useless for
        // authenticating with. The whole hash would be a verifier for anyone who could list it.
        KeyFingerprint = principal.KeyHash.Length >= 8 ? principal.KeyHash[..8] : principal.KeyHash,
    };

    private CallToolResult Changed(string message, IWritablePermissions store, string? principal)
    {
        var updated = principal is null
            ? null
            : store.Snapshot.Principals.FirstOrDefault(p => string.Equals(p.Id, principal, StringComparison.Ordinal));
        return Json(new PermissionsChange { Message = message, Principal = updated is null ? null : Describe(updated) },
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

    private static IReadOnlyList<string> Grants(
        IReadOnlyDictionary<string, JsonElement>? arguments, string name, bool required)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return required
                ? throw new PermissionsArgumentException("permissions.bad_argument", $"'{name}' is required.")
                : [];
        }

        if (value.ValueKind != JsonValueKind.Array)
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
    /// A failure a caller can act on: a stable code, what is wrong, and what to change. Serialised as
    /// JSON rather than a bare sentence so a management UI can branch on the code instead of matching
    /// prose that may be reworded.
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
