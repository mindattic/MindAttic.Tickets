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
}
