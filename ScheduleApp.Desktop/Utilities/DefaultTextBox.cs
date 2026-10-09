using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReactiveUI.Binding;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Utilities;

/// <summary>
/// Shows a <see cref="DefaultedText"/> in a TextBox: its text two-way, grayed out while it's
/// still the default, the whole text selected on entering the box so typing replaces it
/// wholesale (the usual "click a prefilled field and just start typing"), and the default
/// restored on leaving it empty. First tenants: ManualLogEntryDialog/PunchTimeEntryDialog's
/// Reason box.
/// </summary>
public static class DefaultTextBox
{
    /// <summary>The same binding, for a box inside a template:
    /// <c>utilities:DefaultTextBox.Source="{Binding ClockInBuffer}"</c>.</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(DefaultedText), typeof(DefaultTextBox), new PropertyMetadata(null, OnSourceChanged));

    private static readonly DependencyProperty BindingProperty = DependencyProperty.RegisterAttached(
        "Binding", typeof(IDisposable), typeof(DefaultTextBox));

    public static DefaultedText? GetSource(TextBox box) => (DefaultedText?)box.GetValue(SourceProperty);

    public static void SetSource(TextBox box, DefaultedText? value) => box.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        (box.GetValue(BindingProperty) as IDisposable)?.Dispose();
        box.SetValue(BindingProperty, e.NewValue is DefaultedText text ? Bind(box, text) : null);
    }

    /// <summary>Binds <paramref name="box"/> to <paramref name="text"/> until disposed.</summary>
    public static IDisposable Bind(TextBox box, DefaultedText text)
    {
        box.Text = text.Text;

        void OnGotFocus(object? sender, RoutedEventArgs e) => box.SelectAll();

        // SelectAll alone only sticks for keyboard focus: a mouse click re-places the caret
        // afterward unless the click that focuses the box is taken over too (a well-known WPF
        // TextBox quirk, not redundant with GotFocus).
        void OnPreviewMouseDown(object? sender, MouseButtonEventArgs e)
        {
            if (box.IsFocused) return;

            e.Handled = true;
            box.Focus();
        }

        void OnTextChanged(object? sender, TextChangedEventArgs e) => text.Text = box.Text;
        void OnLostFocus(object? sender, RoutedEventArgs e) => text.FinishEditing();

        box.GotFocus += OnGotFocus;
        box.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        box.TextChanged += OnTextChanged;
        box.LostFocus += OnLostFocus;

        return new CompositeDisposable(
            text.WhenAnyValue(t => t.Text)
                .Where(value => value != box.Text)
                .Subscribe(value => box.Text = value),
            text.WhenAnyValue(t => t.IsDefault)
                .Subscribe(isDefault =>
                {
                    if (isDefault && text.GraysDefault)
                        box.Foreground = Brushes.Gray;
                    else
                        box.ClearValue(Control.ForegroundProperty);
                }),
            Disposable.Create(() =>
            {
                box.GotFocus -= OnGotFocus;
                box.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
                box.TextChanged -= OnTextChanged;
                box.LostFocus -= OnLostFocus;
            }));
    }
}
