using System.Collections.ObjectModel;
using MCPHub.Core.Catalog;

namespace MCPHub.App.ViewModels;

/// <summary>Lists the managed MCP servers (the Wixely MCPSharp catalog).</summary>
public sealed class ServicesViewModel : ViewModelBase
{
    public ObservableCollection<ManagedServiceViewModel> Services { get; }

    public ServicesViewModel()
    {
        Services = new ObservableCollection<ManagedServiceViewModel>(
            ServiceCatalog.All.Select(static e => new ManagedServiceViewModel(e)));
    }
}
