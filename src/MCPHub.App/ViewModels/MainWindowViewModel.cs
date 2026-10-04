using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MCPHub.App.Messages;

namespace MCPHub.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly NavItem _logsNav;
    private readonly LogsViewModel _logs;

    [ObservableProperty]
    private NavItem _selectedNav;

    [ObservableProperty]
    private ViewModelBase _currentPage;

    /// <summary>
    /// Why a tray Restart did not happen, shown as a banner. Empty the rest of the time — a restart that
    /// works is self-evident, and one that silently does not would leave the user waiting for a new window.
    /// </summary>
    [ObservableProperty]
    private string? _restartFailure;

    public bool HasRestartFailure => !string.IsNullOrWhiteSpace(RestartFailure);

    public ObservableCollection<NavItem> NavItems { get; }

    public MainWindowViewModel(ServicesViewModel services, AgentViewModel agent, SlopworksViewModel slopworks, LogsViewModel logs, ProxyViewModel proxy, RouterViewModel router, DiagnosticsViewModel diagnostics, RecipesViewModel recipes, UsersViewModel users, PermissionsViewModel permissions, SettingsViewModel settings, UpdatesViewModel updates)
    {
        _logs = logs;
        _logsNav = new NavItem("Logs", logs);

        NavItems =
        [
            new NavItem("Services", services),
            new NavItem("Agent", agent),
            new NavItem("Engine", slopworks),
            _logsNav,
            new NavItem("Proxy", proxy),
            new NavItem("Diagnostics", diagnostics),
            new NavItem("Recipes", recipes),
            // The three pages about users, in the order somebody uses them: make a user, let it reach
            // models, let it reach tools. Users first, because the other two can only name one.
            new NavItem("Users", users),
            new NavItem("Router", router),
            new NavItem("Permissions", permissions),
            new NavItem("Settings", settings),
            new NavItem("Updates", updates),
        ];

        _selectedNav = NavItems[0];
        _currentPage = _selectedNav.Page;

        // A service row's "Logs" action switches here and focuses that service.
        WeakReferenceMessenger.Default.Register<ShowLogsMessage>(this, (_, message) =>
        {
            _logs.SelectService(message.ServiceName);
            SelectedNav = _logsNav;
        });

        // "Add a user" on the Router and Permissions pages: both have controls that need a user before
        // they can do anything, so they send somebody there rather than naming the page in prose.
        WeakReferenceMessenger.Default.Register<ShowPageMessage>(this, (_, message) =>
        {
            if (NavItems.FirstOrDefault(n => string.Equals(n.Title, message.Title, StringComparison.Ordinal)) is { } nav)
                SelectedNav = nav;
        });
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        if (value is not null)
            CurrentPage = value.Page;
    }

    partial void OnRestartFailureChanged(string? value) => OnPropertyChanged(nameof(HasRestartFailure));

    /// <summary>Surfaces a failed tray Restart on the window the user is being handed back.</summary>
    public void ReportRestartFailure(string message) => RestartFailure = message;

    /// <summary>Dismisses the restart banner.</summary>
    [RelayCommand]
    private void DismissRestartFailure() => RestartFailure = null;
}
