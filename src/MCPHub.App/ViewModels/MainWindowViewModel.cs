using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public ObservableCollection<NavItem> NavItems { get; }

    public MainWindowViewModel(ServicesViewModel services, LogsViewModel logs, ProxyViewModel proxy, SettingsViewModel settings)
    {
        _logs = logs;
        _logsNav = new NavItem("Logs", logs);

        NavItems =
        [
            new NavItem("Services", services),
            _logsNav,
            new NavItem("Proxy", proxy),
            new NavItem("Settings", settings),
        ];

        _selectedNav = NavItems[0];
        _currentPage = _selectedNav.Page;

        // A service row's "Logs" action switches here and focuses that service.
        WeakReferenceMessenger.Default.Register<ShowLogsMessage>(this, (_, message) =>
        {
            _logs.SelectService(message.ServiceName);
            SelectedNav = _logsNav;
        });
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        if (value is not null)
            CurrentPage = value.Page;
    }
}
