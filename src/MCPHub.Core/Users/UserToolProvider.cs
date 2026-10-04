using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MCPHub.Core.Permissions;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace MCPHub.Core.Users;

/// <summary>
/// Managing who may reach this hub, as <c>users__*</c> tools: list, create, rotate a key, rename,
/// suspend, delete.
///
/// <para>Identity only. What a user may <em>do</em> is <c>permissions__*</c>'s business, and the split
/// is the point: one user, one key, usable on the Router and the proxy alike, with each surface
/// keeping its own idea of what that user is allowed.</para>
///
/// <para>Deleting a user also drops its tool grants, because a grant whose user has gone is an entry
/// nobody can explain. That is done through the permissions store rather than by identity reaching
/// into it, so neither layer has to know the other's shape.</para>
/// </summary>
public sealed class UserToolProvider : ILocalToolProvider
{
    /// <summary>Namespace key: tools appear as <c>users__*</c>.</summary>
    public const string ProviderKey = "users";

    /// <summary>Identity here cannot be edited because it is mounted read-only.</summary>
    public const string ReadOnlyCode = "users.read_only";

    /// <summary>No user has that id or name.</summary>
    public const string NoSuchUserCode = "users.no_such_user";

    /// <summary>A name matched more than one user, so the id is needed.</summary>
    public const string AmbiguousUserCode = "users.ambiguous_user";

    /// <summary>The stored directory could not be loaded, so no key is recognised until it is repaired.</summary>
    public const string UnreadableCode = "users.unreadable";

    private const string UserArgument = """
        "user": { "type": "string", "description": "Which user: its id, or its name when that is unambiguous. See users__list." }
        """;

