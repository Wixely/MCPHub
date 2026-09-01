using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MCPHub.Core.Logging;
using MCPHub.Core.Models;
using MCPHub.Core.Process;
using MCPHub.Core.Services;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Settings;
using MCPHub.Core.Updates;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace MCPHub.Core.Management;

/// <summary>
/// Lets connected agents operate the servers MCPHub manages, through the proxy as <c>mcphub__*</c> tools:
/// list them, start / stop / restart them, install and update them, and check GitHub for newer releases
/// (of a server, or of MCPHub itself). Which of those an agent may actually use is decided by
/// <see cref="AgentManagementPolicy"/>; this class only does the work.
/// </summary>
/// <remarks>
/// Nothing here reads or returns a server's config files or its logs — that is a deliberate boundary. An
/// agent that hits a misconfigured server is told to ask the user, who has the Logs page.
/// Results are JSON so an agent can read them structurally; failures are ordinary MCP tool errors.
/// Operations on one server are serialised (a second call for the same server while one is in flight is
/// refused) so an agent cannot, say, start what it is mid-way through updating.
/// </remarks>
public sealed class AgentManagementToolProvider : ILocalToolProvider
{
    /// <summary>Namespace key: tools appear as <c>mcphub__*</c>.</summary>
    public const string ProviderKey = "mcphub";

    /// <summary>The read-only inventory tool, available whenever management is on at all.</summary>
    public const string ListTool = "list_services";

    /// <summary>Tools gated by the control switch.</summary>
    public static readonly IReadOnlySet<string> ControlTools = new HashSet<string>(StringComparer.Ordinal) { "start", "stop", "restart" };

    /// <summary>Tools gated by the install switch.</summary>
    public static readonly IReadOnlySet<string> InstallTools = new HashSet<string>(StringComparer.Ordinal) { "install", "update" };

    /// <summary>Tools gated by the update-checks switch.</summary>
    public static readonly IReadOnlySet<string> UpdateCheckTools = new HashSet<string>(StringComparer.Ordinal) { "check_service_updates", "check_hub_update" };

    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private const string ServiceArgument = """
        "service": { "type": "string", "description": "Which server: its key (the prefix before '__' in its tool names, e.g. 'kodi'), product name (e.g. 'KodiMCPSharp') or display name. See mcphub__list_services." }
        """;

    private const string WaitArgument = """
        "wait_seconds": { "type": "integer", "minimum": 0, "maximum": 120, "description": "How long to wait for the server to settle before answering (default 30). 0 returns immediately." }
        """;

