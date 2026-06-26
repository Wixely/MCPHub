using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MCPHub.App.ViewModels;

namespace MCPHub.App;

/// <summary>
/// Resolves a view-model instance to its matching view by convention
/// (<c>…ViewModels.FooViewModel</c> → <c>…Views.FooView</c>).
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        return type is not null
            ? (Control)Activator.CreateInstance(type)!
            : new TextBlock { Text = "View not found: " + name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
