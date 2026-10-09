using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// An ObservableCollection that can take many items in one change: a single Reset instead of
/// one Add per item, so a bound grid (or a FilteredCollection over this) rebuilds once rather
/// than once per row, and anything re-reading the whole collection on change does so once.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces every item with <paramref name="items"/>.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        AppendSilently(items);
        RaiseReset();
    }

    /// <summary>Appends <paramref name="items"/>.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        if (AppendSilently(items))
            RaiseReset();
    }

    private bool AppendSilently(IEnumerable<T> items)
    {
        var added = false;
        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }

        return added;
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
