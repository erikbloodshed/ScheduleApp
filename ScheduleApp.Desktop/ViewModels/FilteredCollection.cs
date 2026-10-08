using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// The items of a source collection that pass <see cref="Filter"/>, in the source's order or the
/// one an optional comparer gives, as a collection an SfDataGrid can bind to. It replaces CollectionViewSource.GetDefaultView(...)
/// plus ICollectionView.Filter and SortDescriptions, which SfDataGrid doesn't honour: the grid
/// builds its own view over whatever it's given. The same shape as the ICollectionView it replaces, so the
/// ViewModels using it set <see cref="Filter"/> and call <see cref="Refresh"/> exactly as
/// before.
///
/// Rows appended to the source -- a load adding its results one by one -- are appended here as
/// they come, if they pass (inserted in order, when there's a comparer). Anything else (a Clear, a removal, a Filter change, Refresh) rebuilds
/// the whole collection and raises a single Reset, as a CollectionView's own Refresh did.
/// </summary>
public sealed class FilteredCollection<T> : ObservableCollection<T>
{
    private readonly ObservableCollection<T> _source;
    private readonly IComparer<T>? _order;
    private Predicate<object>? _filter;

    public FilteredCollection(ObservableCollection<T> source, IComparer<T>? order = null)
    {
        _source = source;
        _order = order;
        _source.CollectionChanged += OnSourceChanged;
        Rebuild();
    }

    /// <summary>Which source items show; null shows them all. Setting it re-applies it.</summary>
    public Predicate<object>? Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Refresh();
        }
    }

    /// <summary>Re-applies <see cref="Filter"/> to every source item.</summary>
    public void Refresh() => Rebuild();

    private bool Passes(T item) => _filter is null || item is null || _filter(item);

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add
            && e.NewItems is { } added
            && e.NewStartingIndex == _source.Count - added.Count)
        {
            foreach (T item in added)
            {
                if (Passes(item))
                    Insert(InsertionIndex(item), item);
            }

            return;
        }

        Rebuild();
    }

    /// <summary>The end, or with a comparer the index after every item that sorts at or before
    /// <paramref name="item"/>, so equal items keep the source's order.</summary>
    private int InsertionIndex(T item)
    {
        if (_order is null)
            return Count;

        var index = Count;
        while (index > 0 && _order.Compare(this[index - 1], item) > 0)
            index--;
        return index;
    }

    private void Rebuild()
    {
        Items.Clear();
        var passing = _source.Where(Passes);
        foreach (var item in _order is null ? passing : passing.OrderBy(i => i, _order))
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
