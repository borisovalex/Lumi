using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class ChatWorkspaceView : UserControl, IDisposable
{
    public static readonly StyledProperty<bool> ShowInternalTitleProperty =
        AvaloniaProperty.Register<ChatWorkspaceView, bool>(nameof(ShowInternalTitle), true);

    public static readonly StyledProperty<bool> UseChatIslandChromeProperty =
        AvaloniaProperty.Register<ChatWorkspaceView, bool>(nameof(UseChatIslandChrome), true);

    public static readonly StyledProperty<Thickness> PreviewIslandMarginProperty =
        AvaloniaProperty.Register<ChatWorkspaceView, Thickness>(nameof(PreviewIslandMargin), new Thickness(0));

    private ChatPreviewPanelController? _previewPanel;
    private ChatView? _chatView;
    private Border? _chatIsland;
    private Border? _chatIslandHighlight;
    private DataStore? _dataStore;
    private DataStore? _attachedDataStore;
    private ChatViewModel? _attachedChatViewModel;

    public ChatWorkspaceView()
    {
        InitializeComponent();
    }

    public bool ShowInternalTitle
    {
        get => GetValue(ShowInternalTitleProperty);
        set => SetValue(ShowInternalTitleProperty, value);
    }

    public bool UseChatIslandChrome
    {
        get => GetValue(UseChatIslandChromeProperty);
        set => SetValue(UseChatIslandChromeProperty, value);
    }

    public Thickness PreviewIslandMargin
    {
        get => GetValue(PreviewIslandMarginProperty);
        set => SetValue(PreviewIslandMarginProperty, value);
    }

    public DataStore? DataStore
    {
        get => _dataStore;
        set
        {
            if (ReferenceEquals(_dataStore, value))
                return;

            _dataStore = value;
            ReconnectPreviewPanel();
        }
    }

    public Action? EnsureChatVisible { get; set; }

    public Func<Guid, bool>? CanShowBrowserPanel { get; set; }

    public ChatView? ChatView => _chatView;

    public bool IsBrowserOpen => _previewPanel?.IsBrowserOpen == true;

    public bool IsDiffOpen => _previewPanel?.IsDiffOpen == true;

    public bool IsPlanOpen => _previewPanel?.IsPlanOpen == true;

    public void FocusComposer() => _chatView?.FocusComposer();

    public void ShowBrowserPanel(Guid chatId) => _previewPanel?.ShowBrowserPanel(chatId);

    public void HideBrowserPanel() => _previewPanel?.HideBrowserPanel();

    public void ShowCurrentBrowserController() => _previewPanel?.ShowCurrentBrowserController();

    public void ShowDiffPanel(FileChangeItem fileChange) => _previewPanel?.ShowDiffPanel(fileChange);

    public void HideDiffPanel() => _previewPanel?.HideDiffPanel();

    public void ShowGitChangesPanel(List<GitFileChangeViewModel> files) => _previewPanel?.ShowGitChangesPanel(files);

    public void ShowPlanPanel() => _previewPanel?.ShowPlanPanel();

    public void HidePlanPanel() => _previewPanel?.HidePlanPanel();

    public void Dispose()
    {
        DisposePreviewPanel();
        GC.SuppressFinalize(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ReconnectPreviewPanel();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ReconnectPreviewPanel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DisposePreviewPanel();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == UseChatIslandChromeProperty)
            ApplyChatIslandChrome();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _chatView = this.FindControl<ChatView>("PageChat");
        _chatIsland = this.FindControl<Border>("ChatIsland");
        _chatIslandHighlight = this.FindControl<Border>("ChatIslandHighlight");

        AutomationProperties.SetName(this, "Page 0 Chat content grid");
        AutomationProperties.SetHelpText(this, "Stable Lumi chat workspace landmark for coding agents and MCP diagnostics.");
        if (_chatView is not null)
        {
            AutomationProperties.SetName(_chatView, "Page 0 Chat view");
            AutomationProperties.SetHelpText(_chatView, "Stable Lumi chat view landmark for coding agents and MCP diagnostics.");
        }

        if (this.FindControl<Button>("CloseBrowserButton") is { } closeBrowserButton)
            closeBrowserButton.Click += (_, _) => HideBrowserPanel();
        if (this.FindControl<Button>("CloseDiffButton") is { } closeDiffButton)
            closeDiffButton.Click += (_, _) => HideDiffPanel();
        if (this.FindControl<Button>("ClosePlanButton") is { } closePlanButton)
            closePlanButton.Click += (_, _) => HidePlanPanel();

        ApplyChatIslandChrome();
    }

    private void ApplyChatIslandChrome()
    {
        if (_chatIsland is null)
            return;

        _chatIsland.Classes.Set("workspace-island", UseChatIslandChrome);
        _chatIsland.Classes.Set("workspace-flat", !UseChatIslandChrome);

        if (_chatIslandHighlight is not null)
            _chatIslandHighlight.IsVisible = UseChatIslandChrome;
    }

    private void ReconnectPreviewPanel()
    {
        var chatViewModel = DataContext as ChatViewModel;

        if (ReferenceEquals(_attachedDataStore, _dataStore) &&
            ReferenceEquals(_attachedChatViewModel, chatViewModel))
        {
            return;
        }

        DisposePreviewPanel();

        if (_dataStore is null || chatViewModel is null)
            return;

        var contentGrid = this.FindControl<Grid>("WorkspaceGrid")
            ?? throw new InvalidOperationException("Chat workspace is missing WorkspaceGrid.");
        var chatIsland = this.FindControl<Control>("ChatIsland")
            ?? throw new InvalidOperationException("Chat workspace is missing ChatIsland.");
        var previewSplitter = this.FindControl<GridSplitter>("PreviewSplitter");
        var browserPanel = this.FindControl<Border>("BrowserIsland")
            ?? throw new InvalidOperationException("Chat workspace is missing BrowserIsland.");
        var browserHost = this.FindControl<ContentControl>("BrowserHost")
            ?? throw new InvalidOperationException("Chat workspace is missing BrowserHost.");
        var diffPanel = this.FindControl<Border>("DiffIsland")
            ?? throw new InvalidOperationException("Chat workspace is missing DiffIsland.");
        var diffHost = this.FindControl<ContentControl>("DiffHost")
            ?? throw new InvalidOperationException("Chat workspace is missing DiffHost.");
        var diffFileNameText = this.FindControl<TextBlock>("DiffFileNameText")
            ?? throw new InvalidOperationException("Chat workspace is missing DiffFileNameText.");
        var planPanel = this.FindControl<Border>("PlanIsland")
            ?? throw new InvalidOperationException("Chat workspace is missing PlanIsland.");

        _previewPanel = new ChatPreviewPanelController(
            this,
            _dataStore,
            chatViewModel,
            contentGrid,
            chatIsland,
            previewSplitter,
            browserPanel,
            browserHost,
            diffPanel,
            diffHost,
            diffFileNameText,
            planPanel,
            ensureChatVisible: () => EnsureChatVisible?.Invoke(),
            canShowBrowserPanel: chatId => CanShowBrowserPanel?.Invoke(chatId) != false);

        _attachedDataStore = _dataStore;
        _attachedChatViewModel = chatViewModel;
    }

    private void DisposePreviewPanel()
    {
        _previewPanel?.Dispose();
        _previewPanel = null;
        _attachedDataStore = null;
        _attachedChatViewModel = null;
    }
}
