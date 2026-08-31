using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Recipes;
using MCPHub.Core.Settings;

namespace MCPHub.App.ViewModels;

/// <summary>
/// The Recipes page: browse, search, add, edit and delete the "if X then Y" notes that tell an agent how
/// to combine MCP servers. Agents edit the same store through the proxy's <c>recipes__*</c> tools, so the
/// list refreshes live when they do.
/// </summary>
public sealed partial class RecipesViewModel : ViewModelBase
{
    private readonly IRecipeStore _store;
    private readonly ISettingsStore _settings;
    private readonly IRecipeAccessPolicy _policy;
    private bool _restoringSelection;
    private bool _initialising;

    // Agent access switches (persisted in settings; the environment can pin either one).
    [ObservableProperty] private bool _agentsEnabled;
    [ObservableProperty] private bool _agentsMayEdit;

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private RecipeRowViewModel? _selected;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private string _editTitle = string.Empty;
    [ObservableProperty] private string _editWhen = string.Empty;
    [ObservableProperty] private string _editThen = string.Empty;
    [ObservableProperty] private string _editServices = string.Empty;
    [ObservableProperty] private string _editNotes = string.Empty;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<RecipeRowViewModel> Recipes { get; } = [];

    public RecipesViewModel(IRecipeStore store, ISettingsStore settings, IRecipeAccessPolicy policy)
    {
        _store = store;
        _settings = settings;
        _policy = policy;

        _initialising = true;
        try
        {
            // Show the effective values (environment override included), not just what settings.json says.
            AgentsEnabled = policy.RecipesEnabled;
            AgentsMayEdit = policy.AgentEditSwitch;
        }
        finally
        {
            _initialising = false;
        }

        _store.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    // ---- agent access ---------------------------------------------------------------------------

    /// <summary>False when <c>MCPHUB_RECIPES_ENABLED</c> pins the value, so the checkbox is shown locked.</summary>
    public bool CanToggleAgentsEnabled => _policy.RecipesEnabledOverrideSource is null;

    /// <summary>False while recipes are off (moot) or <c>MCPHUB_RECIPES_AGENT_EDIT</c> pins the value.</summary>
    public bool CanToggleAgentsMayEdit => AgentsEnabled && _policy.AgentEditOverrideSource is null;

    /// <summary>One line describing what agents can currently do, plus any environment pin.</summary>
    public string AccessSummary
    {
        get
        {
            var what = !_policy.RecipesEnabled
                ? "Recipes are hidden from agents — no recipes__* tools are exposed."
                : _policy.AgentEditEnabled
                    ? "Agents can read recipes and add, edit or remove them."
                    : "Agents can read recipes (recipes__list, recipes__get) but not change them.";

            var pins = new[] { _policy.RecipesEnabledOverrideSource, _policy.AgentEditOverrideSource }
                .Where(p => p is not null)
                .ToList();
            return pins.Count == 0
                ? what
                : $"{what} Pinned by the environment: {string.Join(", ", pins)} — change the container flag to alter it.";
        }
    }

    partial void OnAgentsEnabledChanged(bool value)
    {
        if (!_initialising)
        {
            _settings.Current.RecipesEnabled = value;
            _ = _settings.SaveAsync();
        }
        OnPropertyChanged(nameof(CanToggleAgentsMayEdit));
        OnPropertyChanged(nameof(AccessSummary));
    }

    partial void OnAgentsMayEditChanged(bool value)
    {
        if (!_initialising)
        {
            _settings.Current.RecipesAgentEditEnabled = value;
            _ = _settings.SaveAsync();
        }
        OnPropertyChanged(nameof(AccessSummary));
    }

    // ---- knowledge base -------------------------------------------------------------------------

    /// <summary>Where the knowledge base lives on disk, shown so the user can back it up or edit it by hand.</summary>
    public string FilePath => _store.FilePath;

    public bool HasRecipes => Recipes.Count > 0;

    public bool IsFiltering => FilterText.Trim().Length > 0;

    /// <summary>e.g. "3 of 12 recipes" while filtering, "12 recipes" otherwise.</summary>
    public string CountSummary
    {
        get
        {
            var total = _store.All.Count;
            var noun = total == 1 ? "recipe" : "recipes";
            return IsFiltering ? $"{Recipes.Count} of {total} {noun}" : $"{total} {noun}";
        }
    }

    public string EditorHeading => IsNew ? "New recipe" : "Edit recipe";

    public bool CanDelete => IsEditing && !IsNew;

    partial void OnFilterTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsFiltering));
        Refresh();
    }

    partial void OnIsNewChanged(bool value)
    {
        OnPropertyChanged(nameof(EditorHeading));
        OnPropertyChanged(nameof(CanDelete));
    }

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(CanDelete));

    partial void OnSelectedChanged(RecipeRowViewModel? value)
    {
        // Clearing happens as a side effect of rebuilding the list; only a real pick loads the editor,
        // so an agent adding a recipe mid-edit cannot wipe what the user is typing.
        if (value is null || _restoringSelection)
            return;

        EditTitle = value.Title;
        EditWhen = value.When;
        EditThen = value.Then;
        EditServices = value.ServicesText;
        EditNotes = value.Notes ?? string.Empty;
        IsNew = false;
        IsEditing = true;
        StatusMessage = null;
    }

    private void Refresh()
    {
        var keepId = Selected?.Id;
        _restoringSelection = true;
        try
        {
            Recipes.Clear();
            foreach (var recipe in _store.Search(FilterText))
                Recipes.Add(new RecipeRowViewModel(recipe));

            Selected = keepId is null ? null : Recipes.FirstOrDefault(r => r.Id == keepId);
        }
        finally
        {
            _restoringSelection = false;
        }

        OnPropertyChanged(nameof(HasRecipes));
        OnPropertyChanged(nameof(CountSummary));
    }

    private void SetSelectionQuietly(RecipeRowViewModel? row)
    {
        _restoringSelection = true;
        try { Selected = row; }
        finally { _restoringSelection = false; }
    }

    [RelayCommand]
    private void New()
    {
        SetSelectionQuietly(null);
        EditTitle = string.Empty;
        EditWhen = string.Empty;
        EditThen = string.Empty;
        EditServices = string.Empty;
        EditNotes = string.Empty;
        IsNew = true;
        IsEditing = true;
        StatusMessage = null;
    }

    [RelayCommand]
    private void Save()
    {
        var draft = new RecipeDraft
        {
            Title = EditTitle,
            When = EditWhen,
            Then = EditThen,
            Services = EditServices.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Notes = EditNotes,
        };

        try
        {
            Recipe? saved = IsNew || Selected is null ? null : _store.Update(Selected.Id, draft, RecipeSources.User);
            // Either a new recipe, or one that was deleted underneath us (by an agent, most likely):
            // keep the user's text and store it as new rather than losing it.
            saved ??= _store.Add(draft, RecipeSources.User);

            Refresh();
            SetSelectionQuietly(Recipes.FirstOrDefault(r => r.Id == saved.Id));
            IsNew = false;
            IsEditing = true;
            StatusMessage = $"Saved '{saved.Title}'.";
        }
        catch (RecipeValidationException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is null)
            return;

        var title = Selected.Title;
        _store.Remove(Selected.Id);
        Close();
        StatusMessage = $"Deleted '{title}'.";
    }

    [RelayCommand]
    private void Cancel()
    {
        Close();
        StatusMessage = null;
    }

    private void Close()
    {
        SetSelectionQuietly(null);
        IsEditing = false;
        IsNew = false;
        Refresh();
    }
}

/// <summary>One recipe in the list: read-only projection of a <see cref="Recipe"/>.</summary>
public sealed class RecipeRowViewModel
{
    public RecipeRowViewModel(Recipe recipe)
    {
        Id = recipe.Id;
        Title = recipe.Title;
        When = recipe.When;
        Then = recipe.Then;
        Notes = recipe.Notes;
        ServicesText = string.Join(", ", recipe.Services);
        Source = recipe.Source;
        UpdatedText = recipe.UpdatedAt.LocalDateTime.ToString("g");
    }

    public string Id { get; }
    public string Title { get; }
    public string When { get; }
    public string Then { get; }
    public string? Notes { get; }
    public string ServicesText { get; }
    public string Source { get; }
    public string UpdatedText { get; }

    public bool HasServices => ServicesText.Length > 0;

    /// <summary>The recipe as one line, e.g. "If Kodi isn't running → launch it with adb".</summary>
    public string Summary => $"If {When} → {Then}";

    /// <summary>e.g. "agent · 31/08/2026 14:02".</summary>
    public string Meta => $"{Source} · {UpdatedText}";
}
