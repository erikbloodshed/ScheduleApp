using System.Diagnostics;
using System.Reactive.Disposables;
using System.Windows;
using Microsoft.Win32;
using ReactiveUI.Binding;
using ScheduleApp.Desktop.ViewModels;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Answers the questions a ViewModel asks of its View (see <see cref="ReactiveViewModel"/>):
/// <see cref="ReactiveViewModel.Confirm"/> and <see cref="ReactiveViewModel.Notify"/> with a
/// MessageBox, <see cref="ReactiveViewModel.PickFileToOpen"/> and
/// <see cref="ReactiveViewModel.PickFileToSave"/> with the standard file dialogs,
/// <see cref="ReactiveViewModel.OpenFile"/> with the file's default app, and
/// <see cref="ReactiveViewModel.ShowDialog"/> by showing, modally, whichever window the view
/// locator names as the view for the ViewModel it's handed. Each owned by
/// <paramref name="owner"/>'s window, or the main window when the view isn't in one yet.
///
/// A view registers its ViewModels (and any child ViewModels it shows) in its WhenActivated
/// block, so the answers last exactly as long as the view is on screen.
/// </summary>
public static class ViewInteractions
{
    public static IDisposable Register(ReactiveViewModel viewModel, DependencyObject? owner = null) =>
        new CompositeDisposable(
            viewModel.Confirm.RegisterHandler(interaction =>
            {
                var question = interaction.Input;
                var answer = ShowMessage(
                    owner, question.Message, question.Title, MessageBoxButton.YesNo,
                    question.IsWarning ? MessageBoxImage.Warning : MessageBoxImage.Question);
                interaction.SetOutput(answer == MessageBoxResult.Yes);
            }),
            viewModel.Notify.RegisterHandler(interaction =>
            {
                var notice = interaction.Input;
                ShowMessage(
                    owner, notice.Message, notice.Title, MessageBoxButton.OK,
                    notice.Kind switch
                    {
                        NoticeKind.Warning => MessageBoxImage.Warning,
                        NoticeKind.Error => MessageBoxImage.Error,
                        _ => MessageBoxImage.Information,
                    });
                interaction.SetOutput(RxVoid.Default);
            }),
            viewModel.ShowDialog.RegisterHandler(interaction =>
            {
                var dialogViewModel = interaction.Input;
                if (ViewLocator.GetCurrent().ResolveView(dialogViewModel, null) is not Window dialog)
                {
                    throw new InvalidOperationException(
                        $"No dialog is registered as the view for {dialogViewModel.GetType().Name}.");
                }

                if (OwnerOf(owner) is { } ownerWindow && !ReferenceEquals(ownerWindow, dialog))
                    dialog.Owner = ownerWindow;
                interaction.SetOutput(dialog.ShowDialog() == true);
            }),
            viewModel.PickFileToOpen.RegisterHandler(interaction =>
            {
                var request = interaction.Input;
                var dialog = new OpenFileDialog
                {
                    Filter = request.Filter,
                    FileName = request.FileName ?? string.Empty,
                    InitialDirectory = request.InitialDirectory ?? string.Empty,
                };
                if (request.Title is not null)
                    dialog.Title = request.Title;
                interaction.SetOutput(dialog.ShowDialog(OwnerOf(owner)) == true ? dialog.FileName : null);
            }),
            viewModel.PickFileToSave.RegisterHandler(interaction =>
            {
                var request = interaction.Input;
                var dialog = new SaveFileDialog
                {
                    Filter = request.Filter,
                    FileName = request.FileName ?? string.Empty,
                    InitialDirectory = request.InitialDirectory ?? string.Empty,
                };
                if (request.Title is not null)
                    dialog.Title = request.Title;
                interaction.SetOutput(dialog.ShowDialog(OwnerOf(owner)) == true ? dialog.FileName : null);
            }),
            viewModel.OpenFile.RegisterHandler(interaction =>
            {
                Process.Start(new ProcessStartInfo(interaction.Input) { UseShellExecute = true });
                interaction.SetOutput(RxVoid.Default);
            }));

    private static MessageBoxResult ShowMessage(
        DependencyObject? owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        OwnerOf(owner) is { } ownerWindow
            ? MessageBox.Show(ownerWindow, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);

    /// <summary>The window to own a dialog: <paramref name="owner"/>'s, else the main window --
    /// either only once it's on screen, since a window can't own another before it's been
    /// shown.</summary>
    private static Window? OwnerOf(DependencyObject? owner) =>
        new[] { owner is null ? null : Window.GetWindow(owner), Application.Current.MainWindow }
            .FirstOrDefault(window => window is { IsVisible: true });
}
