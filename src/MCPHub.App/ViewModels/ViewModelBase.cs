using CommunityToolkit.Mvvm.ComponentModel;

namespace MCPHub.App.ViewModels;

/// <summary>Base class for all view-models; provides INotifyPropertyChanged via the MVVM toolkit.</summary>
public abstract class ViewModelBase : ObservableObject;