    private static readonly IReadOnlyList<Tool> ToolDefinitions =
    [
        new Tool
        {
            Name = "list",
            Description = "Every user this hub knows: id, name, whether it is enabled, and a short fingerprint of "
                          + "its key. Keys themselves are not stored and cannot be listed.",
            InputSchema = Schema("""{ "type": "object", "properties": {} }"""),
        },
        new Tool
        {
            Name = "create",
            Description = "Add a user and issue its key. THE KEY IS RETURNED ONCE and cannot be recovered — only "
                          + "its hash is stored. The same key works on the proxy and on the Model Router; what the "
                          + "user may actually do is set with permissions__set_grants and on the Router page.",
            InputSchema = Schema("""
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "What an operator will call this caller, e.g. an agent's nickname." }
                  },
                  "required": ["name"]
                }
                """),
        },
        new Tool
        {
            Name = "rotate_key",
            Description = "Issue a new key for a user, retiring the old one immediately on every surface. Returned once.",
            InputSchema = Schema($$"""
                { "type": "object", "properties": { {{UserArgument}} }, "required": ["user"] }
                """),
        },
        new Tool
        {
            Name = "rename",
            Description = "Change what a user is called. Its id, key and grants are untouched.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": { {{UserArgument}}, "name": { "type": "string" } },
                  "required": ["user", "name"]
                }
                """),
        },
        new Tool
        {
            Name = "set_enabled",
            Description = "Suspend or restore a user. Suspending stops its key everywhere at once and keeps "
                          + "everything else, so it can be restored exactly as it was; to a caller it is "
                          + "indistinguishable from a key that was never issued.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": { {{UserArgument}}, "enabled": { "type": "boolean" } },
                  "required": ["user", "enabled"]
                }
                """),
        },
        new Tool
        {
            Name = "delete",
            Description = "Remove a user, retire its key and drop its tool grants. Use set_enabled to suspend one "
                          + "you may want back.",
            InputSchema = Schema($$"""
                { "type": "object", "properties": { {{UserArgument}} }, "required": ["user"] }
                """),
        },
    ];

    private readonly IUserDirectory _users;
    private readonly IWritablePermissions? _permissions;
    private readonly ILogger<UserToolProvider> _logger;

    /// <param name="permissions">So that deleting a user drops its grants too. Optional, and null means
    /// a hub whose grants are mounted read-only: the user still goes, and its now-inert grant entry is
    /// reported by permissions__list_grants rather than silently left looking live.</param>
    public UserToolProvider(
        IUserDirectory users, IWritablePermissions? permissions, ILogger<UserToolProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(logger);
        _users = users;
        _permissions = permissions;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => ProviderKey;

    /// <inheritdoc />
    public string DisplayName => "Users";

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
                "list" => List(),
                "create" => Create(arguments),
                "rotate_key" => RotateKey(arguments),
                "rename" => Rename(arguments),
                "set_enabled" => SetEnabled(arguments),
                "delete" => Delete(arguments),
                _ => Failed("users.unknown_tool", $"Unknown users tool '{toolName}'."),
            });
        }
        catch (UserArgumentException ex)
        {
            return new ValueTask<CallToolResult>(Failed(ex.Code, ex.Message, ex.Remedy));
        }
        catch (UserNotFoundException ex)
        {
            return new ValueTask<CallToolResult>(Failed(NoSuchUserCode, ex.Message));
        }
        catch (ArgumentException ex)
        {
            return new ValueTask<CallToolResult>(Failed("users.invalid", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new ValueTask<CallToolResult>(Failed(UnreadableCode, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "users__{Tool} failed.", toolName);
            return new ValueTask<CallToolResult>(Failed("users.failed", $"users__{toolName} failed: {ex.Message}"));
        }
    }

    private CallToolResult List() => Json(
        new UserList
        {
            Users = [.. _users.Snapshot.Users.Select(Describe)],
            Editable = _users is IWritableUsers,
            LoadError = (_users as UserStore)?.LoadError,
        },
        UserResultsJsonContext.Default.UserList);

    private CallToolResult Create(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();
        var (user, key) = store.Create(Text(arguments, "name"));
        return Json(new UserKeyIssued
        {
            User = Describe(user),
            Key = key,
            Notice = "This key is shown once and is not recoverable. Store it now; a lost key is rotated, "
                     + "not looked up. It works everywhere this hub checks a key.",
        }, UserResultsJsonContext.Default.UserKeyIssued);
    }

    private CallToolResult RotateKey(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();
        var user = Find(Text(arguments, "user"));
        var key = store.RotateKey(user.Id);
        return Json(new UserKeyIssued
        {
            User = Describe(user),
            Key = key,
            Notice = "The previous key stopped working on every surface the moment this one was issued.",
        }, UserResultsJsonContext.Default.UserKeyIssued);
    }

    private CallToolResult Rename(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();
        var reference = Text(arguments, "user");
        var name = Text(arguments, "name");
        var user = Find(reference);
        store.Rename(user.Id, name);
        return Changed($"'{user.Name}' is now called '{name}'.", user.Id);
    }

    private CallToolResult SetEnabled(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();

        // Arguments first, lookups after: a caller who forgot one should be told that, not "no such
        // user".
        var reference = Text(arguments, "user");
        var enabled = Flag(arguments, "enabled");
        var user = Find(reference);
        store.SetEnabled(user.Id, enabled);
        return Changed(
            enabled
                ? $"'{user.Name}' is enabled again."
                : $"'{user.Name}' is suspended; its key no longer works anywhere.",
            user.Id);
    }

    private CallToolResult Delete(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var store = Writable();
        var user = Find(Text(arguments, "user"));
        store.Delete(user.Id);

        // Grants go with it. Done after the user is gone, so a failure here leaves an inert grant
        // rather than a live key with no owner.
        _permissions?.ForgetUser(user.Id);

        return Json(
            new UserChange { Message = $"'{user.Name}' is gone, its key is retired and its grants are dropped." },
            UserResultsJsonContext.Default.UserChange);
    }

    private IWritableUsers Writable() =>
        _users as IWritableUsers
        ?? throw new UserArgumentException(
            ReadOnlyCode,
            "This hub's users are mounted read-only, so they cannot be edited here.",
            "Edit the users document the hub was given, or run it with an editable store.");

    /// <summary>A user by id, or by name when that is unambiguous. Ambiguity is refused rather than
    /// resolved: rotating the key of the wrong one of two users called "agent" is worse than refusing,
    /// because it silently cuts off whoever held the other.</summary>
    private HubUser Find(string reference)
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
            0 => throw new UserArgumentException(
                NoSuchUserCode, $"No user has the id or name '{reference}'.", "Call users__list to see what exists."),
            _ => throw new UserArgumentException(
                AmbiguousUserCode, $"{byName.Count} users are called '{reference}'.", "Use the user's id instead."),
        };
    }

    private static UserSummary Describe(HubUser user) => new()
    {
        Id = user.Id,
        Name = user.Name,
        Enabled = user.Enabled,

        // Enough to tell two keys apart when somebody says "the key I gave Banter", and useless for
        // authenticating. The whole hash would be a verifier for anyone who could list it.
        KeyFingerprint = user.KeyHash.Length >= 8 ? user.KeyHash[..8] : user.KeyHash,
    };

    private CallToolResult Changed(string message, string id)
    {
        var user = _users.Snapshot.Users.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.Ordinal));
        return Json(
            new UserChange { Message = message, User = user is null ? null : Describe(user) },
            UserResultsJsonContext.Default.UserChange);
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new UserArgumentException("users.bad_argument", $"'{name}' is required.");
        }

        return value.GetString()!;
    }

    private static bool Flag(IReadOnlyDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new UserArgumentException("users.bad_argument", $"'{name}' must be true or false.");
        }

        return value.GetBoolean();
    }

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static CallToolResult Json<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, typeInfo) }] };

    private static CallToolResult Failed(string code, string reason, string? remedy = null) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(
                    new UserError { Code = code, Reason = reason, Remedy = remedy },
                    UserResultsJsonContext.Default.UserError),
            },
        ],
    };
}

/// <summary>A bad argument or a refusal, carrying the code a caller should branch on.</summary>
internal sealed class UserArgumentException(string code, string message, string? remedy = null)
    : Exception(message)
{
    public string Code { get; } = code;

    public string? Remedy { get; } = remedy;
}
