using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Routing;

namespace MCPHub.App.ViewModels;

public sealed record RouterChoice(string? Id, string Name);

/// <summary>One saved model output, with the result of the last connection test run against it.</summary>
public sealed partial class RouterOutputRow : ObservableObject
{
    [ObservableProperty] private string _testSummary = string.Empty;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool? _lastTestPassed;

    public RouterOutputRow(RouterOutput output, string modelSummary, IRelayCommand editCommand, Func<RouterOutputRow, Task> test)
    {
        Output = output;
        Name = output.Name;
        BaseUrl = output.BaseUrl;
        ModelSummary = modelSummary;
        EditCommand = editCommand;
        // Built here so the command can pass this row back without the caller needing it before it exists.
        TestCommand = new AsyncRelayCommand(() => test(this));
    }

    public RouterOutput Output { get; }
    public string Id => Output.Id;
    public string Name { get; }
    public string BaseUrl { get; }
    public string ModelSummary { get; }
    public IRelayCommand EditCommand { get; }
    public IAsyncRelayCommand TestCommand { get; }

    public bool HasTestResult => TestSummary.Length > 0;

    partial void OnTestSummaryChanged(string value) => OnPropertyChanged(nameof(HasTestResult));
}

/// <summary>One saved agent, including when it last reached the Router.</summary>
public sealed partial class RouterInputRow : ObservableObject
{
    [ObservableProperty] private string _activitySummary = string.Empty;

    public RouterInputRow(RouterInput input, string routeSummary, IRelayCommand editCommand)
    {
        Id = input.Id;
        Name = input.Name;
        RouteSummary = routeSummary;
        State = input.Enabled ? "Enabled" : "Disabled";
        EditCommand = editCommand;
    }

    public string Id { get; }
    public string Name { get; }
    public string RouteSummary { get; }
    public string State { get; }
    public IRelayCommand EditCommand { get; }
}

public sealed partial class RouterViewModel : ViewModelBase
{
    private readonly RouterStore _store;
    private readonly RouterHost _host;
    private readonly IRouterActivityLog _activity;
    private readonly IRouterOutputTester _tester;

    /// <summary>Last test result per output id, so rebuilding the rows does not wipe what the user just ran.</summary>
    private readonly Dictionary<string, (string Summary, bool Passed)> _testResults = new(StringComparer.Ordinal);

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _port;
    [ObservableProperty] private string _bindAddress = RouterConfigurationRules.Loopback;
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

    /// <summary>
    /// What the address in the box means, and — while the running listener is on a different one — that
    /// Apply is still needed. Reads the live host rather than the draft so it never overstates the state.
    /// </summary>
    public string ListenerSummary
    {
        get
        {
            var effect = RouterConfigurationRules.IsWildcard(BindAddress)
                ? "Every network interface: agents on other machines can reach this Router, so keep agent keys private."
                : BindAddress == RouterConfigurationRules.Loopback
                    ? "This machine only. Agents elsewhere on your network cannot connect."
                    : $"The interface with address {BindAddress} only.";

            if (!IsRunning) return effect + " Start the router to bind it.";
            var pending = !string.Equals(BindAddress.Trim(), _host.BindAddress, StringComparison.OrdinalIgnoreCase) || Port != _host.Port;
            return pending
                ? $"{effect} Currently listening on {_host.BindAddress}:{_host.Port} — choose Apply listener to rebind without restarting MCPHub."
                : $"{effect} Listening on {_host.BindAddress}:{_host.Port}.";
        }
    }

    /// <summary>Rejected-key activity, so a key that has been rotated out is visible as failing attempts.</summary>
    public string RejectionSummary => _activity is RouterActivityLog log && log.LastRejection is { } when
        ? $"A key was last rejected {Describe(when)}. Check that each agent holds its current key."
        : string.Empty;

    public bool HasRejections => RejectionSummary.Length > 0;

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

    public RouterViewModel(RouterStore store, RouterHost host, IRouterActivityLog activity, IRouterOutputTester tester)
    {
        _store = store;
        _host = host;
        _activity = activity;
        _tester = tester;
        var config = store.Snapshot;
        Port = config.Port;
        BindAddress = config.BindAddress;
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
        OnPropertyChanged(nameof(ListenerSummary));
        OnPropertyChanged(nameof(RejectionSummary));
        OnPropertyChanged(nameof(HasRejections));
        RefreshActivity();
    }

