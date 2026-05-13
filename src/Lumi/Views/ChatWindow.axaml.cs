using System;
using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class ChatWindow : Window
{
    private readonly ChatViewModel? _viewModel;
    private ChatView? _chatView;
    private ChatPreviewPanelController? _previewPanel;

    public ChatWindow()
    {
        InitializeComponent();
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 38;
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
    }

    public ChatWindow(DataStore dataStore, ChatViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        _previewPanel = CreatePreviewPanel(dataStore, viewModel);
        WireViewModel(viewModel);
        UpdateTitle();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _chatView = this.FindControl<ChatView>("DetachedChatView");
    }

    public void FocusComposer()
    {
        Dispatcher.UIThread.Post(() => _chatView?.FocusComposer(), DispatcherPriority.Input);
    }

    private ChatPreviewPanelController CreatePreviewPanel(DataStore dataStore, ChatViewModel viewModel)
    {
        var contentGrid = this.FindControl<Grid>("ChatContentGrid")
            ?? throw new InvalidOperationException("Chat window is missing ChatContentGrid.");
        var chatPane = this.FindControl<ChatView>("DetachedChatView")
            ?? throw new InvalidOperationException("Chat window is missing DetachedChatView.");
        var browserPanel = this.FindControl<Border>("BrowserIsland")
            ?? throw new InvalidOperationException("Chat window is missing BrowserIsland.");
        var browserHost = this.FindControl<ContentControl>("BrowserHost")
            ?? throw new InvalidOperationException("Chat window is missing BrowserHost.");
        var diffPanel = this.FindControl<Border>("DiffIsland")
            ?? throw new InvalidOperationException("Chat window is missing DiffIsland.");
        var diffHost = this.FindControl<ContentControl>("DiffHost")
            ?? throw new InvalidOperationException("Chat window is missing DiffHost.");
        var diffTitle = this.FindControl<TextBlock>("DiffFileNameText")
            ?? throw new InvalidOperationException("Chat window is missing DiffFileNameText.");
        var planPanel = this.FindControl<Border>("PlanIsland")
            ?? throw new InvalidOperationException("Chat window is missing PlanIsland.");

        var controller = new ChatPreviewPanelController(
            this,
            dataStore,
            viewModel,
            contentGrid,
            chatPane,
            this.FindControl<GridSplitter>("PreviewSplitter"),
            browserPanel,
            browserHost,
            diffPanel,
            diffHost,
            diffTitle,
            planPanel);

        if (this.FindControl<Button>("CloseBrowserButton") is { } closeBrowserButton)
            closeBrowserButton.Click += (_, _) => controller.HideBrowserPanel();
        if (this.FindControl<Button>("CloseDiffButton") is { } closeDiffButton)
            closeDiffButton.Click += (_, _) => controller.HideDiffPanel();
        if (this.FindControl<Button>("ClosePlanButton") is { } closePlanButton)
            closePlanButton.Click += (_, _) => controller.HidePlanPanel();

        return controller;
    }

    private void WireViewModel(ChatViewModel viewModel)
    {
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.BrowserShowRequested += ShowBrowserPanel;
        viewModel.BrowserHideRequested += HideBrowserPanel;
        viewModel.DiffShowRequested += ShowDiffPanel;
        viewModel.DiffHideRequested += HideDiffPanel;
        viewModel.GitChangesShowRequested += ShowGitChangesPanel;
        viewModel.PlanShowRequested += ShowPlanPanel;
        viewModel.PlanHideRequested += HidePlanPanel;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.BrowserShowRequested -= ShowBrowserPanel;
            _viewModel.BrowserHideRequested -= HideBrowserPanel;
            _viewModel.DiffShowRequested -= ShowDiffPanel;
            _viewModel.DiffHideRequested -= HideDiffPanel;
            _viewModel.GitChangesShowRequested -= ShowGitChangesPanel;
            _viewModel.PlanShowRequested -= ShowPlanPanel;
            _viewModel.PlanHideRequested -= HidePlanPanel;
        }

        _previewPanel?.Dispose();
        _previewPanel = null;
        base.OnClosed(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.CurrentChatTitle) or nameof(ChatViewModel.CurrentChat))
            UpdateTitle();
    }

    private void UpdateTitle()
    {
        var title = _viewModel?.CurrentChatTitle;
        var windowTitle = string.IsNullOrWhiteSpace(title) ? Loc.Sidebar_NewChat : title;
        Title = windowTitle;
        AutomationProperties.SetName(this, $"{windowTitle} - Lumi");
    }

    private void ShowBrowserPanel(Guid chatId) => _previewPanel?.ShowBrowserPanel(chatId);
    private void HideBrowserPanel() => _previewPanel?.HideBrowserPanel();
    private void ShowDiffPanel(FileChangeItem fileChange) => _previewPanel?.ShowDiffPanel(fileChange);
    private void HideDiffPanel() => _previewPanel?.HideDiffPanel();
    private void ShowGitChangesPanel(System.Collections.Generic.List<GitFileChangeViewModel> files)
        => _previewPanel?.ShowGitChangesPanel(files);
    private void ShowPlanPanel() => _previewPanel?.ShowPlanPanel();
    private void HidePlanPanel() => _previewPanel?.HidePlanPanel();
}
