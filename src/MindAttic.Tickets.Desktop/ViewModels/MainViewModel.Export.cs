using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// Export (M.TIX-T87): the tickets the current view shows, with every filter, the query and the sort applied, saved as
/// CSV or markdown (the same writer as <c>tickets ticket list --format csv|md</c>).
/// </summary>
public sealed partial class MainViewModel
{
    public const string ExportFilter = "CSV (*.csv)|*.csv|Markdown (*.md)|*.md";

    public ICommand ExportCommand { get; private set; } = null!;

    private void InitExport() =>
        ExportCommand = new RelayCommand(async () => await ExportAsync(), () => snapshot is not null && !IsAuditView);

    /// <summary>The rows shown, in their order: the Queue's, the Board's columns left to right, else the Repos list's.</summary>
    private IEnumerable<TicketRowViewModel> ShownRows() =>
        IsQueueView ? QueueRows : IsBoardView ? BoardColumns.SelectMany(c => c.Rows) : TicketRows;

    /// <summary>What Export writes in <paramref name="format"/> (<see cref="TicketExport.Csv"/> or <see cref="TicketExport.Markdown"/>).</summary>
    public string ExportText(string format)
    {
        var byId = (snapshot?.Tickets ?? []).ToDictionary(t => t.Ticket.Id, StringComparer.OrdinalIgnoreCase);
        var rows = ShownRows().Select(r => byId.GetValueOrDefault(r.Id)).OfType<Data.TicketInfo>()
            .Select(t => new ExportRow(t.Repo, t.Ticket, t.Labels, t.Approval));
        return TicketExport.Write(format, rows);
    }

    /// <summary>Asks where to save, then writes the shown tickets there as CSV or markdown (by the file's extension).</summary>
    public async Task ExportAsync()
    {
        if (snapshot is null || IsAuditView) return;
        var count = ShownRows().Count();
        var name = $"tickets-{SelectedView.ToLowerInvariant()}-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv";
        if (Dialogs.SaveFile($"Export {count} ticket(s)", name, ExportFilter) is not { } path) return;
        var format = TicketExport.FormatOf(path) ?? TicketExport.Csv;
        var text = ExportText(format);
        try
        {
            // A BOM lets Excel read a CSV's em dashes and accents as UTF-8; markdown goes without.
            await Task.Run(() => File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == TicketExport.Csv)));
            StatusMessage = $"Exported {count} ticket(s) to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TicketsLog.For("Export").Warning(ex, "Couldn't export to {Path}", path);
            StatusMessage = $"Couldn't export: {ex.Message}";
        }
        Announcement = StatusMessage;
    }
}
