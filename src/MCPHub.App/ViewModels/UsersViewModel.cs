using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly ISettingsStore _settings;
    private readonly AdministrationPolicy _administration;

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
        UserStore users, PermissionsStore permissions, ISettingsStore settings, AdministrationPolicy administration)
    {
        _users = users;
        _permissions = permissions;
        _settings = settings;
        _administration = administration;
        Refresh();
    }

    public ObservableCollection<UserRow> Users { get; } = [];

    public bool HasUsers => Users.Count > 0;

    public bool HasNoUsers => Users.Count == 0;

    public bool HasUserSelected => SelectedUser is not null;

    public bool HasGeneratedKey => GeneratedKey.Length > 0;

    public string EditorTitle => SelectedUser is { } row ? $"Edit user: {row.Name}" : "Add user";

    public string SaveText => HasUserSelected ? "Save changes" : "Add user and generate key";

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
        ? $"{Users.Count} user(s) exist, but keys are not being required — so every caller is treated as "
          + "the single user and gets every tool. Turn on \"Require a key\" to make these users mean anything."
        : null;

    public bool HasWarning => Warning is not null;

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
    partial void OnKeysEnforcedChanged(bool value) => Run(() =>
    {
        if (_loading || _permissions.Snapshot.AllowUnauthenticated == !value)
        {
            return;
        }

        _permissions.SetAllowUnauthenticated(!value);
        RefreshWarning();
        StatusMessage = value
            ? "Keys are now required. A caller with no key is refused on every surface."
            : "Keys are no longer required. Every caller is served as the single user.";
    });

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
            ? "Callers granted the administration tools can now manage users and grants."
            : "The users__ and permissions__ tools are switched off for every caller.";
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
            StatusMessage = $"'{name}' updated. Changes apply to its next call on every surface.";
        }
        else
        {
            var created = _users.Create(name);
            Cancel();
            Refresh();
            ShowKey(created.Key, name.Length > 0 ? name : created.User.Id);
            StatusMessage = $"'{name}' added. Copy its key before dismissing it — it cannot be recovered.";
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
        StatusMessage = "Key rotated. The previous key stopped working everywhere the moment this was issued.";
    });

    [RelayCommand]
    private void Remove() => Run(() =>
    {
        if (SelectedUser is not { } row)
        {
            return;
        }

        _users.Delete(row.Id);

        // Grants go with the user, so nothing is left granting tools to an id nobody holds.
        _permissions.ForgetUser(row.Id);
        Cancel();
        DismissKey();
        Refresh();
        StatusMessage = $"'{row.Name}' removed, its key retired and its tool grants dropped.";
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
        GeneratedKeyNotice = $"New key for {name}. Copy it now; only its hash is stored, so it cannot be "
                             + "recovered. It works everywhere this hub checks a key.";
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
