using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>Asks for one line of text -- a new department's name, a holiday's name. OK needs
/// something typed; <see cref="Value"/> is what was, trimmed.</summary>
public partial class TextPromptViewModel(string title, string prompt, string defaultValue = "") : ReactiveViewModel
{
    public string Title { get; } = title;

    public string Prompt { get; } = prompt;

    /// <summary>Starts at the default, selected as the dialog opens -- a one-keystroke accept
    /// or replace, not something to backspace through.</summary>
    [Reactive]
    public partial string Text { get; set; } = defaultValue;

    /// <summary>The accepted text, trimmed -- meaningful once OK has returned true.</summary>
    public string Value => Text.Trim();

    [ReactiveCommand]
    private async Task<bool> AcceptAsync()
    {
        if (!string.IsNullOrWhiteSpace(Text))
            return true;

        await NotifyAsync("Please enter a value.", "Required", NoticeKind.Warning);
        return false;
    }
}
