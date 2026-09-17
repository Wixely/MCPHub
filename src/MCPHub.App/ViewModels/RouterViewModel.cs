using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Routing;

namespace MCPHub.App.ViewModels;

public sealed record RouterChoice(string? Id, string Name);
public sealed record RouterOutputRow(string Name, string BaseUrl, string ModelSummary, IRelayCommand EditCommand);
public sealed record RouterInputRow(string Name, string RouteSummary, string State, IRelayCommand EditCommand);

public sealed partial class RouterViewModel : ViewModelBase
{
    private readonly RouterStore _store;
    private readonly RouterHost _host;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _port;
    [ObservableProperty] private bool _startOnLaunch;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private RouterChoice? _defaultOutput;
    [ObservableProperty] private RouterOutput? _selectedOutput;
    [ObservableProperty] private string _outputName = string.Empty;
    [ObservableProperty] private string _outputBaseUrl = string.Empty;
    [ObservableProperty] private string _outputModel = string.Empty;
    [ObservableProperty] private string _outputApiKey = string.Empty;
    [ObservableProperty] private bool _clearOutputKey;
    [ObservableProperty] private RouterInput? _selectedInput;
    [ObservableProperty] private string _inputName = string.Empty;
    [ObservableProperty] private bool _inputEnabled = true;
    [ObservableProperty] private RouterChoice? _inputOutput;
    [ObservableProperty] private string _generatedKey = string.Empty;
    [ObservableProperty] private string _generatedKeyNotice = string.Empty;
    [ObservableProperty] private bool _isOutputEditorOpen;
    [ObservableProperty] private bool _isInputEditorOpen;

    public ObservableCollection<RouterOutput> Outputs { get; } = [];
    public ObservableCollection<RouterInput> Inputs { get; } = [];
    public ObservableCollection<RouterOutputRow> OutputRows { get; } = [];
    public ObservableCollection<RouterInputRow> InputRows { get; } = [];
    public ObservableCollection<RouterChoice> DefaultChoices { get; } = [];
    public ObservableCollection<RouterChoice> InputChoices { get; } = [];
    public bool IsRunning => _host.IsRunning;
    public bool IsStopped => !IsRunning;
    public bool IsIdle => !IsBusy;
    public string EndpointUrl => _host.EndpointUrl;
    public string ToggleText => IsRunning ? "Stop router" : "Start router";
    public string RunState => _store.LoadError ?? _host.LastError ?? (IsRunning ? "Running" : "Stopped");
    public bool HasOutput => SelectedOutput is not null;
    public bool HasInput => SelectedInput is not null;
    public bool HasGeneratedKey => GeneratedKey.Length > 0;
    public bool HasNoOutputs => Outputs.Count == 0;
    public bool HasNoInputs => Inputs.Count == 0;
    public string OutputEditorTitle => SelectedOutput is { } output ? $"Edit output: {output.Name}" : "Add model output";
    public string InputEditorTitle => SelectedInput is { } input ? $"Edit agent: {input.Name}" : "Add agent";
    public string OutputSaveText => HasOutput ? "Save changes" : "Add output";
    public string InputSaveText => HasInput ? "Save changes" : "Add agent and generate key";
    public string SavedDefaultSummary => DescribeDefault(_store.Snapshot);
    public string InputRoutePreview
    {
        get
        {
            var config = _store.Snapshot;
            var output = config.Outputs.FirstOrDefault(o => o.Id == (InputOutput?.Id ?? config.DefaultOutputId));
            return output is null ? "No destination: choose an output or apply a global default before this agent can send requests."
                : $"Destination after saving: {output.Name}. " + ModelSummary(output);
        }
    }
    public string CredentialStatus => SelectedOutput?.ProtectedApiKey is not null ? "An upstream key is stored. Leave blank to keep it." : "No upstream key stored. Leave blank for an unauthenticated local model.";

    public RouterViewModel(RouterStore store, RouterHost host)
    {
        _store = store;
        _host = host;
        var config = store.Snapshot;
        Port = config.Port;
        StartOnLaunch = config.StartOnLaunch;
        Refresh();
        if (store.LoadError is { } error) StatusMessage = error;
    }

