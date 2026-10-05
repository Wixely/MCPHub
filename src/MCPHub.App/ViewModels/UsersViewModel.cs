using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.App.Infrastructure;
using MCPHub.Core.Permissions;
using MCPHub.Core.Settings;
using MCPHub.Core.Users;

namespace MCPHub.App.ViewModels;

/// <summary>One user as the list shows it.</summary>
public sealed class UserRow(HubUser user)
{
    public HubUser User { get; } = user;

    public string Id => User.Id;

    public string Name { get; } = user.Name.Length > 0 ? user.Name : "(unnamed)";

    public string State { get; } = user.Enabled ? "Enabled" : "Suspended";

    /// <summary>The hash's first characters, so two keys can be told apart in a conversation without
    /// the list being something worth stealing.</summary>
    public string KeyFingerprint { get; } = user.KeyHash.Length >= 8 ? user.KeyHash[..8] : user.KeyHash;
}

/// <summary>
/// Who may reach this hub.
///
/// <para>Identity only, and that is why the page exists: every surface that needs to know who is
/// calling used to set up its own callers with its own keys, so an agent wanting a model route and a
/// tool needed two of everything and could be suspended in one place while still working in the other.
/// A user created here is selected elsewhere, and one switch stops it everywhere at once.</para>
///
/// <para>What a user may actually <em>do</em> is on the Permissions page. The two switches here belong
/// to identity rather than to grants: whether keys are checked at all, and whether another application
/// may administer this hub through the proxy.</para>
/// </summary>
public sealed partial class UsersViewModel : ViewModelBase
{
    private readonly UserStore _users;
    private readonly PermissionsStore _permissions;
    private readonly IReadOnlyList<IUserDependent> _dependents;
    private readonly ISettingsStore _settings;
    private readonly AdministrationPolicy _administration;
    private readonly IConfirmation _confirm;

    /// <summary>True while Refresh loads the switches, so their change handlers do not write back what
    /// they have just read. The same guard SettingsViewModel uses.</summary>
    private bool _loading;

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _keysEnforced;
    [ObservableProperty] private bool _administerThroughProxy;
    [ObservableProperty] private UserRow? _selectedUser;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private bool _userEnabled = true;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private string _generatedKey = string.Empty;
    [ObservableProperty] private string _generatedKeyNotice = string.Empty;

    public UsersViewModel(
        UserStore users,
        PermissionsStore permissions,
        IEnumerable<IUserDependent> dependents,
        ISettingsStore settings,
        AdministrationPolicy administration,
        IConfirmation confirm)
    {
        _users = users;
        _permissions = permissions;
        _dependents = [.. dependents];
        _settings = settings;
        _administration = administration;
        _confirm = confirm;
        Refresh();
    }

    public ObservableCollection<UserRow> Users { get; } = [];

    public bool HasUsers => Users.Count > 0;

    public bool HasNoUsers => Users.Count == 0;

    public bool HasUserSelected => SelectedUser is not null;

    public bool HasGeneratedKey => GeneratedKey.Length > 0;

    public string EditorTitle => SelectedUser is { } row ? $"Edit user: {row.Name}" : "Add user";

    public string SaveText => HasUserSelected ? "Save changes" : "Add user";

    /// <summary>Why the stored directory could not be read, if it could not. No key is recognised while
    /// this is set and every save throws, so the page says so outright — otherwise Add looks broken.</summary>
    public string? ConfigurationError => _users.LoadError;

    public bool HasConfigurationError => ConfigurationError is not null;

    /// <summary>
    /// The state worth shouting about: users exist and keys are not being checked, so every caller is
    /// served as the single user and gets every tool regardless. Legitimate while setting a hub up,
    /// never somewhere to stay — and invisible from the list, which shows users looking configured.
    /// </summary>
    public string? Warning => !KeysEnforced && Users.Count > 0
        ? "Everyone is allowed, so anything reaching this hub gets every tool whether it is a user or not. "
          + "Choose per-user permissions to make these users count."
        : null;

