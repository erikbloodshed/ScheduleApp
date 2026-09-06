using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Desktop.Services;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Single "is something running" flag shared by every command across every
/// Attendance child ViewModel -- mirrors what used to be AttendanceViewModel.IsRunning,
/// which gated every command in the class, not just the one currently executing.
/// Splitting the tab into child ViewModels can't split this too: Import, Fetch, Generate
/// Reports, Load/Export, and every manual-entry action still all need to disable each
/// other while any one of them is in flight, exactly as before the split. One instance is
/// constructed by AttendanceViewModel and injected into every child that has a command
/// gated by it; each of those subscribes to PropertyChanged(IsRunning) in its own
/// constructor and re-evaluates its own commands' CanExecute, the same way the original
/// single OnIsRunningChanged handler used to notify all of them at once.</summary>
public partial class AttendanceBusyState : ObservableObject
{
    private readonly IStatusBarService _statusBarService;
    private readonly CancellationToken _shutdownToken;
    private CancellationTokenSource? _cts;

    /// <summary>Flows through the async call chain of whichever action is currently
    /// running under RunAsync -- true for any code reached (directly or via further
    /// awaits) from inside that action, false for anything starting fresh from a UI event
    /// handler, a PropertyChanged subscription, or any other call chain that doesn't trace
    /// back through that action. This is what actually distinguishes "genuinely nested on
    /// this call chain" (safe to ride along on the outer Token, since the outer call is
    /// still on the stack above it) from "IsRunning just happens to be true because some
    /// other, unrelated caller hasn't finished yet" (not safe to run unserialized -- see
    /// _gate below) -- IsRunning alone can't tell those apart, which was this class's own
    /// bug (see RunAsync's doc comment). Deliberately an instance field, not static: this
    /// class is normally constructed once and shared (see this class's own summary above),
    /// but nothing about AsyncLocal itself requires that, and scoping it to the instance is
    /// what makes that true rather than incidental.</summary>
    private readonly AsyncLocal<bool> _isNestedCall = new();

    /// <summary>The actual mutual-exclusion primitive as of the RunAsync hardening below --
    /// IsRunning is now purely the UI-facing "something's happening" flag (bindings, the
    /// Cancel button's Visibility, etc.); this is what serializes access to the shared,
    /// app-lifetime-scoped ScheduleDbContext across every non-nested call, by making a
    /// second, unrelated caller actually wait its turn instead of reading IsRunning,
    /// concluding "must be a nested call," and running unserialized against whatever the
    /// first caller is still doing -- see RunAsync's own doc comment for the full history
    /// of why that used to be the wrong assumption.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>shutdownToken defaults to CancellationToken.None (i.e. "never gets
    /// cancelled by anything external") so this class stays constructible without a real
    /// AppShutdownSignal on hand -- see AttendanceViewModel's constructor for the one
    /// place that actually passes a real one, from AppShutdownSignal (Phase 5 of the
    /// cancellation rollout). statusBarService has no such default -- unlike
    /// shutdownToken, there's no safe "do nothing" value to fall back to for a required
    /// collaborator (IStatusBarService.ShowProgress/ClearProgress, called from
    /// OnIsVisiblyRunningChanged below), and the one real construction site
    /// (App.xaml.cs's DI registration) always has a real IStatusBarService singleton on
    /// hand to pass anyway.</summary>
    public AttendanceBusyState(IStatusBarService statusBarService, CancellationToken shutdownToken = default)
    {
        _statusBarService = statusBarService;
        _shutdownToken = shutdownToken;
    }

    [ObservableProperty]
    private bool isRunning;

