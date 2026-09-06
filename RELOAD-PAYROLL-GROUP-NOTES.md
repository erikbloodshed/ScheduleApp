# Reload Payroll Group button (removed)

The manual "Reload" button this file used to document -- a button next to the Payroll
Group panel's "Included" grid header, forcing a recompute of everyone currently in the
group -- has been removed. It wasn't earning its place: `RequestPayrollGroupRefresh`'s
own automatic path already reacts to period changes, and `RecheckOnPageRevisitAsync`
already picks up a schedule edit made while the tab was closed, so the button's real
remaining use case (a one-off recalculation for a single payslip) is now served by a
per-employee Refresh button instead -- see below.

## Replacement: per-employee Refresh button on the Payroll Summary panel

`PayrollSummaryView.xaml`'s employee-name header now has an icon-only Refresh button
(Segoe Fluent Icons/Segoe MDL2 Assets "Refresh" glyph, U+E72C) docked to the row's
rightmost edge, next to the Pay Type badge. It recomputes just the currently selected
employee's payslip -- the same `PayrollSummaryViewModel.RefreshAsync()` compute the
automatic employee/period-change path already uses -- and reports success explicitly
("Payslip recalculated.") the same way a direct button click should, unlike the silent
automatic path.

### What changed

- **PayrollSummaryViewModel.cs**
  - `RefreshAsync()` now returns `Task<bool>` (true on success, false if `_busy.RunAsync`'s
    `onError` fired) instead of `Task`. Every existing caller (`RequestRefresh`'s
    fire-and-forget `_ = RefreshAsync();`, `RecheckOnPageRevisitAsync`'s
    `await RefreshAsync();`, and `PayrollGroupViewModel.RemoveEmployeeFromGroupAsync`'s own
    `await _summary.RefreshAsync();`) already discarded the result, so this is a
    non-breaking signature change.
  - New `RecalculatePayslipCommand` (`RecalculatePayslipAsync`, an `[AsyncRelayCommand]`)
    awaits `RefreshAsync()` directly and shows the success message when it returns true.
  - New `CanRecalculatePayslip()` gate: `!_busy.IsRunning && Result is not null` -- same
    shape as `CanPrintCurrentPayslip`, since there's nothing to recalculate before a
    payslip has loaded once.
  - `NotifyAdjustmentCommands()` and `OnResultChanged` now also notify
    `RecalculatePayslipCommand`.

- **PayrollViewModel.cs**
  - Forwards `RecalculatePayslipCommand` from `Summary`, alongside `PrintCurrentPayslipCommand`.
  - Removed the `ReloadPayrollGroupCommand` forward (see below).

- **PayrollSummaryView.xaml**
  - The employee-name header row changed from a plain horizontal `StackPanel` to a
    `DockPanel` (`LastChildFill="False"`) so the new Refresh button can dock against the
    row's own right edge regardless of how wide the name/badge are.
  - Added `IconHeaderActionButton`, a small round-hover icon-button style (transparent
    background, `#2563EB` glyph color, rounded hover highlight), and the Refresh `Button`
    itself, bound to `RecalculatePayslipCommand`.

- **PayrollGroupViewModel.cs**
  - Removed `ReloadPayrollGroupAsync`/`CanReloadPayrollGroup` entirely.
  - `RefreshPayrollGroupRowsAsync()` reverted from `Task<bool>` back to plain `Task` --
    nothing needs the success flag anymore now that its only bool-consuming caller
    (`ReloadPayrollGroupAsync`) is gone.
  - `NotifyGroupCommands()`/`OnBatchScopeEmployeesChanged()` no longer notify a Reload
    command that no longer exists.
  - `RecheckOnPageRevisitAsync`'s own doc comment, which used to point at the manual
    Reload button as the fallback for an Import/Delete-Employee schedule change (the two
    cases `AnyScheduleChangeSince` can't detect), now points at nudging a period picker or
    re-running "New Payroll Run…"/"Load Payroll Group…" instead.

- **PayrollPage.xaml**
  - Removed the "Reload" button and its wrapping inner `DockPanel` from beside the
    "Included" grid's header; the label is a plain `TextBlock` again.

## Not changed

- No new repository/service calls on either side -- both the old group-wide button and
  the new per-employee one reuse compute paths (`RefreshPayrollGroupRowsAsync`/
  `RefreshAsync`) that already existed for other triggers.
- `RequestPayrollGroupRefresh()`/`RequestRefresh()` (the automatic, deferred-while-busy
  paths) are untouched in behavior -- still what period/scope/selection changes call,
  still silent on success.

## Verification

No dotnet SDK available in-session, same as every prior phase in this codebase --
unverified by a real build. Did do an XML well-formedness sweep across every `.xaml` file
in the project (all 36 clean) plus a `--`-inside-`<!-- -->` sweep on the same set (caught
and fixed three instances this pass introduced, in `PayrollPage.xaml` and
`PayrollSummaryView.xaml` -- XML comments can't contain `--` the way a C# `//` comment or
an XML *attribute* value can), a brace/paren balance check on every edited `.cs` file, and
read through every edited method end-to-end to confirm the `Task`/`Task<bool>` signature
changes compile unchanged at their existing call sites.
