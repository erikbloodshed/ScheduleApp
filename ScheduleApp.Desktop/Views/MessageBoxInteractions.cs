using System.Reactive.Disposables;
using System.Windows;
using ScheduleApp.Desktop.ViewModels;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Answers a ViewModel's <see cref="ViewModelBase.Confirm"/> and <see cref="ViewModelBase.Notify"/>
/// with a MessageBox over <paramref name="owner"/>'s window, or the main window when the view
/// isn't in one yet. Each view registers its own ViewModels (and any child ViewModels it shows)
/// once, from its constructor.
/// </summary>
public static class MessageBoxInteractions
{
    public static IDisposable Register(ViewModelBase viewModel, DependencyObject? owner = null) =>
        new CompositeDisposable(
            viewModel.Confirm.RegisterHandler(interaction =>
            {
                var question = interaction.Input;
                var answer = MessageBox.Show(
                    OwnerOf(owner), question.Message, question.Title, MessageBoxButton.YesNo,
                    question.IsWarning ? MessageBoxImage.Warning : MessageBoxImage.Question);
                interaction.SetOutput(answer == MessageBoxResult.Yes);
            }),
            viewModel.Notify.RegisterHandler(interaction =>
            {
                var notice = interaction.Input;
                MessageBox.Show(
                    OwnerOf(owner), notice.Message, notice.Title, MessageBoxButton.OK,
                    notice.Kind switch
                    {
                        NoticeKind.Warning => MessageBoxImage.Warning,
                        NoticeKind.Error => MessageBoxImage.Error,
                        _ => MessageBoxImage.Information,
                    });
                interaction.SetOutput(RxVoid.Default);
            }));

    private static Window OwnerOf(DependencyObject? owner) =>
        (owner is null ? null : Window.GetWindow(owner)) ?? Application.Current.MainWindow;
}
