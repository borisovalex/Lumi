using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class SharingViewModel : ObservableObject, IDisposable
{
    private readonly DataStore _dataStore;
    private readonly LumiSharingService _sharingService;

    public event Action? CapabilitiesChanged;

    [ObservableProperty] private LumiSharedRepository? _selectedRepository;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isRepositoryDialogOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editRepository = "";
    [ObservableProperty] private string _editBranch = "";
    [ObservableProperty] private int _editUpdateIntervalMinutes = 60;
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private string _statusMessage = "Connect Git repositories that share Lumi capabilities with other people.";

    public ObservableCollection<LumiSharedRepository> Repositories { get; } = [];
    public bool HasRepositories => Repositories.Count > 0;
    public bool HasNoRepositories => !HasRepositories;
    public bool HasSelectedRepository => SelectedRepository is not null;
    public bool CanSyncSelectedRepository => SelectedRepository is not null && !IsBusy;
    public bool CanEditSelectedRepository => SelectedRepository is not null && !IsBusy;
    public bool CanDeleteSelectedRepository => SelectedRepository is not null && !IsBusy;
    public string RepositorySummary => Repositories.Count == 1
        ? "1 repository connected"
        : $"{Repositories.Count} repositories connected";
    public string RepositoryDialogTitle => SelectedRepository is null
        ? "Add capability repository"
        : "Edit capability repository";
    public string RepositoryDialogDescription => "Paste a Git URL or choose a local folder. Lumi imports shared skills, Lumis, MCP servers, and memories from this repo.";

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
        IsRepositoryDialogOpen = true;
        StatusMessage = "Add a Git repository that contains shared Lumi capabilities.";
        NotifyRepositoryStateChanged();
    }

    [RelayCommand]
    private void EditSelectedRepository()
    {
        if (SelectedRepository is null)
            return;

        OpenRepositoryDialog(SelectedRepository);
    }

    [RelayCommand]
    private void OpenRepositoryDialog(LumiSharedRepository? repository)
    {
        if (repository is null)
            return;

        SelectedRepository = repository;
        SyncEditorFromRepository(repository);
        IsEditing = true;
        IsRepositoryDialogOpen = true;
        StatusMessage = $"Editing {repository.DisplayName}.";
        NotifyRepositoryStateChanged();
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

        var branch = EditBranch.Trim();
        var repository = SelectedRepository;
        var duplicate = FindDuplicateRepository(repositoryLocation, branch, repository?.Id);
        if (duplicate is not null)
        {
            SelectedRepository = duplicate;
            StatusMessage = $"That repo and branch are already connected as {duplicate.DisplayName}.";
            NotifyRepositoryStateChanged();
            return;
        }

        if (repository is null)
        {
            repository = new LumiSharedRepository();
            _dataStore.Data.SharedRepositories.Add(repository);
        }

        repository.Name = name;
        repository.Repository = repositoryLocation;
        repository.Branch = branch;
        repository.UpdateIntervalMinutes = interval;
        repository.IsEnabled = EditIsEnabled;
        repository.NextSyncAt = repository.IsEnabled ? null : repository.NextSyncAt;

        await _dataStore.SaveAsync();
        IsEditing = false;
        IsRepositoryDialogOpen = false;
        SelectedRepository = repository;
        RefreshRepositories();
        StatusMessage = $"Saved {repository.DisplayName}. Syncing now...";
        await SyncRepositoryAsync(repository);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        IsRepositoryDialogOpen = false;
        if (SelectedRepository is not null)
            SyncEditorFromRepository(SelectedRepository);
        NotifyRepositoryStateChanged();
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
        StatusMessage = "Syncing connected repositories...";
        try
        {
            var result = await _sharingService.SyncDueRepositoriesAsync(force: true);
            RefreshFromStore();
            StatusMessage = result.RepositoryCount == 1
                ? "Synced 1 repository."
                : $"Synced {result.RepositoryCount} repositories.";
            CapabilitiesChanged?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncRepositoryItemAsync(LumiSharedRepository? repository)
    {
        if (repository is null)
            return;

        SelectedRepository = repository;
        await SyncRepositoryAsync(repository);
    }

    [RelayCommand]
    private async Task DeleteRepositoryItemAsync(LumiSharedRepository? repository)
    {
        if (repository is null)
            return;

        SelectedRepository = repository;
        await DeleteSelectedRepositoryAsync();
    }

    private async Task SyncRepositoryAsync(LumiSharedRepository repository)
    {
        IsBusy = true;
        try
        {
            var result = await _sharingService.SyncRepositoryAsync(repository);
            RefreshFromStore();
            StatusMessage = result.RepositoryCount == 0
                ? "No repositories synced."
                : $"Synced {repository.DisplayName}.";
            CapabilitiesChanged?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = ex.Message;
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

        LumiSharedRepository? selected = null;
        if (selectedId is not null)
            selected = _dataStore.Data.SharedRepositories.FirstOrDefault(repository => repository.Id == selectedId);
        selected ??= Repositories.FirstOrDefault();
        SelectedRepository = selected;
        NotifyRepositoryStateChanged();
    }

    partial void OnSelectedRepositoryChanged(LumiSharedRepository? value)
    {
        if (value is null)
        {
            NotifyRepositoryStateChanged();
            return;
        }

        SyncEditorFromRepository(value);
        NotifyRepositoryStateChanged();
    }

    partial void OnIsBusyChanged(bool value) => NotifyRepositoryStateChanged();

    partial void OnIsRepositoryDialogOpenChanged(bool value) => NotifyRepositoryStateChanged();

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

    private LumiSharedRepository? FindDuplicateRepository(string repositoryLocation, string branch, Guid? excludedId)
    {
        var key = BuildRepositoryIdentityKey(repositoryLocation, branch);
        return _dataStore.Data.SharedRepositories.FirstOrDefault(repository =>
            repository.Id != excludedId
            && string.Equals(BuildRepositoryIdentityKey(repository.Repository, repository.Branch), key, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildRepositoryIdentityKey(string repositoryLocation, string branch)
        => $"{NormalizeRepositoryLocation(repositoryLocation)}\n{NormalizeBranchName(branch)}";

    private static string NormalizeRepositoryLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var trimmed = value.Trim().TrimEnd('/', '\\');
        if (LooksLikeRemoteGitReference(trimmed))
        {
            return trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? trimmed[..^4]
                : trimmed;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(trimmed))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }

    private static string NormalizeBranchName(string? value)
    {
        var branch = value?.Trim() ?? "";
        const string headsPrefix = "refs/heads/";
        return branch.StartsWith(headsPrefix, StringComparison.OrdinalIgnoreCase)
            ? branch[headsPrefix.Length..]
            : branch;
    }

    private static bool LooksLikeRemoteGitReference(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
           || value.Contains('@') && value.Contains(':');

    private void NotifyRepositoryStateChanged()
    {
        OnPropertyChanged(nameof(HasRepositories));
        OnPropertyChanged(nameof(HasNoRepositories));
        OnPropertyChanged(nameof(HasSelectedRepository));
        OnPropertyChanged(nameof(CanSyncSelectedRepository));
        OnPropertyChanged(nameof(CanEditSelectedRepository));
        OnPropertyChanged(nameof(CanDeleteSelectedRepository));
        OnPropertyChanged(nameof(RepositorySummary));
        OnPropertyChanged(nameof(RepositoryDialogTitle));
        OnPropertyChanged(nameof(RepositoryDialogDescription));
    }

    public void Dispose()
    {
        _sharingService.RepositoriesChanged -= OnSharingServiceRepositoriesChanged;
        _sharingService.CapabilitiesChanged -= OnSharingServiceCapabilitiesChanged;
        _sharingService.Dispose();
    }
}