    /// <summary>True only while an operation the person actually asked for -- a Period
    /// edit, a report-scope tree check/uncheck, or some other explicit action -- is in
    /// flight, as opposed to a tab's own silent auto-load-on-select. IsRunning above
    /// still flips for *both* cases, since it's what actually serializes access to the
    /// shared, app-lifetime-scoped ScheduleDbContext across every command in every child
    /// (see this class's own doc comment); that guard has to cover silent auto-loads too,
    /// or two of them (or one auto-load and one explicit action) could still race the
    /// same DbContext. A *visible* busy indicator is a different concern: this property
    /// drives the status bar's own left-aligned progress indicator (see
    /// OnIsVisiblyRunningChanged below, and IStatusBarService.ShowProgress's
    /// indeterminate overload) so it only appears for work the person is actually
    /// waiting on -- see ReportViewModel.TryAutoRun, which is what starts that work now
    /// that there's no explicit "Generate Reports" button to click. Summary/Punch
    /// Records/Manual Entries each also silently re-run their own load every time their
    /// tab is (re)selected (see each child's OnIsXTabSelectedChanged), which is normally
    /// over before the person can even react -- wiring the indicator to plain IsRunning
    /// meant that near-instant silent refresh still flipped it, so simply switching tabs
    /// made a spinner flicker on and off for no reason the person did anything.
    ///
    /// Previously drove a separate inline progress bar duplicated in each of
    /// AttendanceView.xaml (twice -- the page-level toolbar and, again, the Summary
    /// tab's own Period row) and PayrollPage.xaml, each showing the exact same
    /// information about the exact same shared flag in three different places at once.
    /// Now shown once, on the status bar, alongside PayrollGroupViewModel's own
    /// determinate IsPayrollGroupLoading/PayrollGroupLoadPercent indicator (see that
    /// property's own doc comment) -- the two never overlap in practice, since every
    /// PayrollGroupViewModel compute deliberately runs with visibly: false specifically
    /// so it doesn't also flip this property (see RefreshPayrollGroupRowsAsync's own doc
    /// comment), so ShowProgress is never asked to show both at once.
    ///
    /// [NotifyCanExecuteChangedFor(nameof(CancelCommand))] is required here, not optional
    /// -- CommunityToolkit.Mvvm does NOT automatically call CancelCommand.
    /// NotifyCanExecuteChanged() just because CancelCommand's [RelayCommand(CanExecute =
    /// nameof(IsVisiblyRunning))] attribute references this property; that inference has
    /// to be declared explicitly, on this side, or the generated setter never tells
    /// CancelCommand anything changed. Without it, Visibility below still updates fine
    /// (it's a plain property binding, unrelated to ICommand) so the button correctly
    /// appears -- but WPF never re-queries CanExecute, so it stays stuck disabled at
    /// whatever it evaluated to when the button was first bound (false, since nothing was
    /// running yet). That's the "button shows up but isn't clickable" bug this fixes.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isVisiblyRunning;

    /// <summary>Drives the status bar's left-aligned progress indicator off this
    /// property directly, rather than each of the three pages that share this one
    /// instance (Attendance, Schedule, Payroll) separately subscribing to
    /// PropertyChanged and doing the same ShowProgress/ClearProgress call themselves --
    /// there's exactly one shared flag, so there only needs to be one place reacting to
    /// it. "Working…" is the same generic label StatusBarNotificationExtensions.ShowInfo
    /// already defaults to for background activity elsewhere in this app; a specific
    /// per-operation label (e.g. "Importing punches…") isn't available here the way
    /// PayrollGroupViewModel's own "Computing payroll group…" is -- this flag alone
    /// doesn't know which of Import/Fetch/Generate Reports/Load/Save/etc. actually set
    /// it, only that one of them did. Always paired: true shows, false clears -- see
    /// RunAsync's own doc comment for why every true this sets is guaranteed to
    /// eventually reach a matching false in the same method's finally, so there's no
    /// path that leaves the status bar stuck reading "Working…" forever.</summary>
    partial void OnIsVisiblyRunningChanged(bool value)
    {
        if (value)
            _statusBarService.ShowProgress("Working…");
        else
            _statusBarService.ClearProgress();
    }

    /// <summary>The token for whichever operation IsRunning currently represents.
    /// default(CancellationToken) -- already non-cancellable, never throws if awaited or
    /// checked -- whenever nothing is running, so a Core method can pass this through
    /// unconditionally rather than null-checking it first. Each Core method should read
    /// this exactly once, right after setting IsRunning = true, and pass that same value
    /// down through every repository/service call for the rest of its own run: reading
    /// Token again partway through would silently pick up a *different* operation's source
    /// if something else had already started by then, which can't happen today (IsRunning
    /// itself prevents that) but would be a landmine to leave for later.</summary>
    public CancellationToken Token => _cts?.Token ?? default;

    /// <summary>Requests cancellation of whichever operation is currently running, if
    /// any -- a no-op if IsRunning is currently false. Every Core method is expected to
    /// observe this by passing Token through to its own repository/service calls (EF Core
    /// throws OperationCanceledException on the next await once this fires) and to catch
    /// that exception itself rather than let it surface as an error; this method only
    /// requests the cancellation, it doesn't wait for or confirm it.
    ///
    /// [RelayCommand] here (rather than a separately-named wrapper method) is what backs
    /// AttendanceView.xaml's global Cancel button (see the toolbar row above the
    /// TabControl) -- CancelCommand is forwarded from AttendanceViewModel the same way
    /// every other child command is, so the button doesn't care which tab or dialog
    /// actually started whatever's running. CanExecute is tied to IsVisiblyRunning, not
    /// IsRunning, for the same reason the button's own Visibility is (see
    /// IsVisiblyRunning's doc comment): a tab's silent auto-load-on-select sets IsRunning
    /// but was never something the person asked to wait on, so it shouldn't make a Cancel
    /// button appear enabled either. See IsVisiblyRunning's [NotifyCanExecuteChangedFor]
    /// above for what actually keeps this in sync -- it isn't automatic.</summary>
    [RelayCommand(CanExecute = nameof(IsVisiblyRunning))]
    public void Cancel() => _cts?.Cancel();