    private static readonly IReadOnlyList<Tool> ToolDefinitions =
    [
        new Tool
        {
            Name = ListTool,
            Description = "List the MCP servers MCPHub manages, with each one's install state, installed and latest known " +
                          "versions, run state and port. A server's own tools are only available through this proxy while it " +
                          "is Running. Optional 'query' narrows by name or key.",
            InputSchema = Schema("""
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Case-insensitive text to match against a server's key, product name or display name." }
                  }
                }
                """),
        },
        new Tool
        {
            Name = "start",
            Description = "Start an installed server that is stopped, then wait for it to report healthy. Once it is Running its " +
                          "tools appear in this proxy's tool list shortly after — re-list tools before calling them. Already-running " +
                          "servers are left alone.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": { {{ServiceArgument}}, {{WaitArgument}} },
                  "required": ["service"]
                }
                """),
        },
        new Tool
        {
            Name = "stop",
            Description = "Stop a running server. Its tools disappear from this proxy while it is stopped.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": { {{ServiceArgument}}, {{WaitArgument}} },
                  "required": ["service"]
                }
                """),
        },
        new Tool
        {
            Name = "restart",
            Description = "Stop a server and start it again (or just start it if it was stopped) — e.g. after the user has " +
                          "changed its configuration. Waits for it to report healthy.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": { {{ServiceArgument}}, {{WaitArgument}} },
                  "required": ["service"]
                }
                """),
        },
        new Tool
        {
            Name = "install",
            Description = "Download and install a server that is not installed yet, from its latest GitHub release. Refuses if " +
                          "it is already installed (use mcphub__update). Pass start=true to start it once installed.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": {
                    {{ServiceArgument}},
                    "start": { "type": "boolean", "description": "Start the server after installing it (default false)." },
                    {{WaitArgument}}
                  },
                  "required": ["service"]
                }
                """),
        },
        new Tool
        {
            Name = "update",
            Description = "Check GitHub for an installed server's latest release and, if it is newer, install it (the user's " +
                          "config is preserved). A running server is stopped for the update and started again afterwards. Does " +
                          "nothing when already up to date unless force=true, which reinstalls the current release.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": {
                    {{ServiceArgument}},
                    "force": { "type": "boolean", "description": "Reinstall even when already up to date (default false)." },
                    "restart": { "type": "boolean", "description": "Start the server again after updating if it was running (default true)." },
                    {{WaitArgument}}
                  },
                  "required": ["service"]
                }
                """),
        },
        new Tool
        {
            Name = "check_service_updates",
            Description = "Ask GitHub for the latest release of one server (pass 'service') or of every managed server, and " +
                          "report which installed ones have an update available. Does not install anything.",
            InputSchema = Schema($$"""
                {
                  "type": "object",
                  "properties": { {{ServiceArgument}} }
                }
                """),
        },
        new Tool
        {
            Name = "check_hub_update",
            Description = "Compare the running MCPHub against its newest GitHub release. MCPHub never replaces itself — if a " +
                          "newer version exists, tell the user and give them the release page URL.",
            InputSchema = Schema("""{ "type": "object", "properties": {} }"""),
        },
    ];

    private readonly IServiceManager _manager;
    private readonly IServiceProcessHost _processHost;
    private readonly IReleaseService _releases;
    private readonly ILogStore _logStore;
    private readonly ILogger<AgentManagementToolProvider> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public AgentManagementToolProvider(
        IServiceManager manager,
        IServiceProcessHost processHost,
        IReleaseService releases,
        ILogStore logStore,
        ILogger<AgentManagementToolProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(processHost);
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(logger);
        _manager = manager;
        _processHost = processHost;
        _releases = releases;
        _logStore = logStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => ProviderKey;

    /// <inheritdoc />
    public string DisplayName => "MCPHub";

    /// <inheritdoc />
    public IReadOnlyList<Tool> Tools => ToolDefinitions;

    /// <summary>
    /// MCP server instructions for the capabilities currently granted, so an agent knows it can bring a
    /// server up (or update one) before it needs to.
    /// </summary>
    public static string BuildServerInstructions(bool control, bool install, bool updateChecks)
    {
        var sb = new StringBuilder();
        sb.Append("MCPHub also exposes mcphub__* tools for the MCP servers it manages. Call mcphub__list_services to see ")
          .Append("each server's install state, version and run state — a server's tools are only in this tool list while it is Running.");
        if (control)
            sb.Append(" You may start, stop and restart servers (mcphub__start / mcphub__stop / mcphub__restart): if the tools you ")
              .Append("need belong to a server that is installed but not Running, start it and re-list tools.");
        if (install)
            sb.Append(" You may install servers that are not installed and apply updates (mcphub__install / mcphub__update).");
        if (updateChecks)
            sb.Append(" You may check GitHub for newer releases (mcphub__check_service_updates for servers, mcphub__check_hub_update ")
              .Append("for MCPHub itself — MCPHub does not update itself, so report a newer release to the user).");
        sb.Append(" Server configuration files and logs are not available through these tools; if a server will not start or ")
          .Append("is misconfigured, tell the user, who can see its logs in MCPHub.");
        return sb.ToString();
    }

    /// <inheritdoc />
    public async ValueTask<CallToolResult> CallAsync(string toolName, IReadOnlyDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        try
        {
            return toolName switch
            {
                ListTool => await ListAsync(arguments, cancellationToken),
                "start" => await WithServiceAsync(arguments, StartAsync, cancellationToken),
                "stop" => await WithServiceAsync(arguments, StopAsync, cancellationToken),
                "restart" => await WithServiceAsync(arguments, RestartAsync, cancellationToken),
                "install" => await WithServiceAsync(arguments, InstallAsync, cancellationToken),
                "update" => await WithServiceAsync(arguments, UpdateAsync, cancellationToken),
                "check_service_updates" => await CheckServiceUpdatesAsync(arguments, cancellationToken),
                "check_hub_update" => await CheckHubUpdateAsync(cancellationToken),
                _ => Error($"Unknown mcphub tool '{toolName}'."),
            };
        }
        catch (ManagementArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch (GithubAuthException)
        {
            return Error("GitHub rejected MCPHub's token (401). Ask the user to clear or replace it in MCPHub → Settings → GitHub token.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mcphub__{Tool} failed.", toolName);
            return Error($"mcphub__{toolName} failed: {ex.Message}");
        }
    }

    // ---- inventory ------------------------------------------------------------------------------

    private async Task<CallToolResult> ListAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        await _manager.RefreshInstalledAsync(ct);
        foreach (var service in _manager.Services)
        {
            if (service.LatestVersion is null)
                _manager.ApplyCachedLatest(service);
        }

        var query = GetString(args, "query")?.Trim();
        var services = _manager.Services
            .Where(s => string.IsNullOrEmpty(query)
                        || s.Catalog.MatchesSearch(query)
                        || s.Catalog.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Catalog.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(Summarize)
            .ToList();

        return Json(new ServiceListResult { Count = services.Count, Services = services }, ManagementJsonContext.Default.ServiceListResult);
    }

    private async Task<CallToolResult> CheckServiceUpdatesAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        List<ManagedService> targets = Has(args, "service") ? [Resolve(args)] : _manager.Services.ToList();

        await _manager.RefreshInstalledAsync(ct);

        var reachable = 0;
        var updates = 0;
        foreach (var service in targets)
        {
            if (await _manager.CheckForUpdatesAsync(service, ct) is not null)
                reachable++;
            if (service.UpdateStatus == UpdateStatus.UpdateAvailable)
                updates++;
        }

        var result = new ServiceUpdateCheckResult
        {
            Checked = targets.Count,
            Reachable = reachable,
            UpdatesAvailable = updates,
            Note = reachable == 0
                ? "Couldn't reach GitHub (or it is rate-limiting: 60 requests/hour without a token — the user can add one in MCPHub → Settings)."
                : updates == 0
                    ? "Every installed server is up to date."
                    : $"{updates} installed server(s) have an update available; apply one with mcphub__update.",
            Services = targets.OrderBy(s => s.Catalog.DisplayName, StringComparer.OrdinalIgnoreCase).Select(Summarize).ToList(),
        };
        return Json(result, ManagementJsonContext.Default.ServiceUpdateCheckResult);
    }

    private async Task<CallToolResult> CheckHubUpdateAsync(CancellationToken ct)
    {
        var release = await _releases.GetLatestReleaseAsync(HubRelease.Catalog, ct);
        if (release is null)
            return Error("Couldn't reach GitHub to check for an MCPHub release (or none is published yet). Try again shortly.");

        var status = UpdateStatusCalculator.Compute(HubRelease.CurrentVersion, release.Version);
        var asset = HubRelease.AssetForThisOs(release);
        var result = new HubUpdateResult
        {
            CurrentVersion = HubRelease.CurrentVersion,
            LatestVersion = release.Version,
            UpdateStatus = status,
            PublishedAt = release.PublishedAt?.ToString("yyyy-MM-dd"),
            ReleasePageUrl = HubRelease.LatestReleasePageUrl,
            AssetName = asset?.Name,
            AssetSizeBytes = asset?.Size,
            Note = status == UpdateStatus.UpdateAvailable
                ? $"MCPHub {release.Version} is available (running {HubRelease.CurrentVersion}). MCPHub does not update itself: tell the user and give them the release page URL."
                : $"MCPHub {HubRelease.CurrentVersion} is the newest release.",
        };
        return Json(result, ManagementJsonContext.Default.HubUpdateResult);
    }

    // ---- lifecycle --------------------------------------------------------------------------------

    private async Task<CallToolResult> StartAsync(ManagedService service, IReadOnlyDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var name = service.Catalog.Name;
        if (!service.IsInstalled)
            return Error($"{name} is not installed, so it cannot be started. Install it first with mcphub__install.");

        var wait = GetWait(args);
        if (IsUp(service.RunState))
            return ActionResult("start", service, $"{name} is already {service.RunState}; nothing to do.");

        Note(service, "Agent requested start (mcphub__start).");
        await StartAndWaitAsync(service, wait, ct);
        return StartOutcome("start", service, prefix: null);
    }

    private async Task<CallToolResult> StopAsync(ManagedService service, IReadOnlyDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var name = service.Catalog.Name;
        var wait = GetWait(args);
        if (!IsUp(service.RunState) && !_processHost.IsRunning(name))
            return ActionResult("stop", service, $"{name} is already {service.RunState}; nothing to do.");

        Note(service, "Agent requested stop (mcphub__stop).");
        var stopped = await StopAndWaitAsync(service, wait, ct);
        return stopped
            ? ActionResult("stop", service, $"{name} is now {service.RunState}.")
            : Error($"{name} did not stop within the wait; it is {service.RunState}. Re-check with mcphub__list_services.");
    }

    private async Task<CallToolResult> RestartAsync(ManagedService service, IReadOnlyDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var name = service.Catalog.Name;
        if (!service.IsInstalled)
            return Error($"{name} is not installed, so it cannot be restarted. Install it first with mcphub__install.");

        var wait = GetWait(args);
        var wasRunning = IsUp(service.RunState) || _processHost.IsRunning(name);
        Note(service, wasRunning ? "Agent requested restart (mcphub__restart)." : "Agent requested start via mcphub__restart.");

        if (wasRunning && !await StopAndWaitAsync(service, wait, ct))
            return Error($"{name} did not stop within the wait; it is {service.RunState}, so it was not started again.");

        await StartAndWaitAsync(service, wait, ct);
        return StartOutcome("restart", service, prefix: wasRunning ? "Restarted: " : "Was not running; started: ", wasRunning: wasRunning);
    }

    private async Task<CallToolResult> InstallAsync(ManagedService service, IReadOnlyDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var name = service.Catalog.Name;
        // Validate every argument before touching anything: a typo must not cost a download.
        var start = GetBool(args, "start", false);
        var wait = GetWait(args);
        if (service.IsInstalled)
            return Error($"{name} is already installed ({service.InstalledVersion ?? "version unknown"}). Use mcphub__update to update or reinstall it.");

        Note(service, "Agent requested install (mcphub__install).");
        await _manager.InstallOrUpdateAsync(service, progress: null, ct);
        var message = $"Installed {name} {service.InstalledVersion}.";

        if (start)
        {
            await StartAndWaitAsync(service, wait, ct);
            return StartOutcome("install", service, prefix: message + " ");
        }

        return ActionResult("install", service, message + " It is not running; start it with mcphub__start when needed.");
    }

    private async Task<CallToolResult> UpdateAsync(ManagedService service, IReadOnlyDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var name = service.Catalog.Name;
        if (!service.IsInstalled)
            return Error($"{name} is not installed; there is nothing to update. Use mcphub__install.");

        var force = GetBool(args, "force", false);
        var restart = GetBool(args, "restart", true);
        var wait = GetWait(args);
        var previous = service.InstalledVersion;

        var release = await _manager.CheckForUpdatesAsync(service, ct);
        if (release is null)
            return Error($"Couldn't reach GitHub to find {name}'s latest release (rate limit? a GitHub token in MCPHub → Settings lifts it). Nothing was changed.");

        if (service.UpdateStatus != UpdateStatus.UpdateAvailable && !force)
        {
            return ActionResult("update", service,
                $"{name} is already up to date ({previous}); nothing to do. Pass force=true to reinstall the current release.",
                previousVersion: previous);
        }

        var wasRunning = IsUp(service.RunState) || _processHost.IsRunning(name);
        Note(service, $"Agent requested update to {release.Version} (mcphub__update).");
        await _manager.InstallOrUpdateAsync(service, progress: null, ct);

        var verb = force && string.Equals(previous, service.InstalledVersion, StringComparison.OrdinalIgnoreCase) ? "Reinstalled" : "Updated";
        var message = $"{verb} {name}: {previous} → {service.InstalledVersion}.";

        if (wasRunning && restart)
        {
            // The install stopped it to unlock the executable; bring it back as the Services page does.
            await StartAndWaitAsync(service, wait, ct);
            return StartOutcome("update", service, prefix: message + " ", wasRunning: true, previousVersion: previous);
        }

        var tail = wasRunning
            ? " It was running and has been left stopped, as requested."
            : " It was not running and has been left stopped.";
        return ActionResult("update", service, message + tail, wasRunning, previous);
    }

    // ---- lifecycle helpers ----------------------------------------------------------------------

    private static bool IsUp(ServiceRunState state)
        => state is ServiceRunState.Starting or ServiceRunState.Running or ServiceRunState.Unhealthy;

    private async Task StartAndWaitAsync(ManagedService service, TimeSpan wait, CancellationToken ct)
    {
        await _processHost.StartAsync(service, ct);
        await WaitForAsync(service, state => state != ServiceRunState.Starting, wait, ct);
    }

    /// <summary>Stops the service and waits for it to settle; false if it is still on its way down.</summary>
    private async Task<bool> StopAndWaitAsync(ManagedService service, TimeSpan wait, CancellationToken ct)
    {
        await _processHost.StopAsync(service, ct);
        await WaitForAsync(service, state => state is ServiceRunState.Stopped or ServiceRunState.Faulted, wait, ct);
        return service.RunState is ServiceRunState.Stopped or ServiceRunState.Faulted;
    }

    private static async Task WaitForAsync(ManagedService service, Func<ServiceRunState, bool> settled, TimeSpan timeout, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!settled(service.RunState) && stopwatch.Elapsed < timeout)
            await Task.Delay(PollInterval, ct);
    }

    /// <summary>Turns the state a start settled in into a result: Running is success, Faulted an error, Starting a "not yet".</summary>
    private CallToolResult StartOutcome(string action, ManagedService service, string? prefix, bool? wasRunning = null, string? previousVersion = null)
    {
        var name = service.Catalog.Name;
        switch (service.RunState)
        {
            case ServiceRunState.Running:
                return ActionResult(action, service,
                    $"{prefix}{name} is Running on port {service.Port?.ToString() ?? "?"}. Its tools will appear in this proxy's tool list momentarily — re-list tools before using them.",
                    wasRunning, previousVersion);
            case ServiceRunState.Faulted:
            case ServiceRunState.Stopped:
                return Error($"{prefix}{name} failed to start (it is {service.RunState}). It is usually a missing setting in its config or a port already in use — ask the user to check its logs in MCPHub.");
            default:
                return ActionResult(action, service,
                    $"{prefix}{name} is still {service.RunState} — its health check has not passed yet. Re-check with mcphub__list_services in a few seconds; if it stays that way, ask the user to look at its logs in MCPHub.",
                    wasRunning, previousVersion);
        }
    }

    /// <summary>Resolves the target server and runs <paramref name="body"/> under that server's gate.</summary>
    private async Task<CallToolResult> WithServiceAsync(
        IReadOnlyDictionary<string, JsonElement>? args,
        Func<ManagedService, IReadOnlyDictionary<string, JsonElement>?, CancellationToken, Task<CallToolResult>> body,
        CancellationToken ct)
    {
        var service = Resolve(args);
        var gate = _gates.GetOrAdd(service.Catalog.Name, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
            return Error($"Another operation on {service.Catalog.Name} is still in progress. Wait for it, then re-check with mcphub__list_services.");

        try
        {
            return await body(service, args, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private ManagedService Resolve(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var id = RequireString(args, "service");
        var match = _manager.Services.FirstOrDefault(s =>
            string.Equals(s.Catalog.Key, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Catalog.Name, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Catalog.DisplayName, id, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            return match;

        var known = string.Join(", ", _manager.Services.Select(s => s.Catalog.Key).OrderBy(k => k, StringComparer.Ordinal));
        throw new ManagementArgumentException($"No managed server matches '{id}'. Known server keys: {known}. Call mcphub__list_services for details.");
    }

    private static ServiceSummary Summarize(ManagedService service) => new()
    {
        Name = service.Catalog.Name,
        Key = service.Catalog.Key,
        DisplayName = service.Catalog.DisplayName,
        Description = service.Catalog.Description,
        Installed = service.IsInstalled,
        InstalledVersion = service.InstalledVersion,
        LatestVersion = service.LatestVersion,
        UpdateStatus = service.UpdateStatus,
        RunState = service.RunState,
        Port = service.Port,
        RepositoryUrl = service.Catalog.RepositoryUrl,
    };

    /// <summary>Writes a line into the server's own log so the user can see an agent drove the change.</summary>
    private void Note(ManagedService service, string text)
    {
        _logStore.Append(service.Catalog.Name, new LogLine(DateTimeOffset.Now, LogStream.Info, text));
        _logger.LogInformation("{Service}: {Message}", service.Catalog.Name, text);
    }

    // ---- argument helpers -------------------------------------------------------------------------

    private static bool Has(IReadOnlyDictionary<string, JsonElement>? args, string name)
        => args is not null && args.TryGetValue(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string? GetString(IReadOnlyDictionary<string, JsonElement>? args, string name)
    {
        if (!Has(args, name))
            return null;
        var v = args![name];
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => throw new ManagementArgumentException($"'{name}' must be a string."),
        };
    }

    private static string RequireString(IReadOnlyDictionary<string, JsonElement>? args, string name)
    {
        var value = GetString(args, name)?.Trim();
        return string.IsNullOrEmpty(value) ? throw new ManagementArgumentException($"'{name}' is required.") : value;
    }

    private static bool GetBool(IReadOnlyDictionary<string, JsonElement>? args, string name, bool fallback)
    {
        if (!Has(args, name))
            return fallback;
        var v = args![name];
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when EnvironmentFlag.Parse(v.GetString()) is { } parsed => parsed,
            _ => throw new ManagementArgumentException($"'{name}' must be true or false."),
        };
    }

    private static TimeSpan GetWait(IReadOnlyDictionary<string, JsonElement>? args)
    {
        if (!Has(args, "wait_seconds"))
            return DefaultWait;
        var v = args!["wait_seconds"];
        double seconds = v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => throw new ManagementArgumentException("'wait_seconds' must be a number of seconds."),
        };
        if (double.IsNaN(seconds) || seconds < 0)
            seconds = 0;
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxWait.TotalSeconds));
    }

    // ---- result helpers -------------------------------------------------------------------------

    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static CallToolResult ActionResult(string action, ManagedService service, string message, bool? wasRunning = null, string? previousVersion = null)
        => Json(new ServiceActionResult
        {
            Action = action,
            Message = message,
            WasRunning = wasRunning,
            PreviousVersion = previousVersion,
            Service = Summarize(service),
        }, ManagementJsonContext.Default.ServiceActionResult);

    private static CallToolResult Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, typeInfo) }] };

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}

/// <summary>A bad or missing tool argument; surfaces as a plain tool error rather than an exception.</summary>
internal sealed class ManagementArgumentException(string message) : Exception(message);
