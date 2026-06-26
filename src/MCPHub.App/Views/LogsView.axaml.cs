using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MCPHub.App.ViewModels;

namespace MCPHub.App.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _viewModel;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.LinesChanged -= OnLinesChanged;

        _viewModel = DataContext as LogsViewModel;

        if (_viewModel is not null)
            _viewModel.LinesChanged += OnLinesChanged;
    }

    private void OnLinesChanged()
    {
        if (_viewModel is { AutoScroll: true, Lines.Count: > 0 } vm)
            Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(vm.Lines[^1]), DispatcherPriority.Background);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(_viewModel.BuildText());
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save log",
            SuggestedFileName = $"{_viewModel.SelectedService ?? "mcphub"}-log.txt",
            DefaultExtension = "txt",
        });

        if (file is null)
            return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(_viewModel.BuildText());
    }
}
