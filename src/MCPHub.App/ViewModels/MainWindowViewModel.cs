using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MCPHub.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private NavItem _selectedNav;

    [ObservableProperty]
    private ViewModelBase _currentPage;

    public ObservableCollection<NavItem> NavItems { get; }

    public MainWindowViewModel(ServicesViewModel services)
    {
        NavItems =
        [
            new NavItem("Services", services),
            new NavItem("Proxy", new PlaceholderViewModel(
                "Proxy / Aggregator",
                "The single aggregated MCP endpoint (http://localhost:5800/mcp) arrives in milestone M4.")),
            new NavItem("Settings", new PlaceholderViewModel(
                "Settings",
                "Shared servers folder, download flavour, proxy port, tray behaviour and more arrive in milestone M5.")),
        ];

        _selectedNav = NavItems[0];
        _currentPage = _selectedNav.Page;
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        if (value is not null)
            CurrentPage = value.Page;
    }
}
