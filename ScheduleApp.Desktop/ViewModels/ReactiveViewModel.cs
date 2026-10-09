using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// A ReactiveObject that asks the person things through Interactions its View answers
/// (Views/ViewInteractions.cs) -- a Yes/No question, a notice, a dialog of its own, a file to
/// open or save to -- so the ViewModel never shows a window itself, and a test can answer them.
/// The base of ViewModelBase, and of dialogs with nothing to report on the status bar.
/// </summary>
public abstract class ReactiveViewModel : ReactiveObject
{
    /// <summary>A Yes/No question; the output is true for Yes. The View answers it with a
    /// MessageBox (Views/ViewInteractions.cs), so the ViewModel never shows a window.</summary>
    public Interaction<Confirmation, bool> Confirm { get; } = new();

    /// <summary>A message the user has to acknowledge (OK only), for one too long or too
    /// important for the status bar, such as the problems an import found.</summary>
    public Interaction<Notice, RxVoid> Notify { get; } = new();

    /// <summary>Shows the dialog for a ViewModel this one built, modally, and answers true if
    /// it was accepted. The View resolves the dialog through the view locator (whichever
    /// window is the IViewFor of that ViewModel's type), so this ViewModel never touches a
    /// window; whatever the dialog settled on is read back off the dialog's own ViewModel.</summary>
    public Interaction<ReactiveViewModel, bool> ShowDialog { get; } = new();

    /// <summary>Asks for a file to open; the output is its path, or null if the person
    /// cancelled.</summary>
    public Interaction<FileRequest, string?> PickFileToOpen { get; } = new();

    /// <summary>Asks where to save a file; the output is its path, or null if the person
    /// cancelled.</summary>
    public Interaction<FileRequest, string?> PickFileToSave { get; } = new();

    /// <summary>Opens a file in whatever the system has registered for its type, such as a
    /// just-saved workbook in Excel.</summary>
    public Interaction<string, RxVoid> OpenFile { get; } = new();

    /// <summary>Asks <paramref name="message"/> as a Yes/No question (see <see cref="Confirm"/>).</summary>
    protected async Task<bool> ConfirmAsync(string message, string title, bool isWarning = false) =>
        await Confirm.Handle(new Confirmation(title, message, isWarning));

    /// <summary>Shows <paramref name="message"/> until the user acknowledges it (see <see cref="Notify"/>).</summary>
    protected async Task NotifyAsync(string message, string title, NoticeKind kind = NoticeKind.Information) =>
        await Notify.Handle(new Notice(title, message, kind));

    /// <summary>Shows <paramref name="dialog"/>'s dialog (see <see cref="ShowDialog"/>); true if it
    /// was accepted.</summary>
    protected async Task<bool> ShowDialogAsync(ReactiveViewModel dialog) => await ShowDialog.Handle(dialog);

    /// <summary>A file to open (see <see cref="PickFileToOpen"/>), or null if cancelled.</summary>
    protected async Task<string?> PickFileToOpenAsync(
        string filter, string? fileName = null, string? title = null, string? initialDirectory = null) =>
        await PickFileToOpen.Handle(new FileRequest(filter, fileName, title, initialDirectory));

    /// <summary>Where to save a file (see <see cref="PickFileToSave"/>), or null if cancelled.</summary>
    protected async Task<string?> PickFileToSaveAsync(
        string filter, string? fileName = null, string? title = null, string? initialDirectory = null) =>
        await PickFileToSave.Handle(new FileRequest(filter, fileName, title, initialDirectory));

    /// <summary>Opens <paramref name="path"/> in its default app (see <see cref="OpenFile"/>).</summary>
    protected async Task OpenFileAsync(string path) => await OpenFile.Handle(path);
}
