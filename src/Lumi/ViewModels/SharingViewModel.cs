using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;
using Lumi.Services;
using StrataSearch;

namespace Lumi.ViewModels;

public partial class SharingViewModel : ObservableObject, IDisposable
{
    private readonly DataStore _dataStore;
    private readonly LumiSharingService _sharingService;

    public event Action? CapabilitiesChanged;

    [ObservableProperty] private LumiSharedRepository? _selectedRepository;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editRepository = "";
    [ObservableProperty] private string _editBranch = "";
    [ObservableProperty] private int _editUpdateIntervalMinutes = 60;
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private string _statusMessage = "Add a team repository to sync shared Lumi capabilities.";
    [ObservableProperty] private string _capabilitySearchQuery = "";
    [ObservableProperty] private ShareableCapabilityItem? _selectedCapability;

    public ObservableCollection<LumiSharedRepository> Repositories { get; } = [];
    public ObservableCollection<ShareableCapabilityItem> ShareableCapabilities { get; } = [];

    public SharingViewModel(DataStore dataStore)
        : this(dataStore, new LumiSharingService(dataStore))
    {
    }

    internal SharingViewModel(DataStore dataStore, LumiSharingService sharingService)
    {
        _dataStore = dataStore;
        _sharingService = sharingService;
        _sharingService.RepositoriesChanged += OnSharingServiceRepositoriesChanged;
        _sharingService.CapabilitiesChanged += OnSharingServiceCapabilitiesChanged;
        RefreshFromStore();
    }

    public void StartPeriodicSync() => _sharingService.StartPeriodicSync();

    public void RefreshFromStore()
    {
        RefreshRepositories();
        RefreshShareableCapabilities();
    }

    [RelayCommand]
    private void NewRepository()
    {
        SelectedRepository = null;
        EditName = "";
        EditRepository = "";
        EditBranch = "";
        EditUpdateIntervalMinutes = 60;
        EditIsEnabled = true;
        IsEditing = true;
        StatusMessage = "Paste a git URL or local repository path that contains shared Lumi capabilities.";
    }

    [RelayCommand]
    private async Task SaveRepositoryAsync()
    {
        var repositoryLocation = EditRepository.Trim();
        if (string.IsNullOrWhiteSpace(repositoryLocation))
        {
            StatusMessage = "Repository path or URL is required.";
            return;
        }

        var interval = Math.Max(5, EditUpdateIntervalMinutes);
        var name = string.IsNullOrWhiteSpace(EditName)
            ? BuildDefaultRepositoryName(repositoryLocation)
            : EditName.Trim();

        var repository = SelectedRepository;
        if (repository is null)
        {
            repository = new LumiSharedRepository();
            _dataStore.Data.SharedRepositories.Add(repository);
        }

        repository.Name = name;
        repository.Repository = repositoryLocation;
        repository.Branch = EditBranch.Trim();
        repository.UpdateIntervalMinutes = interval;
        repository.IsEnabled = EditIsEnabled;
        repository.NextSyncAt = repository.IsEnabled ? null : repository.NextSyncAt;

        await _dataStore.SaveAsync();
        IsEditing = false;
        SelectedRepository = repository;
        RefreshRepositories();
        StatusMessage = $"Saved {repository.DisplayName}. Syncing now...";
        await SyncRepositoryAsync(repository);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        if (SelectedRepository is not null)
            SyncEditorFromRepository(SelectedRepository);
    }

