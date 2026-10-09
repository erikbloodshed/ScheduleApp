using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ScheduleApp.Desktop.Utilities;

/// <summary>
/// Keeps a PasswordBox and a ViewModel's password in step. PasswordBox.Password isn't a
/// DependencyProperty -- deliberately, so a password doesn't sit in the binding system -- so
/// it can't be bound the usual way: what's typed is pushed into the ViewModel, and a value the
/// ViewModel sets (a remembered password, or clearing a refused one) is put back into the box.
/// A box the ViewModel clears takes the focus, so the person retypes it straight away.
/// </summary>
public static class PasswordBoxBinding
{
    /// <summary>Binds <paramref name="box"/> to the ViewModel's password -- read through
    /// <paramref name="values"/>, written through <paramref name="push"/> -- until
    /// disposed.</summary>
    public static IDisposable Bind(PasswordBox box, IObservable<string> values, Action<string> push)
    {
        void OnPasswordChanged(object? sender, RoutedEventArgs e) => push(box.Password);

        box.PasswordChanged += OnPasswordChanged;

        return new CompositeDisposable(
            values
                .Where(value => value != box.Password)
                .Subscribe(value =>
                {
                    box.Password = value;
                    if (value.Length == 0 && box.IsLoaded)
                        box.Focus();
                }),
            Disposable.Create(() => box.PasswordChanged -= OnPasswordChanged));
    }
}
