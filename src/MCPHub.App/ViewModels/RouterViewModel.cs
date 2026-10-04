using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MCPHub.App.Messages;
using MCPHub.Core.Routing;
using MCPHub.Core.Users;

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

/// <summary>A user that may be given Router access, as the editor's dropdown offers it.</summary>
public sealed record RouterUserChoice(string Id, string Name, bool Enabled)
{
    /// <summary>Shown in the list: a suspended user can hold a route, and it will do nothing until it is
    /// enabled on the Users page, so the list says which it is rather than looking the same either way.</summary>
    public string Label => Enabled ? Name : $"{Name} (suspended)";
}

/// <summary>
/// One user with Router access: where its requests go, and when it last arrived.
///
/// <para>Its name and state are the user directory's, read at build time rather than stored here — a
/// route names a user and nothing else, so this row is the only place the two are shown together.</para>
/// </summary>
public sealed partial class RouterInputRow : ObservableObject
{
    [ObservableProperty] private string _activitySummary = string.Empty;

    /// <summary>Live, because it is changed on another page: a user suspended on Users keeps its route
    /// and stops working, and this row is where somebody looking at the Router would see that.</summary>
    [ObservableProperty] private string _state = string.Empty;

    [ObservableProperty] private string _name = string.Empty;

    public RouterInputRow(RouterInput input, HubUser? user, string routeSummary, IRelayCommand editCommand)
    {
        Id = input.UserId;
        RouteSummary = routeSummary;
        EditCommand = editCommand;
        Describe(user);
    }

    public string Id { get; }
    public string RouteSummary { get; }
    public IRelayCommand EditCommand { get; }

    /// <summary>Re-reads the user behind this route. Null means the user is gone, which the Users page
    /// prevents by dropping routes with the user — but an imported archive can still produce it.</summary>
    public void Describe(HubUser? user)
    {
        Name = user?.Name is { Length: > 0 } name ? name : user is null ? "(user no longer exists)" : "(unnamed)";
        State = user is null ? "Missing" : user.Enabled ? "Enabled" : "Suspended";
    }
}

public sealed partial class RouterViewModel : ViewModelBase
{
    private readonly RouterStore _store;
    private readonly IUserDirectory _users;
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
    [ObservableProperty] private RouterUserChoice? _inputUser;
    [ObservableProperty] private RouterChoice? _inputOutput;
    [ObservableProperty] private bool _isOutputEditorOpen;
    [ObservableProperty] private bool _isInputEditorOpen;

    public ObservableCollection<RouterOutput> Outputs { get; } = [];
    public ObservableCollection<RouterInput> Inputs { get; } = [];
    public ObservableCollection<RouterOutputRow> OutputRows { get; } = [];
    public ObservableCollection<RouterInputRow> InputRows { get; } = [];
    public ObservableCollection<RouterChoice> DefaultChoices { get; } = [];
    public ObservableCollection<RouterChoice> InputChoices { get; } = [];

    /// <summary>Users that can be given access: everyone without a route, plus whoever is being edited.</summary>
    public ObservableCollection<RouterUserChoice> UserChoices { get; } = [];
    public bool IsRunning => _host.IsRunning;
    public bool IsStopped => !IsRunning;
    public bool IsIdle => !IsBusy;
    public string EndpointUrl => _host.EndpointUrl;
    public string ToggleText => IsRunning ? "Stop router" : "Start router";
    public string RunState => _store.LoadError ?? _host.LastError ?? (IsRunning ? "Running" : "Stopped");

    /// <summary>
    /// Why the stored configuration could not be read, if it could not. While this is set every save throws,
    /// so the page has to say so outright — otherwise Add and Apply look simply broken.
    /// </summary>
    public string? ConfigurationError => _store.LoadError;

    public bool HasConfigurationError => ConfigurationError is not null;
    public bool HasOutput => SelectedOutput is not null;
    public bool HasInput => SelectedInput is not null;
    public bool HasNoOutputs => Outputs.Count == 0;
    public bool HasNoInputs => Inputs.Count == 0;
    public string OutputEditorTitle => SelectedOutput is { } output ? $"Edit output: {output.Name}" : "Add model output";

    public string InputEditorTitle => SelectedInput is { } input
        ? $"Models for {NameOf(input.UserId)}"
        : "Give a user access";

    public string OutputSaveText => HasOutput ? "Save changes" : "Add output";
    public string InputSaveText => HasInput ? "Save changes" : "Give access";

