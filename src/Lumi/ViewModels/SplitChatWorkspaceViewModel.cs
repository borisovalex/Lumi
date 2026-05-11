using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public enum SplitChatDragSource
{
    Sidebar,
    PaneHeader
}

public sealed partial class SplitChatPaneViewModel : ObservableObject
{
    private Chat? _titleSource;

    public SplitChatPaneViewModel(ChatViewModel chatViewModel, Chat chat, bool usesPrimaryViewModel)
    {
        PaneId = Guid.NewGuid();
        ChatViewModel = chatViewModel;
        UsesPrimaryViewModel = usesPrimaryViewModel;
        Chat = chat;
        ChatViewModel.PropertyChanged += OnChatViewModelPropertyChanged;
    }

    public Guid PaneId { get; }
    public ChatViewModel ChatViewModel { get; }
    public bool UsesPrimaryViewModel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChatId))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private Chat? _chat;

    [ObservableProperty] private bool _isFocused;

    public Guid? ChatId => Chat?.Id;
    public string Title => Chat?.Title ?? string.Empty;

    partial void OnChatChanged(Chat? oldValue, Chat? newValue)
    {
        if (_titleSource is not null)
            _titleSource.PropertyChanged -= OnChatPropertyChanged;

        _titleSource = newValue;
        if (_titleSource is not null)
            _titleSource.PropertyChanged += OnChatPropertyChanged;
        OnPropertyChanged(nameof(ChatId));
        OnPropertyChanged(nameof(Title));
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Chat.Title))
            OnPropertyChanged(nameof(Title));
    }

    private void OnChatViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.CurrentChat)
            && !ReferenceEquals(ChatViewModel.CurrentChat, Chat))
            Chat = ChatViewModel.CurrentChat;
    }

    public void Detach()
    {
        ChatViewModel.PropertyChanged -= OnChatViewModelPropertyChanged;
        if (_titleSource is not null)
            _titleSource.PropertyChanged -= OnChatPropertyChanged;
        _titleSource = null;
    }
}

public sealed partial class SplitChatWorkspaceViewModel : ObservableObject
{
    public const int MaxPanes = 8;

    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly ChatViewModel _primaryChatViewModel;
    private readonly Dictionary<ChatViewModel, PaneChatViewModelSubscriptions> _secondarySubscriptions = [];

