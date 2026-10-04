using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Permissions;
using MCPHub.Core.Settings;
using MCPHub.Proxy;

namespace MCPHub.App.ViewModels;

/// <summary>One principal as the list shows it.</summary>
public sealed class PermissionsPrincipalRow
{
    public PermissionsPrincipalRow(PermissionsPrincipal principal)
    {
        Principal = principal;
        Name = principal.Name.Length > 0 ? principal.Name : "(unnamed)";
        State = principal.Enabled ? "Enabled" : "Suspended";
        GrantSummary = principal.Tools.Length == 0
            ? "No tools"
            : principal.Tools.Contains(PermissionsConfigurationRules.EverythingGrant)
                ? "Every tool"
                : string.Join(", ", principal.Tools);

        // The hash's first characters, so two keys can be told apart in a conversation without the
        // list being something worth stealing.
        KeyFingerprint = principal.KeyHash.Length >= 8 ? principal.KeyHash[..8] : principal.KeyHash;
    }

    public PermissionsPrincipal Principal { get; }

    public string Id => Principal.Id;

    public string Name { get; }

    public string State { get; }

    public string GrantSummary { get; }

    public string KeyFingerprint { get; }
}

/// <summary>One reason a tool is unavailable, as the Explain panel shows it.</summary>
public sealed class PermissionsDenialRow
{
    public PermissionsDenialRow(ToolDenial denial)
    {
        Code = denial.Code;
        Reason = denial.Reason;
        Remedy = denial.Remedy ?? string.Empty;
        PinnedBy = denial.PinnedBy ?? string.Empty;
    }

    public string Code { get; }

    public string Reason { get; }

    public string Remedy { get; }

    /// <summary>The environment variable forcing this, when one is. Shown prominently because it is the
    /// reason a checkbox will not stay where it is put.</summary>
    public string PinnedBy { get; }

    public bool HasRemedy => Remedy.Length > 0;

    public bool IsPinned => PinnedBy.Length > 0;
}

/// <summary>
/// Who may use the proxy, and which of its tools.
///
/// <para>Shaped after the Router page, which solves the same problems: a list of keyed callers, an
/// editor beside it, and a key that can be shown exactly once. The one thing here that page does not
/// need is <b>Explain</b> — tool access is decided by several independent policies at once, so "the
/// tool is missing" has more than one possible cause and an operator cannot see them from a list.</para>
/// </summary>
public sealed partial class PermissionsViewModel : ViewModelBase
{
    private readonly PermissionsStore _store;
    private readonly ISettingsStore _settings;
    private readonly PermissionsManagementPolicy _managementPolicy;
    private readonly CompositeToolAuthorization _authorization;

    /// <summary>True while Refresh is loading the switches, so their change handlers do not write back
    /// what they have just read. The same guard SettingsViewModel uses, for the same reason.</summary>
    private bool _loading;

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _keysEnforced;
    [ObservableProperty] private bool _manageThroughProxy;
    [ObservableProperty] private PermissionsPrincipalRow? _selectedPrincipal;
    [ObservableProperty] private string _principalName = string.Empty;
    [ObservableProperty] private bool _principalEnabled = true;
    [ObservableProperty] private string _principalGrants = string.Empty;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private string _generatedKey = string.Empty;
    [ObservableProperty] private string _generatedKeyNotice = string.Empty;
    [ObservableProperty] private string _explainTool = string.Empty;
    [ObservableProperty] private string _explainSummary = string.Empty;

    public PermissionsViewModel(
        PermissionsStore store,
        ISettingsStore settings,
        PermissionsManagementPolicy managementPolicy,
        CompositeToolAuthorization authorization)
    {
        _store = store;
        _settings = settings;
        _managementPolicy = managementPolicy;
        _authorization = authorization;
        Refresh();
    }

    public ObservableCollection<PermissionsPrincipalRow> Principals { get; } = [];

    public ObservableCollection<PermissionsDenialRow> Denials { get; } = [];

    public bool HasPrincipals => Principals.Count > 0;

    public bool HasNoPrincipals => Principals.Count == 0;

    public bool HasPrincipalSelected => SelectedPrincipal is not null;

    public bool HasGeneratedKey => GeneratedKey.Length > 0;

    public bool HasDenials => Denials.Count > 0;

    public bool HasExplanation => ExplainSummary.Length > 0;

