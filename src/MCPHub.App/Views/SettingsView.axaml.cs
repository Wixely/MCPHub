using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MCPHub.App.ViewModels;

namespace MCPHub.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose shared servers folder",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
            viewModel.SetFolder(path);
    }

    private static readonly FilePickerFileType ZipArchive = new("MCPHub settings archive")
    {
        Patterns = ["*.zip"],
        MimeTypes = ["application/zip"],
    };

    private async void OnExportSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export MCPHub settings",
            SuggestedFileName = SettingsViewModel.SuggestedArchiveName,
            DefaultExtension = "zip",
            FileTypeChoices = [ZipArchive],
            ShowOverwritePrompt = true,
        });

        if (file?.TryGetLocalPath() is { Length: > 0 } path)
            await viewModel.ExportToAsync(path);
    }

    private async void OnImportSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import MCPHub settings",
            AllowMultiple = false,
            FileTypeFilter = [ZipArchive],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } path)
            return;

        // Two steps on purpose: the tick boxes are re-pointed at what this archive actually holds, and the
        // user confirms the selection (and supplies a password) before anything is written.
        if (await viewModel.InspectArchiveAsync(path))
            await ConfirmImportAsync(viewModel, path);
    }

    private async Task ConfirmImportAsync(SettingsViewModel viewModel, string path)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var apply = new Button { Content = "Import", HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right };

        var dialog = new Window
        {
            Title = "Import settings",
            SizeToContent = SizeToContent.Height,
            Width = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = $"Import the ticked categories from {System.IO.Path.GetFileName(path)}? " +
                               "This replaces the matching settings in MCPHub — recipes are merged, everything else is overwritten.",
                    },
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Text = viewModel.ArchiveStatus ?? string.Empty },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, apply },
                    },
                },
            },
        };

        var confirmed = false;
        apply.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();

        if (confirmed)
            await viewModel.ImportFromAsync(path);
        else
            viewModel.ArchiveStatus = "Import cancelled. Nothing was changed.";
    }
}