    [RelayCommand]
    private async Task DeleteSelectedRepositoryAsync()
    {
        if (SelectedRepository is null)
            return;

        var deletedName = SelectedRepository.DisplayName;
        IsBusy = true;
        try
        {
            await _sharingService.RemoveRepositoryAsync(SelectedRepository);
            SelectedRepository = null;
            IsEditing = false;
            RefreshFromStore();
            StatusMessage = $"Removed {deletedName} and its imported shared capabilities.";
            CapabilitiesChanged?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncSelectedRepositoryAsync()
    {
        if (SelectedRepository is not null)
            await SyncRepositoryAsync(SelectedRepository);
    }

    [RelayCommand]
    private async Task SyncAllRepositoriesAsync()
    {
        IsBusy = true;
        StatusMessage = "Syncing all enabled sharing repositories...";
        try
        {
            var result = await _sharingService.SyncDueRepositoriesAsync(force: true);
            RefreshFromStore();
            StatusMessage = $"Synced {result.RepositoryCount} repositories: {result.SkillCount} skills, {result.AgentCount} Lumis, {result.McpServerCount} MCPs, {result.MemoryCount} memories.";
            CapabilitiesChanged?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PublishSelectedCapabilityAsync()
    {
        if (SelectedRepository is null)
        {
            StatusMessage = "Choose a sharing repository first.";
            return;
        }

        if (SelectedCapability is null)
        {
            StatusMessage = "Choose a skill, Lumi, or memory to publish.";
            return;
        }

        IsBusy = true;
        try
        {
            LumiSharingPublishResult result;
            switch (SelectedCapability.Item)
            {
                case Skill skill:
                    result = await _sharingService.PublishSkillAsync(SelectedRepository, skill);
                    break;
                case LumiAgent agent:
                    result = await _sharingService.PublishAgentAsync(SelectedRepository, agent);
                    break;
                case Memory memory:
                    result = await _sharingService.PublishMemoryAsync(SelectedRepository, memory);
                    break;
                default:
                    StatusMessage = "This capability type cannot be published yet.";
                    return;
            }

            await _dataStore.SaveAsync();
            _dataStore.SyncSkillFiles();
            RefreshShareableCapabilities();
            StatusMessage = result.RelativePath is { Length: > 0 }
                ? $"{result.Message} Updated {result.RelativePath} and staged it in git when available."
                : result.Message;
            CapabilitiesChanged?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SyncRepositoryAsync(LumiSharedRepository repository)
    {
        IsBusy = true;
        try
        {
            var result = await _sharingService.SyncRepositoryAsync(repository);
            RefreshFromStore();
            StatusMessage = result.RepositoryCount == 0
                ? $"No repositories synced."
                : $"Synced {repository.DisplayName}: {result.SkillCount} skills, {result.AgentCount} Lumis, {result.McpServerCount} MCPs, {result.MemoryCount} memories.";
            CapabilitiesChanged?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshRepositories()
    {
        var selectedId = SelectedRepository?.Id;
        Repositories.Clear();
        foreach (var repository in _dataStore.Data.SharedRepositories.OrderBy(static repository => repository.DisplayName))
            Repositories.Add(repository);

        if (selectedId is not null)
            SelectedRepository = _dataStore.Data.SharedRepositories.FirstOrDefault(repository => repository.Id == selectedId);
    }

    private void RefreshShareableCapabilities()
    {
        var query = CapabilitySearchQuery;
        var items = _dataStore.Data.Skills
            .Select(skill => ShareableCapabilityItem.FromSkill(skill))
            .Concat(_dataStore.Data.Agents.Select(agent => ShareableCapabilityItem.FromAgent(agent)))
            .Concat(_dataStore.Data.Memories
                .Where(static memory => string.Equals(memory.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase))
                .Select(memory => ShareableCapabilityItem.FromMemory(memory)))
            .ToList();

        var ranked = string.IsNullOrWhiteSpace(query)
            ? items.OrderBy(static item => item.TypeSort).ThenBy(static item => item.Name).ToArray()
            : SearchPipeline.Rank(
                items,
                query,
                static item =>
                [
                    SearchField.Primary(item.Name, 3.2),
                    new SearchField(item.TypeLabel, 1.6),
                    new SearchField(item.Description, 1.2),
                    new SearchField(item.SourceLabel, 0.8)
                ],
                static item => new SearchSortMetadata(Text: item.Name));

        var selectedKey = SelectedCapability?.StableKey;
        ShareableCapabilities.Clear();
        foreach (var item in ranked)
            ShareableCapabilities.Add(item);
        SelectedCapability = ShareableCapabilities.FirstOrDefault(item => item.StableKey == selectedKey);
    }

    partial void OnSelectedRepositoryChanged(LumiSharedRepository? value)
    {
        if (value is null)
            return;

        SyncEditorFromRepository(value);
        IsEditing = true;
    }

    partial void OnCapabilitySearchQueryChanged(string value) => RefreshShareableCapabilities();

    private void SyncEditorFromRepository(LumiSharedRepository repository)
    {
        EditName = repository.Name;
        EditRepository = repository.Repository;
        EditBranch = repository.Branch;
        EditUpdateIntervalMinutes = repository.UpdateIntervalMinutes;
        EditIsEnabled = repository.IsEnabled;
    }

    private void OnSharingServiceRepositoriesChanged() => DispatchRefresh(RefreshRepositories);

    private void OnSharingServiceCapabilitiesChanged()
    {
        DispatchRefresh(() =>
        {
            RefreshFromStore();
            CapabilitiesChanged?.Invoke();
        });
    }

    private static void DispatchRefresh(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private static string BuildDefaultRepositoryName(string repositoryLocation)
    {
        var trimmed = repositoryLocation.TrimEnd('/', '\\');
        var name = trimmed;
        var separatorIndex = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        if (separatorIndex >= 0 && separatorIndex + 1 < trimmed.Length)
            name = trimmed[(separatorIndex + 1)..];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return string.IsNullOrWhiteSpace(name) ? "Shared repository" : name;
    }

    public void Dispose()
    {
        _sharingService.RepositoriesChanged -= OnSharingServiceRepositoriesChanged;
        _sharingService.CapabilitiesChanged -= OnSharingServiceCapabilitiesChanged;
        _sharingService.Dispose();
    }
}

public sealed class ShareableCapabilityItem
{
    private ShareableCapabilityItem(
        string typeLabel,
        int typeSort,
        string name,
        string description,
        string sourceLabel,
        string stableKey,
        object item)
    {
        TypeLabel = typeLabel;
        TypeSort = typeSort;
        Name = name;
        Description = description;
        SourceLabel = sourceLabel;
        StableKey = stableKey;
        Item = item;
    }

    public string TypeLabel { get; }
    public int TypeSort { get; }
    public string Name { get; }
    public string Description { get; }
    public string SourceLabel { get; }
    public string StableKey { get; }
    public object Item { get; }

    public static ShareableCapabilityItem FromSkill(Skill skill)
        => new("Skill", 0, skill.Name, skill.Description, GetSourceLabel(skill.SharedSource), $"skill:{skill.Id:N}", skill);

    public static ShareableCapabilityItem FromAgent(LumiAgent agent)
        => new("Lumi", 1, agent.Name, agent.Description, GetSourceLabel(agent.SharedSource), $"lumi:{agent.Id:N}", agent);

    public static ShareableCapabilityItem FromMemory(Memory memory)
        => new("Memory", 2, memory.Key, memory.Content, GetSourceLabel(memory.SharedSource), $"memory:{memory.Id:N}", memory);

    private static string GetSourceLabel(SharedCapabilitySource? source)
        => source is null || string.IsNullOrWhiteSpace(source.RepositoryName)
            ? "Personal"
            : $"Shared · {source.RepositoryName}";
}
