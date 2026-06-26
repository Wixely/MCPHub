namespace MCPHub.App.ViewModels;

/// <summary>A left-navigation entry pairing a label with the page shown when it is selected.</summary>
public sealed record NavItem(string Title, ViewModelBase Page);
