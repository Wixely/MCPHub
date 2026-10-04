using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MCPHub.Core.Permissions;
using MCPHub.Proxy;

namespace MCPHub.App.ViewModels;

/// <summary>One tool, as a checkbox in the full list.</summary>
public sealed partial class ToolRow : ObservableObject
{
    [ObservableProperty] private bool _isGranted;

    public ToolRow(string name, ToolGroupRow group)
    {
        Name = name;
        Group = group;
        var at = name.IndexOf(ProxyConstants.NamespaceSeparator, StringComparison.Ordinal);
        ShortName = at > 0 ? name[(at + ProxyConstants.NamespaceSeparator.Length)..] : name;
    }

    /// <summary>The exposed name, which is also exactly what a grant for this one tool says.</summary>
    public string Name { get; }

    /// <summary>Without the server prefix, since the prefix is already the group it is listed under.</summary>
    public string ShortName { get; }

    public ToolGroupRow Group { get; }

    public string GroupName => Group.DisplayName;
}

/// <summary>
/// One server's tools, as a single checkbox in the simple list and a heading in the full one.
///
/// <para>Checked means the whole server — <c>kodi__*</c> — rather than today's tools listed out, so a
/// tool the service adds later is covered too. That is the difference between the two lists: this one
/// grants a service, the other grants named tools.</para>
/// </summary>
public sealed partial class ToolGroupRow : ObservableObject
{
    [ObservableProperty] private bool _isGranted;

    public ToolGroupRow(ToolGroup group)
    {
        Key = group.Key;
        DisplayName = group.DisplayName;
        IsService = group.Kind == ToolGroupKind.Service;
        IsAvailable = group.IsAvailable;
        Tools = [.. group.Tools.Select(t => new ToolRow(t, this))];
    }

    public string Key { get; }

    public string DisplayName { get; }

    public bool IsService { get; }

    /// <summary>False for a service that is registered but not connected: its tools are unknown, so the
    /// whole-service checkbox is the only way to grant it and the full list has nothing to show.</summary>
    public bool IsAvailable { get; }

    public ObservableCollection<ToolRow> Tools { get; }

    /// <summary>The grant this row writes when checked.</summary>
    public string Wildcard => Key + PermissionsConfigurationRules.ServerWildcardSuffix;

    public string Summary
    {
        get
        {
            if (IsGranted)
            {
                return Tools.Count == 0 ? "Every tool, including any it adds later" : "Every tool";
            }

            var chosen = Tools.Count(t => t.IsGranted);
            return Tools.Count == 0
                ? IsAvailable ? "No tools" : "Not connected — its tools are unknown"
                : chosen == 0 ? $"None of {Tools.Count}" : $"{chosen} of {Tools.Count}";
        }
    }

    public void RefreshSummary() => OnPropertyChanged(nameof(Summary));
}
