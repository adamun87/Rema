using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Lumi.ViewModels;

public sealed class TranscriptTurnControl : UserControl
{
    private const double TurnItemSpacing = 8d;
    private readonly StackPanel _itemsHost;

    public ObservableCollection<TranscriptItem> Items { get; } = [];
    public string StableId { get; }

    public TranscriptTurnControl(string stableId)
    {
        StableId = stableId;

        _itemsHost = new StackPanel
        {
            Spacing = TurnItemSpacing
        };

        Content = _itemsHost;
        Items.CollectionChanged += OnItemsChanged;
    }

    public int IndexOf(TranscriptItem item) => Items.IndexOf(item);

    public bool Remove(TranscriptItem item) => Items.Remove(item);

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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
    }

    private void RebuildItemHosts()
    {
        _itemsHost.Children.Clear();
        foreach (var item in Items)
            _itemsHost.Children.Add(CreateItemHost(item));
    }

    private static Control CreateItemHost(TranscriptItem item)
    {
        return new ContentPresenter
        {
            Content = item,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
    }
}