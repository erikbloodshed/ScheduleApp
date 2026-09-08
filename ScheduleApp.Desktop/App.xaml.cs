using System.IO;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Configuration;
using ScheduleApp.Core.Users;
using ScheduleApp.Data;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Data.Users;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;

namespace ScheduleApp.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private IServiceScope? _scope;

    /// <summary>Nothing in this app subscribed to any of WPF/.NET's unhandled-exception
    /// events before this constructor existed -- an exception that escaped every local
    /// try/catch (an async void event handler throwing, a fire-and-forget `_ = SomeAsync()`
    /// task faulting somewhere its own _busy.RunAsync wrap didn't catch, anything genuinely
    /// unanticipated) crashed the whole process instantly with no message at all, which is
    /// indistinguishable from the app just vanishing. Wired up here, in the constructor,
    /// rather than in OnStartup, so it's in place before anything else in startup runs
    /// (including the config-loading/Migrate() try/catch blocks below, which already handle
    /// their own known failure cases -- this is the catch-all behind those, not a
    /// replacement for them).
    ///
    /// DispatcherUnhandledException specifically is the one that matters most day to day:
    /// it's every exception that reaches back to the message loop from a UI-thread call,
    /// which is where the vast majority of this app's own code runs. Handled = true after
    /// showing the message keeps the app running rather than still crashing after the
    /// MessageBox closes -- a shown-but-recoverable error beats losing whatever the person
    /// was in the middle of, for the same reason the Migrate()/AnyAsync() failures below
    /// show a message and let the person decide what to do next rather than the OS just
    /// terminating the app on their behalf. AppDomain.CurrentDomain.UnhandledException
    /// covers a non-UI-thread exception that isn't caught anywhere -- the runtime is
    /// already tearing the process down by the time this fires (that's true regardless of
    /// whether anything subscribes here), so this can only show a last message on the way
    /// out, not prevent it. TaskScheduler.UnobservedTaskException covers a faulted Task
    /// from one of this app's many fire-and-forget `_ = SomeAsync()` calls that never gets
    /// observed/awaited by anything -- logged rather than shown as a MessageBox, since by
    /// the time the GC finalizer thread raises this the person has typically moved on from
    /// whatever triggered it, and a message box popping up from a finalizer thread with no
    /// clear connection to anything currently on screen would just be confusing.</summary>
    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "Something went wrong and Schedule Manager needs your attention.\n\n" +
                args.Exception.Message +
                "\n\nYou can keep working, but if this keeps happening, a restart is worth trying.",
                "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                MessageBox.Show(
                    "Schedule Manager hit an unrecoverable error and needs to close.\n\n" + ex.Message,
                    "Fatal error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ShutdownMode is left at its App.xaml default (OnLastWindowClose) -- MainWindow
        // is the only window this app ever creates and shows now that the sign-in gate
        // lives inside it as an overlay (see MainWindow's own doc comment) rather than as
        // a separate LoginWindow/SetupAdminWindow shown and closed before MainWindow
        // existed, so there's no window-count-hits-zero-mid-startup race to work around
        // here anymore.

        // First-run database setup gate. SharedConfigFile.ResolvePath() not existing is
        // what "this machine hasn't been set up yet" means (see that type's own doc
        // comment) -- checked here, before configuration is even loaded, so a database
        // always gets set up first, ahead of SetupAdminPanel's own first-account
        // username/password further down (the only username/password this required
        // first-run path ever collects -- see DatabaseSetupDialog's own doc comment for
        // why this gate never touches SQL Server or DatabaseProvisioningService at all,
        // unlike the two other, optional entry points to that same dialog). All this
        // gate itself does is collect a Server/Database name and save a
        // Windows-Authenticated connection string for it -- the database doesn't need
        // to exist yet; the fall-through Migrate() call just below creates it, the
        // moment normal startup resumes with that connection string in hand. Without
        // this gate, a machine where the local appsettings.json's bundled
        // Trusted_Connection default (see that file) happens to already work would
        // sail straight past any database prompt and only ever reach SetupAdminPanel;
        // a machine where it doesn't would only reach DatabaseSetupDialog reactively,
        // after a failed Migrate() -- and either way, the database wasn't necessarily
        // the first thing set up.
        //
        // Deliberately checked before the try/catch below, not inside it: this isn't a
        // config-loading failure, it's a decision to make before attempting to load
        // configuration at all.
        if (!File.Exists(SharedConfigFile.ResolvePath()))
        {
            // Best-effort prefill for Server/Database name only (see DatabaseSetupDialog's
            // own ctor doc comment) -- the local appsettings.json's own bundled connection
            // string, not a working connection on a genuinely fresh machine, just enough to
            // save re-typing ".\SQLEXPRESS" / "ScheduleAppDb" when this machine matches the
            // shipped default. optional: true and a swallowed exception either way: malformed
            // local JSON here isn't fatal -- the dialog just opens with blank fields instead,
            // and the real "can't load appsettings.json" failure still surfaces from the
            // try/catch below once this gate is past.
            string? prefillConnectionString = null;
            try
            {
                var localConfig = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true)
                    .Build();
                prefillConnectionString = localConfig.GetConnectionString("ScheduleDb");
            }
            catch
            {
                // Swallowed -- see comment above.
            }

            var provisioningService = new DatabaseProvisioningService();
            var setupDialog = new DatabaseSetupDialog(
                provisioningService, prefillConnectionString,
                mode: DatabaseSetupMode.WindowsAuthOnly, isRequiredFirstRun: true);

            if (setupDialog.ShowDialog() != true || setupDialog.ConnectionString is not { } newConnectionString)
            {
                // Cancel, the window's X button, or (defensively) Create succeeding
                // without ConnectionString somehow set -- no skipping either way (see
                // the two elicited answers this gate was built from). Nothing's been
                // created yet at this point in startup (no DbContext, no DI container),
                // so there's nothing to dispose beyond the dialog itself, already closed.
                Shutdown(-1);
                return;
            }

            try
            {
                // SharedConfigWriter, not appsettings.json -- same reasoning as the
                // reactive Migrate()-failure path further down, which this mirrors (see
                // its own comment on saving).
                new SharedConfigWriter().Save(connectionString: newConnectionString);
            }
            catch (Exception saveEx)
            {
                // Unlike the reactive path further down, nothing's actually been
                // created against SQL Server yet at this point -- this gate only ever
                // builds a connection string (see DatabaseSetupDialog's own doc
                // comment), it never runs CREATE DATABASE itself. Only saving that
                // string failed here (most likely UnauthorizedAccessException on
                // %ProgramData%\ScheduleApp). Nothing to fall back to either way --
                // this gate exists specifically because nothing was configured yet, so
                // unlike the reactive path this can't offer "restart to use it" and
                // just has to stop.
                MessageBox.Show(
                    "Could not save the new connection string to " + SharedConfigFile.DefaultPath +
                    ".\n\n" + saveEx.Message +
                    "\n\nSet ConnectionStrings:ScheduleDb there by hand, then restart.",
                    "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(-1);
                return;
            }

            // Falls through into normal startup below, which picks the file just written
            // up via its own AddJsonFile(..., optional: true) call right after this, and
            // whose own Migrate() call (further down) is what actually creates the
            // database -- see DatabaseSetupDialog's own doc comment for why that's by
            // design, not a gap. No restart needed here, unlike the reactive
            // Migrate()-failure path further down (which already has a DbContext built
            // against the old connection string by the time it gets there).
        }

        IConfigurationRoot configuration;
        string connectionString;
        AttendanceSettings attendanceSettings;
        PayrollSettings payrollSettings;
        PushListenerSettings pushListenerSettings;
        SignInSettings signInSettings;
        ConnectionProfilesSettings connectionProfilesSettings;
        try
        {
            configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                // Optional, machine-wide override -- see SharedConfigFile's own doc comment.
                // Added *after* the local file above so it wins if present; a machine that
                // hasn't set it up yet just keeps using ConnectionStrings:ScheduleDb from
                // appsettings.json, same as before this existed.
                .AddJsonFile(SharedConfigFile.ResolvePath(), optional: true, reloadOnChange: false)
                .Build();

            connectionString = configuration.GetConnectionString("ScheduleDb")
                ?? throw new InvalidOperationException(
                    "Missing 'ConnectionStrings:ScheduleDb' -- set it in appsettings.json, or in " +
                    $"{SharedConfigFile.DefaultPath} (see SharedConfigFile).");

            // Attendance defaults are optional -- a missing/incomplete "Attendance" section
            // just means the user starts with blank fields on the Attendance tab.
            attendanceSettings = configuration.GetSection("Attendance").Get<AttendanceSettings>()
                ?? new AttendanceSettings();

            // Same story for "Payroll" -- a missing/incomplete section just means
            // PayrollPolicy starts at its own built-in defaults (see PayrollSettings),
            // editable later from the Settings dialog once that section exists there
            // the way AttendancePolicy's already is.
            payrollSettings = configuration.GetSection("Payroll").Get<PayrollSettings>()
                ?? new PayrollSettings();

            // Same story for "PushListener" -- a missing section just means the tab starts
            // pointed at the http://localhost:8080 default (see PushListenerSettings), editable
            // in the tab itself either way.
            pushListenerSettings = configuration.GetSection("PushListener").Get<PushListenerSettings>()
                ?? new PushListenerSettings();

            // Same story as the sections above -- a missing "SignIn" section just
            // means SignInPanel/SetupAdminPanel keep showing the built-in logo.
            signInSettings = configuration.GetSection("SignIn").Get<SignInSettings>()
                ?? new SignInSettings();

            // Same story again -- a missing "ConnectionProfiles" section just means
            // SettingsDialog's Database tab starts with no saved profiles (it
            // synthesizes a single "Current" entry from connectionString above for
            // display -- see SettingsDialog's own constructor).
            connectionProfilesSettings = configuration.GetSection("ConnectionProfiles").Get<ConnectionProfilesSettings>()
                ?? new ConnectionProfilesSettings();
        }
        catch (Exception ex)
        {
            // Most likely cause here is malformed JSON in the shared config file (see
            // SharedConfigFile) -- either hand-edited badly, or (very unlikely, but not
            // impossible) read mid-write by another process. The local appsettings.json
            // failing to parse is possible too, but far less likely since it isn't hand-
            // edited in normal use the way the shared file now is via the Settings dialog.
            // Same "show it, don't crash silently" treatment as the Migrate() failure below
            // -- this one just has to happen before the DI container exists to build it.
            MessageBox.Show(
                "Could not load configuration.\n\n" + ex.Message +
                "\n\nCheck appsettings.json and " + SharedConfigFile.DefaultPath +
                " (if that's set up) for malformed JSON.",
                "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        var services = new ServiceCollection();
        services.AddDbContext<ScheduleDbContext>(options =>
            options.UseSqlServer(connectionString,
                sql => sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null)));
        services.AddScoped<IScheduleRepository, ScheduleRepository>();
        services.AddScoped<IAttendanceLogRepository, SqlAttendanceLogRepository>();
        services.AddScoped<IManualAttendanceLogRepository, SqlManualAttendanceLogRepository>();
        // Hand-edited per-day punch pairings (see DayPunchPairing). Scoped alongside the
        // two punch-log repositories above for the same reason they are -- it shares the
        // one ScheduleDbContext, and is read by AttendanceWorkflowService on every
        // attendance run and written by the Day Punch Pairing editor.
        services.AddScoped<IDayPunchPairingRepository, SqlDayPunchPairingRepository>();
        services.AddScoped<IUserAccountRepository, SqlUserAccountRepository>();
        services.AddScoped<IPayrollAdjustmentRepository, PayrollAdjustmentRepository>();
        services.AddScoped<IPayrollUndertimeWaiverRepository, PayrollUndertimeWaiverRepository>();
        services.AddScoped<IPayrollRunRepository, PayrollRunRepository>();
        services.AddScoped<IHolidayRepository, HolidayRepository>();
        services.AddScoped<IAttendanceRunner, ScheduleDbAttendanceRunner>();
        // Scoped, not Singleton -- depends on IAttendanceRunner/IPayrollAdjustmentRepository/
        // IPayrollUndertimeWaiverRepository above, all three themselves Scoped, so a
        // Singleton registration here would capture them past their own scope's lifetime
        // (the "captive dependency" problem). Extracted out of PayrollViewModel (build-order
        // step 4) so PayrollWizardViewModel's own Step 3 (Group C) can constructor-inject the
        // exact same computation PayrollViewModel already uses, rather than each keeping its
        // own copy -- see IPayrollComputationService's own doc comment.
        services.AddScoped<IPayrollComputationService, PayrollComputationService>();
        // Scoped (not a plain `new()` inside AttendanceViewModel, which is where it used
        // to live) so MainViewModel can share the exact same instance -- see
        // AttendanceDataVersion.ScheduleVersion's own doc comment for why that sharing is
        // what actually lets an Attendance Summary refresh notice a schedule edit made on
        // the Schedule page. Scoped rather than Singleton for the same reason every other
        // per-session-effectively-singleton ViewModel below is: this app only ever creates
        // one IServiceScope for its whole run (see the comment further down), so Scoped
        // already behaves like a session-lifetime Singleton here without risking a real
        // Singleton one day outliving a second scope this app doesn't have yet.
        services.AddScoped<AttendanceDataVersion>();
        // Same "one app-session scope behaves like a per-session Singleton" reasoning as
        // AttendanceDataVersion just above, and depends on that same registration (its own
        // constructor takes AttendanceDataVersion to read RosterVersion) -- see
        // ActiveRosterProvider's own doc comment. Shared by AttendanceViewModel
        // (ReportScopeViewModel), MainViewModel (ScheduleImportExportViewModel's own
        // PayslipScopeDialog hand-off), and PayrollViewModel (PayslipScopeViewModel via
        // PrintExport, PayrollWizardViewModel via Run, and Group's own two roster reads),
        // the same three-facade reach AttendanceDataVersion/AttendanceBusyState already
        // have.
        services.AddScoped<ActiveRosterProvider>();
        // Opens the Day Punch Pairing editor and persists the result. Scoped for the same
        // "one app-session scope behaves like a per-session Singleton" reasoning as the
        // ViewModels below, and because it depends on the Scoped repositories above. One
        // instance serves both entry points -- the Attendance Summary grid's row menu (via
        // ReportViewModel) and the Schedule calendar's tile menu (via
        // ScheduleAssignmentViewModel) -- so a save from either bumps the same
        // AttendanceDataVersion.PairingVersion the Summary tab watches.
        services.AddScoped<IDayPunchPairingEditorLauncher, DayPunchPairingEditorLauncher>();
        // Scoped, same "one app-session scope behaves like a per-session Singleton"
        // reasoning as AttendanceDataVersion just above -- and now genuinely shared by
        // every ViewModel that touches the database: MainViewModel, PayrollViewModel,
        // AND AttendanceViewModel (constructor-injected into all three, replacing what
        // used to be a `new AttendanceBusyState(...)` in each -- AttendanceViewModel was
        // the last holdout; see its own _busy field doc comment for why that was a bug,
        // not a deliberate isolation).
        //
        // This one instance is what actually serializes DB access across every page:
        // MainViewModel's RefreshScheduleForSelectedEmployeeAsync/
        // RefreshCalendarAttendanceStatusesAsync (the Schedule tab's calendar, driven off
        // SelectedEmployee), PayrollViewModel's own RequestRefresh/RefreshAsync (driven off
        // that same SelectedEmployee, since PayrollPage shares MainViewModel's tree -- see
        // PayrollPage's own doc comment), and AttendanceViewModel's Import/DeviceFetch/
        // Report/PunchRecords/ManualEntries/ManualEntryEditor. All of them read/write
        // through the one shared, app-lifetime-scoped ScheduleDbContext instance (see the
        // AddDbContext call above), and EF Core's DbContext isn't safe for two of those
        // operations to be in flight at once -- a second operation starting before the
        // first completes throws InvalidOperationException("A second operation was started
        // on this context instance before a previous operation completed").
        //
        // Each of these three used to carry its own separate AttendanceBusyState (or, for
        // Schedule/Payroll, briefly shared just between those two -- see below), which only
        // ever serialized a page's own calls against itself, not against another page's.
        // Schedule and Payroll were unified first, on the reasoning that they're the two
        // pages driven by the same SelectedEmployee change; AttendanceViewModel was left out
        // on the assumption that its own separate tree/selection (ReportScopeViewModel)
        // meant nothing there could overlap with Schedule/Payroll's SelectedEmployee-driven
        // calls. That reasoning missed that AttendancePage.OnNavigatedFromAsync is a no-op:
        // leaving the Attendance tab mid-Import/mid-Fetch/mid-Generate-Reports doesn't cancel
        // it, so that work keeps running in the background, under its own separate gate,
        // while the person switches to Schedule (or Payroll) and triggers a read or write
        // there -- two gates, each individually correct, guarding one DbContext neither knew
        // the other was also guarding. That's the intermittent "second operation" crash
        // reachable from the Schedule tab even though nothing on the Schedule tab itself was
        // racing against itself. One instance across all three pages closes it the same way
        // the Schedule/Payroll sharing closed the first half.
        services.AddScoped(sp => new AttendanceBusyState(
            sp.GetRequiredService<IStatusBarService>(), sp.GetRequiredService<AppShutdownSignal>().Token));
        services.AddSingleton(attendanceSettings);
        services.AddSingleton(payrollSettings);
        services.AddSingleton(pushListenerSettings);
        services.AddSingleton(signInSettings);
        services.AddSingleton(connectionProfilesSettings);
        // Lets the Settings dialog (see MainWindow's gear button) show the connection
        // string that's actually in effect right now, whether it came from
        // appsettings.json or from the shared file already overriding it.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IStatusBarService, StatusBarService>();
        services.AddSingleton<ViewStateStore>();
        services.AddSingleton<RememberedSignInStore>();
        services.AddSingleton<NavigationDrawerStateStore>();
        services.AddSingleton<AppShutdownSignal>();
        services.AddSingleton<SharedConfigWriter>();
        services.AddSingleton<DatabaseBackupService>();
        services.AddSingleton<DatabaseProvisioningService>();
        // Set once, by MainWindow.OnAuthSucceeded after SignInPanel/SetupAdminPanel
        // succeeds -- see CurrentUserContext's own doc comment for why Singleton rather
        // than Scoped.
        services.AddSingleton<CurrentUserContext>();

        // Scoped, not Transient, for the nav pages and their ViewModels.
        //
        // RootNavigationView.SetServiceProvider(...) (see MainWindow) makes
        // NavigationView resolve TargetPageType straight from this container on
        // *every* navigation -- Wpf.Ui's own page cache/NavigationCacheMode is
        // only consulted when no IServiceProvider is set, so it never even runs
        // here. That means the container's registration is the only thing
        // controlling whether switching tabs gets back the same page or a fresh
        // one. Transient handed back a brand-new SchedulePage/AttendancePage/
        // PushListenerPage (and therefore a brand-new ViewModel) on every single
        // switch, which is why leaving Schedule and coming back re-hit the
        // database, dropped whatever was in the punch-log search box, collapsed
        // the employee tree, reset the selected Attendance sub-tab, etc., even
        // though each page's OnNavigatedToAsync already had a "_loaded" guard
        // meant to prevent exactly that -- the guard was on an object that got
        // thrown away and rebuilt underneath it.
        //
        // Since App only ever creates one IServiceScope (see _scope below) for
        // the app's entire run, Scoped here behaves like a per-session Singleton:
        // the same ViewModels live for as long as the app is open, so a
        // tab switch just re-displays whatever was already loaded instead of
        // reloading it. Scoped rather than Singleton because these ViewModels
        // depend (via IScheduleRepository etc.) on the Scoped ScheduleDbContext
        // -- matching that lifetime avoids a Singleton quietly holding a scoped
        // dependency hostage if this app ever grows a second scope (e.g. a
        // second window). It also fixes a smaller side effect: PushListenerViewModel
        // is IDisposable, and the container was tracking every throwaway
        // Transient instance for disposal at app-exit instead of disposing each
        // one when its tab was left, keeping every previous instance (and its
        // running DispatcherTimer) alive in memory for the rest of the session.
        //
        // EmployeesPage takes MainViewModel too (same Scoped instance SchedulePage
        // gets) rather than a ViewModel of its own -- see EmployeesPage's own doc
        // comment for why. PayrollPage takes both MainViewModel (its tree, same
        // reasoning) and PayrollViewModel (its own payroll-specific state) -- see
        // PayrollPage's own doc comment.
        services.AddScoped<MainViewModel>();
        services.AddScoped<AttendanceViewModel>();
        // Hands MainViewModel the exact same ManualEntryEditorViewModel instance
        // AttendanceViewModel already built for itself (via `new` in its own
        // constructor -- unchanged by this registration), rather than constructing a
        // second one of its own. A second, container-constructed
        // ManualEntryEditorViewModel would have resolved a *different*
        // AttendanceBusyState.RunAsync/IsRunning cycle from the one Import/DeviceFetch/
        // PunchRecords/ManualEntries already share on AttendanceViewModel's instance --
        // two independent "is a manual entry save in flight" flags for what's supposed to
        // be one editor, each blind to the other. Reading AttendanceViewModel's
        // already-built instance back out here avoids that split. (AttendanceBusyState
        // itself is now the one shared-app-wide instance -- see its own registration's
        // doc comment above -- so this is no longer also what closes the Schedule-vs-
        // Attendance DbContext race; it's purely about not duplicating the editor's own
        // busy/IsRunning bookkeeping.)
        services.AddScoped(sp => sp.GetRequiredService<AttendanceViewModel>().ManualEntryEditor);
        services.AddScoped<PushListenerViewModel>();
        services.AddScoped<PayrollViewModel>();
        services.AddScoped<Views.SchedulePage>();
        services.AddScoped<Views.EmployeesPage>();
        // Three separate pages, not one AttendancePage -- see AttendanceSummaryPage's own
        // doc comment. All three still resolve the exact same AttendanceViewModel above
        // (Scoped, so all three share it for the app's one lifetime scope, same as every
        // other page here), so navigating between them keeps whatever was already loaded
        // instead of re-querying the database.
        services.AddScoped<Views.AttendanceSummaryPage>();
        services.AddScoped<Views.PunchRecordsPage>();
        services.AddScoped<Views.ManualEntriesPage>();
        services.AddScoped<Views.PushListenerPage>();
        services.AddScoped<Views.PayrollPage>();
        services.AddTransient<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();
        _scope = _serviceProvider.CreateScope();

        try
        {
            var db = _scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

            // EnableRetryOnFailure above only covers ordinary queries/SaveChanges automatically
            // -- Migrate() itself is one of a handful of EF Core APIs (GetPendingMigrations()
            // and EnsureCreated() are the others we touch, in PushListener/Program.cs) that do
            // NOT automatically go through the configured execution strategy
            // (dotnet/efcore#27450). Wrapping it explicitly in CreateExecutionStrategy() is what
            // actually makes the whole call retriable, so a SQL Server Express instance that's
            // still starting up (the likely cause of the intermittent "could not connect / apply
            // migrations" error below) gets a few chances to come online first, instead of this
            // failing on whatever the very first connection attempt happens to catch.
            //
            // ExecuteAsync + MigrateAsync, not the synchronous Execute/Migrate this used to
            // call -- Execute() runs synchronously on whatever thread calls it, and this method
            // hadn't hit its first await yet at this point, so it was still running on the WPF
            // UI thread. With up to 5 retries and a 10-second max delay each, a SQL Server
            // Express instance that's slow to come up (e.g. right after the machine boots) could
            // block that thread -- window frozen, "Not Responding" -- for minutes at a time
            // before either succeeding or reaching the catch block below. Same fix already
            // applied on the PushListener side (see its Program.cs, which awaits
            // GetPendingMigrationsAsync() through its own execution strategy for the same
            // reason) -- this just brings Migrate() here in line with that.
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(() => db.Database.MigrateAsync());
        }
        catch (Exception ex)
        {
            // Most likely cause on a genuinely fresh machine: SQL Server Express is
            // installed but nothing's been set up in it yet -- no ScheduleAppDb, no
            // login for whichever account this connection string names. Offering
            // DatabaseSetupDialog here (see its own doc comment) instead of only
            // showing the error means that first run doesn't require SQL Server
            // Management Studio, sqlcmd, or hand-typing the T-SQL from
            // ScheduleApp.PushListener's own README just to get past this screen. A
            // less likely cause -- SQL Server Express not running at all, or a
            // connection string pointed at the wrong server entirely -- just makes the
            // wizard's own Create button fail with the same underlying SqlException,
            // which it shows the same way BackupRestoreDialog/RunAsync already does.
            var runSetup = MessageBox.Show(
                "Could not connect to SQL Server / apply migrations.\n\n" + ex.Message +
                "\n\nCheck the connection string (in appsettings.json, or " +
                SharedConfigFile.DefaultPath + " if that's set up) and that SQL Server " +
                "Express is running.\n\n" +
                "Would you like to run Database Setup now, to create the database and a " +
                "SQL Server login?",
                "Startup error", MessageBoxButton.YesNo, MessageBoxImage.Error);

            if (runSetup == MessageBoxResult.Yes)
            {
                var provisioningService = _scope.ServiceProvider.GetRequiredService<DatabaseProvisioningService>();
                var setupDialog = new DatabaseSetupDialog(provisioningService, connectionString);

                if (setupDialog.ShowDialog() == true && setupDialog.ConnectionString is { } newConnectionString)
                {
                    try
                    {
                        // SharedConfigWriter, not appsettings.json -- the shared file
                        // is what's guaranteed writable by whichever account is
                        // running Schedule Manager right now (see SharedConfigFile's
                        // own remarks); appsettings.json sits next to the .exe, which
                        // a standard install often doesn't grant write access to.
                        var sharedConfigWriter = _scope.ServiceProvider.GetRequiredService<SharedConfigWriter>();
                        var savedPath = sharedConfigWriter.Save(connectionString: newConnectionString);
                        MessageBox.Show(
                            $"Database and login created. Saved the new connection string to {savedPath}.\n\n" +
                            "Restart Schedule Manager to use it.",
                            "Database setup complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception saveEx)
                    {
                        // The database/login themselves were created successfully at
                        // this point -- only saving the result failed (most likely
                        // UnauthorizedAccessException on %ProgramData%\ScheduleApp,
                        // same failure mode MainWindow.SettingsButton_Click's own Save
                        // already handles). Worth spelling out the fallback rather
                        // than losing a connection string that already works.
                        MessageBox.Show(
                            "The database and login were created, but saving the new connection " +
                            "string to " + SharedConfigFile.DefaultPath + " failed.\n\n" + saveEx.Message +
                            "\n\nSet ConnectionStrings:ScheduleDb there (or in appsettings.json) to:\n\n" +
                            newConnectionString + "\n\nby hand, then restart.",
                            "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }

            Shutdown(-1);
            return;
        }

        // Sign-in gate -- see MainWindow's own doc comment for how AuthOverlay/
        // SignInPanel/SetupAdminPanel replaced the old separate LoginWindow/
        // SetupAdminWindow split. OnStartup is async void (rather than the plain void
        // it was before this feature) specifically so awaits like this one are real
        // awaits, not a blocking .GetAwaiter().GetResult() -- that would run on this
        // thread's SynchronizationContext (already a live DispatcherSynchronizationContext
        // by this point in startup), and blocking it while EF Core's async chain tries to
        // resume on that same context is the classic WPF sync-over-async deadlock. Config
        // loading above stays synchronous since none of it awaits anything; Migrate() above
        // now genuinely awaits too (see its own comment for why that changed) -- this is no
        // longer the only await in the method, just the next one.
        var userAccountRepository = _scope.ServiceProvider.GetRequiredService<IUserAccountRepository>();

        bool hasAccounts;
        try
        {
            hasAccounts = await userAccountRepository.AnyAsync();
        }
        catch (Exception ex)
        {
            // Same "show it, don't crash silently" treatment as the Migrate() failure
            // above -- this is still effectively a database-connectivity check, just
            // one table later.
            MessageBox.Show(
                "Could not check for existing accounts.\n\n" + ex.Message,
                "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        // hasAccounts decides which of AuthOverlay's two panels MainWindow shows --
        // false (nothing in UserAccounts yet, true only on the very first run against a
        // freshly migrated database) means SetupAdminPanel; true means SignInPanel. See
        // ShowSignInOverlay's own doc comment for why that decision is made here rather
        // than inside MainWindow itself.
        var mainWindow = _scope.ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.ShowSignInOverlay(hasAccounts);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Cancel before disposing the scope below -- see AppShutdownSignal's own doc
        // comment for why the order matters: this gives an in-flight Load/Export/Generate
        // Reports/Import/Fetch a clean, cooperative way to unwind (an
        // OperationCanceledException every Core method already catches quietly) instead
        // of racing a ScheduleDbContext that's about to be torn out from under it.
        // GetService (not GetRequiredService) since a very early startup failure --
        // see the Migrate() catch block above -- still reaches this method, and
        // shouldn't throw a second exception on the way out over a signal nothing
        // had a chance to start using yet.
        _scope?.ServiceProvider.GetService<AppShutdownSignal>()?.Cancel();

        _scope?.Dispose();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}