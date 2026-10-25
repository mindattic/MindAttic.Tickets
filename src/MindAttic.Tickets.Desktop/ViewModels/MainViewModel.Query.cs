using System.Collections.ObjectModel;
using System.Windows.Input;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The filter bar's query and saved filters (M.TIX-T78): a <see cref="TicketQuery"/> (<c>repo:</c> <c>type:</c> <c>status:</c>
/// <c>label:</c> <c>approval:</c> and free text) narrowing both lists on top of the other filters, and named queries kept in
/// the database (shared with <c>tickets filter</c> and <c>tickets ticket list --filter</c>) to pick, save and delete.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The saved-filter choice that applies none.</summary>
    public const string NoSavedFilter = "(none)";

    private string query = "", queryError = "", selectedSavedFilter = NoSavedFilter;
    private TicketQuery parsedQuery = TicketQuery.All;

    public ObservableCollection<string> SavedFilterChoices { get; } = [NoSavedFilter];

    public ICommand SaveFilterCommand { get; private set; } = null!;
    public ICommand DeleteFilterCommand { get; private set; } = null!;

    private void InitQuery()
    {
        SaveFilterCommand = new RelayCommand(async () => await SaveFilterAsync(), () => snapshot is not null && Query.Trim().Length > 0 && QueryError.Length == 0);
        DeleteFilterCommand = new RelayCommand(async () => await DeleteFilterAsync(), () => SelectedSavedFilter != NoSavedFilter);
    }

    /// <summary>The query typed in the filter bar. One that doesn't parse filters nothing out and says why in <see cref="QueryError"/>.</summary>
    public string Query
    {
        get => query;
        set
        {
            if (!Set(ref query, value ?? "")) return;
            if (TicketQuery.TryParse(query, out var parsed, out var error))
            {
                parsedQuery = parsed;
                QueryError = "";
            }
            else
            {
                parsedQuery = TicketQuery.All;
                QueryError = error!;
                Announcement = error!;
            }
            // Typing past a saved filter's query leaves it unpicked.
            if (SelectedSavedFilter != NoSavedFilter && SavedQuery(SelectedSavedFilter) is { } saved && saved != query.Trim())
            {
                selectedSavedFilter = NoSavedFilter;
                Raise(nameof(SelectedSavedFilter));
            }
            ApplyFilters();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Why <see cref="Query"/> doesn't parse, or empty.</summary>
    public string QueryError { get => queryError; private set => Set(ref queryError, value); }

    /// <summary>The saved filter picked; picking one puts its query in <see cref="Query"/>.</summary>
    public string SelectedSavedFilter
    {
        get => selectedSavedFilter;
        set
        {
            if (!Set(ref selectedSavedFilter, value ?? NoSavedFilter)) return;
            if (selectedSavedFilter == NoSavedFilter) Query = "";
            else if (SavedQuery(selectedSavedFilter) is { } saved)
            {
                Query = saved;
                Announcement = $"Filter {selectedSavedFilter}: {saved}";
            }
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string? SavedQuery(string name) =>
        snapshot?.SavedFilters.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Query;

    private bool QueryMatch(TicketInfo t) => parsedQuery.IsEmpty || parsedQuery.Matches(t.Facts);

    /// <summary>Keeps the saved-filter choices in step with the database's.</summary>
    private void SyncSavedFilters(Snapshot s)
    {
        var names = s.SavedFilters.Select(f => f.Name).ToList();
        foreach (var gone in SavedFilterChoices.Skip(1).Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList())
            SavedFilterChoices.Remove(gone);
        for (var i = 0; i < names.Count; i++)
        {
            var at = SavedFilterChoices.IndexOf(names[i]);
            if (at < 0) SavedFilterChoices.Insert(i + 1, names[i]);
            else if (at != i + 1) SavedFilterChoices.Move(at, i + 1);
        }
        if (!SavedFilterChoices.Contains(selectedSavedFilter))
        {
            selectedSavedFilter = NoSavedFilter;
            Raise(nameof(SelectedSavedFilter));
        }
    }

    /// <summary>Saves the query under a name asked for (the picked filter's name offered, so saving again updates it).</summary>
    public async Task SaveFilterAsync()
    {
        var text = Query.Trim();
        if (text.Length == 0 || QueryError.Length > 0) return;
        var answer = Dialogs.Ask(new Prompt("Save filter", $"Save the query\n\n{text}\n\nas a named filter? A name already saved is replaced.", "Name",
            Initial: SelectedSavedFilter != NoSavedFilter ? SelectedSavedFilter : "", Ok: "Save"));
        if (answer is null || answer.Text.Trim().Length == 0) return;
        var name = answer.Text.Trim();
        if (await Do(() => store.SaveFilter(name, text), $"Saved filter {name}."))
        {
            var shown = SavedFilterChoices.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
            selectedSavedFilter = shown;
            Raise(nameof(SelectedSavedFilter));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Deletes the picked saved filter; the query stays in the box.</summary>
    public async Task DeleteFilterAsync()
    {
        if (SelectedSavedFilter is var name && name == NoSavedFilter) return;
        selectedSavedFilter = NoSavedFilter;
        Raise(nameof(SelectedSavedFilter));
        await Do(() => store.DeleteFilter(name), $"Deleted filter {name}.");
    }
}
