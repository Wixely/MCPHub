using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MCPHub.App.ViewModels;

namespace MCPHub.App.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _viewModel;
    private ScrollViewer? _scroll;

    // Whether new lines should keep the view pinned to the newest entry. Follows the user: true while
    // they're at the bottom, false once they scroll up to read history — standard log-tailing behaviour.
    private bool _stickToBottom = true;

    // Slack (px) for treating the view as "at the bottom" so a partially-visible last line still counts.
    private const double BottomThreshold = 12;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        LogList.Loaded += OnLogListLoaded;
    }

    private void OnLogListLoaded(object? sender, RoutedEventArgs e)
    {
        _scroll = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is not null)
            _scroll.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null)
            return;

        // New log lines grow the extent without moving the offset; that must not turn following off.
        // Only a real user scroll (offset/viewport change) re-evaluates whether we're at the bottom.
        if (e.OffsetDelta.Y == 0 && e.ViewportDelta.Y == 0)
            return;

        _stickToBottom = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - BottomThreshold;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LinesChanged -= OnLinesChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as LogsViewModel;

        if (_viewModel is not null)
        {
            _viewModel.LinesChanged += OnLinesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Switching services shows a different log — jump to and follow its newest line.
        if (e.PropertyName == nameof(LogsViewModel.SelectedService))
            _stickToBottom = true;
    }

    private void OnLinesChanged()
    {
        if (_stickToBottom && _viewModel is { Lines.Count: > 0 } vm)
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