    public SplitChatWorkspaceViewModel(
        DataStore dataStore,
        CopilotService copilotService,
        ChatViewModel primaryChatViewModel)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _primaryChatViewModel = primaryChatViewModel;
        Panes.CollectionChanged += (_, _) => RefreshLayoutState();
    }

    public ObservableCollection<SplitChatPaneViewModel> Panes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusedChatId))]
    private SplitChatPaneViewModel? _focusedPane;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private int _rows = 1;
    [ObservableProperty] private int _columns = 1;

    public Guid? FocusedChatId => FocusedPane?.ChatId;
    public bool CanAddPane => Panes.Count < MaxPanes;
    public IEnumerable<ChatViewModel> ChatViewModels => Panes.Select(pane => pane.ChatViewModel).Distinct();

    public event Action<Guid?>? FocusedChatChanged;
    public event Action? ChatUpdated;
    public event Action? FeatureManagementStateChanged;
    public event Action<Guid, string>? ChatTitleChanged;
    public event Action<ChatViewModel, Guid>? BrowserShowRequested;
    public event Action<ChatViewModel>? BrowserHideRequested;
    public event Action<ChatViewModel, FileChangeItem>? DiffShowRequested;
    public event Action<ChatViewModel>? DiffHideRequested;
    public event Action<ChatViewModel>? PlanShowRequested;
    public event Action<ChatViewModel>? PlanHideRequested;
    public event Action<ChatViewModel, List<GitFileChangeViewModel>>? GitChangesShowRequested;

    partial void OnFocusedPaneChanged(SplitChatPaneViewModel? oldValue, SplitChatPaneViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsFocused = false;
        if (newValue is not null)
            newValue.IsFocused = true;

        OnPropertyChanged(nameof(FocusedChatId));
        FocusedChatChanged?.Invoke(newValue?.ChatId);
    }

    public async Task OpenChatInSplitViewAsync(Chat chat)
    {
        if (!IsActive)
        {
            await ActivateFromSingleChatAsync(chat);
            return;
        }

        var existing = FindPane(chat.Id);
        if (existing is not null)
        {
            FocusPane(existing);
            return;
        }

        if (!CanAddPane)
        {
            await ReplaceFocusedChatAsync(chat);
            return;
        }

        if (FocusedPane?.Chat is null)
        {
            await ReplaceFocusedChatAsync(chat);
            return;
        }

        await AddPaneAsync(chat);
    }

    public async Task ReplaceFocusedChatAsync(Chat chat)
    {
        if (!IsActive)
        {
            await _primaryChatViewModel.LoadChatAsync(chat);
            return;
        }

        var target = FocusedPane ?? Panes.FirstOrDefault();
        if (target is null)
        {
            await OpenChatInSplitViewAsync(chat);
            return;
        }

        var existing = FindPane(chat.Id);
        if (existing is not null)
        {
            if (existing == target || target.Chat is not null)
            {
                FocusPane(existing);
                return;
            }

            await MoveExistingPaneIntoDraftSlotAsync(existing, target);
            return;
        }

        await LoadPaneChatAsync(target, chat);
        FocusPane(target);
    }

    public async Task DropSidebarChatAsync(Chat chat, int targetIndex)
    {
        if (!IsActive)
        {
            await ActivateFromDropAsync(chat, targetIndex);
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, Panes.Count);
        if (targetIndex >= Panes.Count)
        {
            await OpenChatInSplitViewAsync(chat);
            return;
        }

        await ReplacePaneChatAsync(Panes[targetIndex], chat);
    }

    public void MovePane(Guid paneId, int targetIndex)
    {
        if (!IsActive || Panes.Count < 2)
            return;

        var source = Panes.FirstOrDefault(pane => pane.PaneId == paneId);
        if (source is null)
            return;

        var oldIndex = Panes.IndexOf(source);
        targetIndex = Math.Clamp(targetIndex, 0, Panes.Count - 1);
        if (oldIndex == targetIndex)
        {
            FocusPane(source);
            return;
        }

        Panes.Move(oldIndex, targetIndex);
        FocusPane(source);
    }

    public async Task ClosePaneAsync(SplitChatPaneViewModel pane)
    {
        if (!Panes.Contains(pane))
            return;

        var oldIndex = Panes.IndexOf(pane);
        var wasFocused = pane == FocusedPane;
        RemovePane(pane);

        if (Panes.Count == 0)
        {
            await ExitSplitViewAsync();
            return;
        }

        if (wasFocused)
            FocusPane(Panes[Math.Min(oldIndex, Panes.Count - 1)]);

        if (Panes.Count == 1)
            await ExitToSinglePaneAsync(Panes[0]);
    }

    private async Task ExitToSinglePaneAsync(SplitChatPaneViewModel remainingPane)
    {
        var focusedChat = remainingPane.Chat;
        var shouldClearPrimary = focusedChat is null;
        var shouldLoadPrimary = focusedChat is not null && _primaryChatViewModel.CurrentChat?.Id != focusedChat.Id;

        RemovePane(remainingPane);
        FocusedPane = null;
        IsActive = false;
        RefreshLayoutState();

        if (shouldClearPrimary)
            _primaryChatViewModel.ClearChat();
        else if (shouldLoadPrimary)
            await _primaryChatViewModel.LoadChatAsync(focusedChat!);
    }

    public async Task ExitSplitViewAsync()
    {
        var hadFocusedPane = FocusedPane is not null;
        var focusedChat = FocusedPane?.Chat;
        var shouldClearPrimary = hadFocusedPane && focusedChat is null;
        var shouldLoadPrimary = focusedChat is not null && _primaryChatViewModel.CurrentChat?.Id != focusedChat.Id;

        foreach (var pane in Panes.ToList())
            RemovePane(pane);

        FocusedPane = null;
        IsActive = false;
        RefreshLayoutState();

        if (shouldClearPrimary)
            _primaryChatViewModel.ClearChat();
        else if (shouldLoadPrimary)
            await _primaryChatViewModel.LoadChatAsync(focusedChat!);
    }

    public void FocusPane(SplitChatPaneViewModel? pane)
    {
        if (pane is null || !Panes.Contains(pane))
            return;

        if (FocusedPane == pane)
        {
            OnPropertyChanged(nameof(FocusedChatId));
            FocusedChatChanged?.Invoke(pane.ChatId);
            return;
        }

        FocusedPane = pane;
    }

    public ChatViewModel? StartNewChatInFocusedPane(Guid? projectId)
    {
        if (!IsActive)
            return null;

        var pane = FocusedPane ?? Panes.FirstOrDefault();
        if (pane is null)
            return null;

        if (FocusedPane != pane)
            FocusPane(pane);

        pane.ChatViewModel.ClearChat();
        pane.Chat = null;
        if (projectId.HasValue)
            pane.ChatViewModel.SetProjectId(projectId.Value);
        else
            pane.ChatViewModel.ClearProjectId();

        OnPropertyChanged(nameof(FocusedChatId));
        FocusedChatChanged?.Invoke(pane.ChatId);
        return pane.ChatViewModel;
    }

    public void SyncAvailableModelsFromPrimary()
    {
        foreach (var pane in Panes)
            SyncModelState(pane.ChatViewModel);
    }

    public async Task RemoveChatAsync(Guid chatId)
    {
        var removedFocusedPane = FocusedPane?.ChatId == chatId;
        foreach (var pane in Panes.Where(pane => pane.ChatId == chatId).ToList())
            RemovePane(pane);

        foreach (var viewModel in _secondarySubscriptions.Keys.ToList())
            viewModel.CleanupSession(chatId);

        if (Panes.Count == 0)
        {
            FocusedPane = null;
            IsActive = false;
            RefreshLayoutState();
            return;
        }

        if (removedFocusedPane || FocusedPane is null || !Panes.Contains(FocusedPane))
            FocusPane(Panes[0]);

        if (Panes.Count == 1)
            await ExitSplitViewAsync();
    }

    public void CleanupChat(Guid chatId)
    {
        foreach (var viewModel in _secondarySubscriptions.Keys.ToList())
            viewModel.CleanupSession(chatId);

        RefreshLayoutState();
    }

    private async Task ActivateFromSingleChatAsync(Chat requestedChat)
    {
        var currentChat = _primaryChatViewModel.CurrentChat;
        if (currentChat is null)
        {
            await AddPrimaryPaneAsync(requestedChat);
            IsActive = true;
            return;
        }

        await AddPrimaryPaneAsync(currentChat);
        if (currentChat.Id != requestedChat.Id)
            await AddPaneAsync(requestedChat);
        IsActive = true;
    }

    private async Task ActivateFromDropAsync(Chat droppedChat, int targetIndex)
    {
        var currentChat = _primaryChatViewModel.CurrentChat;
        if (currentChat is null || currentChat.Id == droppedChat.Id)
        {
            await AddPrimaryPaneAsync(droppedChat);
            IsActive = true;
            return;
        }

        if (targetIndex <= 0)
        {
            var droppedPane = await AddPaneAsync(droppedChat);
            await AddPrimaryPaneAsync(currentChat);
            FocusPane(droppedPane);
        }
        else
        {
            await AddPrimaryPaneAsync(currentChat);
            await AddPaneAsync(droppedChat);
        }

        IsActive = true;
    }

    private async Task AddPrimaryPaneAsync(Chat chat)
    {
        if (_primaryChatViewModel.CurrentChat?.Id != chat.Id)
            await _primaryChatViewModel.LoadChatAsync(chat);

        var pane = new SplitChatPaneViewModel(_primaryChatViewModel, chat, usesPrimaryViewModel: true);
        Panes.Add(pane);
        FocusPane(pane);
    }

    private async Task<SplitChatPaneViewModel> AddPaneAsync(Chat chat)
    {
        var chatViewModel = new ChatViewModel(_dataStore, _copilotService);
        SyncModelState(chatViewModel);
        WireSecondaryChatViewModel(chatViewModel);
        await chatViewModel.LoadChatAsync(chat);

        var pane = new SplitChatPaneViewModel(chatViewModel, chat, usesPrimaryViewModel: false);
        Panes.Add(pane);
        FocusPane(pane);
        return pane;
    }

    private async Task ReplacePaneChatAsync(SplitChatPaneViewModel pane, Chat chat)
    {
        var existing = FindPane(chat.Id);
        if (existing is not null && existing != pane)
        {
            if (pane.Chat is null)
            {
                await MoveExistingPaneIntoDraftSlotAsync(existing, pane);
                return;
            }

            FocusPane(existing);
            return;
        }

        await LoadPaneChatAsync(pane, chat);
        FocusPane(pane);
    }

    private async Task MoveExistingPaneIntoDraftSlotAsync(
        SplitChatPaneViewModel existing,
        SplitChatPaneViewModel draftPane)
    {
        var targetIndex = Panes.IndexOf(draftPane);
        if (targetIndex < 0 || !Panes.Contains(existing))
        {
            FocusPane(existing);
            return;
        }

        RemovePane(draftPane);
        if (!Panes.Contains(existing))
            return;

        var existingIndex = Panes.IndexOf(existing);
        var destinationIndex = Math.Min(targetIndex, Panes.Count - 1);
        if (existingIndex >= 0 && existingIndex != destinationIndex)
            Panes.Move(existingIndex, destinationIndex);

        FocusPane(existing);
        if (Panes.Count == 1)
            await ExitToSinglePaneAsync(existing);
    }

    private static async Task LoadPaneChatAsync(SplitChatPaneViewModel pane, Chat chat)
    {
        await pane.ChatViewModel.LoadChatAsync(chat);
        pane.Chat = chat;
    }

    private SplitChatPaneViewModel? FindPane(Guid chatId)
        => Panes.FirstOrDefault(pane => pane.ChatId == chatId);

    private void RemovePane(SplitChatPaneViewModel pane)
    {
        if (!Panes.Remove(pane))
            return;

        pane.Detach();
        if (!pane.UsesPrimaryViewModel)
            UnwireSecondaryChatViewModel(pane.ChatViewModel);
    }

    private void RefreshLayoutState()
    {
        (Rows, Columns) = GetGridDimensions(Panes.Count);
        IsActive = Panes.Count > 0;
        OnPropertyChanged(nameof(CanAddPane));
    }

    internal static (int Rows, int Columns) GetGridDimensions(int paneCount)
        => paneCount switch
        {
            <= 1 => (1, 1),
            2 => (1, 2),
            <= 4 => (2, 2),
            <= 6 => (2, 3),
            _ => (2, 4)
        };

    private void SyncModelState(ChatViewModel chatViewModel)
    {
        foreach (var model in _primaryChatViewModel.AvailableModels)
        {
            if (!chatViewModel.AvailableModels.Contains(model))
                chatViewModel.AvailableModels.Add(model);
        }

        chatViewModel.ApplyModelSelection(
            _primaryChatViewModel.SelectedModel,
            _primaryChatViewModel.GetSelectedReasoningEffort());
    }

    private void WireSecondaryChatViewModel(ChatViewModel chatViewModel)
    {
        if (ReferenceEquals(chatViewModel, _primaryChatViewModel)
            || _secondarySubscriptions.ContainsKey(chatViewModel))
            return;

        var subscriptions = new PaneChatViewModelSubscriptions(this, chatViewModel);
        _secondarySubscriptions[chatViewModel] = subscriptions;
    }

    private void UnwireSecondaryChatViewModel(ChatViewModel chatViewModel)
    {
        if (_secondarySubscriptions.Remove(chatViewModel, out var subscriptions))
            subscriptions.Dispose();
    }

    private sealed class PaneChatViewModelSubscriptions : IDisposable
    {
        private readonly SplitChatWorkspaceViewModel _owner;
        private readonly ChatViewModel _chatViewModel;

        public PaneChatViewModelSubscriptions(SplitChatWorkspaceViewModel owner, ChatViewModel chatViewModel)
        {
            _owner = owner;
            _chatViewModel = chatViewModel;
            _chatViewModel.ChatUpdated += OnChatUpdated;
            _chatViewModel.FeatureManagementStateChanged += OnFeatureManagementStateChanged;
            _chatViewModel.ChatTitleChanged += OnChatTitleChanged;
            _chatViewModel.BrowserShowRequested += OnBrowserShowRequested;
            _chatViewModel.BrowserHideRequested += OnBrowserHideRequested;
            _chatViewModel.DiffShowRequested += OnDiffShowRequested;
            _chatViewModel.DiffHideRequested += OnDiffHideRequested;
            _chatViewModel.PlanShowRequested += OnPlanShowRequested;
            _chatViewModel.PlanHideRequested += OnPlanHideRequested;
            _chatViewModel.GitChangesShowRequested += OnGitChangesShowRequested;
        }

        private void OnChatUpdated() => _owner.ChatUpdated?.Invoke();
        private void OnFeatureManagementStateChanged() => _owner.FeatureManagementStateChanged?.Invoke();
        private void OnChatTitleChanged(Guid chatId, string title) => _owner.ChatTitleChanged?.Invoke(chatId, title);
        private void OnBrowserShowRequested(Guid chatId) => _owner.BrowserShowRequested?.Invoke(_chatViewModel, chatId);
        private void OnBrowserHideRequested() => _owner.BrowserHideRequested?.Invoke(_chatViewModel);
        private void OnDiffShowRequested(FileChangeItem item) => _owner.DiffShowRequested?.Invoke(_chatViewModel, item);
        private void OnDiffHideRequested() => _owner.DiffHideRequested?.Invoke(_chatViewModel);
        private void OnPlanShowRequested() => _owner.PlanShowRequested?.Invoke(_chatViewModel);
        private void OnPlanHideRequested() => _owner.PlanHideRequested?.Invoke(_chatViewModel);
        private void OnGitChangesShowRequested(List<GitFileChangeViewModel> files)
            => _owner.GitChangesShowRequested?.Invoke(_chatViewModel, files);

        public void Dispose()
        {
            _chatViewModel.ChatUpdated -= OnChatUpdated;
            _chatViewModel.FeatureManagementStateChanged -= OnFeatureManagementStateChanged;
            _chatViewModel.ChatTitleChanged -= OnChatTitleChanged;
            _chatViewModel.BrowserShowRequested -= OnBrowserShowRequested;
            _chatViewModel.BrowserHideRequested -= OnBrowserHideRequested;
            _chatViewModel.DiffShowRequested -= OnDiffShowRequested;
            _chatViewModel.DiffHideRequested -= OnDiffHideRequested;
            _chatViewModel.PlanShowRequested -= OnPlanShowRequested;
            _chatViewModel.PlanHideRequested -= OnPlanHideRequested;
            _chatViewModel.GitChangesShowRequested -= OnGitChangesShowRequested;
        }
    }
}