    public void RefreshHostState()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(EndpointUrl));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(RunState));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnGeneratedKeyChanged(string value) => OnPropertyChanged(nameof(HasGeneratedKey));
    partial void OnSelectedOutputChanged(RouterOutput? value)
    {
        OutputName = value?.Name ?? string.Empty;
        OutputBaseUrl = value?.BaseUrl ?? string.Empty;
        OutputModel = value?.Model ?? string.Empty;
        OutputApiKey = string.Empty;
        ClearOutputKey = false;
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(CredentialStatus));
        OnPropertyChanged(nameof(OutputEditorTitle));
        OnPropertyChanged(nameof(OutputSaveText));
    }
    partial void OnSelectedInputChanged(RouterInput? value)
    {
        InputName = value?.Name ?? string.Empty;
        InputEnabled = value?.Enabled ?? true;
        InputOutput = InputChoices.FirstOrDefault(o => o.Id == value?.OutputId);
        OnPropertyChanged(nameof(HasInput));
        OnPropertyChanged(nameof(InputEditorTitle));
        OnPropertyChanged(nameof(InputSaveText));
    }

    partial void OnInputOutputChanged(RouterChoice? value) => OnPropertyChanged(nameof(InputRoutePreview));
    [RelayCommand] private void NewOutput() { SelectedOutput = null; OnSelectedOutputChanged(null); IsOutputEditorOpen = true; }
    [RelayCommand] private void NewInput() { SelectedInput = null; OnSelectedInputChanged(null); IsInputEditorOpen = true; }
    [RelayCommand] private void CancelOutput() { IsOutputEditorOpen = false; SelectedOutput = null; OnSelectedOutputChanged(null); }
    [RelayCommand] private void CancelInput() { IsInputEditorOpen = false; SelectedInput = null; OnSelectedInputChanged(null); }
    public void EditOutput(RouterOutput output) { SelectedOutput = output; OnSelectedOutputChanged(output); IsOutputEditorOpen = true; }
    public void EditInput(RouterInput input) { SelectedInput = input; OnSelectedInputChanged(input); IsInputEditorOpen = true; }
    [RelayCommand] private void DismissKey() { GeneratedKey = string.Empty; GeneratedKeyNotice = string.Empty; }

    [RelayCommand]
    private void SaveOutput() => Run(() =>
    {
        if (!IsOutputEditorOpen) return;
        var name = OutputName.Trim();
        _store.SaveOutput(SelectedOutput?.Id, OutputName, OutputBaseUrl, OutputModel,
            ClearOutputKey ? string.Empty : string.IsNullOrWhiteSpace(OutputApiKey) ? null : OutputApiKey);
        CancelOutput();
        Refresh();
        StatusMessage = $"Output '{name}' saved. Assign it as a global default or an agent destination to use it.";
    });

    [RelayCommand]
    private void RemoveOutput() => Run(() =>
    {
        if (SelectedOutput is not { } output) return;
        _store.RemoveOutput(output.Id);
        CancelOutput();
        Refresh();
        StatusMessage = "Output removed.";
    });

    [RelayCommand]
    private void SaveInput() => Run(() =>
    {
        if (!IsInputEditorOpen) return;
        var name = InputName.Trim();
        if (SelectedInput is { } input)
        {
            _store.SaveInput(input.Id, InputName, InputOutput?.Id, InputEnabled);
            CancelInput();
            Refresh();
            StatusMessage = $"Agent '{name}' updated. Route and access changes apply to new requests.";
        }
        else
        {
            var created = _store.AddInput(InputName, InputOutput?.Id);
            CancelInput();
            Refresh();
            ShowKey(created.Key, name);
            StatusMessage = $"Agent '{name}' added. Copy its key before dismissing it.";
        }
    });

    [RelayCommand]
    private void RotateKey() => Run(() =>
    {
        if (SelectedInput is not { } input) return;
        var key = _store.RotateKey(input.Id);
        // Keep unsaved name/route edits intact; rotation changes only the credential.
        Refresh();
        ShowKey(key, input.Name);
        StatusMessage = "Key rotated. The previous key can no longer start requests.";
    });

    [RelayCommand]
    private void RemoveInput() => Run(() =>
    {
        if (SelectedInput is not { } input) return;
        _store.RemoveInput(input.Id);
        CancelInput();
        DismissKey();
        Refresh();
        StatusMessage = "Input removed. Its key can no longer start requests.";
    });

    [RelayCommand]
    private void SaveDefault() => Run(() =>
    {
        _store.SetDefault(DefaultOutput?.Id);
        Refresh();
        StatusMessage = "Global default saved. Per-agent overrides are unchanged.";
    });

    [RelayCommand]
    private void SaveListener() => Run(() =>
    {
        if (IsRunning && Port != _store.Snapshot.Port) throw new ArgumentException("Stop the router before changing its port.");
        _store.Configure(Port, StartOnLaunch);
        RefreshHostState();
        StatusMessage = "Listener settings saved.";
    });

    [RelayCommand]
    private async Task ToggleAsync()
    {
        IsBusy = true;
        try
        {
            if (IsRunning) await _host.StopAsync();
            else
            {
                _store.Configure(Port, StartOnLaunch);
                await _host.StartAsync();
            }
            StatusMessage = IsRunning ? "Router started." : "Router stopped. Active model requests were cancelled.";
        }
        catch (Exception ex) { StatusMessage = FriendlyError(ex); }
        finally { IsBusy = false; RefreshHostState(); }
    }

    private void ShowKey(string key, string name)
    {
        GeneratedKey = key;
        GeneratedKeyNotice = $"New key for {name}. Copy it now; it cannot be recovered after dismissal.";
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { StatusMessage = FriendlyError(ex); }
    }
    private static string FriendlyError(Exception ex) => ex is ArgumentException or InvalidOperationException
        ? ex.Message : "Router operation failed. Check the port, configuration folder permissions, and available disk space.";

    private static string ModelSummary(RouterOutput output) => output.Model is { Length: > 0 }
        ? $"Model sent to provider: {output.Model}" : "Model: supplied by the agent";
    private static string DescribeDefault(RouterConfiguration config) => config.Outputs.FirstOrDefault(o => o.Id == config.DefaultOutputId) is { } output
        ? $"Saved global default: {output.Name}" : "No global default is saved. Agents using the default have no destination.";

    private void Refresh()
    {
        var config = _store.Snapshot;
        var draftRouteId = InputOutput?.Id;
        Outputs.Clear(); Inputs.Clear(); OutputRows.Clear(); InputRows.Clear(); DefaultChoices.Clear(); InputChoices.Clear();
        DefaultChoices.Add(new(null, "No default output"));
        InputChoices.Add(new(null, "Use global default"));
        foreach (var output in config.Outputs.OrderBy(o => o.Name))
        {
            Outputs.Add(output);
            OutputRows.Add(new(output.Name, output.BaseUrl, ModelSummary(output), new RelayCommand(() => EditOutput(output))));
            DefaultChoices.Add(new(output.Id, output.Name));
            InputChoices.Add(new(output.Id, output.Name));
        }
        foreach (var input in config.Inputs.OrderBy(i => i.Name))
        {
            Inputs.Add(input);
            var output = config.Outputs.FirstOrDefault(o => o.Id == (input.OutputId ?? config.DefaultOutputId));
            var route = output is null ? "No destination assigned" : input.OutputId is null ? $"Global default → {output.Name}" : $"Assigned output → {output.Name}";
            InputRows.Add(new(input.Name, route, input.Enabled ? "Enabled" : "Disabled", new RelayCommand(() => EditInput(input))));
        }
        DefaultOutput = DefaultChoices.First(o => o.Id == config.DefaultOutputId);
        InputOutput = InputChoices.FirstOrDefault(o => o.Id == draftRouteId) ?? InputChoices[0];
        OnPropertyChanged(nameof(HasNoOutputs));
        OnPropertyChanged(nameof(HasNoInputs));
        OnPropertyChanged(nameof(SavedDefaultSummary));
        OnPropertyChanged(nameof(InputRoutePreview));
    }
}