    public bool HasWarning => Warning is not null;

    /// <summary>What the chosen mode means, under the two buttons, so the consequence is on the page
    /// rather than only in the dialog that appears when it changes.</summary>
    public string AccessSummary => KeysEnforced
        ? "Every program needs a user's key. Each gets only what it is given on Router and Permissions."
        : "No key is needed. Anything that can reach this hub gets every tool.";

    /// <summary>Set when an environment variable pins the administration switch, so the checkbox is
    /// shown locked rather than appearing not to save.</summary>
    public string? AdministrationPinnedBy => _administration.OverrideSource;

    public bool IsAdministrationPinned => AdministrationPinnedBy is not null;

    public bool CanEditAdministration => !IsAdministrationPinned;

    partial void OnSelectedUserChanged(UserRow? value)
    {
        OnPropertyChanged(nameof(HasUserSelected));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(SaveText));
        if (value is { } row)
        {
            UserName = row.User.Name;
            UserEnabled = row.User.Enabled;
            IsEditorOpen = true;
        }
    }

    partial void OnGeneratedKeyChanged(string value) => OnPropertyChanged(nameof(HasGeneratedKey));

    /// <summary>
    /// Applied at once rather than on a Save button, like the agent-management switches: the proxy asks
    /// per request, so there is nothing to apply later and a checkbox needing a save would be one that
    /// lies in between.
    /// </summary>
    /// <summary>
    /// The other half of the choice, so the two radio buttons can each bind to a property of their own
    /// rather than to one negated binding that only works in one direction.
    /// </summary>
    public bool IsOpenAccess
    {
        get => !KeysEnforced;
        set
        {
            if (value)
            {
                KeysEnforced = false;
            }
        }
    }

    /// <summary>
    /// Changing how the hub decides who gets in, after asking.
    ///
    /// <para>Asked rather than applied because there is no halfway state: turning permissions on
    /// refuses every program that has not been given a key, and turning them off hands every tool to
    /// anything that can reach the hub. Both are worth a sentence before they happen, and the radio
    /// goes back where it was if the answer is no.</para>
    /// </summary>
    partial void OnKeysEnforcedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOpenAccess));
        OnPropertyChanged(nameof(AccessSummary));
        if (_loading || _permissions.Snapshot.AllowUnauthenticated == !value)
        {
            return;
        }

        _ = ApplyAccessModeAsync(value);
    }

    private async Task ApplyAccessModeAsync(bool enforced)
    {
        if (!await _confirm.AskAsync(enforced ? EnforceKeysQuestion() : AllowEveryoneQuestion()))
        {
            _loading = true;
            KeysEnforced = !enforced;
            _loading = false;
            StatusMessage = "Left as it was.";
            return;
        }

        Run(() =>
        {
            _permissions.SetAllowUnauthenticated(!enforced);
            RefreshWarning();
            StatusMessage = enforced
                ? "Per-user permissions are on. Every program needs a key now."
                : "Everyone is allowed. Any program reaching this hub gets every tool.";
        });
    }

    /// <summary>
    /// The warning that matters: there is no partial mode, so anything not set up as a user stops
    /// working the moment this is on. Says outright when that means everything.
    /// </summary>
    private ConfirmRequest EnforceKeysQuestion()
    {
        var consequence = Users.Count == 0
            ? "There are no users yet, so nothing will be able to reach this hub until you add one and copy its key into that program's MCP configuration."
            : $"Anything not set up as one of these {Users.Count} user(s), with that user's key in its MCP configuration, stops working immediately.";

        return new ConfirmRequest(
            "Turn on per-user permissions?",
            "Every program using this hub must then authenticate with a user's key — the proxy and the "
            + "Model Router alike. There is no partial mode: no-key access is not available while this is on."
            + Environment.NewLine + Environment.NewLine
            + consequence,
            "Turn it on",
            "Leave it off");
    }

    private static ConfirmRequest AllowEveryoneQuestion() => new(
        "Allow everyone again?",
        "Any program that can reach this hub will get every tool, with no key at all."
        + Environment.NewLine + Environment.NewLine
        + "Your users and everything they have been given are kept, but none of it is checked while this is on.",
        "Allow everyone",
        "Keep permissions on");

    partial void OnAdministerThroughProxyChanged(bool value) => Run(() =>
    {
        if (_loading || IsAdministrationPinned || _settings.Current.AdministrationEnabled == value)
        {
            return;
        }

        _settings.Current.AdministrationEnabled = value;

        // Fire and forget, as the agent-management switches do: the proxy reads the live setting on
        // every request, so the switch is already in force and the write is only so it survives a
        // restart.
        _ = _settings.SaveAsync();
        StatusMessage = value
            ? "Users granted the users__ and permissions__ tools can now manage this hub."
            : "The users__ and permissions__ tools are switched off.";
    });

    [RelayCommand]
    private void AddUser()
    {
        SelectedUser = null;
        UserName = string.Empty;
        UserEnabled = true;
        IsEditorOpen = true;
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(SaveText));
    }

    [RelayCommand]
    private void Save() => Run(() =>
    {
        if (!IsEditorOpen)
        {
            return;
        }

        var name = UserName.Trim();
        if (SelectedUser is { } row)
        {
            _users.Rename(row.Id, name);
            _users.SetEnabled(row.Id, UserEnabled);
            Cancel();
            Refresh();
            StatusMessage = $"'{name}' saved. Applies to its next request.";
        }
        else
        {
            var created = _users.Create(name);
            Cancel();
            Refresh();
            ShowKey(created.Key, name.Length > 0 ? name : created.User.Id);
            StatusMessage = $"'{name}' added. Copy its key now — it is shown once.";
        }
    });

    [RelayCommand]
    private void RotateKey() => Run(() =>
    {
        if (SelectedUser is not { } row)
        {
            return;
        }

        var key = _users.RotateKey(row.Id);
        Refresh();
        ShowKey(key, row.Name);
        StatusMessage = "New key issued. The old one stopped working.";
    });

    [RelayCommand]
    private void Remove() => Run(() =>
    {
        if (SelectedUser is not { } row)
        {
            return;
        }

        _users.Delete(row.Id);

        // Grants and the Router route go with the user, so nothing is left naming an id nobody holds.
        // Each layer drops its own entry, which is why this is a loop rather than two calls by name.
        foreach (var dependent in _dependents)
        {
            dependent.ForgetUser(row.Id);
        }
        Cancel();
        DismissKey();
        Refresh();
        StatusMessage = $"'{row.Name}' removed, with its key, its models and its tools.";
    });

    [RelayCommand]
    private void Cancel()
    {
        IsEditorOpen = false;
        SelectedUser = null;
        UserName = string.Empty;
        UserEnabled = true;
    }

    [RelayCommand]
    private void DismissKey()
    {
        GeneratedKey = string.Empty;
        GeneratedKeyNotice = string.Empty;
    }

    private void ShowKey(string key, string name)
    {
        GeneratedKey = key;
        GeneratedKeyNotice = $"Key for {name}. Shown once — copy it now.";
    }

    private void Refresh()
    {
        Users.Clear();
        foreach (var user in _users.Snapshot.Users)
        {
            Users.Add(new UserRow(user));
        }

        _loading = true;
        try
        {
            KeysEnforced = !_permissions.Snapshot.AllowUnauthenticated;
            AdministerThroughProxy = _administration.AdministrationEnabled;
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(HasUsers));
        OnPropertyChanged(nameof(HasNoUsers));
        OnPropertyChanged(nameof(ConfigurationError));
        OnPropertyChanged(nameof(HasConfigurationError));
        RefreshWarning();
    }

    private void RefreshWarning()
    {
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(HasWarning));
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            StatusMessage = ex is ArgumentException or InvalidOperationException or UserNotFoundException
                ? ex.Message
                : "The change could not be saved. Check the settings folder's permissions and available disk space.";
        }
    }
}
