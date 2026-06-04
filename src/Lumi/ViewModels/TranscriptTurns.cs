using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumi.ViewModels;

public sealed class TranscriptTurn : ObservableObject
{
    private double _measuredHeight;

    public ObservableCollection<TranscriptItem> Items { get; } = [];
    public string StableId { get; }

    public TranscriptTurn(string stableId)
    {
        StableId = stableId;
    }

    public double MeasuredHeight
    {
        get => _measuredHeight;
        set => SetProperty(ref _measuredHeight, value);
    }

    public int IndexOf(TranscriptItem item) => Items.IndexOf(item);

    public bool Remove(TranscriptItem item) => Items.Remove(item);

}

public readonly record struct TranscriptTurnControlDiagnosticsSnapshot(
    int ControlCreateCount,
    int ItemHostCreateCount);

public sealed class TranscriptTurnControl : UserControl
{
    private readonly StackPanel _itemsHost;
    private TranscriptTurn? _turn;
    private bool _isAttachedToVisualTree;
    private bool _isSubscribedToTurnItems;
    private static int _controlCreateCount;
    private static int _itemHostCreateCount;

    public static readonly StyledProperty<TranscriptTurn?> TurnProperty =
        AvaloniaProperty.Register<TranscriptTurnControl, TranscriptTurn?>(nameof(Turn));

    private static readonly AttachedProperty<IDisposable?> ItemVisibilityBindingProperty =
        AvaloniaProperty.RegisterAttached<TranscriptTurnControl, ContentPresenter, IDisposable?>("ItemVisibilityBinding");

    static TranscriptTurnControl()
    {
        TurnProperty.Changed.AddClassHandler<TranscriptTurnControl>((control, args) =>
            control.OnTurnChanged(control._turn, control.Turn));
    }

    public TranscriptTurnControl()
    {
        Interlocked.Increment(ref _controlCreateCount);

        _itemsHost = new StackPanel
        {
            Spacing = TranscriptLayoutMetrics.TurnSpacing
        };

        Content = _itemsHost;
        SizeChanged += OnSizeChanged;
    }

    public TranscriptTurn? Turn
    {
        get => GetValue(TurnProperty);
        set => SetValue(TurnProperty, value);
    }

    public ObservableCollection<TranscriptItem>? Items => Turn?.Items;

    public string? StableId => Turn?.StableId;

    public static TranscriptTurnControlDiagnosticsSnapshot CaptureDiagnostics() => new(
        Volatile.Read(ref _controlCreateCount),
        Volatile.Read(ref _itemHostCreateCount));