    public string EditorTitle =>
        SelectedPrincipal is { } row ? $"Edit principal: {row.Name}" : "Add principal";

    public string SaveText => HasPrincipalSelected ? "Save changes" : "Add principal and generate key";

    /// <summary>
    /// Why the stored document could not be read, if it could not. While this is set nothing is
    /// recognised and every save throws, so the page has to say so outright — otherwise Add looks
    /// simply broken.
    /// </summary>
    public string? ConfigurationError => _store.LoadError;

    public bool HasConfigurationError => ConfigurationError is not null;

    /// <summary>
    /// The state worth shouting about: principals exist and keys are not being checked, so every
    /// caller is served as the single-user tenant and no grant has any effect. Legitimate to pass
    /// through while setting a hub up, never somewhere to stay — and invisible from the list alone,
    /// which shows principals looking perfectly configured.
    /// </summary>
    public string? Warning => !KeysEnforced && Principals.Count > 0
        ? $"{Principals.Count} principal(s) are configured, but keys are not being enforced — so every "
          + "caller is treated as the single user and gets every tool. Turn on \"Require a key\" to make "
          + "these grants mean anything."
        : null;

    public bool HasWarning => Warning is not null;

    /// <summary>Set when an environment variable is pinning the proxy-management switch, so the
    /// checkbox is shown locked rather than appearing not to save.</summary>
    public string? ManageThroughProxyPinnedBy => _managementPolicy.OverrideSource;

    public bool IsManageThroughProxyPinned => ManageThroughProxyPinnedBy is not null;

    public bool CanEditManageThroughProxy => !IsManageThroughProxyPinned;

