using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MCPHub.App.Messages;
using MCPHub.Core.Permissions;
using MCPHub.Core.Users;
using MCPHub.Proxy;

namespace MCPHub.App.ViewModels;

/// <summary>A user as the list shows it, with what it is currently granted.</summary>
public sealed class GrantRow
{
    public GrantRow(HubUser user, PermissionsGrant? grant)
    {
        Id = user.Id;
        Name = user.Name.Length > 0 ? user.Name : "(unnamed)";
        Enabled = user.Enabled;
        State = user.Enabled ? "Enabled" : "Suspended";
        Tools = grant?.Tools ?? [];
        Summary = Tools.Length == 0
            ? "No tools"
            : Tools.Contains(PermissionsConfigurationRules.EverythingGrant)
                ? "Every tool"
                : string.Join(", ", Tools);
    }

    public string Id { get; }

    public string Name { get; }

    public bool Enabled { get; }

    public string State { get; }

    public string[] Tools { get; }

    public string Summary { get; }
}

/// <summary>One reason a tool is unavailable, as the Explain panel shows it.</summary>
public sealed class PermissionsDenialRow(ToolDenial denial)
{
    public string Code { get; } = denial.Code;

    public string Reason { get; } = denial.Reason;

    public string Remedy { get; } = denial.Remedy ?? string.Empty;

    /// <summary>The environment variable forcing this, when one is. Shown prominently because it is the
    /// reason a checkbox will not stay where it is put.</summary>
    public string PinnedBy { get; } = denial.PinnedBy ?? string.Empty;

    public bool HasRemedy => Remedy.Length > 0;

    public bool IsPinned => PinnedBy.Length > 0;
}

/// <summary>
/// Which tools each user may use.
///
/// <para>Grants only — users and their keys are on the Users page, because one caller has one identity
/// across every surface. A user with no grants may use no tools, which is why absence is the default
/// rather than something to configure.</para>
///
/// <para>The panel at the bottom is why the page is not just a list. Tool access is decided by several
/// independent policies at once, so "the tool is missing" has more than one possible cause and no list
/// can show that.</para>
/// </summary>
public sealed partial class PermissionsViewModel : ViewModelBase
{
    private readonly UserStore _users;
    private readonly PermissionsStore _permissions;
    private readonly CompositeToolAuthorization _authorization;
    private readonly IToolCatalog _catalog;

    /// <summary>True while the picker is being loaded or cascaded, so a checkbox changing another does
    /// not read back as somebody clicking it.</summary>
    private bool _syncing;

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private GrantRow? _selectedUser;
    [ObservableProperty] private string _newGrant = string.Empty;
    [ObservableProperty] private string _explainTool = string.Empty;
    [ObservableProperty] private string _explainSummary = string.Empty;
    [ObservableProperty] private bool _isAdvanced;
    [ObservableProperty] private bool _grantEverything;
    [ObservableProperty] private string _toolFilter = string.Empty;

    public PermissionsViewModel(
        UserStore users,
        PermissionsStore permissions,
        CompositeToolAuthorization authorization,
        IToolCatalog catalog)
    {
        _users = users;
        _permissions = permissions;
        _authorization = authorization;
        _catalog = catalog;
        Refresh();
    }

    /// <summary>Servers, each a single checkbox meaning "every tool it has, now and later".</summary>
    public ObservableCollection<ToolGroupRow> Groups { get; } = [];

    /// <summary>Every tool there is, filtered by <see cref="ToolFilter"/>, for picking them one by one.</summary>
    public ObservableCollection<ToolRow> VisibleTools { get; } = [];

    /// <summary>
    /// Grants that name something this hub cannot currently see — a tool of a service that is not
    /// connected, or a server not installed yet. Carried through a save untouched: a picker that can
    /// only show what exists must not delete what it cannot show.
    /// </summary>
    public ObservableCollection<string> KeptGrants { get; } = [];

    public bool HasKeptGrants => KeptGrants.Count > 0;

    public bool HasTools => VisibleTools.Count > 0;

    public string ModeText => IsAdvanced ? "Choosing tools one by one" : "Choosing whole services";

    public ObservableCollection<GrantRow> Rows { get; } = [];

