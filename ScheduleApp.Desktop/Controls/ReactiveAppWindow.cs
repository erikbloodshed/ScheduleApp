using System.Diagnostics;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// An <see cref="AppWindow"/> that is the view for a <typeparamref name="TViewModel"/>: what
/// ReactiveUI's own ReactiveWindow is to a plain Window, for the app's chromeless windows. A
/// dialog's XAML derives from it with x:TypeArguments, sets <see cref="ViewModel"/>, and binds
/// to it in WhenActivated.
/// </summary>
[DebuggerDisplay("{BindingRoot}, {ViewModel}")]
public class ReactiveAppWindow<TViewModel> : AppWindow, IViewFor<TViewModel>
    where TViewModel : class
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(TViewModel), typeof(ReactiveAppWindow<TViewModel>), new PropertyMetadata(null));

    public ReactiveAppWindow() => this.WhenActivated();

    public TViewModel? BindingRoot => ViewModel;

    public TViewModel? ViewModel
    {
        get => (TViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    object? IViewFor.ViewModel
    {
        get => ViewModel;
        set => ViewModel = (TViewModel?)value;
    }
}