    public static void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _controlCreateCount, 0);
        Interlocked.Exchange(ref _itemHostCreateCount, 0);
    }

    private void OnTurnChanged(TranscriptTurn? oldTurn, TranscriptTurn? newTurn)
    {
        if (ReferenceEquals(oldTurn, newTurn))
            return;

        VerifyUiThread();
        UnsubscribeFromTurnItems(oldTurn);

        _turn = newTurn;

        SubscribeToTurnItems(newTurn);
        if (newTurn is not null && Bounds.Height > 0)
            newTurn.MeasuredHeight = Bounds.Height;

        RebuildItemHosts();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
        SubscribeToTurnItems(_turn);
        RebuildItemHosts();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeFromTurnItems(_turn);
        _isAttachedToVisualTree = false;
        ClearItemHosts();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_turn is not null && e.NewSize.Height > 0)
            _turn.MeasuredHeight = e.NewSize.Height;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        VerifyUiThread();
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null && e.NewStartingIndex >= 0:
                if (e.NewStartingIndex > _itemsHost.Children.Count)
                {
                    RebuildItemHosts();
                    break;
                }

                for (var i = 0; i < e.NewItems.Count; i++)
                    InsertItemHost(e.NewStartingIndex + i, GetTranscriptItem(e.NewItems[i]));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null && e.OldStartingIndex >= 0:
                if (e.OldStartingIndex + e.OldItems.Count > _itemsHost.Children.Count)
                {
                    RebuildItemHosts();
                    break;
                }

                for (var i = 0; i < e.OldItems.Count; i++)
                {
                    ClearItemHost(_itemsHost.Children[e.OldStartingIndex]);
                    _itemsHost.Children.RemoveAt(e.OldStartingIndex);
                }
                break;
            case NotifyCollectionChangedAction.Replace
                when e.OldItems is not null
                     && e.NewItems is not null
                     && e.OldStartingIndex >= 0
                     && e.NewStartingIndex == e.OldStartingIndex
                     && e.OldItems.Count == e.NewItems.Count
                     && e.NewStartingIndex + e.NewItems.Count <= _itemsHost.Children.Count:
                for (var i = 0; i < e.NewItems.Count; i++)
                    ReplaceItemHost(e.NewStartingIndex + i, GetTranscriptItem(e.NewItems[i]));
                break;
            case NotifyCollectionChangedAction.Move
                when e.OldItems is not null
                     && e.OldItems.Count == 1
                     && e.OldStartingIndex >= 0
                     && e.NewStartingIndex >= 0
                     && e.OldStartingIndex < _itemsHost.Children.Count:
                var child = _itemsHost.Children[e.OldStartingIndex];
                _itemsHost.Children.RemoveAt(e.OldStartingIndex);
                if (e.NewStartingIndex <= _itemsHost.Children.Count)
                    _itemsHost.Children.Insert(e.NewStartingIndex, child);
                else
                    RebuildItemHosts();
                break;
            case NotifyCollectionChangedAction.Move:
            case NotifyCollectionChangedAction.Reset:
            default:
                RebuildItemHosts();
                break;
        }
    }

    private void RebuildItemHosts()
    {
        VerifyUiThread();
        ClearItemHosts();
        if (_turn is null)
            return;

        foreach (var item in _turn.Items)
            _itemsHost.Children.Add(CreateItemHost(item));
    }

    private void ClearItemHosts()
    {
        for (var i = _itemsHost.Children.Count - 1; i >= 0; i--)
            ClearItemHost(_itemsHost.Children[i]);

        _itemsHost.Children.Clear();
    }

    private void InsertItemHost(int index, TranscriptItem item)
    {
        _itemsHost.Children.Insert(index, CreateItemHost(item));
    }

    private void ReplaceItemHost(int index, TranscriptItem item)
    {
        ClearItemHost(_itemsHost.Children[index]);
        _itemsHost.Children[index] = CreateItemHost(item);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Runtime transcript hosts bind to internal TranscriptItem properties; this desktop app is not trimmed and binding avoids leak-prone view-capturing event handlers.")]
    private static ContentPresenter CreateItemHost(TranscriptItem item)
    {
        Interlocked.Increment(ref _itemHostCreateCount);
        var presenter = new ContentPresenter
        {
            Content = item,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var binding = presenter.Bind(
            IsVisibleProperty,
            new Binding
            {
                Path = nameof(TranscriptItem.IsItemVisible),
                Source = item,
                Mode = BindingMode.OneWay
            });
        presenter.SetValue(ItemVisibilityBindingProperty, binding);

        return presenter;
    }

    private static void ClearItemHost(Control host)
    {
        if (host is not ContentPresenter presenter)
            return;

        presenter.GetValue(ItemVisibilityBindingProperty)?.Dispose();
        presenter.ClearValue(ItemVisibilityBindingProperty);
        presenter.ClearValue(IsVisibleProperty);
        presenter.Content = null;
    }

    private static TranscriptItem GetTranscriptItem(object? value)
    {
        return value as TranscriptItem
               ?? throw new InvalidOperationException("Expected TranscriptItem in transcript collection change event.");
    }

    private static void VerifyUiThread()
    {
        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException("TranscriptTurnControl item hosts must be updated on the UI thread.");
    }

    private void SubscribeToTurnItems(TranscriptTurn? turn)
    {
        if (!_isAttachedToVisualTree || _isSubscribedToTurnItems || turn is null)
            return;

        turn.Items.CollectionChanged += OnItemsChanged;
        _isSubscribedToTurnItems = true;
    }

    private void UnsubscribeFromTurnItems(TranscriptTurn? turn)
    {
        if (!_isSubscribedToTurnItems || turn is null)
            return;

        turn.Items.CollectionChanged -= OnItemsChanged;
        _isSubscribedToTurnItems = false;
    }
}