    public ObservableCollection<PermissionsDenialRow> Denials { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public bool HasNoRows => Rows.Count == 0;

    public bool HasUserSelected => SelectedUser is not null;

    public bool HasDenials => Denials.Count > 0;

    public bool HasExplanation => ExplainSummary.Length > 0;

    public string EditorTitle => SelectedUser is { } row ? $"Tools for {row.Name}" : "Select a user";

    /// <summary>Why the stored document could not be read, if it could not. Nothing is granted while
    /// this is set and every save throws, so the page says so outright.</summary>
    public string? ConfigurationError => _permissions.LoadError;

    public bool HasConfigurationError => ConfigurationError is not null;

    /// <summary>
    /// Set when the selected user is suspended: its grants are real and apply to nothing, which is
    /// worth saying on the page that edits them rather than leaving somebody to wonder why a grant
    /// changed nothing.
    /// </summary>
    public string? SelectedUserWarning => SelectedUser is { Enabled: false } row
        ? $"'{row.Name}' is suspended, so none of this applies until it is enabled on the Users page."
        : null;

    public bool HasSelectedUserWarning => SelectedUserWarning is not null;

    partial void OnSelectedUserChanged(GrantRow? value)
    {
        OnPropertyChanged(nameof(HasUserSelected));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(SelectedUserWarning));
        OnPropertyChanged(nameof(HasSelectedUserWarning));
        LoadPicker(value?.Tools ?? []);
    }

    partial void OnIsAdvancedChanged(bool value) => OnPropertyChanged(nameof(ModeText));

    partial void OnToolFilterChanged(string value) => RefreshVisibleTools();

    /// <summary>
    /// Everything, including servers that are not installed yet — the one grant a list of checkboxes
    /// cannot express, so it is a checkbox of its own that disables the rest.
    /// </summary>
    partial void OnGrantEverythingChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        foreach (var group in Groups)
        {
            group.IsGranted = value;
            foreach (var tool in group.Tools)
            {
                tool.IsGranted = value;
            }

            group.RefreshSummary();
        }

