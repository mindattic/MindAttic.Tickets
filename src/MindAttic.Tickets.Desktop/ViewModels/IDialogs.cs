namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>A filled-in new ticket.</summary>
public sealed record NewTicket(string Repo, string Type, string Summary, string Ref, string Description = "");

/// <summary>The answer to a prompt: the choice (if it offered one) and the text (if it asked for one).</summary>
public sealed record PromptAnswer(string? Choice, string Text);

/// <summary>A question the view model asks; the window shows a dialog, tests answer directly.</summary>
/// <param name="Multiline">The text is several lines (a ticket's markdown description): Enter starts a new line.</param>
public sealed record Prompt(string Title, string Message, string? TextLabel = null, bool TextRequired = true,
    string? ChoiceLabel = null, IReadOnlyList<string>? Choices = null, string? Choice = null, string? Initial = null, string Ok = "OK",
    bool Multiline = false);

/// <summary>Modal questions, kept behind an interface so the view models are testable without windows.</summary>
public interface IDialogs
{
    /// <summary>Null when cancelled.</summary>
    PromptAnswer? Ask(Prompt prompt);

    /// <summary>Null when cancelled.</summary>
    NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type);

    /// <summary>A file to save to (M.TIX-T87's Export), <paramref name="fileName"/> suggested; null when cancelled. <paramref name="filter"/>
    /// is a file dialog filter ("CSV (*.csv)|*.csv|…").</summary>
    string? SaveFile(string title, string fileName, string filter) => null;

    /// <summary>
    /// Null when cancelled. The dialog starts the description from the type's template and warns, as the summary is
    /// typed, of open tickets that read alike (M.TIX-T88); a dialog that does neither just answers the plain overload.
    /// </summary>
    NewTicket? NewTicket(NewTicketRequest request) => NewTicket(request.Repos, request.Repo, request.Type);
}

/// <summary>What the new-ticket dialog starts on, and how it finds the open tickets of a repo that read like a summary.</summary>
public sealed record NewTicketRequest(IReadOnlyList<string> Repos, string? Repo, string Type,
    Func<string, string, IReadOnlyList<MindAttic.Tickets.Log.SimilarTicket>> Similar);
