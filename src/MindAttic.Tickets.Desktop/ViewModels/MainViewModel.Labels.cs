using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// Ticket labels and components (M.TIX-T74): the detail pane adds labels and removes one by its chip's ✕, and the label
/// filter narrows every list to the tickets carrying one. A component is a <c>component:</c> label.
/// </summary>
public sealed partial class MainViewModel
{
    private ICommand? addLabelCommand, removeLabelCommand, filterByLabelCommand;

    public ICommand AddLabelCommand => addLabelCommand ??= new RelayCommand(async () => await AddLabelAsync(), () => Detail is not null);
    public ICommand RemoveLabelCommand => removeLabelCommand ??= new RelayCommand(async p => await RemoveLabelAsync(p as string), p => p is string && Detail is not null);

    /// <summary>A chip clicked: show only the tickets with its label.</summary>
    public ICommand FilterByLabelCommand => filterByLabelCommand ??= new RelayCommand(p => FilterByLabel(p as string), p => p is string);

    /// <summary>The filter's choices follow the labels in use; a filter on a label no ticket has any more is kept until changed.</summary>
    private void SyncLabelChoices(IReadOnlyList<string> names)
    {
        var wanted = new List<string> { AnyLabel };
        wanted.AddRange(names);
        if (LabelFilter != AnyLabel && !wanted.Contains(LabelFilter)) wanted.Add(LabelFilter);
        if (LabelChoices.SequenceEqual(wanted)) return;
        for (var i = LabelChoices.Count - 1; i >= 0; i--)
            if (!wanted.Contains(LabelChoices[i])) LabelChoices.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < LabelChoices.Count && LabelChoices[i] == wanted[i]) continue;
            if (LabelChoices.Contains(wanted[i])) LabelChoices.Remove(wanted[i]);
            LabelChoices.Insert(i, wanted[i]);
        }
    }

    private void FilterByLabel(string? label)
    {
        if (label is null) return;
        LabelFilter = label;
        StatusMessage = Announcement = $"Showing tickets labelled {label}.";
    }

    private async Task AddLabelAsync()
    {
        if (Detail is not { } d) return;
        var answer = Dialogs.Ask(new Prompt($"Label {d.Id}",
            "One or more labels, separated by spaces or commas: a letter or digit first, then letters, digits and - _ . : /. A component is 'component:<name>'.",
            "Labels", Ok: "Add"));
        if (answer is null) return;
        var labels = answer.Text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length == 0) return;
        if (labels.FirstOrDefault(l => TicketsDb.NormalizeLabel(l) is null) is { } bad)
        {
            d.Error = $"\"{bad}\" isn't a label: a letter or digit first, then letters, digits and - _ . : /.";
            StatusMessage = d.Error;
            Announcement = "Error: " + d.Error;
            return;
        }
        await Do(() => store.AddLabels(d.Id, labels), $"{d.Id} labelled {string.Join(", ", labels.Select(l => TicketsDb.NormalizeLabel(l)))}.");
    }

    private async Task RemoveLabelAsync(string? label)
    {
        if (Detail is not { } d || label is null) return;
        await Do(() => store.RemoveLabel(d.Id, label), $"Removed label {label} from {d.Id}.");
    }
}
