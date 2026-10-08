using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// The items of a source collection that pass <see cref="Filter"/>, in the source's order, as a
/// collection an SfDataGrid can bind to. It replaces CollectionViewSource.GetDefaultView(...)
/// plus ICollectionView.Filter, which SfDataGrid doesn't honour: the grid builds its own view
/// over whatever it's given. The same shape as the ICollectionView it replaces, so the
/// ViewModels using it set <see cref="Filter"/> and call <see cref="Refresh"/> exactly as
/// before.
///
/// Rows appended to the source -- a load adding its results one by one -- are appended here as
/// they come, if they pass. Anything else (a Clear, a removal, a Filter change, Refresh) rebuilds
/// the whole collection and raises a single Reset, as a CollectionView's own Refresh did.
/// </summary>
public sealed class FilteredCollection<T> : ObservableCollection<T>
{
    private readonly ObservableCollection<T> _source;
    private Predicate<object>? _filter;

    public FilteredCollection(ObservableCollection<T> source)
    {
        _source = source;
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
                    Add(item);
            }

            return;
        }

        Rebuild();
    }

    private void Rebuild()
    {
        Items.Clear();
        foreach (var item in _source)
        {
            if (Passes(item))
                Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
