using System.Diagnostics;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.Views;
using Syncfusion.SfSkinManager;
using Syncfusion.UI.Xaml.NavigationDrawer;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop;

/// <summary>
/// The main window -- see <see cref="ShellViewModel"/> for the sign-in gate, title, pinned
/// drawer and footer dialogs. What stays here is the window's own: the navigation drawer's
/// layout, and opening pages in the Frame as the drawer's items are picked.
/// </summary>
public partial class MainWindow
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>The page on screen, told when it's left -- see <see cref="NavigateTo"/>.</summary>
    private INavigationAware? _currentPage;

    public MainWindow(IServiceProvider serviceProvider, IStatusBarService statusBarService, ShellViewModel viewModel)
    {
        InitializeComponent();

        // Pages come from DI, the same scoped instances each time -- see App.xaml.cs.
        _serviceProvider = serviceProvider;
        ViewModel = viewModel;
        statusBarService.SetStatusBarPresenter(RootStatusBarPresenter);

        SignInPanelControl.ViewModel = viewModel.SignIn;
        SetupAdminPanelControl.ViewModel = viewModel.SetupAdmin;
        SignInPanelControl.ApplyLogo(viewModel.LogoPath);
        SetupAdminPanelControl.ApplyLogo(viewModel.LogoPath);
        NavigationDrawer.SizeChanged += NavigationDrawer_SizeChanged;

        this.WhenActivated((MultipleDisposable d) =>
        {
            ViewInteractions.Register(viewModel, this).DisposeWith(d);
            viewModel.Restart.RegisterHandler(context =>
            {
                if (Environment.ProcessPath is { } exePath)
                {
                    Process.Start(exePath);
                    Application.Current.Shutdown();
                }

                context.SetOutput(RxVoid.Default);
            }).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Title, v => v.Title).DisposeWith(d);

            // The overlay and the pin, until someone signs in -- the pin sits on the title
            // bar, which the overlay doesn't cover.
            this.OneWayBind(ViewModel, vm => vm.Stage, v => v.AuthOverlay.Visibility,
                stage => VisibleWhen(stage != ShellStage.SignedIn)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Stage, v => v.SignInPanelControl.Visibility,
                stage => VisibleWhen(stage == ShellStage.SignIn)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Stage, v => v.SetupAdminPanelControl.Visibility,
                stage => VisibleWhen(stage == ShellStage.SetupAdmin)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Stage, v => v.PinPaneButton.Visibility,
                stage => VisibleWhen(stage == ShellStage.SignedIn)).DisposeWith(d);
            viewModel.WhenAnyValue(vm => vm.Stage)
                .Where(stage => stage == ShellStage.SignedIn)
                .Take(1)
                .Subscribe(_ => NavigateTo(ScheduleNavItem))
                .DisposeWith(d);
            Observable.Merge(SignInPanelControl.ExitRequested, SetupAdminPanelControl.ExitRequested)
                .Subscribe(_ => Close())
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.PinGlyph, v => v.PinPaneGlyph.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PinToolTip, v => v.PinPaneButton.ToolTip).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.TogglePinnedCommand, v => v.PinPaneButton).DisposeWith(d);
            viewModel.WhenAnyValue(vm => vm.IsDrawerPinned).Subscribe(ApplyDrawerPinned).DisposeWith(d);
        });

        // Focus needs the window rendered, so it waits for Loaded.
        Loaded += (_, _) =>
        {
            switch (viewModel.Stage)
            {
                case ShellStage.SignIn:
                    SignInPanelControl.FocusUsername();
                    break;
                case ShellStage.SetupAdmin:
                    SetupAdminPanelControl.FocusUsername();
                    break;
            }
        };
    }

    /// <summary>Called by startup before Show(): which card greets whoever's at the keyboard
    /// -- see <see cref="ShellViewModel.BeginSignIn"/>.</summary>
    public void ShowSignInOverlay(bool hasExistingAccounts) => ViewModel!.BeginSignIn(hasExistingAccounts);

    /// <summary>Pinned: the drawer is Expanded, its own toggle hidden since the pin owns that
    /// job. Unpinned: Compact, an icon rail whose toggle expands the menu over the page until a
    /// page is picked, with Attendance's three pages in a popup off the rail.
    ///
    /// Expanded, the drawer pushes the content right by the difference between its two widths
    /// but still sizes it as if Compact, so the page would run that far off the window; the
    /// Frame's right margin takes the difference back.</summary>
    private void ApplyDrawerPinned(bool pinned)
    {
        NavigationDrawer.DisplayMode = pinned ? DisplayMode.Expanded : DisplayMode.Compact;
        NavigationDrawer.IsToggleButtonVisible = !pinned;
        NavigationDrawer.IsOpen = pinned;
        ContentFrame.Margin = pinned
            ? new Thickness(0, 0, NavigationDrawer.ExpandedModeWidth - NavigationDrawer.CompactModeWidth, 0)
            : default;
    }

    /// <summary>Keeps the page as wide as the window while the drawer is pinned. The drawer
    /// resizes its content to the window only while Compact; Expanded, it leaves the content at
    /// the width it had when it opened, so maximizing or restoring the window would leave the
    /// page that width until the next pin or unpin. The width the drawer gives the content is
    /// held by its own animation, which has to be cleared before a new width can take. Runs
    /// after the drawer's own SizeChanged handler, which it subscribes in its
    /// constructor.</summary>
    private void NavigationDrawer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel?.IsDrawerPinned != true
            || NavigationDrawer.Template?.FindName("ContentViewContentPresenter", NavigationDrawer) is not ContentPresenter content)
            return;

        content.BeginAnimation(WidthProperty, null);
        content.Width = e.NewSize.Width - NavigationDrawer.CompactModeWidth;
    }

    /// <summary>A page item opens its page (its Tag is the page's type); a footer item opens its
    /// dialog (its Tag names it). Attendance itself has no Tag -- the drawer expands its
    /// sub-items, or pops them up off the compact rail, on its own. Unpinned, picking a page
    /// folds the expanded menu back to the rail, out of the page's way.</summary>
    private void NavigationDrawer_ItemClicked(object? sender, NavigationItemClickedEventArgs e)
    {
        switch (e.Item?.Tag)
        {
            case Type:
                NavigateTo(e.Item);
                if (ViewModel?.IsDrawerPinned != true)
                    NavigationDrawer.IsOpen = false;
                break;
            case string dialog when DialogCommand(dialog) is { } command:
                ((ICommand)command).Execute(null);
                break;
        }
    }

    private ReactiveCommand<RxVoid, RxVoid>? DialogCommand(string dialog) => dialog switch
    {
        "Holidays" => ViewModel!.OpenManageHolidaysCommand,
        "Users" => ViewModel!.OpenManageUsersCommand,
        "Backup" => ViewModel!.OpenBackupRestoreCommand,
        "Settings" => ViewModel!.OpenSettingsCommand,
        _ => null,
    };

    /// <summary>Shows <paramref name="item"/>'s page (its Tag) and marks the item selected,
    /// which the drawer doesn't do itself for a navigation that didn't come from a click (the
    /// first page after sign-in). The page being left, then the new one, are told
    /// (<see cref="INavigationAware"/>) once the Frame has switched
    /// (<see cref="ContentFrame_Navigated"/>).</summary>
    private void NavigateTo(NavigationItem item)
    {
        if (item.Tag is not Type pageType)
            return;

        var page = (Page)_serviceProvider.GetRequiredService(pageType);
        NavigationDrawer.SelectedItem = item;

        // A Frame doesn't pass inherited properties on to its Page, and the theme reaches a
        // control through one (SfSkinManager.Theme; see App.xaml.cs), so the page is themed
        // directly or everything on it keeps WPF's own look.
        if (SfSkinManager.GetTheme(page) is null)
            SfSkinManager.SetTheme(page, SfSkinManager.ApplicationTheme);

        if (ReferenceEquals(ContentFrame.Content, page))
            return;

        ContentFrame.Navigate(page);
    }

    /// <summary>Clears the journal the Frame just added to -- the app has no back/forward -- so
    /// it doesn't hold on to the pages it showed, then runs the page lifecycle calls
    /// <see cref="NavigateTo"/> deferred to here. async void, so an exception from a page's
    /// OnNavigatedToAsync reaches App's DispatcherUnhandledException handler.</summary>
    private async void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        while (ContentFrame.CanGoBack)
            ContentFrame.RemoveBackEntry();

        var previous = _currentPage;
        _currentPage = e.Content as INavigationAware;

        if (previous is not null && !ReferenceEquals(previous, _currentPage))
            await previous.OnNavigatedFromAsync();
        if (_currentPage is not null)
            await _currentPage.OnNavigatedToAsync();
    }

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
