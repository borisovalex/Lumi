using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class MainWindow
{
    private void HideSharedSidePanelsForActiveChatChange(MainViewModel vm)
    {
        if (!vm.SplitWorkspace.IsActive)
        {
            HideBrowserPanel();
            HideDiffPanel();
            HidePlanPanel();
            return;
        }

        if (!IsSplitSidePanelOwnerVisible(vm, _browserPanelOwner))
            HideBrowserPanel();
        if (!IsSplitSidePanelOwnerVisible(vm, _diffPanelOwner))
            HideDiffPanel();
        if (!IsSplitSidePanelOwnerVisible(vm, _planPanelOwner))
            HidePlanPanel();
    }

    private static bool IsSplitSidePanelOwnerVisible(MainViewModel vm, ChatViewModel? owner)
        => owner is not null && vm.SplitWorkspace.ChatViewModels.Contains(owner);

    private void PrepareSharedSidePanelLayout(Control sidePanel)
    {
        if (_chatContentGrid is null)
            return;

        var defs = _chatContentGrid.ColumnDefinitions;
        while (defs.Count < 3)
            defs.Add(new ColumnDefinition());

        defs[0].Width = new GridLength(1, GridUnitType.Star);
        defs[1].Width = GridLength.Auto;
        defs[2].Width = new GridLength(1, GridUnitType.Star);

        if (_chatIsland is not null)
            Grid.SetColumn(_chatIsland, 0);
        if (_splitWorkspaceHost is not null)
            Grid.SetColumn(_splitWorkspaceHost, 0);
        if (_splitDropOverlay is not null)
            Grid.SetColumn(_splitDropOverlay, 0);

        Grid.SetColumn(sidePanel, 2);
    }

    private void ResetSharedSidePanelLayoutIfClosed()
    {
        if (_chatContentGrid is null)
            return;

        if ((_browserIsland?.IsVisible ?? false)
            || (_diffIsland?.IsVisible ?? false)
            || (_planIsland?.IsVisible ?? false))
            return;

        var defs = _chatContentGrid.ColumnDefinitions;
        while (defs.Count < 3)
            defs.Add(new ColumnDefinition());

        defs[0].Width = new GridLength(1, GridUnitType.Star);
        defs[1].Width = new GridLength(0);
        defs[2].Width = new GridLength(0);

        if (_chatIsland is not null)
            Grid.SetColumn(_chatIsland, 0);
        if (_splitWorkspaceHost is not null)
            Grid.SetColumn(_splitWorkspaceHost, 0);
        if (_splitDropOverlay is not null)
            Grid.SetColumn(_splitDropOverlay, 0);
    }

    private void SetBrowserPanelOwner(ChatViewModel owner)
    {
        if (!ReferenceEquals(_browserPanelOwner, owner))
            ClearBrowserPanelOwner();

        _browserPanelOwner = owner;
        owner.IsBrowserOpen = true;
    }

    private void ClearBrowserPanelOwner()
    {
        if (_browserPanelOwner is not null)
            _browserPanelOwner.IsBrowserOpen = false;
        _browserPanelOwner = null;
    }

    private void SetDiffPanelOwner(ChatViewModel owner)
    {
        if (!ReferenceEquals(_diffPanelOwner, owner))
            ClearDiffPanelOwner();

        _diffPanelOwner = owner;
        owner.IsDiffOpen = true;
    }

    private void ClearDiffPanelOwner()
    {
        if (_diffPanelOwner is not null)
            _diffPanelOwner.IsDiffOpen = false;
        _diffPanelOwner = null;
    }

    private void SetPlanPanelOwner(ChatViewModel owner)
    {
        if (!ReferenceEquals(_planPanelOwner, owner))
        {
            ClearPlanPanelOwner();
            _planPanelOwner = owner;
            owner.PropertyChanged += OnPlanPanelOwnerPropertyChanged;
        }

        owner.IsPlanOpen = true;
        if (_planMarkdown is not null)
            _planMarkdown.Markdown = owner.PlanContent;
    }

    private void ClearPlanPanelOwner()
    {
        if (_planPanelOwner is not null)
        {
            _planPanelOwner.PropertyChanged -= OnPlanPanelOwnerPropertyChanged;
            _planPanelOwner.IsPlanOpen = false;
        }
        _planPanelOwner = null;
        if (_planMarkdown is not null)
            _planMarkdown.Markdown = null;
    }

    private void OnPlanPanelOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.PlanContent)
            && ReferenceEquals(sender, _planPanelOwner)
            && _planMarkdown is not null)
            _planMarkdown.Markdown = _planPanelOwner?.PlanContent;
    }

    private void EnsureBrowserViewLoaded(MainViewModel vm, BrowserService browserService)
    {
        if (_browserView is null)
        {
            if (_browserHost is null) return;
            _browserView = new BrowserView();
            _browserHost.Content = _browserView;
        }
        _browserView.SetBrowserService(browserService, vm.DataStore);
    }

    private async void ShowBrowserPanel(ChatViewModel owner, Guid chatId)
    {
        if (_browserIsland is null || _chatContentGrid is null) return;

        var vm = DataContext as MainViewModel;
        if (vm is null) return;

        // Get the per-chat browser service
        var browserService = owner.GetBrowserServiceForChat(chatId);
        if (browserService is null) return;

        // Hide diff panel if open (they share column 2)
        if (_diffIsland is { IsVisible: true })
        {
            _diffIsland.IsVisible = false;
            _diffIsland.Opacity = 1;
            _diffIsland.RenderTransform = null;
            ClearDiffPanelOwner();
        }

        // Hide plan panel if open (they share column 2)
        if (_planIsland is { IsVisible: true })
        {
            _planIsland.IsVisible = false;
            _planIsland.Opacity = 1;
            _planIsland.RenderTransform = null;
            ClearPlanPanelOwner();
        }

        // Switch to the correct per-chat BrowserService
        EnsureBrowserViewLoaded(vm, browserService);
        SetBrowserPanelOwner(owner);

        // If browser panel is already visible (switching chats), just refresh bounds
        if (_browserIsland.IsVisible)
        {
            // Hide old controller, show new one
            _browserView?.RefreshBounds();
            if (browserService.Controller is not null)
                browserService.Controller.IsVisible = true;
            return;
        }

        // Cancel any in-progress animation
        var ct = ReplaceCancellationTokenSource(ref _browserAnimCts).Token;

        // Ensure we're on the Chat tab
        if (vm.SelectedNavIndex != 0)
            vm.SelectedNavIndex = 0;

        // Switch to split layout: chat workspace (1*) | splitter (Auto) | shared side panel (1*)
        const double browserOffsetX = 40.0;
        PrepareSharedSidePanelLayout(_browserIsland);

        // Prepare initial state — transparent + shifted from the outer edge
        _browserIsland.RenderTransform = new TranslateTransform(browserOffsetX, 0);
        _browserIsland.Opacity = 0;
        _browserIsland.IsVisible = true;
        if (_browserSplitter is not null)
            _browserSplitter.IsVisible = true;

        // Animate fade-in + slide from the outer edge (both on the Border visual)
        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(TranslateTransform.XProperty, browserOffsetX),
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(TranslateTransform.XProperty, 0.0),
                    }
                },
            }
        };

        try { await anim.RunAsync(_browserIsland, ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        // Finalize — clear transform and show WebView2 overlay after animation
        _browserIsland.Opacity = 1;
        _browserIsland.RenderTransform = null;
        SetBrowserPanelOwner(owner);

        if (browserService.Controller is not null)
            browserService.Controller.IsVisible = true;
        Dispatcher.UIThread.Post(() => _browserView?.RefreshBounds(), DispatcherPriority.Loaded);
    }

    /// <summary>Hides the browser panel and returns to single-column chat layout.</summary>
    private async void HideBrowserPanel(ChatViewModel? owner = null)
    {
        if (_browserIsland is null || _chatContentGrid is null) return;
        if (owner is not null && !ReferenceEquals(owner, _browserPanelOwner))
        {
            owner.IsBrowserOpen = false;
            return;
        }
        if (!_browserIsland.IsVisible)
        {
            ClearBrowserPanelOwner();
            return;
        }

        // Cancel any in-progress animation
        var ct = ReplaceCancellationTokenSource(ref _browserAnimCts).Token;

        // Hide WebView2 overlay immediately so it doesn't float during animation
        _browserView?.ClearBrowserService();

        const double browserOffsetX = 40.0;

        // Animate fade-out + slide to the outer edge
        _browserIsland.RenderTransform = new TranslateTransform(0, 0);

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(TranslateTransform.XProperty, 0.0),
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(TranslateTransform.XProperty, browserOffsetX),
                    }
                },
            }
        };

        try { await anim.RunAsync(_browserIsland, ct); }
        catch (OperationCanceledException) { /* cancelled — cleanup below */ }

        // Collapse regardless of cancellation
        _browserIsland.IsVisible = false;
        _browserIsland.Opacity = 1;
        _browserIsland.RenderTransform = null;
        if (_browserSplitter is not null && !(_diffIsland?.IsVisible ?? false) && !(_planIsland?.IsVisible ?? false))
            _browserSplitter.IsVisible = false;

        ClearBrowserPanelOwner();
        ResetSharedSidePanelLayoutIfClosed();
        Grid.SetColumn(_browserIsland, 2);
    }


    private void EnsureDiffViewLoaded()
    {
        if (_diffHost is null) return;
        if (_diffView is null)
            _diffView = new DiffView();
        // Always restore DiffView as the host content (may have been swapped for git changes list)
        if (_diffHost.Content != _diffView)
            _diffHost.Content = _diffView;
    }

    private async void ShowDiffPanel(ChatViewModel owner, FileChangeItem fileChange)
    {
        if (_diffIsland is null || _chatContentGrid is null) return;

        // Hide browser panel if it's open (they share column 2)
        if (_browserIsland is { IsVisible: true })
        {
            _browserIsland.IsVisible = false;
            _browserIsland.Opacity = 1;
            _browserIsland.RenderTransform = null;
            if (_browserSplitter is not null) _browserSplitter.IsVisible = false;
            _browserView?.ClearBrowserService();
            ClearBrowserPanelOwner();
        }

        // Hide plan panel if open (they share column 2)
        if (_planIsland is { IsVisible: true })
        {
            _planIsland.IsVisible = false;
            _planIsland.Opacity = 1;
            _planIsland.RenderTransform = null;
            ClearPlanPanelOwner();
        }

        // Cancel any in-progress animation
        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;

        var vm = DataContext as MainViewModel;

        // Ensure we're on the Chat tab
        if (vm is not null && vm.SelectedNavIndex != 0)
            vm.SelectedNavIndex = 0;

        EnsureDiffViewLoaded();

        // Update header text
        if (_diffFileNameText is not null)
            _diffFileNameText.Text = System.IO.Path.GetFileName(fileChange.FilePath);

        // Set diff content
        _diffView?.SetFileChangeDiff(fileChange);

        // Switch to split layout
        const double offsetX = 40.0;
        SetDiffPanelOwner(owner);
        PrepareSharedSidePanelLayout(_diffIsland);

        _diffIsland.RenderTransform = new TranslateTransform(offsetX, 0);
        _diffIsland.Opacity = 0;
        _diffIsland.IsVisible = true;
        if (_browserSplitter is not null) _browserSplitter.IsVisible = true;

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.XProperty, offsetX) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.XProperty, 0.0) } },
            }
        };

        try { await anim.RunAsync(_diffIsland, ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        _diffIsland.Opacity = 1;
        _diffIsland.RenderTransform = null;

        SetDiffPanelOwner(owner);
    }

    private async void HideDiffPanel(ChatViewModel? owner = null)
    {
        if (_diffIsland is null || _chatContentGrid is null) return;
        if (owner is not null && !ReferenceEquals(owner, _diffPanelOwner))
        {
            owner.IsDiffOpen = false;
            return;
        }
        if (!_diffIsland.IsVisible)
        {
            ClearDiffPanelOwner();
            return;
        }

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;

        const double offsetX = 40.0;
        _diffIsland.RenderTransform = new TranslateTransform(0, 0);

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.XProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.XProperty, offsetX) } },
            }
        };

        try { await anim.RunAsync(_diffIsland, ct); }
        catch (OperationCanceledException) { }

        _diffIsland.IsVisible = false;
        _diffIsland.Opacity = 1;
        _diffIsland.RenderTransform = null;
        if (_browserSplitter is not null && !(_browserIsland?.IsVisible ?? false) && !(_planIsland?.IsVisible ?? false))
            _browserSplitter.IsVisible = false;

        ClearDiffPanelOwner();
        ResetSharedSidePanelLayoutIfClosed();
    }

    /// <summary>Shows a list of git changed files in the diff panel. Clicking a file opens its diff.</summary>
    private void ShowGitChangesPanel(ChatViewModel owner, List<GitFileChangeViewModel> files)
    {
        if (_diffIsland is null || _chatContentGrid is null) return;

        _lastGitChangesList = files;

        var tertiaryBrush = Avalonia.Media.Brushes.Gray as Avalonia.Media.IBrush;
        if (this.TryFindResource("Brush.TextTertiary", this.ActualThemeVariant, out var tObj) && tObj is Avalonia.Media.IBrush tBrush)
            tertiaryBrush = tBrush;

        // Build a file list panel
        var listPanel = new StackPanel { Spacing = 2, Margin = new Thickness(8, 4) };
        foreach (var file in files)
        {
            var kindColor = file.Kind switch
            {
                GitChangeKind.Added or GitChangeKind.Untracked => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(63, 185, 80)),
                GitChangeKind.Deleted => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(248, 81, 73)),
                GitChangeKind.Renamed => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(88, 166, 255)),
                _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(210, 153, 34))
            };

            var row = new Button
            {
                Name = CreateGitDiffRowName(file.FileName, listPanel.Children.Count),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Thickness(10, 8),
                Background = Avalonia.Media.Brushes.Transparent,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            AutomationProperties.SetName(row, $"Open diff for {file.FileName}");
            row.Classes.Add("subtle");

            var content = new DockPanel();

            // Status letter badge
            var badge = new Border
            {
                Width = 22, Height = 22,
                CornerRadius = new CornerRadius(4),
                Background = new Avalonia.Media.SolidColorBrush(kindColor.Color, 0.15),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = file.KindIcon,
                    FontSize = 11,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    Foreground = kindColor,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            };
            DockPanel.SetDock(badge, Dock.Left);
            content.Children.Add(badge);

            var textStack = new StackPanel { Spacing = 1, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = file.FileName,
                FontSize = 12,
                FontWeight = Avalonia.Media.FontWeight.Medium,
            });
            if (!string.IsNullOrEmpty(file.Directory))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = file.Directory,
                    FontSize = 10,
                    Foreground = tertiaryBrush,
                });
            }
            content.Children.Add(textStack);

            // Line stats on the right
            if (file.HasStats)
            {
                var statsPanel = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                DockPanel.SetDock(statsPanel, Dock.Right);
                if (file.LinesAdded > 0)
                    statsPanel.Children.Add(new TextBlock
                    {
                        Text = $"+{file.LinesAdded}",
                        FontSize = 11,
                        FontFamily = new Avalonia.Media.FontFamily("Cascadia Code, Consolas, monospace"),
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(63, 185, 80)),
                    });
                if (file.LinesRemoved > 0)
                    statsPanel.Children.Add(new TextBlock
                    {
                        Text = $"−{file.LinesRemoved}",
                        FontSize = 11,
                        FontFamily = new Avalonia.Media.FontFamily("Cascadia Code, Consolas, monospace"),
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(248, 81, 73)),
                    });
                // Insert before textStack so DockPanel docks it right
                content.Children.Insert(1, statsPanel);
            }

            row.Content = content;

            // Click opens the diff for this file with a back button
            var capturedFile = file;
            row.Click += (_, _) => ShowGitFileDiffWithBackNav(capturedFile);

            listPanel.Children.Add(row);
        }

        // Update header
        if (_diffFileNameText is not null)
            _diffFileNameText.Text = $"Changes ({files.Count})";

        // Show the list in the diff host (bypass EnsureDiffViewLoaded since we want custom content)
        if (_diffHost is not null)
        {
            _diffHost.Content = new ScrollViewer
            {
                Content = listPanel,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };
        }

        // Show the diff panel (reuse the same show animation logic)
        ShowDiffPanelAnimated(owner);
    }

    private static string CreateGitDiffRowName(string fileName, int rowIndex)
    {
        var safeFileName = new string(fileName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return $"GitDiffRow_{rowIndex}_{safeFileName}";
    }

    /// <summary>Opens a single file diff with breadcrumb back-nav in the header.</summary>
    private void ShowGitFileDiffWithBackNav(GitFileChangeViewModel file)
    {
        if (_diffHost is null) return;

        // Create a fresh DiffView for this file
        var diffView = new DiffView();
        _diffHost.Content = diffView;

        // Update header: clickable "Changes" breadcrumb + file name
        if (_diffFileNameText is not null)
        {
            _diffFileNameText.Text = null;
            _diffFileNameText.Inlines?.Clear();
            var inlines = _diffFileNameText.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();

            var accentBrush = Avalonia.Media.Brushes.DodgerBlue as Avalonia.Media.IBrush;
            var tertiaryBrush = Avalonia.Media.Brushes.Gray as Avalonia.Media.IBrush;
            if (this.TryFindResource("Brush.AccentDefault", this.ActualThemeVariant, out var accentObj) && accentObj is Avalonia.Media.IBrush ab)
                accentBrush = ab;
            if (this.TryFindResource("Brush.TextTertiary", this.ActualThemeVariant, out var tertiaryObj) && tertiaryObj is Avalonia.Media.IBrush tb)
                tertiaryBrush = tb;

            var changesRun = new Avalonia.Controls.Documents.Run("Changes")
            {
                Foreground = accentBrush,
            };
            inlines.Add(changesRun);
            inlines.Add(new Avalonia.Controls.Documents.Run("  ›  ") { Foreground = tertiaryBrush });
            inlines.Add(new Avalonia.Controls.Documents.Run(file.FileName));

            _diffFileNameText.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);

            _diffFileNameText.PointerPressed -= OnDiffBreadcrumbClick;
            _diffFileNameText.PointerPressed += OnDiffBreadcrumbClick;
        }

        // Build the diff view
        if (file.Change.Kind is GitChangeKind.Added or GitChangeKind.Untracked)
        {
            _ = ShowAddedGitFileDiffAsync(file.Change.FullPath, diffView);
        }
        else
        {
            _ = LoadGitUnifiedDiffAsync(file.Change, diffView);
        }
    }

    private async Task ShowAddedGitFileDiffAsync(string filePath, DiffView diffView)
    {
        var content = await Task.Run(() =>
        {
            try { return System.IO.File.Exists(filePath) ? System.IO.File.ReadAllText(filePath) : string.Empty; }
            catch { return string.Empty; }
        });
        Dispatcher.UIThread.Post(() => diffView.SetSnapshotDiff(filePath, string.Empty, content));
    }

    private void OnDiffBreadcrumbClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_lastGitChangesList is not null)
        {
            var owner = _diffPanelOwner ?? (DataContext as MainViewModel)?.ChatVM;
            if (owner is not null)
                ShowGitChangesPanel(owner, _lastGitChangesList);
        }
    }

    private async Task LoadGitUnifiedDiffAsync(GitFileChange change, DiffView diffView)
    {
        var repoDir = System.IO.Path.GetDirectoryName(change.FullPath) ?? "";
        var diff = await GitService.GetFileDiffAsync(repoDir, System.IO.Path.GetFileName(change.FullPath));
        if (diff is null)
            diff = await GitService.GetFileDiffAsync(repoDir, change.RelativePath);
        Dispatcher.UIThread.Post(() => diffView.SetUnifiedDiffText(change.FullPath, diff));
    }

    /// <summary>Shows the diff island with animation (shared by file diff and git changes list).</summary>
    private async void ShowDiffPanelAnimated(ChatViewModel owner)
    {
        if (_diffIsland is null || _chatContentGrid is null) return;

        // Hide browser panel if it's open
        if (_browserIsland is { IsVisible: true })
        {
            _browserIsland.IsVisible = false;
            _browserIsland.Opacity = 1;
            _browserIsland.RenderTransform = null;
            if (_browserSplitter is not null) _browserSplitter.IsVisible = false;
            _browserView?.ClearBrowserService();
            ClearBrowserPanelOwner();
        }

        // Hide plan panel if open
        if (_planIsland is { IsVisible: true })
        {
            _planIsland.IsVisible = false;
            _planIsland.Opacity = 1;
            _planIsland.RenderTransform = null;
            ClearPlanPanelOwner();
        }

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;
        var vm = DataContext as MainViewModel;
        if (vm is not null && vm.SelectedNavIndex != 0)
            vm.SelectedNavIndex = 0;

        const double offsetX = 40.0;
        SetDiffPanelOwner(owner);
        PrepareSharedSidePanelLayout(_diffIsland);

        _diffIsland.RenderTransform = new TranslateTransform(offsetX, 0);
        _diffIsland.Opacity = 0;
        _diffIsland.IsVisible = true;
        if (_browserSplitter is not null) _browserSplitter.IsVisible = true;

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.XProperty, offsetX) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.XProperty, 0.0) } },
            }
        };

        try { await anim.RunAsync(_diffIsland, ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        _diffIsland.Opacity = 1;
        _diffIsland.RenderTransform = null;
        SetDiffPanelOwner(owner);
    }


    private async void ShowPlanPanel(ChatViewModel owner)
    {
        if (_planIsland is null || _chatContentGrid is null) return;

        // Hide browser panel if open (they share column 2)
        if (_browserIsland is { IsVisible: true })
        {
            _browserIsland.IsVisible = false;
            _browserIsland.Opacity = 1;
            _browserIsland.RenderTransform = null;
            if (_browserSplitter is not null) _browserSplitter.IsVisible = false;
            _browserView?.ClearBrowserService();
            ClearBrowserPanelOwner();
        }

        // Hide diff panel if open
        if (_diffIsland is { IsVisible: true })
        {
            _diffIsland.IsVisible = false;
            _diffIsland.Opacity = 1;
            _diffIsland.RenderTransform = null;
            ClearDiffPanelOwner();
        }

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;

        var vm = DataContext as MainViewModel;
        if (vm is not null && vm.SelectedNavIndex != 0)
            vm.SelectedNavIndex = 0;

        // Switch to split layout
        const double offsetX = 40.0;
        SetPlanPanelOwner(owner);
        PrepareSharedSidePanelLayout(_planIsland);

        _planIsland.RenderTransform = new TranslateTransform(offsetX, 0);
        _planIsland.Opacity = 0;
        _planIsland.IsVisible = true;
        if (_browserSplitter is not null) _browserSplitter.IsVisible = true;

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.XProperty, offsetX) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.XProperty, 0.0) } },
            }
        };

        try { await anim.RunAsync(_planIsland, ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        _planIsland.Opacity = 1;
        _planIsland.RenderTransform = null;
        SetPlanPanelOwner(owner);
    }

    private async void HidePlanPanel(ChatViewModel? owner = null)
    {
        if (_planIsland is null || _chatContentGrid is null) return;
        if (owner is not null && !ReferenceEquals(owner, _planPanelOwner))
        {
            owner.IsPlanOpen = false;
            return;
        }
        if (!_planIsland.IsVisible)
        {
            ClearPlanPanelOwner();
            return;
        }

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;

        const double offsetX = 40.0;
        _planIsland.RenderTransform = new TranslateTransform(0, 0);

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.XProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.XProperty, offsetX) } },
            }
        };

        try { await anim.RunAsync(_planIsland, ct); }
        catch (OperationCanceledException) { }

        _planIsland.IsVisible = false;
        _planIsland.Opacity = 1;
        _planIsland.RenderTransform = null;
        if (_browserSplitter is not null && !(_browserIsland?.IsVisible ?? false) && !(_diffIsland?.IsVisible ?? false))
            _browserSplitter.IsVisible = false;

        ClearPlanPanelOwner();
        ResetSharedSidePanelLayoutIfClosed();
    }
}
