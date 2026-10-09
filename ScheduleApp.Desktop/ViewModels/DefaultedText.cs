using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// A text a person can type over that shows a computed default until they do -- a manual
/// punch's Reason, defaulting to the punch type ("Clock In") or slot ("Time In") being logged,
/// so someone with nothing more specific to say isn't blocked on typing something. The view
/// shows it grayed out while <see cref="IsDefault"/> (see Utilities/DefaultTextBox).
///
/// <see cref="Value"/> is what to save: the typed text, trimmed, or the default when the box
/// was left as it was or emptied.
/// </summary>
public sealed partial class DefaultedText : ReactiveObject
{
    private readonly Func<string> _computeDefault;
    private bool _showingDefault;

    public DefaultedText(Func<string> computeDefault, string? initialText = null)
    {
        _computeDefault = computeDefault;

        // The first real edit -- typing, pasting, deleting a character -- makes it the
        // person's own text, which stops tracking the default.
        this.WhenAnyValue(x => x.Text)
            .Skip(1)
            .Where(_ => !_showingDefault)
            .Subscribe(_ => IsDefault = false);

        if (string.IsNullOrWhiteSpace(initialText))
            ShowDefault();
        else
            Text = initialText;
    }

    [Reactive]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>Whether the view grays the default out as a hint (the usual), rather than
    /// showing it as plainly as typed text -- for a saved value that inherited the default,
    /// where the default is what's actually in effect.</summary>
    public bool GraysDefault { get; init; } = true;

    /// <summary>Still showing the default, untouched.</summary>
    [Reactive]
    public partial bool IsDefault { get; private set; }

    public string Value => string.IsNullOrWhiteSpace(Text) ? _computeDefault() : Text.Trim();

    /// <summary>Re-shows the default after what it's computed from changed -- a no-op once
    /// the person has typed their own text.</summary>
    public void RefreshDefault()
    {
        if (IsDefault)
            ShowDefault();
    }

    /// <summary>When the person leaves the box: emptied means "no override", so the default
    /// shows again rather than a blank box hiding what will actually be saved.</summary>
    public void FinishEditing()
    {
        if (string.IsNullOrWhiteSpace(Text))
            ShowDefault();
    }

    private void ShowDefault()
    {
        _showingDefault = true;
        Text = _computeDefault();
        IsDefault = true;
        _showingDefault = false;
    }
}