        _syncing = false;
    }

    /// <summary>Builds the picker from the catalogue as it is right now, then ticks what this user holds.</summary>
    private void LoadPicker(IReadOnlyList<string> patterns)
    {
        _syncing = true;
        try
        {
            foreach (var group in Groups)
            {
                group.PropertyChanged -= OnGroupChanged;
                foreach (var tool in group.Tools)
                {
                    tool.PropertyChanged -= OnToolChanged;
                }
            }

            Groups.Clear();
            foreach (var group in _catalog.Groups)
            {
                var row = new ToolGroupRow(group);
                row.PropertyChanged += OnGroupChanged;
                foreach (var tool in row.Tools)
                {
                    tool.PropertyChanged += OnToolChanged;
                }

                Groups.Add(row);
            }

            GrantEverything = patterns.Contains(PermissionsConfigurationRules.EverythingGrant, StringComparer.Ordinal);

            var known = new HashSet<string>(StringComparer.Ordinal) { PermissionsConfigurationRules.EverythingGrant };
            foreach (var group in Groups)
            {
                known.Add(group.Wildcard);
                var wholeGroup = GrantEverything || patterns.Contains(group.Wildcard, StringComparer.Ordinal);
                group.IsGranted = wholeGroup;
                foreach (var tool in group.Tools)
                {
                    known.Add(tool.Name);
                    tool.IsGranted = wholeGroup || patterns.Contains(tool.Name, StringComparer.Ordinal);
                }

                group.IsGranted = wholeGroup || (group.Tools.Count > 0 && group.Tools.All(t => t.IsGranted));
                group.RefreshSummary();
            }

            KeptGrants.Clear();
            foreach (var pattern in patterns.Where(p => !known.Contains(p)))
            {
                KeptGrants.Add(pattern);
            }

            OnPropertyChanged(nameof(HasKeptGrants));
            RefreshVisibleTools();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>A group's checkbox cascades to its tools: checked is "all of them, and any added later".</summary>
    private void OnGroupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncing || e.PropertyName != nameof(ToolGroupRow.IsGranted) || sender is not ToolGroupRow group)
        {
            return;
        }

        _syncing = true;
        foreach (var tool in group.Tools)
        {
            tool.IsGranted = group.IsGranted;
        }

        if (!group.IsGranted)
        {
            GrantEverything = false;
        }

        group.RefreshSummary();
        _syncing = false;
    }

    /// <summary>And a tool's checkbox reports back up, so the simple list never claims a whole service
    /// that is only partly ticked.</summary>
    private void OnToolChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncing || e.PropertyName != nameof(ToolRow.IsGranted) || sender is not ToolRow tool)
        {
            return;
        }

        _syncing = true;
        tool.Group.IsGranted = tool.Group.Tools.Count > 0 && tool.Group.Tools.All(t => t.IsGranted);
        if (!tool.IsGranted)
        {
            GrantEverything = false;
        }

        tool.Group.RefreshSummary();
        _syncing = false;
    }

    private void RefreshVisibleTools()
    {
        var filter = ToolFilter.Trim();
        VisibleTools.Clear();
        foreach (var tool in Groups.SelectMany(g => g.Tools))
        {
            if (filter.Length == 0
                || tool.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || tool.GroupName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                VisibleTools.Add(tool);
            }
        }

        OnPropertyChanged(nameof(HasTools));
    }

    /// <summary>The patterns the picker currently describes, ready to store.</summary>
    private IReadOnlyList<string> SelectedPatterns()
    {
        if (GrantEverything)
        {
            // Everything already covers whatever was kept, so saying both would only be noise.
            return [PermissionsConfigurationRules.EverythingGrant];
        }

        var patterns = new List<string>();
        foreach (var group in Groups)
        {
            if (group.IsGranted)
            {
                patterns.Add(group.Wildcard);
                continue;
            }

            patterns.AddRange(group.Tools.Where(t => t.IsGranted).Select(t => t.Name));
        }

        patterns.AddRange(KeptGrants);
        return patterns;
    }

    partial void OnExplainSummaryChanged(string value) => OnPropertyChanged(nameof(HasExplanation));

    /// <summary>Opens the Users page. This page can only name users, so the answer to "where do I make
    /// one" should be a button rather than a sentence pointing at the nav bar.</summary>
    [RelayCommand]
    private void AddUser() => WeakReferenceMessenger.Default.Send(ShowPageMessage.Users);

    [RelayCommand]
    private void Save() => Run(() =>
    {
        if (SelectedUser is not { } row)
        {
            return;
        }

        var grants = SelectedPatterns();
        _permissions.SetGrants(row.Id, grants);
        Reselect(row.Id);
        StatusMessage = grants.Count == 0
            ? $"'{row.Name}' may now use no tools."
            : $"'{row.Name}' saved. Applies to its next request.";
    });

    /// <summary>
    /// Adds a grant by hand, for the one thing checkboxes cannot offer: a tool of a service that is not
    /// connected, or a server that is not installed yet. A pattern the picker does know is ticked there
    /// instead of being listed twice.
    /// </summary>
    [RelayCommand]
    private void AddGrant()
    {
        var pattern = NewGrant.Trim();
        if (pattern.Length == 0)
        {
            return;
        }

        if (pattern == PermissionsConfigurationRules.EverythingGrant)
        {
            GrantEverything = true;
        }
        else if (Groups.FirstOrDefault(g => string.Equals(g.Wildcard, pattern, StringComparison.Ordinal)) is { } group)
        {
            group.IsGranted = true;
        }
        else if (Groups.SelectMany(g => g.Tools)
                     .FirstOrDefault(t => string.Equals(t.Name, pattern, StringComparison.Ordinal)) is { } tool)
        {
            tool.IsGranted = true;
        }
        else if (!KeptGrants.Contains(pattern, StringComparer.Ordinal))
        {
            KeptGrants.Add(pattern);
            OnPropertyChanged(nameof(HasKeptGrants));
        }

        NewGrant = string.Empty;
    }

    /// <summary>Drops a hand-written grant. The picker's own rows are unticked rather than removed.</summary>
    [RelayCommand]
    private void RemoveGrant(string? pattern)
    {
        if (pattern is not null && KeptGrants.Remove(pattern))
        {
            OnPropertyChanged(nameof(HasKeptGrants));
        }
    }

    /// <summary>Ticks every tool on offer. Leaves the kept grants and "everything" alone: this is a
    /// shortcut through a long list, not a different kind of grant.</summary>
    [RelayCommand]
    private void SelectAllTools() => SetVisibleTools(true);

    /// <summary>The way back out of a full list, and the thing people reach for before re-picking.</summary>
    [RelayCommand]
    private void SelectNoTools() => SetVisibleTools(false);

    /// <summary>
    /// Ticks or unticks the rows on screen, filter included — "select all" inside a search means the
    /// matches, which is what makes a long list workable.
    /// </summary>
    private void SetVisibleTools(bool granted)
    {
        _syncing = true;
        foreach (var tool in VisibleTools)
        {
            tool.IsGranted = granted;
        }

        foreach (var group in Groups)
        {
            group.IsGranted = group.Tools.Count > 0 && group.Tools.All(t => t.IsGranted);
            group.RefreshSummary();
        }

        if (!granted)
        {
            GrantEverything = false;
        }

        _syncing = false;
    }

    /// <summary>
    /// Toggles the rows a person has selected in the list, which is what Space does to a multi-selection.
    /// Mixed selections all go the same way — on if any is off — so one press has one obvious outcome.
    /// </summary>
    public void ToggleTools(IReadOnlyList<ToolRow> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count == 0)
        {
            return;
        }

        var granted = !tools.All(t => t.IsGranted);
        _syncing = true;
        foreach (var tool in tools)
        {
            tool.IsGranted = granted;
        }

        foreach (var group in tools.Select(t => t.Group).Distinct())
        {
            group.IsGranted = group.Tools.Count > 0 && group.Tools.All(t => t.IsGranted);
            group.RefreshSummary();
        }

        if (!granted)
        {
            GrantEverything = false;
        }

        _syncing = false;
    }

    [RelayCommand]
    private void RevokeAll() => Run(() =>
    {
        if (SelectedUser is not { } row)
        {
            return;
        }

        _permissions.SetGrants(row.Id, []);
        Reselect(row.Id);
        StatusMessage = $"'{row.Name}' may now use no tools.";
    });

    /// <summary>
    /// Why the selected user can or cannot use a named tool.
    ///
    /// <para>The page's reason for existing. Tool access is an AND over independent policies — this
    /// user's grants, whether it is enabled, whether agent management is on, whether a capability under
    /// it is on, whether an environment variable is pinning any of them — so a tool can be withheld for
    /// several reasons at once. A list of grants cannot show that, and neither can a single yes or no:
    /// an operator who grants the tool and sees no change concludes the grant failed, when a switch was
    /// also off all along. So every reason is listed, each with what to change.</para>
    /// </summary>
    [RelayCommand]
    private void Explain() => Run(() =>
    {
        Denials.Clear();
        OnPropertyChanged(nameof(HasDenials));

        if (SelectedUser is not { } row)
        {
            ExplainSummary = "Select a user above first.";
            return;
        }

        var tool = ExplainTool.Trim();
        if (tool.Length == 0)
        {
            ExplainSummary = "Type a tool name, for example mcphub__install.";
            return;
        }

        foreach (var denial in _authorization.ExplainAll(new TenantContext(row.Id), ServerKeyOf(tool), tool))
        {
            Denials.Add(new PermissionsDenialRow(denial));
        }

        OnPropertyChanged(nameof(HasDenials));
        ExplainSummary = Denials.Count == 0
            ? $"'{row.Name}' can use {tool}."
            : $"'{row.Name}' cannot use {tool} — {Denials.Count} reason(s), all of which need fixing.";
    });

    /// <summary>The server a namespaced tool belongs to; a bare name is its own key, which is what
    /// somebody typing one means.</summary>
    private static string ServerKeyOf(string tool)
    {
        var at = tool.IndexOf(ProxyConstants.NamespaceSeparator, StringComparison.Ordinal);
        return at > 0 ? tool[..at] : tool;
    }

    /// <summary>Rebuilds the list and puts the selection back where it was, so saving a grant does not
    /// close the editor the operator is still working in.</summary>
    private void Reselect(string id)
    {
        Refresh();
        SelectedUser = Rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
    }

    private void Refresh()
    {
        Rows.Clear();
        foreach (var user in _users.Snapshot.Users)
        {
            Rows.Add(new GrantRow(user, _permissions.GrantsFor(user.Id)));
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasNoRows));
        OnPropertyChanged(nameof(ConfigurationError));
        OnPropertyChanged(nameof(HasConfigurationError));
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            StatusMessage = ex is ArgumentException or InvalidOperationException
                ? ex.Message
                : "The change could not be saved. Check the settings folder's permissions and available disk space.";
        }
    }
}
