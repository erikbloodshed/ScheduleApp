using System.Collections.ObjectModel;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Makes an ObservableCollection hold exactly the items of another sequence, in its order, by
/// removing and inserting only what differs rather than clearing and refilling it. An
/// SfTreeView bound to the collection then keeps the nodes it already has, and with them their
/// expansion and selection, which a Clear would throw away on every keystroke of a search box.
/// </summary>
internal static class CollectionSync
{
    public static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> wanted) where T : class
    {
        var wantedList = wanted as IReadOnlyList<T> ?? [.. wanted];
        var wantedSet = new HashSet<T>(wantedList, ReferenceEqualityComparer.Instance);

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wantedSet.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (var i = 0; i < wantedList.Count; i++)
        {
            var item = wantedList[i];
            if (i < target.Count && ReferenceEquals(target[i], item)) continue;

            var existing = IndexOf(target, item, i + 1);
            if (existing >= 0)
                target.Move(existing, i);
            else
                target.Insert(i, item);
        }
    }

    private static int IndexOf<T>(ObservableCollection<T> items, T item, int start) where T : class
    {
        for (var i = start; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item)) return i;
        }

        return -1;
    }
}