    partial void OnSelectedPrincipalChanged(PermissionsPrincipalRow? value)
    {
        OnPropertyChanged(nameof(HasPrincipalSelected));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(SaveText));
        if (value is { } row)
        {
            PrincipalName = row.Principal.Name;
            PrincipalEnabled = row.Principal.Enabled;
            PrincipalGrants = string.Join(Environment.NewLine, row.Principal.Tools);
            IsEditorOpen = true;
        }
    }

    partial void OnGeneratedKeyChanged(string value) => OnPropertyChanged(nameof(HasGeneratedKey));

    partial void OnExplainSummaryChanged(string value) => OnPropertyChanged(nameof(HasExplanation));

    /// <summary>
    /// Applied immediately rather than on a Save button, like the agent-management switches: the
    /// proxy asks per request, so there is nothing to apply later and a checkbox that needed saving
    /// would be a checkbox that lies between the click and the save.
    /// </summary>
    partial void OnKeysEnforcedChanged(bool value) => Run(() =>
    {
        if (_loading || _store.Snapshot.AllowUnauthenticated == !value)
        {
            return;
        }

        _store.SetAllowUnauthenticated(!value);
        RefreshWarning();
        StatusMessage = value
            ? "Keys are now required. A caller with no key is refused."
            : "Keys are no longer required. Every caller is served as the single user.";
    });

    partial void OnManageThroughProxyChanged(bool value) => Run(() =>
    {
        if (_loading || IsManageThroughProxyPinned || _settings.Current.PermissionsManagementEnabled == value)
        {
            return;
        }

        _settings.Current.PermissionsManagementEnabled = value;

        // Fire and forget, as the agent-management switches do: the proxy reads the live setting on
        // every request, so the switch is already in force and the write is only so it survives a
        // restart.
        _ = _settings.SaveAsync();
        StatusMessage = value
            ? "Callers granted the permissions tools can now read and edit this policy."
            : "The permissions tools are switched off for every caller.";
    });

    [RelayCommand]
    private void AddPrincipal()
    {
        SelectedPrincipal = null;
        PrincipalName = string.Empty;
        PrincipalEnabled = true;
        PrincipalGrants = string.Empty;
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

        var grants = ParseGrants(PrincipalGrants);
        if (SelectedPrincipal is { } row)
        {
            _store.SetGrants(row.Id, grants);
            _store.SetEnabled(row.Id, PrincipalEnabled);
            var name = PrincipalName.Trim();
            Cancel();
            Refresh();
            StatusMessage = $"'{name}' updated. Changes apply to its next call, including one already connected.";
        }
        else
        {
            var name = PrincipalName.Trim();
            var created = _store.CreatePrincipal(name, grants);
            Cancel();
            Refresh();
            ShowKey(created.Key, name.Length > 0 ? name : created.Principal.Id);
            StatusMessage = $"'{name}' added. Copy its key before dismissing it — it cannot be recovered.";
        }
    });

    [RelayCommand]
    private void RotateKey() => Run(() =>
    {
        if (SelectedPrincipal is not { } row)
        {
            return;
        }

        var key = _store.RotateKey(row.Id);
        Refresh();
        ShowKey(key, row.Name);
        StatusMessage = "Key rotated. The previous key stopped working the moment this one was issued.";
    });

    [RelayCommand]
    private void Remove() => Run(() =>
    {
        if (SelectedPrincipal is not { } row)
        {
            return;
        }

        _store.DeletePrincipal(row.Id);
        Cancel();
        DismissKey();
        Refresh();
        StatusMessage = $"'{row.Name}' removed and its key retired.";
    });

    [RelayCommand]
    private void Cancel()
    {
        IsEditorOpen = false;
        SelectedPrincipal = null;
        PrincipalName = string.Empty;
        PrincipalEnabled = true;
        PrincipalGrants = string.Empty;
    }

    [RelayCommand]
    private void DismissKey()
    {
        GeneratedKey = string.Empty;
        GeneratedKeyNotice = string.Empty;
    }

    /// <summary>
    /// Why the selected principal can or cannot use a named tool.
    ///
    /// <para>The page's reason for existing. Tool access is an AND over independent policies — this
    /// principal's grants, whether agent management is on, whether a capability under it is on,
    /// whether an environment variable is pinning any of them — so a tool can be withheld for several
    /// reasons at once. A list of grants cannot show that, and neither can a single yes or no: an
    /// operator who grants the tool and sees no change concludes the grant failed, when a switch was
    /// also off all along. So every reason is listed, each with what to change.</para>
    /// </summary>
    [RelayCommand]
    private void Explain() => Run(() =>
    {
        Denials.Clear();
        OnPropertyChanged(nameof(HasDenials));

        if (SelectedPrincipal is not { } row)
        {
            ExplainSummary = "Select a principal first.";
            return;
        }

        var tool = ExplainTool.Trim();
        if (tool.Length == 0)
        {
            ExplainSummary = "Name a tool, as the proxy advertises it — for example mcphub__install.";
            return;
        }

        var tenant = new TenantContext(row.Id);
        var serverKey = ServerKeyOf(tool);
        foreach (var denial in _authorization.ExplainAll(tenant, serverKey, tool))
        {
            Denials.Add(new PermissionsDenialRow(denial));
        }

        OnPropertyChanged(nameof(HasDenials));
        ExplainSummary = Denials.Count == 0
            ? $"'{row.Name}' can use {tool}."
            : $"'{row.Name}' cannot use {tool}. {Denials.Count} reason(s) — all of them have to be fixed.";
    });

    /// <summary>The server a namespaced tool belongs to; a bare name is its own key, which is what
    /// somebody typing one means.</summary>
    private static string ServerKeyOf(string tool)
    {
        var at = tool.IndexOf(ProxyConstants.NamespaceSeparator, StringComparison.Ordinal);
        return at > 0 ? tool[..at] : tool;
    }

    /// <summary>One grant per line, trimmed, blanks dropped — a text box rather than a grid because a
    /// grant is a short string and an operator pasting five of them should not have to click five
    /// times.</summary>
    private static string[] ParseGrants(string text) =>
    [
        .. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    ];

    private void ShowKey(string key, string name)
    {
        GeneratedKey = key;
        GeneratedKeyNotice = $"New key for {name}. Copy it now; only its hash is stored, so it cannot be recovered.";
    }

    private void Refresh()
    {
        var snapshot = _store.Snapshot;
        Principals.Clear();
        foreach (var principal in snapshot.Principals)
        {
            Principals.Add(new PermissionsPrincipalRow(principal));
        }

        // Guarded rather than written through the backing fields: the handlers would otherwise persist
        // what they have just read, and the generated properties are what the view binds to.
        _loading = true;
        try
        {
            KeysEnforced = !snapshot.AllowUnauthenticated;
            ManageThroughProxy = _managementPolicy.ManagementEnabled;
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(HasPrincipals));
        OnPropertyChanged(nameof(HasNoPrincipals));
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
            StatusMessage = ex is ArgumentException or InvalidOperationException or PermissionsNotFoundException
                ? ex.Message
                : "The change could not be saved. Check the settings folder's permissions and available disk space.";
        }
    }
}
