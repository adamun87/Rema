using Avalonia.Controls;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

public sealed class TranscriptTurnControl : UserControl, IStrataVirtualizedItem
{
    private const double TurnItemSpacing = 8d;
    private readonly StackPanel _itemsHost;

    public ObservableCollection<TranscriptItem> Items { get; } = [];

    public object? VirtualizationRecycleKey { get; }
    public object? VirtualizationMeasureKey { get; }
    public double? VirtualizationHeightHint { get; private set; } = 32d;

    public TranscriptTurnControl(string stableId, object? recycleKey = null)
    {
        VirtualizationMeasureKey = stableId;
        VirtualizationRecycleKey = recycleKey ?? typeof(TranscriptTurnControl);

        _itemsHost = new StackPanel
        {
            Spacing = TurnItemSpacing
        };

        Content = _itemsHost;
        Items.CollectionChanged += OnItemsChanged;
        RefreshHeightHint();
    }

    public int IndexOf(TranscriptItem item) => Items.IndexOf(item);

    public bool Remove(TranscriptItem item) => Items.Remove(item);

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<TranscriptItem>())
                item.PropertyChanged -= OnItemPropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<TranscriptItem>())
                item.PropertyChanged += OnItemPropertyChanged;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null && e.NewStartingIndex >= 0:
                for (var i = 0; i < e.NewItems.Count; i++)
                    _itemsHost.Children.Insert(e.NewStartingIndex + i, CreateItemHost((TranscriptItem)e.NewItems[i]!));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null && e.OldStartingIndex >= 0:
                for (var i = 0; i < e.OldItems.Count; i++)
                    _itemsHost.Children.RemoveAt(e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems is not null && e.NewStartingIndex >= 0:
                for (var i = 0; i < e.NewItems.Count; i++)
                    _itemsHost.Children[e.NewStartingIndex + i] = CreateItemHost((TranscriptItem)e.NewItems[i]!);
                break;
            case NotifyCollectionChangedAction.Move:
            case NotifyCollectionChangedAction.Reset:
            default:
                RebuildItemHosts();
                break;
        }

        RefreshHeightHint();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranscriptItem.VirtualizationHeightHint))
            RefreshHeightHint();
    }

    private void RefreshHeightHint()
    {
        var total = 0d;
        foreach (var item in Items)
            total += System.Math.Max(24d, item.VirtualizationHeightHint ?? 24d);

        if (Items.Count > 1)
            total += (Items.Count - 1) * TurnItemSpacing;

        var nextHeightHint = System.Math.Max(32d, total);
        if (VirtualizationHeightHint is double currentHeightHint && System.Math.Abs(currentHeightHint - nextHeightHint) < 0.5d)
            return;

        VirtualizationHeightHint = nextHeightHint;
        if (VisualRoot is not null)
            InvalidateMeasure();
    }

    private void RebuildItemHosts()
    {
        _itemsHost.Children.Clear();
        foreach (var item in Items)
            _itemsHost.Children.Add(CreateItemHost(item));
    }

    private static ContentControl CreateItemHost(TranscriptItem item)
    {
        return new ContentControl
        {
            Content = item,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
    }
}