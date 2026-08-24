using CommunityToolkit.Mvvm.Input;

namespace MCPHub.App.ViewModels;

/// <summary>
/// One entry in a service's Config dropdown — a friendly label, the file it opens, and the command
/// that opens it.
/// </summary>
public sealed partial class ConfigFileViewModel : ViewModelBase
{
    private readonly Action<string> _open;

    public ConfigFileViewModel(string fileName, string displayName, bool isPrimary, Action<string> open)
    {
        FileName = fileName;
        DisplayName = displayName;
        IsPrimary = isPrimary;
        _open = open;
    }

    /// <summary>The config file this entry opens, e.g. <c>remote_admin_windows_servers.json</c>.</summary>
    public string FileName { get; }

    /// <summary>Label shown in the menu, e.g. "Windows servers".</summary>
    public string DisplayName { get; }

    /// <summary>True for the service's own <c>{Name}.json</c>.</summary>
    public bool IsPrimary { get; }

    /// <summary>Tooltip: the actual file name, so the friendly label is never ambiguous.</summary>
    public string ToolTip => FileName;

    [RelayCommand]
    private void Open() => _open(FileName);
}