    /// <summary>Runs <paramref name="action"/> wrapped in the exact IsRunning/
    /// IsVisiblyRunning/Token/try-catch-finally shape every Attendance command needs
    /// around the shared, app-lifetime-scoped ScheduleDbContext (see this class's own
    /// doc comment) -- centralized here after that shape had been hand-copied into
    /// PunchRecordsViewModel, ManualEntriesViewModel, ManualEntryEditorViewModel,
    /// ReportViewModel, DeviceFetchViewModel, and AttendanceImportViewModel. Three of
    /// the bugs this class's own history already turned up (DeleteManualEntryAsync
    /// missing the wrap entirely; the Punch Records autosuggest race; the reentrancy
    /// this method's own top branch now guards against) were exactly this shape being
    /// copied incompletely, or two copies of it running inside one another --
    /// collapsing it to one implementation means there's only one place left for that
    /// class of bug to happen.
    ///
    /// Reentrant: ManualEntryEditorViewModel.AddOrEditManualEntryAsync's own RunAsync
    /// action ends by calling ManualEntriesViewModel.RefreshIfLoadedAsync, which (if
    /// the grid is already showing something) calls LoadManualEntriesCoreAsync, which
    /// calls RunAsync again -- a second, genuinely nested call on this same instance,
    /// on the same async call chain, with the first one still on the stack above it.
    /// The ORIGINAL fix for this (a plain `if (IsRunning)` check) unconditionally reset
    /// IsRunning/IsVisiblyRunning to false and (via OnIsRunningChanged) disposed the
    /// shared CancellationTokenSource the moment the *inner* call finished, regardless
    /// of whether an *outer* call was still mid-flight -- neither call had any way to
    /// know the other existed. That's what actually produced the hang-then-crash on Add
    /// Manual Entry (see AddOrEditManualEntryAsync's own doc comment for how it got
    /// there): the busy state and its CancellationTokenSource got torn down out from
    /// under a still-running operation, then any command re-enabled by that premature
    /// IsRunning=false could start a second, genuinely concurrent EF Core operation
    /// against the shared DbContext, which EF Core doesn't support and throws on.
    ///
    /// That first fix's own `if (IsRunning)` guard turned out to be too broad: it can't
    /// tell "genuinely nested, same call chain, outer call still on the stack above this
    /// one" (safe to just run the action against the outer Token) apart from "IsRunning
    /// happens to be true because some completely unrelated caller hasn't finished yet"
    /// (NOT safe -- there's no outer call here to own anything). The Schedule tab's own
    /// busy-state race is exactly the second case: switching employees while a schedule
    /// write is saving lets a deferred ScheduleCalendarViewModel refresh (queued behind
    /// _busy.IsRunning via its own _scheduleRefreshPending flag) and that write's own
    /// explicit follow-up refresh both reach here moments apart, neither one nested
    /// inside the other -- see ScheduleAssignmentViewModel.SetScheduleForSelectionAsync's
    /// own guard comment for the full sequence. A plain `if (IsRunning)` check treated
    /// the second of those two arrivals as "nested" and ran it unserialized right
    /// alongside the first -- the same "two operations against one DbContext" crash as
    /// the Manual Entry bug above, just reached a different way.
    ///
    /// _isNestedCall (an AsyncLocal -- see its own doc comment) is what actually tells
    /// the two cases apart now, since it flows with the async call chain itself rather
    /// than reading one shared flag: true only for code reached from inside *this* call's
    /// own action (including through further awaits inside it), false for any other call
    /// chain no matter what IsRunning currently reads. A genuinely nested call still just
    /// rides along on the outer call's Token and never touches
    /// IsRunning/IsVisiblyRunning/_cts/_gate, exactly as before. Anything else -- even
    /// while IsRunning happens to be true -- now awaits _gate (see its own doc comment)
    /// instead of assuming it's safe to proceed, so an unrelated second caller actually
    /// waits its turn and becomes its own new outermost call once the first one releases
    /// the gate, rather than racing it. Waiting on _shutdownToken specifically (not just
    /// leaving the wait uncancellable) means a call still queued behind another one when
    /// the app shuts down simply never runs, rather than waking up to start work against
    /// a context that's already going away.
    ///
    /// visibly sets IsVisiblyRunning immediately, before action runs -- the common case
    /// (an explicit button click, or a showFeedback flag threaded through from one). An
    /// action that only wants the busy indicator to appear partway through its own work
    /// (e.g. AddManualEntryAsync, which shouldn't look busy while a confirmation dialog
    /// is just sitting open waiting on the person) can still pass false here and set
    /// IsVisiblyRunning = true itself once it's ready, the same as every call site did
    /// before this helper existed.
    ///
    /// OperationCanceledException is always swallowed silently (a Cancel click or app
    /// shutdown is never a failure). Any other exception goes to onError instead of a
    /// hardcoded status bar call, since a couple of callers need to do more than just
    /// report it -- see ReportViewModel.RunCoreAsync's periodChanged handling, which
    /// only clears a previous result if the run that failed was for a different period.
    /// Passing null for onError means a failure is silently swallowed, same as an
    /// omitted catch block would be.
    ///
    /// IsRunning/IsVisiblyRunning are always reset to false in finally, regardless of
    /// how action exits -- including a plain early `return` inside it, same as before --
    /// but only for the outermost call; see the reentrancy paragraphs above.
    ///
    /// That close second read turned up a real gap: _isNestedCall.Value was set true
    /// right before `await action(cancellationToken)` but never explicitly cleared
    /// afterward, so -- per AsyncLocal's own ambient-flow semantics -- it stayed true for
    /// the rest of this call's own execution, including the outer finally below, where
    /// IsRunning = false synchronously raises PropertyChanged before this method itself
    /// ever returns to whoever called it. Every "arrived while busy, so defer and replay
    /// once idle" call site (PayrollGroupViewModel.RequestPayrollGroupRefresh/
    /// RequestFullRosterRefresh, PayrollSummaryViewModel.RequestRefresh,
    /// ScheduleCalendarViewModel's own _scheduleRefreshPending handling) fires its own
    /// deferred, unawaited (`_ = RefreshXAsync()`) follow-up from exactly that
    /// PropertyChanged handler -- which meant that follow-up's own RunAsync call
    /// inherited _isNestedCall.Value == true from the still-unwinding outer call and took
    /// the "genuinely nested, ride along, skip _gate" branch above, even though it isn't
    /// nested at all: the outer action has already finished: this is a brand-new
    /// operation that needs its own turn through _gate like any other unrelated caller.
    /// Loading a payroll group is the easiest way to see it, since
    /// OnBatchScopeEmployeesChanged queues BOTH RequestPayrollGroupRefresh and
    /// RequestFullRosterRefresh off the one BatchScopeEmployees assignment -- both
    /// deferred calls fire from the same PropertyChanged handler, both wrongly believe
    /// they're nested, both skip _gate, and both then run for real, concurrently,
    /// against the shared DbContext. Fixed by clearing _isNestedCall.Value back to false
    /// as the first thing in the finally below, right after action has actually finished
    /// (success, cancellation, or error) but before IsRunning flips back to false and
    /// anything reacts to that -- so the "nested" window is scoped to action's own
    /// execution and nothing that merely happens to run afterward, on the same call.
    /// Still not build-verified (same caveat as before); this specific fix is worth
    /// confirming against a real concurrent repro once a build is available.
    ///
    /// A real build eventually did surface a further gap in this same area, caught via
    /// PayrollPage.RestorePayrollGroupSelection racing PayrollSummaryViewModel.RequestRefresh
    /// against the shared ScheduleDbContext (a Dispatcher.BeginInvoke call made synchronously
    /// from inside action capturing _isNestedCall.Value == true as part of its own
    /// ExecutionContext, then wrongly believing it's still nested once that queued callback
    /// actually runs, well after the window that set that flag has already closed). That was
    /// fixed by suppressing ExecutionContext flow around every call to action in RunAction
    /// below -- and then REVERTED, because that suppression turned out to also strip
    /// _isNestedCall's flowed value from action's OWN continuation the instant action hit its
    /// own first internal await, silently un-nesting any RunAsync call reached later in that
    /// same action (e.g. AddOrEditManualEntryAsync's own action awaits
    /// _employeeDirectory.GetAllAsync first, then later calls
    /// ManualEntriesViewModel.RefreshIfLoadedAsync -- genuinely nested, but no longer looked
    /// it once suppression was in place) -- which then wrongly took THIS branch, tried to
    /// acquire _gate, and deadlocked forever against the true outer call still awaiting it.
    /// See RunAction's own doc comment for the confirmed mechanism and why a guaranteed,
    /// permanent, every-page deadlock reachable via an ordinary Delete-then-Add-manual-entry
    /// sequence is worse than the narrower Payroll race the suppression was fixing -- that
    /// race is back until it's fixed some other way, scoped to that specific call site rather
    /// than here.</summary>
    public async Task RunAsync(bool visibly, Func<CancellationToken, Task> action, Action<Exception>? onError = null)
    {
        if (_isNestedCall.Value)
        {
            // Genuinely nested on this call chain -- see this method's own doc comment.
            // Ride along on the outer call's Token; don't touch
            // IsRunning/IsVisiblyRunning/_cts/_gate, since the outer call is the only one
            // that gets to decide when those actually go back to idle.
            try
            {
                await RunAction(action, Token);
            }
            catch (OperationCanceledException)
            {
                // The person clicked Cancel (or the app is shutting down) -- not a failure.
            }
            catch (Exception ex)
            {
                // Full exception (message + stack trace + any inner exception) to the Debug
                // Output window -- visible on every debug run with zero extra Visual Studio
                // configuration, unlike breaking on the throw itself (which needs Debug >
                // Windows > Exception Settings > Common Language Runtime Exceptions checked,
                // and even then only helps if the exception type isn't already being caught
                // somewhere before that point -- which, for anything reaching here, it just
                // was, by design). "nested" tags which of RunAsync's two branches this is --
                // see this method's own doc comment for why that distinction matters: a
                // crash reported here means action itself threw while genuinely riding along
                // on an outer call's Token, not a fresh, gate-acquired call racing another.
                Debug.WriteLine($"[AttendanceBusyState.RunAsync, nested] {ex}");
                onError?.Invoke(ex);
            }

            return;
        }

        // Not nested on this call chain -- even if IsRunning is already true because
        // some other, unrelated caller hasn't finished yet, wait for it rather than
        // running unserialized alongside it. See this method's own doc comment for the
        // race this closes.
        try
        {
            await _gate.WaitAsync(_shutdownToken);
        }
        catch (OperationCanceledException)
        {
            // App shutting down while this call was still queued behind another one --
            // it never got a turn, so there's nothing to run or clean up.
            return;
        }

        try
        {
            IsRunning = true;
            IsVisiblyRunning = visibly;
            var cancellationToken = Token;
            _isNestedCall.Value = true;

            try
            {
                await RunAction(action, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The person clicked Cancel (or the app is shutting down) -- not a failure.
            }
            catch (Exception ex)
            {
                // See the nested branch's own catch above for why this goes to Debug
                // Output rather than relying on breaking at the throw. "gate-acquired" here
                // (as opposed to that branch's "nested") means this really was a fresh,
                // unrelated call that wasn't riding along on anyone else's Token -- so a
                // "second operation" InvalidOperationException landing here specifically
                // means TWO fresh calls both made it past _gate somehow, which would point
                // at a bug in the gate/reentrancy logic itself rather than in whichever
                // action was running.
                Debug.WriteLine($"[AttendanceBusyState.RunAsync, gate-acquired] {ex}");
                onError?.Invoke(ex);
            }
            finally
            {
                // Close the "nested" window here, before IsRunning flips back to false
                // below -- action has now genuinely finished (however it exited), so
                // anything that runs as a *result* of IsRunning going false (the
                // PropertyChanged cascade just below, and in particular every "arrived
                // while busy, so defer and replay once idle" call site that fires its own
                // unawaited follow-up from that handler) is a brand-new operation, not a
                // continuation of this one, and needs to queue on _gate like any other
                // unrelated caller rather than inheriting this flag for free. See this
                // method's own doc comment for the concrete Payroll Group race this closes.
                _isNestedCall.Value = false;
                IsRunning = false;
                IsVisiblyRunning = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Task RunAction(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        action(cancellationToken);

    /// <summary>Creates a fresh CancellationTokenSource exactly when IsRunning transitions
    /// to true, and disposes it (without creating a new one) when IsRunning transitions
    /// back to false -- CommunityToolkit.Mvvm's [ObservableProperty] only invokes this on
    /// an actual value change, so redundant same-value assignments (there aren't any
    /// today, but nothing enforces that) can't leak or recreate a source mid-operation.
    /// The previous source (if any) is always safe to dispose here regardless of which
    /// branch runs: by the time IsRunning can change again, whichever Core method was
    /// using it has already returned from its own try/finally (that's the one thing
    /// IsRunning is for), so nothing still holds a reference expecting it to stay alive.
    ///
    /// Linked to _shutdownToken (CreateLinkedTokenSource, not a bare new source) so app
    /// shutdown (Phase 5 -- see AppShutdownSignal) cancels whatever's currently running
    /// the same way an explicit Cancel click (Phase 4) does, without this class needing
    /// to know or care which of the two actually fired.</summary>
    partial void OnIsRunningChanged(bool value)
    {
        _cts?.Dispose();
        _cts = value ? CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken) : null;
    }
}

/// <summary>Marks the moment AttendancePage.OnNavigatedToAsync's initial load sequence
/// (ReportScopeViewModel.LoadEmployeeTreeCommand, then AttendanceViewModel.
/// ActivateInitialTabAsync) has actually finished, so the Summary/Punch Records/Manual
/// Entries tabs' own auto-load-on-select handlers don't race it. See
/// AttendanceViewModel.ActivateInitialTabAsync's doc comment for the full story --
/// TabControl selects its first TabItem the moment its visual tree is built, which used
/// to fire an auto-load *before* the employee tree had even started loading, and two
/// concurrent reads on the same (single, app-lifetime-scoped) ScheduleDbContext threw.
/// Deliberately one shared instance (not a bool per child ViewModel) so every tab's guard
/// flips open at exactly the same moment, the same guarantee the original single
/// _readyForTabAutoLoad field gave when all three handlers lived in one class.</summary>
public sealed class AttendanceTabActivationGate
{
    public bool IsReady { get; set; }
}

/// <summary>Four independent, in-memory-only counters marking "something in
/// AttendanceLogs changed" (DeviceLogsVersion), "something in ManualAttendanceLogs
/// changed" (ManualLogsVersion), "something in the schedule (ScheduleEntries)
/// changed" (ScheduleVersion), or "something in the employee/department roster
/// changed" (RosterVersion) at some point during this run. Bumped by whichever
/// ViewModel just wrote to that table -- AttendanceImportViewModel/
/// DeviceFetchViewModel bump DeviceLogsVersion after a successful import/fetch;
/// ManualEntryEditorViewModel bumps ManualLogsVersion after a successful Add/Edit/
/// Delete; MainViewModel bumps ScheduleVersion after a successful Set/Edit/Clear
/// Schedule, Set Leave, Import Schedule, or an employee delete (which also deletes that
/// employee's schedule entries -- see MainViewModel.DeleteEmployeeAsync); and
/// EmployeeTreeViewModel/ScheduleImportExportViewModel bump RosterVersion after a
/// successful Add/Delete Department, Add/Edit/Delete/Blacklist/Unblacklist Employee, or
/// Import Employees -- see RosterVersion's own doc comment just below for the one place
/// that reads it. The first three are read by ReportViewModel/PunchRecordsViewModel/
/// ManualEntriesViewModel to decide whether their own auto-reload-on-tab-select
/// actually needs to re-query the database, or whether nothing relevant has changed
/// since their last successful load and the grid (and its scroll position/selection)
/// can just be left alone. See each of those three's own ShouldAutoReload for the full
/// staleness check, which also folds in each tab's own query parameters (period, date
/// range, report-scope selection) -- these counters only cover "did the underlying data
/// move", not "did what the person's asking to see change".
///
/// Deliberately four separate counters, not one: PunchRecordsViewModel only ever shows
/// device punches (see its own doc comment), so a manual-entry edit or a schedule change
/// shouldn't force it to re-query; ManualEntriesViewModel is similarly indifferent to
/// DeviceLogsVersion/ScheduleVersion. ReportViewModel is the one reader that cares about
/// DeviceLogsVersion/ManualLogsVersion/ScheduleVersion together, since Generate Reports
/// compares the schedule against both punch tables (see AttendanceWorkflowService) -- a
/// schedule edit changes the comparison's expected side even when neither punch table
/// moved at all. RosterVersion has no reader in common with any of the other three --
/// see its own doc comment for why it's checked from a different place entirely.
///
/// ScheduleVersion and RosterVersion are instance-shared with MainViewModel (the
/// Schedule/Employees page), unlike DeviceLogsVersion/ManualLogsVersion, which are only
/// ever bumped from within the Attendance page's own child ViewModels -- see
/// App.xaml.cs's registration of this class and AttendanceViewModel's/MainViewModel's
/// constructors for how the one instance reaches both pages.
///
/// Also instance-shared with PayrollViewModel (via PayrollGroupViewModel), the same
/// three-facade reach ActiveRosterProvider's own doc comment already describes for
/// RosterVersion -- PayrollGroupViewModel.RecheckOnPageRevisit reads ScheduleVersion
/// indirectly, through AnyScheduleChangeSince/_lastScheduleChangeVersionByPin below, so a
/// schedule edit made on the Schedule page is reflected in an already-loaded payroll
/// group the next time the Payroll page is revisited, without paying for a recompute of
/// employees the edit didn't actually touch -- see BumpScheduleForEmployees/
/// AnyScheduleChangeSince's own doc comments for that finer-grained tracking.
///
/// Not persisted -- these only need to distinguish "nothing changed" from "something
/// changed" within one running session; a restart legitimately starts every tab from
/// "never loaded" regardless, at which point every reader's own null-snapshot check
/// already forces a real load with no need to consult these at all.
///
/// Deliberately plain auto-incrementing properties on a plain class, not
/// ObservableObject/[ObservableProperty] -- nothing binds to these in XAML or needs to
/// react the instant one changes; each reader only ever polls the current value at the
/// one moment it's deciding whether to reload.</summary>
public sealed class AttendanceDataVersion
{
    public int DeviceLogsVersion { get; private set; }
    public int ManualLogsVersion { get; private set; }
    public int ScheduleVersion { get; private set; }

    /// <summary>Bumped whenever a hand-edited punch pairing is saved or reset (see
    /// DayPunchPairingEditorLauncher, the only writer) -- a DayPunchPairing changes
    /// which punches form which work interval on a Flexible day, so it moves that
    /// day's Worked/Remain/Overtime figures and can flip its Complete/Partial
    /// status. Read by ReportViewModel alongside DeviceLogsVersion/
    /// ManualLogsVersion/ScheduleVersion for exactly the same reason those three
    /// are: the Attendance Summary tab has already-computed results on screen that
    /// a pairing edit makes stale. Coarse "did ANYONE's pairing move" like
    /// DeviceLogsVersion, not per-employee like _lastScheduleChangeVersionByPin --
    /// the one reader recomputes the whole period either way. Not read on the
    /// payroll side: a pairing change reaches payroll through the attendance run it
    /// already re-does, not as a separate input.</summary>
    public int PairingVersion { get; private set; }

    /// <summary>Bumped whenever the company-wide Holidays table changes -- by
    /// ScheduleAssignmentViewModel's Mark/Remove Holiday command (the calendar right-click)
    /// and by ManageHolidaysDialog's Add/Edit/Delete. Read by PayrollGroupViewModel and
    /// PayrollSummaryViewModel's own RecheckOnPageRevisitAsync, and by
    /// PayrollComputationService (its _lastAttendanceRun cache snapshots this alongside the
    /// holiday dates it fetched) -- a holiday change moves every employee's Holiday Pay for
    /// any period it falls in, so unlike ScheduleVersion there's no per-employee dimension
    /// to track: every reader treats a change here as "recompute unconditionally," the same
    /// coarse "did ANYONE's data move" shape the plain BumpSchedule()/DeviceLogsVersion
    /// already have. Not read by ReportViewModel -- a holiday changes payroll, not the
    /// schedule-vs-punches comparison the Attendance Summary tab shows.</summary>
    public int HolidayVersion { get; private set; }

    /// <summary>Unlike the other three counters, this one has exactly one reader in the
    /// whole app: ActiveRosterProvider, which compares this against the version it last
    /// fetched under to decide whether its own cached Active-only department/employee
    /// snapshot (see that class's own doc comment) is still good, or needs a fresh
    /// GetActiveDepartmentsWithEmployeesAsync/GetActiveUnassignedEmployeesAsync round
    /// trip. Nothing here is ReportViewModel-style "should this tab redraw" logic --
    /// ReportScopeViewModel/PayslipScopeViewModel/PayrollWizardViewModel/
    /// PayrollRunViewModel/PayrollGroupViewModel all still reload their own tree/roster
    /// on every tab-open or dialog-open exactly as before; RosterVersion only changes
    /// whether that reload actually costs a database round trip or is served from
    /// ActiveRosterProvider's cache.
    ///
    /// Bumped after every write that could change what GetActiveDepartmentsWithEmployeesAsync/
    /// GetActiveUnassignedEmployeesAsync would return: EmployeeTreeViewModel's
    /// AddDepartmentAsync, DeleteDepartmentAsync, AddEmployeeAsync, EditEmployeeAsync,
    /// DeleteEmployeeAsync, BlacklistEmployeeAsync, and UnblacklistEmployeeAsync, plus
    /// ScheduleImportExportViewModel.ImportEmployeesAsync. DeleteEmployeeAsync bumps
    /// both this and ScheduleVersion -- deleting an employee changes both the roster and
    /// (by cascade) the schedule. ImportScheduleAsync deliberately does NOT bump this --
    /// it only writes ScheduleEntries, never touches Employee/Department -- the same
    /// distinction ScheduleVersion's own doc comment already draws for ImportEmployeesAsync
    /// in the other direction.</summary>
    public int RosterVersion { get; private set; }

    /// <summary>Which employee Pins BumpScheduleForEmployees below has actually been told
    /// were touched, and the ScheduleVersion each was touched at -- lets a reader that only
    /// cares about a handful of specific employees (PayrollGroupViewModel, whose own
    /// currently-loaded payroll group is almost always a small slice of the whole roster)
    /// ask "did MY employees' schedule change" instead of settling for ReportViewModel's
    /// own coarser "did ANYONE's" reading of the bare ScheduleVersion counter above. Keyed
    /// by Pin -- every reader that would ever consult this is on the payroll side, which
    /// already keys everything else off Pin (see PayrollAdjustment.EmployeeId's own doc
    /// comment). ScheduleAssignmentViewModel's own writes are keyed by Pin too now (see
    /// ScheduleEntry.EmployeeId's own doc comment on the Schedule/Payroll re-keying), so
    /// BumpScheduleForEmployees below no longer has any Id-to-Pin translation to do -- it
    /// just records the same Pin its caller already wrote with.
    ///
    /// Not every ScheduleVersion bump adds an entry here -- see BumpScheduleForEmployees's
    /// own doc comment for exactly which callers populate this and which still call the
    /// plain BumpSchedule() above instead.</summary>
    private readonly Dictionary<int, int> _lastScheduleChangeVersionByPin = [];

    public void BumpDeviceLogs() => DeviceLogsVersion++;
    public void BumpManualLogs() => ManualLogsVersion++;
    public void BumpPairings() => PairingVersion++;
    public void BumpSchedule() => ScheduleVersion++;
    public void BumpRoster() => RosterVersion++;
    public void BumpHoliday() => HolidayVersion++;

    /// <summary>Same ScheduleVersion bump as the plain BumpSchedule() above -- every existing
    /// reader (ReportViewModel's ShouldAutoReload chief among them) keeps working exactly as
    /// before, unaware this overload was even called instead -- plus an entry in
    /// _lastScheduleChangeVersionByPin for each of <paramref name="employeePins"/>, letting
    /// AnyScheduleChangeSince below answer for just these employees rather than the whole
    /// roster.
    ///
    /// Called only from ScheduleAssignmentViewModel's five Set Schedule/Set Leave/Clear
    /// Schedule commands (single-employee and bulk-checked-employees alike) -- those are the
    /// ordinary "change a schedule for an employee" paths PayrollGroupViewModel's own
    /// RecheckOnPageRevisit exists for, and each already has the exact Employee (and
    /// therefore Pin) it just wrote on hand. EmployeeTreeViewModel.DeleteEmployeeAsync and
    /// ScheduleImportExportViewModel.ImportScheduleAsync deliberately still call the plain
    /// BumpSchedule() above, unchanged -- a delete touches every date that employee ever had
    /// a schedule entry for, not a specific handful, and an import's affected employees/dates
    /// come from a workbook this class has no reason to parse just to attribute a version
    /// bump. ScheduleVersion still moves for both, so ReportViewModel still notices; this
    /// dictionary simply gains no entry, so AnyScheduleChangeSince won't flag either one on
    /// its own -- PayrollGroupViewModel's existing manual Reload button is the fallback there,
    /// same role it already plays for a change made while the Payroll page wasn't open at
    /// all.</summary>
    public void BumpScheduleForEmployees(IReadOnlyCollection<int> employeePins)
    {
        ScheduleVersion++;
        foreach (var pin in employeePins) _lastScheduleChangeVersionByPin[pin] = ScheduleVersion;
    }

    /// <summary>True if any of <paramref name="pins"/> has had BumpScheduleForEmployees
    /// called for it (see that method's own doc comment for exactly which writes that
    /// covers) at a ScheduleVersion strictly greater than <paramref name="sinceVersion"/> --
    /// what PayrollGroupViewModel.RecheckOnPageRevisit uses instead of comparing the raw
    /// ScheduleVersion counter directly, since a loaded payroll group is almost always a
    /// small subset of the whole roster and an edit for someone not in it shouldn't force a
    /// recompute. <paramref name="sinceVersion"/> is normally the caller's own snapshot of
    /// ScheduleVersion taken the moment it last finished loading.</summary>
    public bool AnyScheduleChangeSince(IReadOnlyCollection<int> pins, int sinceVersion) =>
        pins.Any(pin => _lastScheduleChangeVersionByPin.TryGetValue(pin, out var v) && v > sinceVersion);
}
