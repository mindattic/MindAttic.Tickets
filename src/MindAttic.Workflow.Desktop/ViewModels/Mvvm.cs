using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MindAttic.Workflow.Desktop.ViewModels;

// Copied from MindAttic.Orchestrator.Wpf until both use MindAttic.Components.Wpf's copy (M.COMP-T11).

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

public static class CollectionSync
{
    /// <summary>Makes <paramref name="target"/> hold exactly <paramref name="desired"/>, in order, by moving existing
    /// items rather than re-adding them, so bound controls keep focus and selection across refreshes.</summary>
    public static void ReorderTo<T>(this ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var at = IndexOf(target, desired[i], i + 1);
            if (at >= 0) target.Move(at, i);
            else target.Insert(i, desired[i]);
        }
        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }

    /// <summary>Keyed sync: reuses rows whose key survives, creates new ones, drops the rest.</summary>
    public static void SyncBy<TRow, TSource, TKey>(
        this ObservableCollection<TRow> target, IEnumerable<TSource> source,
        Func<TSource, TKey> sourceKey, Func<TRow, TKey> rowKey,
        Func<TSource, TRow> create, Action<TRow, TSource> update) where TRow : class where TKey : notnull
    {
        var existing = new Dictionary<TKey, TRow>();
        foreach (var r in target) existing.TryAdd(rowKey(r), r);
        var seen = new HashSet<TKey>();
        var desired = new List<TRow>();
        foreach (var s in source)
        {
            if (!seen.Add(sourceKey(s))) continue;
            if (existing.TryGetValue(sourceKey(s), out var row)) update(row, s);
            else row = create(s);
            desired.Add(row);
        }
        target.ReorderTo(desired);
    }

    private static int IndexOf<T>(ObservableCollection<T> items, T item, int start) where T : class
    {
        for (var i = start; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }
}