    /// <summary>Re-reads each agent's last connection. Cheap enough to run on the page's one-second tick.</summary>
    public void RefreshActivity()
    {
        foreach (var row in InputRows)
            row.ActivitySummary = DescribeActivity(_activity.Get(row.Id));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnGeneratedKeyChanged(string value) => OnPropertyChanged(nameof(HasGeneratedKey));
    partial void OnBindAddressChanged(string value) => OnPropertyChanged(nameof(ListenerSummary));
    partial void OnPortChanged(int value) => OnPropertyChanged(nameof(ListenerSummary));
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

    /// <summary>Sets the bind address box from a preset button, leaving the change unapplied until Apply.</summary>
    [RelayCommand]
    private void UseLoopback() => BindAddress = RouterConfigurationRules.Loopback;

    /// <inheritdoc cref="UseLoopback"/>
    [RelayCommand]
    private void UseAllInterfaces() => BindAddress = RouterConfigurationRules.AnyIPv4;

    [RelayCommand]
    private void SaveOutput() => Run(() =>
    {
        if (!IsOutputEditorOpen) return;
        var name = OutputName.Trim();
        var id = _store.SaveOutput(SelectedOutput?.Id, OutputName, OutputBaseUrl, OutputModel,
            ClearOutputKey ? string.Empty : string.IsNullOrWhiteSpace(OutputApiKey) ? null : OutputApiKey);
        // The stored result of an earlier test describes settings that no longer apply.
        _testResults.Remove(id);
        CancelOutput();
        Refresh();
        StatusMessage = $"Output '{name}' saved. Use Test to check it answers, then assign it as a default or an agent destination.";
    });

    [RelayCommand]
    private void RemoveOutput() => Run(() =>
    {
        if (SelectedOutput is not { } output) return;
        _store.RemoveOutput(output.Id);
        _testResults.Remove(output.Id);
        CancelOutput();
        Refresh();
        StatusMessage = "Output removed.";
    });

    /// <summary>
    /// Probes one output's provider directly, so a misconfigured URL, key or model name is reported here
    /// rather than as an opaque failure inside an agent. Runs whether or not the Router is started.
    /// </summary>
    public async Task TestOutputAsync(RouterOutputRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsTesting) return;

        row.IsTesting = true;
        row.TestSummary = "Testing…";
        row.LastTestPassed = null;
        try
        {
            var result = await _tester.TestAsync(row.Output);
            row.TestSummary = result.Summary;
            row.LastTestPassed = result.Succeeded;
            _testResults[row.Id] = (result.Summary, result.Succeeded);
            StatusMessage = $"{row.Name}: {result.Summary}";
        }
        finally
        {
            row.IsTesting = false;
        }
    }

    /// <summary>Tests every saved output at once, so a whole configuration can be checked in one action.</summary>
    [RelayCommand]
    private async Task TestAllOutputsAsync()
    {
        if (OutputRows.Count == 0)
        {
            StatusMessage = "There are no outputs to test yet.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = $"Testing {OutputRows.Count} output(s)…";
            foreach (var row in OutputRows.ToList())
                await TestOutputAsync(row);

            var failed = OutputRows.Count(r => r.LastTestPassed == false);
            StatusMessage = failed == 0
                ? $"All {OutputRows.Count} output(s) answered."
                : $"{failed} of {OutputRows.Count} output(s) failed. See each row for the reason.";
        }
        finally { IsBusy = false; }
    }

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
        // Drop the history too, so a future agent reusing the id cannot inherit someone else's activity.
        _activity.Forget(input.Id);
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

