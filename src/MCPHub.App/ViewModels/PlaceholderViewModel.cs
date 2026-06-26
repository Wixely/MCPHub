namespace MCPHub.App.ViewModels;

/// <summary>Simple page shown for features that arrive in a later milestone.</summary>
public sealed class PlaceholderViewModel : ViewModelBase
{
    public PlaceholderViewModel(string title, string message)
    {
        Title = title;
        Message = message;
    }

    public string Title { get; }

    public string Message { get; }
}
