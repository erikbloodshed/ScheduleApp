using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;

namespace ScheduleApp.Desktop.Views;

/// <summary>One line of text -- see <see cref="ViewModels.TextPromptViewModel"/>.</summary>
public partial class InputDialog
{
    /// <summary>Shown for a TextPromptViewModel its opener builds (see
    /// ReactiveViewModel.ShowDialog); the view locator creates it through this
    /// constructor.</summary>
    public InputDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Title, v => v.Title).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Prompt, v => v.PromptText.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.Text, v => v.ValueBox.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.AcceptCommand, v => v.OkButton).DisposeWith(d);
            viewModel.AcceptCommand
                .Where(accepted => accepted)
                .Subscribe(_ => DialogResult = true)
                .DisposeWith(d);
        });

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }
}