    /// <summary>
    /// Saves the listener settings and, when the Router is running, rebinds it to them immediately. A socket
    /// cannot be moved in place, so in-flight requests on the old address end — but MCPHub is not restarted
    /// and every route, key and output survives.
    /// </summary>
    [RelayCommand]
    private async Task ApplyListenerAsync()
    {
        IsBusy = true;
        try
        {
            var address = BindAddress;
            var port = Port;
            var rebound = await _host.ApplyListenerAsync(() => _store.Configure(address, port, StartOnLaunch));
            BindAddress = _store.Snapshot.BindAddress;
            StatusMessage = rebound
                ? $"Listener applied. The Router is now on {_host.BindAddress}:{_host.Port}; agents must use the new address."
                : IsRunning ? "Listener settings saved; they already match the running listener."
                : "Listener settings saved. They apply when the Router starts.";
        }
        catch (Exception ex)
        {
            // ApplyListenerAsync restores the previous listener before rethrowing, so the message is the whole story.
            BindAddress = _store.Snapshot.BindAddress;
            Port = _store.Snapshot.Port;
            StatusMessage = FriendlyListenerError(ex);
        }
        finally { IsBusy = false; RefreshHostState(); }
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        IsBusy = true;
        try
        {
            if (IsRunning) await _host.StopAsync();
            else
            {
                _store.Configure(BindAddress, Port, StartOnLaunch);
                await _host.StartAsync();
            }
            StatusMessage = IsRunning ? "Router started." : "Router stopped. Active model requests were cancelled.";
        }
        catch (Exception ex) { StatusMessage = FriendlyListenerError(ex); }
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

    /// <summary>A failed bind is nearly always a taken port or an address this machine does not own.</summary>
    private static string FriendlyListenerError(Exception ex) => ex switch
    {
        ArgumentException or InvalidOperationException => ex.Message,
        IOException => "Could not bind that address and port — another application is probably using it, or the address does not belong to this machine. The previous listener was restored.",
        _ => "Router operation failed. Check the port, configuration folder permissions, and available disk space.",
    };

    private static string ModelSummary(RouterOutput output) => output.Model is { Length: > 0 }
        ? $"Model sent to provider: {output.Model}" : "Model: supplied by the agent";
    private static string DescribeDefault(RouterConfiguration config) => config.Outputs.FirstOrDefault(o => o.Id == config.DefaultOutputId) is { } output
        ? $"Saved global default: {output.Name}" : "No global default is saved. Agents using the default have no destination.";

    private static string DescribeActivity(RouterActivity activity) => activity.HasConnected
        ? $"Last connected {Describe(activity.LastConnected)} · {activity.RequestCount:N0} request(s)"
        : "Never connected";

    /// <summary>Coarse relative time — the question is "recently or not", not the exact second.</summary>
    private static string Describe(DateTimeOffset when)
    {
        var ago = DateTimeOffset.UtcNow - when;
        return ago switch
        {
            { TotalSeconds: < 0 } => when.ToLocalTime().ToString("g"),
            { TotalSeconds: < 10 } => "just now",
            { TotalMinutes: < 1 } => $"{ago.TotalSeconds:0}s ago",
            { TotalHours: < 1 } => $"{ago.TotalMinutes:0}m ago",
            { TotalDays: < 1 } => $"{ago.TotalHours:0}h ago",
            { TotalDays: < 7 } => $"{ago.TotalDays:0}d ago",
            _ => "on " + when.ToLocalTime().ToString("d"),
        };
    }

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
            var row = new RouterOutputRow(output, ModelSummary(output), new RelayCommand(() => EditOutput(output)), TestOutputAsync);
            if (_testResults.TryGetValue(output.Id, out var previous))
            {
                row.TestSummary = previous.Summary;
                row.LastTestPassed = previous.Passed;
            }
            OutputRows.Add(row);
            DefaultChoices.Add(new(output.Id, output.Name));
            InputChoices.Add(new(output.Id, output.Name));
        }
        foreach (var input in config.Inputs.OrderBy(i => i.Name))
        {
            Inputs.Add(input);
            var output = config.Outputs.FirstOrDefault(o => o.Id == (input.OutputId ?? config.DefaultOutputId));
            var route = output is null ? "No destination assigned" : input.OutputId is null ? $"Global default → {output.Name}" : $"Assigned output → {output.Name}";
            InputRows.Add(new(input, route, new RelayCommand(() => EditInput(input))));
        }
        DefaultOutput = DefaultChoices.First(o => o.Id == config.DefaultOutputId);
        InputOutput = InputChoices.FirstOrDefault(o => o.Id == draftRouteId) ?? InputChoices[0];
        RefreshActivity();
        OnPropertyChanged(nameof(HasNoOutputs));
        OnPropertyChanged(nameof(HasNoInputs));
        OnPropertyChanged(nameof(SavedDefaultSummary));
        OnPropertyChanged(nameof(InputRoutePreview));
        OnPropertyChanged(nameof(RejectionSummary));
        OnPropertyChanged(nameof(HasRejections));
    }
}
