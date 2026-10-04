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

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private GrantRow? _selectedUser;
    [ObservableProperty] private string _grants = string.Empty;
    [ObservableProperty] private string _explainTool = string.Empty;
    [ObservableProperty] private string _explainSummary = string.Empty;

    public PermissionsViewModel(
        UserStore users, PermissionsStore permissions, CompositeToolAuthorization authorization)
    {
        _users = users;
        _permissions = permissions;
        _authorization = authorization;
        Refresh();
    }

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
        Grants = value is null ? string.Empty : string.Join(Environment.NewLine, value.Tools);
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

        var grants = ParseGrants(Grants);
        _permissions.SetGrants(row.Id, grants);
        Reselect(row.Id);
        StatusMessage = $"'{row.Name}' may now use {grants.Length} tool(s). Applies to its next request.";
    });

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

    /// <summary>One grant per line, trimmed, blanks dropped — a text box rather than a grid because a
    /// grant is a short string and an operator pasting five of them should not click five times.</summary>
    private static string[] ParseGrants(string text) =>
    [
        .. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    ];

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
