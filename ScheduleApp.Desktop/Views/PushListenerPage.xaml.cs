using System.Windows.Controls;
using ScheduleApp.Desktop.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace ScheduleApp.Desktop.Views;

public partial class PushListenerPage : Page, INavigationAware
{
    public PushListenerPage(PushListenerViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    // Deliberately does NOT test the connection (or pull the device list) on navigation --
    // that used to happen automatically on first visit, but this tab can point at a
    // PushListener on a different machine (see the class doc comment), and a network call
    // firing just from opening the tab isn't always wanted. The "Test Connection" button
    // in PushListenerView.xaml (bound to TestConnectionCommand) is how the person now
    // triggers it themselves.
    public Task OnNavigatedToAsync() => Task.CompletedTask;

    public Task OnNavigatedFromAsync() => Task.CompletedTask;
}
