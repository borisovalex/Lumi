using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class MainWindow
{
    private void ConfigureSplitDropTarget(Interactive? target)
    {
        if (target is null)
            return;

        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragEnterEvent, OnSplitChatDragEnter, RoutingStrategies.Tunnel, handledEventsToo: true);
        target.AddHandler(DragDrop.DragOverEvent, OnSplitChatDragOver, RoutingStrategies.Tunnel, handledEventsToo: true);
        target.AddHandler(DragDrop.DragLeaveEvent, OnSplitChatDragLeave, RoutingStrategies.Tunnel, handledEventsToo: true);
        target.AddHandler(DragDrop.DropEvent, OnSplitChatDrop, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void AttachChatListItemPointerHandlers(ListBoxItem item)
    {
        if (!_chatListItemInputHooked.Add(item))
            return;

        item.AddHandler(PointerPressedEvent, OnChatListItemPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        item.PointerMoved += OnChatListItemPointerMoved;
        item.PointerReleased += OnChatListItemPointerReleased;
        item.PointerCaptureLost += OnSplitDragPointerCaptureLost;
        item.AddHandler(InputElement.ContextRequestedEvent, OnChatListItemContextRequested,
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnChatListItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBoxItem item)
            return;

        var point = e.GetCurrentPoint(item);
        if (item.DataContext is not Chat chat)
            return;

        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            OpenChatItemContextMenu(item, chat);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;
        BeginPendingSplitDrag(item, e, new SplitChatDragPayload(chat.Id, SplitChatDragSource.Sidebar, null));
    }

    private async void OnChatListItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not ListBoxItem item)
            return;

        await TryStartPendingSplitDragAsync(item, e);
    }

    private async void OnChatListItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not ListBoxItem item)
            return;

        PruneSplitDragStartedPointers();
        if (_splitDragStartedPointers.Remove(e.Pointer))
            return;

        if (!TryCompletePendingSplitClick(item, e, out var payload))
        {
            if (e.InitialPressMouseButton != MouseButton.Left
                || item.DataContext is not Chat fallbackChat
                || DataContext is not MainViewModel fallbackVm
                || !IsLocalPointInside(item, e.GetPosition(item)))
            {
                return;
            }

            e.Handled = true;
            await fallbackVm.OpenChatCommand.ExecuteAsync(fallbackChat);
            return;
        }

        e.Handled = true;
        if (payload.Source != SplitChatDragSource.Sidebar
            || item.DataContext is not Chat chat
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        await vm.OpenChatCommand.ExecuteAsync(chat);
    }

    private void OnChatListItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not Chat chat)
            return;

        e.Handled = true;
        OpenChatItemContextMenu(item, chat);
    }

    private static void OpenChatItemContextMenu(ListBoxItem item, Chat chat)
    {
        var contextMenuOwner = FindChatItemContextMenuOwner(item);
        if (contextMenuOwner is null || contextMenuOwner.ContextMenu is not { } menu)
            return;

        menu.Close();
        menu.DataContext = chat;
        menu.PlacementTarget = contextMenuOwner;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(contextMenuOwner);
    }

    private static Control? FindChatItemContextMenuOwner(ListBoxItem item)
    {
        foreach (var control in item.GetVisualDescendants().OfType<Control>())
        {
            if (control.ContextMenu is not null)
                return control;
        }

        return item.ContextMenu is not null ? item : null;
    }

    private void OpenChatInSplitViewMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || DataContext is not MainViewModel vm)
            return;

        var contextMenu = FindOwningContextMenu(menuItem);
        var chat = menuItem.DataContext as Chat
            ?? contextMenu?.DataContext as Chat
            ?? contextMenu?.PlacementTarget?.DataContext as Chat;
        contextMenu?.Close();

        if (chat is null)
            return;

        vm.OpenChatInSplitViewCommand.Execute(chat);
        e.Handled = true;
    }

    private static ContextMenu? FindOwningContextMenu(MenuItem menuItem)
        => menuItem.GetLogicalAncestors().OfType<ContextMenu>().FirstOrDefault()
           ?? menuItem.GetVisualAncestors().OfType<ContextMenu>().FirstOrDefault();

    private void OnSplitPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SplitChatPaneViewModel pane }
            || DataContext is not MainViewModel vm)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        vm.SplitWorkspace.FocusPane(pane);
        vm.ActiveChatId = pane.ChatId;
    }

    private void OnSplitPanePointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.SplitWorkspace.IsActive)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (FindSplitPaneFromSource(e.Source) is not { } pane)
            return;

        vm.SplitWorkspace.FocusPane(pane);
        vm.ActiveChatId = pane.ChatId;
    }

    private static SplitChatPaneViewModel? FindSplitPaneFromSource(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: SplitChatPaneViewModel pane })
                return pane;
        }

        return null;
    }

    private void OnSplitPaneHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SplitChatPaneViewModel pane })
            return;

        if (IsInsideSplitPaneCloseButton(e.Source))
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (DataContext is MainViewModel vm)
            vm.SplitWorkspace.FocusPane(pane);

        if (pane.ChatId is not { } chatId)
            return;

        e.Handled = true;
        BeginPendingSplitDrag((Control)sender, e, new SplitChatDragPayload(chatId, SplitChatDragSource.PaneHeader, pane.PaneId));
    }

    private async void OnSplitPaneHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control)
            return;

        await TryStartPendingSplitDragAsync(control, e);
    }

    private void OnSplitPaneHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control control)
            return;

        if (TryCompletePendingSplitClick(control, e, out _))
            e.Handled = true;
    }

    private void OnSplitDragPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is Control control && ReferenceEquals(_pendingSplitChatDrag?.Source, control))
            _pendingSplitChatDrag = null;
    }

    private static bool IsInsideSplitPaneCloseButton(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button button && button.Classes.Contains("split-pane-close"))
                return true;
        }

        return false;
    }

    private void BeginPendingSplitDrag(Control source, PointerPressedEventArgs e, SplitChatDragPayload payload)
    {
        _pendingSplitChatDrag = new SplitChatPendingDrag(source, payload, e.GetPosition(this), e.Pointer, e);
        e.Pointer.Capture(source);
    }

    private async Task TryStartPendingSplitDragAsync(Control source, PointerEventArgs e)
    {
        var pending = _pendingSplitChatDrag;
        if (pending is null
            || !ReferenceEquals(pending.Source, source)
            || !ReferenceEquals(pending.Pointer, e.Pointer))
        {
            return;
        }

        var current = e.GetPosition(this);
        var delta = current - pending.StartPoint;
        if (Math.Abs(delta.X) < SplitDragStartThreshold && Math.Abs(delta.Y) < SplitDragStartThreshold)
            return;

        _pendingSplitChatDrag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        _splitDragStartedPointers[pending.Pointer] = DateTimeOffset.UtcNow;
        await StartNativeSplitDragAsync(pending.TriggerEvent, pending.Payload, pending.Source);
        if (!_splitDropHandledDuringNativeDrag)
            await TryApplySplitDropAtCurrentPointerAsync(pending.Payload);
    }

    private void PruneSplitDragStartedPointers()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(800);
        foreach (var pointer in _splitDragStartedPointers
                     .Where(pair => pair.Value < cutoff)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _splitDragStartedPointers.Remove(pointer);
        }
    }

    private bool TryCompletePendingSplitClick(Control source, PointerReleasedEventArgs e, out SplitChatDragPayload payload)
    {
        payload = default!;
        var pending = _pendingSplitChatDrag;
        if (pending is null || !ReferenceEquals(pending.Source, source) || !ReferenceEquals(pending.Pointer, e.Pointer))
            return false;

        _pendingSplitChatDrag = null;
        e.Pointer.Capture(null);

        var releasePosition = e.GetPosition(source);
        if (!IsLocalPointInside(source, releasePosition))
            return false;

        payload = pending.Payload;
        return true;
    }

    private static bool IsLocalPointInside(Control control, Point point)
        => point.X >= 0
           && point.Y >= 0
           && point.X <= control.Bounds.Width
           && point.Y <= control.Bounds.Height;

    private async Task<DragDropEffects> StartNativeSplitDragAsync(PointerPressedEventArgs e, SplitChatDragPayload payload, Control source)
    {
        var rawPayload = FormatSplitChatDragPayload(payload);
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(SplitChatDragFormat, rawPayload));
        data.Add(DataTransferItem.CreateText(SplitChatDragTextPrefix + rawPayload));
        _splitDropHandledDuringNativeDrag = false;

        if (DataContext is MainViewModel vm)
            BeginSplitDragFeedback(vm, payload, source);

        try
        {
            return await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            EndSplitDragFeedback();
        }
    }

    private void BeginSplitDragFeedback(MainViewModel vm, SplitChatDragPayload payload, Control source)
    {
        _activeSplitDragPayload = payload;
        _activeSplitDragSource = source;
        source.Classes.Add("split-drag-source");

        if (TryGetCurrentSplitDropPoint(out var dropPoint))
            UpdateSplitDropOverlay(vm, payload, dropPoint);
        else
            ShowSplitDropOverlay(vm, payload, activeIndex: -1);

        _splitDragFeedbackTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        _splitDragFeedbackTimer.Tick -= OnSplitDragFeedbackTick;
        _splitDragFeedbackTimer.Tick += OnSplitDragFeedbackTick;
        _splitDragFeedbackTimer.Start();
    }

    private void EndSplitDragFeedback()
    {
        _splitDragFeedbackTimer?.Stop();
        _activeSplitDragSource?.Classes.Remove("split-drag-source");
        _activeSplitDragSource = null;
        _activeSplitDragPayload = null;
        HideSplitDropOverlay();
    }

    private void OnSplitDragFeedbackTick(object? sender, EventArgs e)
    {
        if (_activeSplitDragPayload is not { } payload || DataContext is not MainViewModel vm)
            return;

        if (TryGetCurrentSplitDropPoint(out var dropPoint))
            UpdateSplitDropOverlay(vm, payload, dropPoint);
        else
            ShowSplitDropOverlay(vm, payload, activeIndex: -1);
    }

    private void OnSplitChatDragEnter(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !TryGetSplitChatDragPayload(e, out var payload))
            return;

        e.DragEffects = DragDropEffects.Move;
        UpdateSplitDropOverlay(vm, payload, e.GetPosition(GetSplitDropPositionTarget()));
        e.Handled = true;
    }

    private void OnSplitChatDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !TryGetSplitChatDragPayload(e, out var payload))
            return;

        e.DragEffects = DragDropEffects.Move;
        UpdateSplitDropOverlay(vm, payload, e.GetPosition(GetSplitDropPositionTarget()));
        e.Handled = true;
    }

    private void OnSplitChatDragLeave(object? sender, DragEventArgs e)
    {
        if (!TryGetSplitChatDragPayload(e, out var payload))
            return;

        if (_activeSplitDragPayload is not null && DataContext is MainViewModel vm)
            ShowSplitDropOverlay(vm, payload, activeIndex: -1);
        else
            HideSplitDropOverlay();

        e.Handled = true;
    }

    private async void OnSplitChatDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !TryGetSplitChatDragPayload(e, out var payload))
            return;

        e.Handled = true;
        e.DragEffects = DragDropEffects.Move;
        _splitDropHandledDuringNativeDrag = true;
        _splitDragFeedbackTimer?.Stop();

        await HandleSplitDropAsync(vm, payload, e.GetPosition(GetSplitDropPositionTarget()));
    }

    private async Task HandleSplitDropAsync(MainViewModel vm, SplitChatDragPayload payload, Point dropPoint)
    {
        if (IsSplitCancelDropPoint(dropPoint))
        {
            HideSplitDropOverlay();
            return;
        }

        var zoneCount = GetSplitDropZoneCount(vm, payload);
        var targetIndex = GetSplitDropIndex(dropPoint, zoneCount);
        HideSplitDropOverlay();

        if (payload.Source == SplitChatDragSource.PaneHeader && payload.PaneId is Guid paneId)
        {
            vm.SplitWorkspace.MovePane(paneId, targetIndex);
            vm.ActiveChatId = vm.SplitWorkspace.FocusedChatId;
            return;
        }

        var chat = vm.DataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == payload.ChatId);
        if (chat is null)
            return;

        await vm.SplitWorkspace.DropSidebarChatAsync(chat, targetIndex);
        vm.SelectedNavIndex = 0;
        vm.ActiveChatId = vm.SplitWorkspace.FocusedChatId;
    }

    private async Task<bool> TryApplySplitDropAtCurrentPointerAsync(SplitChatDragPayload payload)
    {
        if (DataContext is not MainViewModel vm)
            return false;

        if (!TryGetCurrentSplitDropPoint(out var dropPoint))
            return false;

        await HandleSplitDropAsync(vm, payload, dropPoint);
        return true;
    }

    private bool TryGetCurrentSplitDropPoint(out Point dropPoint)
    {
        dropPoint = default;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !GetCursorPos(out var cursor))
            return false;

        var target = GetSplitDropPositionTarget();
        if (!target.IsEffectivelyVisible)
            return false;

        var point = Avalonia.VisualExtensions.PointToClient(target, new PixelPoint(cursor.X, cursor.Y));
        var bounds = new Rect(target.Bounds.Size);
        if (point.X < bounds.Left - SplitDropEdgeTolerance
            || point.X > bounds.Right + SplitDropEdgeTolerance
            || point.Y < bounds.Top - SplitDropEdgeTolerance
            || point.Y > bounds.Bottom + SplitDropEdgeTolerance)
        {
            return false;
        }

        dropPoint = new Point(
            Math.Clamp(point.X, bounds.Left, bounds.Right),
            Math.Clamp(point.Y, bounds.Top, bounds.Bottom));
        return true;
    }

    private static string FormatSplitChatDragPayload(SplitChatDragPayload payload)
        => $"{(int)payload.Source}|{payload.ChatId}|{payload.PaneId?.ToString() ?? ""}";

    private static bool TryGetSplitChatDragPayload(DragEventArgs e, out SplitChatDragPayload payload)
    {
        var rawPayload = e.DataTransfer.TryGetValue(SplitChatDragFormat);
        if (TryParseSplitChatDragPayload(rawPayload, out payload))
            return true;

        var textPayload = e.DataTransfer.TryGetText();
        if (textPayload?.StartsWith(SplitChatDragTextPrefix, StringComparison.Ordinal) == true
            && TryParseSplitChatDragPayload(textPayload[SplitChatDragTextPrefix.Length..], out payload))
        {
            return true;
        }

        foreach (var item in e.DataTransfer.Items)
        {
            if (item.TryGetRaw(SplitChatDragFormat) is not string raw)
                continue;

            if (TryParseSplitChatDragPayload(raw, out payload))
                return true;
        }

        payload = default!;
        return false;
    }

    private static bool TryParseSplitChatDragPayload(string? raw, out SplitChatDragPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var parts = raw.Split('|');
            if (parts.Length >= 2
                && int.TryParse(parts[0], out var sourceValue)
                && Enum.IsDefined(typeof(SplitChatDragSource), sourceValue)
                && Guid.TryParse(parts[1], out var chatId))
            {
                Guid? paneId = null;
                if (parts.Length > 2 && Guid.TryParse(parts[2], out var parsedPaneId))
                    paneId = parsedPaneId;

                payload = new SplitChatDragPayload(chatId, (SplitChatDragSource)sourceValue, paneId);
                return true;
            }
        }

        payload = default!;
        return false;
    }

    private Control GetSplitDropPositionTarget()
        => (Control?)_chatContentGrid ?? this;

    private void UpdateSplitDropOverlay(MainViewModel vm, SplitChatDragPayload payload, Point overlayPoint)
    {
        var zoneCount = GetSplitDropZoneCount(vm, payload);
        if (zoneCount <= 0)
            return;

        var activeIndex = IsSplitCancelDropPoint(overlayPoint)
            ? SplitDropCancelIndex
            : GetSplitDropIndex(overlayPoint, zoneCount);
        ShowSplitDropOverlay(vm, payload, activeIndex);
    }

    private void ShowSplitDropOverlay(MainViewModel vm, SplitChatDragPayload payload, int activeIndex)
    {
        if (_splitDropOverlay is null || _splitDropZoneHost is null)
            return;

        var zoneCount = GetSplitDropZoneCount(vm, payload);
        if (zoneCount <= 0)
            return;

        var isCancelActive = activeIndex == SplitDropCancelIndex;
        var zoneActiveIndex = isCancelActive ? -1 : Math.Clamp(activeIndex, -1, zoneCount - 1);
        var displayedActiveIndex = isCancelActive ? SplitDropCancelIndex : zoneActiveIndex;
        if (_splitDropOverlay.IsVisible
            && _activeSplitDropIndex == displayedActiveIndex
            && _splitDropZoneHost.Children.Count == zoneCount)
        {
            SetSplitCancelTargetActive(isCancelActive);
            return;
        }

        _activeSplitDropIndex = displayedActiveIndex;
        SetSplitCancelTargetActive(isCancelActive);
        BuildSplitDropZones(vm, payload, zoneCount, zoneActiveIndex);
        _splitDropOverlay.IsVisible = true;
        _splitDropOverlay.Opacity = 1;
    }

    private void BuildSplitDropZones(MainViewModel vm, SplitChatDragPayload payload, int zoneCount, int activeIndex)
    {
        if (_splitDropZoneHost is null)
            return;

        _splitDropZoneHost.Children.Clear();
        _splitDropZoneHost.RowDefinitions.Clear();
        _splitDropZoneHost.ColumnDefinitions.Clear();

        var (rows, columns) = SplitChatWorkspaceViewModel.GetGridDimensions(zoneCount);
        for (var row = 0; row < rows; row++)
            _splitDropZoneHost.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        for (var column = 0; column < columns; column++)
            _splitDropZoneHost.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        var existingPaneCount = vm.SplitWorkspace.IsActive ? vm.SplitWorkspace.Panes.Count : 0;
        var isSingleModePlacement = !vm.SplitWorkspace.IsActive
            && payload.Source == SplitChatDragSource.Sidebar
            && zoneCount > 1;
        for (var index = 0; index < zoneCount; index++)
        {
            var isActive = index == activeIndex;
            var isAddZone = payload.Source == SplitChatDragSource.Sidebar
                && ((vm.SplitWorkspace.IsActive && index >= existingPaneCount) || isSingleModePlacement);
            var isMoveZone = payload.Source == SplitChatDragSource.PaneHeader;
            var title = GetSplitDropZoneTitle(vm, isSingleModePlacement, isAddZone, isMoveZone, index);
            var iconKey = GetSplitDropZoneIconKey(vm, isSingleModePlacement, isAddZone, isMoveZone);
            var accentBrush = GetThemeBrush("Brush.AccentDefault", Brushes.DodgerBlue);
            var subtleAccentBrush = GetThemeBrush("Brush.AccentSubtle", Brushes.Transparent);
            var emphasizeAddSlot = vm.SplitWorkspace.IsActive && isAddZone;
            var isEmphasized = isActive || emphasizeAddSlot;
            var iconBrush = isEmphasized
                ? accentBrush
                : GetThemeBrush("Brush.TextSecondary", Brushes.White);

            var zone = new Border
            {
                Margin = new Thickness(6),
                Padding = new Thickness(18),
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(isActive ? 2 : 1),
                Opacity = isActive ? 1 : 0.82,
                BorderBrush = isEmphasized
                    ? accentBrush
                    : GetThemeBrush("Brush.BorderSubtle", Brushes.DodgerBlue),
                Background = isEmphasized
                    ? subtleAccentBrush
                    : GetThemeBrush("Brush.SurfaceCard", Brushes.Transparent),
                Child = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 9,
                    Children =
                    {
                        new Border
                        {
                            Width = 34,
                            Height = 34,
                            CornerRadius = new CornerRadius(12),
                            Background = isEmphasized
                                ? subtleAccentBrush
                                : GetThemeBrush("Brush.Surface1", Brushes.Transparent),
                            Child = new PathIcon
                            {
                                Width = 16,
                                Height = 16,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center,
                                Data = GetIconGeometry(iconKey),
                                Foreground = iconBrush
                            }
                        },
                        new TextBlock
                        {
                            Text = title,
                            FontSize = isActive ? 17 : 15,
                            FontWeight = FontWeight.Bold,
                            MaxWidth = 220,
                            TextWrapping = TextWrapping.Wrap,
                            TextAlignment = TextAlignment.Center,
                            Foreground = GetThemeBrush("Brush.TextPrimary", Brushes.White)
                        }
                    }
                }
            };
            zone.Classes.Add("split-drop-zone");
            if (isActive)
                zone.Classes.Add("active");
            if (isAddZone)
                zone.Classes.Add("add");

            Grid.SetRow(zone, index / columns);
            Grid.SetColumn(zone, index % columns);
            _splitDropZoneHost.Children.Add(zone);
        }
    }

    private static string GetSplitDropZoneTitle(
        MainViewModel vm,
        bool isSingleModePlacement,
        bool isAddZone,
        bool isMoveZone,
        int index)
    {
        if (isMoveZone)
            return Loc.Split_MovePane;

        if (isSingleModePlacement)
            return index == 0 ? Loc.Split_OpenLeft : Loc.Split_OpenRight;

        if (!vm.SplitWorkspace.IsActive)
            return Loc.Split_OpenHere;

        return isAddZone ? Loc.Split_AddPane : Loc.Split_DropHere;
    }

    private static string GetSplitDropZoneIconKey(
        MainViewModel vm,
        bool isSingleModePlacement,
        bool isAddZone,
        bool isMoveZone)
    {
        if (isMoveZone)
            return "Icon.Split";

        if (isSingleModePlacement || isAddZone)
            return "Icon.Plus";

        return vm.SplitWorkspace.IsActive ? "Icon.ReplacePane" : "Icon.Split";
    }

    private bool IsSplitCancelDropPoint(Point dropPoint)
    {
        if (!TryGetSplitCancelDropBounds(out var cancelBounds))
            return false;

        return cancelBounds.Contains(dropPoint);
    }

    private bool TryGetSplitCancelDropBounds(out Rect bounds)
    {
        bounds = default;
        var target = GetSplitDropPositionTarget();
        if (!target.IsEffectivelyVisible)
            return false;

        var size = target.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return false;

        bounds = new Rect(0, 0, size.Width, Math.Min(SplitDropCancelBandHeight, size.Height));
        return true;
    }

    private void SetSplitCancelTargetActive(bool isActive)
    {
        if (_splitDropCancelTarget is null)
            return;

        if (isActive)
            _splitDropCancelTarget.Classes.Add("active");
        else
            _splitDropCancelTarget.Classes.Remove("active");
    }

    private static int GetSplitDropZoneCount(MainViewModel vm, SplitChatDragPayload payload)
    {
        if (!vm.SplitWorkspace.IsActive)
        {
            var currentChat = vm.ChatVM.CurrentChat;
            return currentChat is not null && currentChat.Id != payload.ChatId ? 2 : 1;
        }

        var count = vm.SplitWorkspace.Panes.Count;
        var isAlreadyOpen = vm.SplitWorkspace.Panes.Any(pane => pane.ChatId == payload.ChatId);
        if (payload.Source == SplitChatDragSource.Sidebar && !isAlreadyOpen && vm.SplitWorkspace.CanAddPane)
            count++;

        return Math.Clamp(count, 1, SplitChatWorkspaceViewModel.MaxPanes);
    }

    private int GetSplitDropIndex(Point overlayPoint, int zoneCount)
    {
        if (zoneCount <= 0)
            return 0;

        var (rows, columns) = SplitChatWorkspaceViewModel.GetGridDimensions(zoneCount);
        var targetBounds = GetSplitDropPositionTarget().Bounds;
        var width = Math.Max(1, targetBounds.Width);
        var height = Math.Max(1, targetBounds.Height);
        var column = Math.Clamp((int)(overlayPoint.X / Math.Max(1, width / columns)), 0, columns - 1);
        var row = Math.Clamp((int)(overlayPoint.Y / Math.Max(1, height / rows)), 0, rows - 1);
        return Math.Clamp(row * columns + column, 0, zoneCount - 1);
    }

    private void HideSplitDropOverlay()
    {
        _activeSplitDropIndex = -1;
        if (_splitDropOverlay is not null)
        {
            _splitDropOverlay.Opacity = 0;
            _splitDropOverlay.IsVisible = false;
        }
        SetSplitCancelTargetActive(false);
        if (_splitDropZoneHost is not null)
            _splitDropZoneHost.Children.Clear();
    }

    private void QueueSplitPaneAnimations()
    {
        Dispatcher.UIThread.Post(EnsureSplitPaneGridImplicitAnimations, DispatcherPriority.Loaded);
    }

    private void EnsureSplitPaneGridImplicitAnimations()
    {
        if (_splitWorkspaceHost is null || !_splitWorkspaceHost.IsVisible)
            return;

        var animatedVisuals = _splitWorkspaceHost.GetVisualDescendants()
            .Where(static visual =>
                visual is ContentPresenter
                || visual is UniformGrid
                || visual is Border border && border.Classes.Contains("split-chat-island"));

        foreach (var visual in animatedVisuals)
            EnsureSplitPaneImplicitAnimations(visual);
    }

    private static void EnsureSplitPaneImplicitAnimations(Visual element)
    {
        var visual = ElementComposition.GetElementVisual(element);
        if (visual is null || visual.ImplicitAnimations is not null)
            return;

        var compositor = visual.Compositor;
        var offsetAnim = compositor.CreateVector3DKeyFrameAnimation();
        offsetAnim.Target = "Offset";
        offsetAnim.InsertExpressionKeyFrame(1f, "this.FinalValue");
        offsetAnim.Duration = TimeSpan.FromMilliseconds(180);

        var implicitAnims = compositor.CreateImplicitAnimationCollection();
        implicitAnims["Offset"] = offsetAnim;
        visual.ImplicitAnimations = implicitAnims;
    }
}