    /// <summary>
    /// Why the dropdown is empty, when it is. No key is issued here any more, so the only way to add
    /// somebody is to pick a user that exists — and an empty list with no explanation is the kind of
    /// dead end that makes somebody think the feature is broken. Shown beside an "Add a user" button.
    /// </summary>
    public string? NoUsersHint => UserChoices.Count > 0
        ? null
        : _users.Snapshot.Users.Length == 0
            ? "No users yet."
            : "Every user already has access. Edit one below, or add another user.";

    public bool HasNoUsersHint => NoUsersHint is not null;
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
                ? "Every network interface, so other machines can reach this. Keep user keys private."
                : BindAddress == RouterConfigurationRules.Loopback
                    ? "This machine only."
                    : $"The interface with address {BindAddress} only.";

            if (!IsRunning) return effect + " Start the router to bind it.";
            var pending = !string.Equals(BindAddress.Trim(), _host.BindAddress, StringComparison.OrdinalIgnoreCase) || Port != _host.Port;
            return pending
                ? $"{effect} Still listening on {_host.BindAddress}:{_host.Port} — choose Apply listener to move it."
                : $"{effect} Listening on {_host.BindAddress}:{_host.Port}.";
        }
    }

    /// <summary>Rejected-key activity, so a key that has been rotated out is visible as failing attempts.</summary>
    public string RejectionSummary => _activity is RouterActivityLog log && log.LastRejection is { } when
        ? $"A key was last rejected {Describe(when)}. Check each user holds its current key."
        : string.Empty;

    public bool HasRejections => RejectionSummary.Length > 0;

    public string InputRoutePreview
    {
        get
        {
            var config = _store.Snapshot;
            var output = config.Outputs.FirstOrDefault(o => o.Id == (InputOutput?.Id ?? config.DefaultOutputId));
            return output is null ? "Nowhere to send requests yet: choose an output, or set a global default."
                : $"Goes to {output.Name} after saving. " + ModelSummary(output);
        }
    }
    public string CredentialStatus => SelectedOutput?.ProtectedApiKey is not null ? "An upstream key is stored. Leave blank to keep it." : "No upstream key stored. Leave blank for an unauthenticated local model.";

    public RouterViewModel(
        RouterStore store,
        IUserDirectory users,
        RouterHost host,
        IRouterActivityLog activity,
        IRouterOutputTester tester)
    {
        _store = store;
        _users = users;
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

    /// <summary>
    /// Re-reads what each row shows that something else can change: its last connection, and the user
    /// behind it. Cheap enough to run on the page's one-second tick, which is what keeps a user suspended
    /// on the Users page from still reading "Enabled" here.
    /// </summary>
    public void RefreshActivity()
    {
        var users = _users.Snapshot.Users;
        foreach (var row in InputRows)
        {
            row.ActivitySummary = DescribeActivity(_activity.Get(row.Id));
            row.Describe(users.FirstOrDefault(u => string.Equals(u.Id, row.Id, StringComparison.Ordinal)));
        }
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
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
        // The user being edited is offered even though it already holds a route, since otherwise the
        // dropdown would show somebody else's name against the route on screen.
        RefreshUserChoices(value?.UserId);
        InputUser = value is null
            ? UserChoices.FirstOrDefault()
            : UserChoices.FirstOrDefault(u => string.Equals(u.Id, value.UserId, StringComparison.Ordinal));
        InputOutput = InputChoices.FirstOrDefault(o => o.Id == value?.OutputId) ?? InputChoices.FirstOrDefault();
        OnPropertyChanged(nameof(HasInput));
        OnPropertyChanged(nameof(InputEditorTitle));
        OnPropertyChanged(nameof(InputSaveText));
    }

    partial void OnInputOutputChanged(RouterChoice? value) => OnPropertyChanged(nameof(InputRoutePreview));
    /// <summary>Opens the Users page. This page can only name users, so the answer to "where do I make
    /// one" should be a button rather than a sentence pointing at the nav bar.</summary>
    [RelayCommand]
    private void AddUser() => WeakReferenceMessenger.Default.Send(ShowPageMessage.Users);

    [RelayCommand] private void NewOutput() { SelectedOutput = null; OnSelectedOutputChanged(null); IsOutputEditorOpen = true; }
    [RelayCommand] private void NewInput() { SelectedInput = null; OnSelectedInputChanged(null); IsInputEditorOpen = true; }
    [RelayCommand] private void CancelOutput() { IsOutputEditorOpen = false; SelectedOutput = null; OnSelectedOutputChanged(null); }
    [RelayCommand] private void CancelInput() { IsInputEditorOpen = false; SelectedInput = null; OnSelectedInputChanged(null); }
    public void EditOutput(RouterOutput output) { SelectedOutput = output; OnSelectedOutputChanged(output); IsOutputEditorOpen = true; }
    public void EditInput(RouterInput input) { SelectedInput = input; OnSelectedInputChanged(input); IsInputEditorOpen = true; }


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
        StatusMessage = $"Output '{name}' saved. Use Test to check it answers.";
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

    /// <summary>
    /// Gives the chosen user Router access, or moves where its requests go. No key is issued and none is
    /// shown: the user already holds one from the Users page, and that is the whole point — a second key
    /// here is what used to make an agent carry two.
    /// </summary>
    [RelayCommand]
    private void SaveInput() => Run(() =>
    {
        if (!IsInputEditorOpen) return;
        if (InputUser is not { } user)
        {
            StatusMessage = UserChoices.Count == 0
                ? "No users to give access to. Add one on the Users page first."
                : "Choose a user first.";
            return;
        }

        var editing = SelectedInput is not null;
        _store.SetRoute(user.Id, InputOutput?.Id);
        CancelInput();
        Refresh();
        StatusMessage = editing
            ? $"'{user.Name}' re-routed. Applies to its next request."
            : $"'{user.Name}' may now use the Router with the key it already holds.";
    });

    /// <summary>
    /// Takes Router access away and leaves the user alone — it keeps its key and whatever tools it is
    /// granted, which is the distinction that did not exist while the Router issued its own keys.
    /// </summary>
    [RelayCommand]
    private void RemoveInput() => Run(() =>
    {
        if (SelectedInput is not { } input) return;
        var name = NameOf(input.UserId);
        _store.RemoveRoute(input.UserId);
        // Drop the history too, so a user given access again later starts with a clean record rather than
        // appearing to have connected under an arrangement that has since been revoked.
        _activity.Forget(input.UserId);
        CancelInput();
        Refresh();
        StatusMessage = $"'{name}' can no longer use the Router. Its key and tools are untouched.";
    });

    [RelayCommand]
    private void SaveDefault() => Run(() =>
    {
        _store.SetDefault(DefaultOutput?.Id);
        Refresh();
        StatusMessage = "Global default saved. Users with their own output are unchanged.";
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
        var users = _users.Snapshot.Users;
        foreach (var input in config.Inputs.OrderBy(i => NameOf(i.UserId), StringComparer.CurrentCultureIgnoreCase))
        {
            Inputs.Add(input);
            var output = config.Outputs.FirstOrDefault(o => o.Id == (input.OutputId ?? config.DefaultOutputId));
            var route = output is null ? "No destination assigned" : input.OutputId is null ? $"Global default → {output.Name}" : $"Assigned output → {output.Name}";
            var user = users.FirstOrDefault(u => string.Equals(u.Id, input.UserId, StringComparison.Ordinal));
            InputRows.Add(new(input, user, route, new RelayCommand(() => EditInput(input))));
        }

        RefreshUserChoices(SelectedInput?.UserId);
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

    /// <summary>
    /// Rebuilds the editor's user list: everyone without a route, plus <paramref name="include"/> so the
    /// route being edited still shows its own user.
    /// </summary>
    private void RefreshUserChoices(string? include)
    {
        var routed = _store.Snapshot.Inputs.Select(i => i.UserId).ToHashSet(StringComparer.Ordinal);
        var chosen = InputUser?.Id;
        UserChoices.Clear();
        foreach (var user in _users.Snapshot.Users.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!routed.Contains(user.Id) || string.Equals(user.Id, include, StringComparison.Ordinal))
                UserChoices.Add(new(user.Id, user.Name.Length > 0 ? user.Name : "(unnamed)", user.Enabled));
        }

        InputUser = UserChoices.FirstOrDefault(u => string.Equals(u.Id, chosen ?? include, StringComparison.Ordinal))
                    ?? UserChoices.FirstOrDefault();
        OnPropertyChanged(nameof(NoUsersHint));
        OnPropertyChanged(nameof(HasNoUsersHint));
    }

    /// <summary>A routed user's name, or something honest when the user has been deleted out from under
    /// the route — which the Users page drops routes to avoid, but an imported archive can still produce.</summary>
    private string NameOf(string userId) =>
        _users.Snapshot.Users.FirstOrDefault(u => string.Equals(u.Id, userId, StringComparison.Ordinal)) is { } user
            ? user.Name.Length > 0 ? user.Name : "(unnamed)"
            : "(user no longer exists)";
}
