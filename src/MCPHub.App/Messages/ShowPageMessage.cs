namespace MCPHub.App.Messages;

/// <summary>
/// Sent by a page that needs the user somewhere else — the Router and Permissions pages both have
/// controls that can only be used once a user exists, and the answer to "where do I make one" should
/// be a button rather than a sentence telling somebody to go and find a page.
/// </summary>
/// <param name="Title">The nav item's title, as <see cref="MCPHub.App.ViewModels.NavItem.Title"/> spells it.</param>
public sealed record ShowPageMessage(string Title)
{
    /// <summary>Where users are created, which is the only page anything needs to send somebody to today.</summary>
    public static ShowPageMessage Users { get; } = new("Users");
}
