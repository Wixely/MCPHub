using System.Text.Json;
using MCPHub.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Settings;

/// <summary>Loads and persists MCPHub's <see cref="MCPHubSettings"/> as <c>settings.json</c>.</summary>
public interface ISettingsStore
{
    /// <summary>The in-memory settings (mutated via the UI, then persisted with <see cref="SaveAsync"/>).</summary>
    MCPHubSettings Current { get; }

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly ILogger<SettingsStore> _logger;
    private readonly object _gate = new();

    public SettingsStore(IAppPaths appPaths, ILogger<SettingsStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appPaths.SettingsDirectory);
        _path = Path.Combine(appPaths.SettingsDirectory, "settings.json");
        Current = Load();
    }

    public MCPHubSettings Current { get; private set; }

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            try
            {
                var json = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.MCPHubSettings);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Failed to save settings to {Path}.", _path);
            }
        }

        return Task.CompletedTask;
    }

    private MCPHubSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.MCPHubSettings);
                if (settings is not null)
                    return settings;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Could not read settings; using defaults.");
        }

        return new MCPHubSettings();
    }
}
