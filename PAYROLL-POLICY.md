# Payroll Policy

Every pay rule ScheduleApp applies, written down so it can be checked against how
the company actually pays people. **Part 1** is what the app does today. **Part 2**
is where that still diverges from Philippine labor law. **Part 3** is the planned
fix for the remainder.

The authority for Part 1 is `ScheduleApp.Payroll/PayrollCalculator.cs` — if this
document and that file disagree, the file wins and this document is the bug.

**Status:** rest-day rates shipped 2026-09-08 (`AddRestDayPremiumOverride`); Holiday
premium made configurable the same day (`AddHolidayPremiumOverride`); per-employee
Undertime exemption and Work Time scheduling default shipped the same day too
(`AddEmployeeWorkTimeAndUndertimeExemption`). Holiday classification (Regular vs.
Special Non-Working) is **not built** — see Part 3.

---

# Part 1 — Current policy

## 1.1 What payroll is computed from

Per employee, per period, from three inputs: the period's attendance summaries
(one row per scheduled day, or one per segment for a split shift), the period's
manually-entered adjustment rows, and the company holiday list.

The computation is pure and stateless — re-run from scratch every time the Payroll
tab is opened. Nothing about the result is stored except the adjustment rows a
person typed in.

| Section | Lines |
|---|---|
| Computed Gross Pay | Basic Pay, Overtime, Night Diff, *(Rest Day Pay — only if eligible)* |
| Gross Pay adjustments | Premium Pay, Allowance, Incentive |
| Computed Deductions | Undertime |
| Deduction adjustments | SSS, PhilHealth, Pag-IBIG, Cash Advance, Charges |

**Net Pay** = Total Gross Pay − Total Deductions, rounded to the nearest
`NetPayRoundingMultiple` (default ₱0.01, i.e. effectively no rounding).

## 1.2 Pay types

Every employee is either **Daily-rated** or **Monthly-rated**. This affects only
Basic Pay; every other line behaves identically for both.

**Daily-rated.** Basic Pay is a flat `DailyRate` for each credited day.

```
hourly rate = DailyRate ÷ StandardHoursPerDay        (default 8)
```

**Monthly-rated.** Basic Pay is `effectiveDailyRate` times a day count —
`basicPayDays` — the same "rate × days present" shape as Daily-rated above, just
with a per-period rate instead of a fixed one:

```
divisor             = 15 − (rest days falling in the period's first 15 calendar days)
effectiveDailyRate  = (MonthlyRate ÷ 2) ÷ divisor
hourly rate         = effectiveDailyRate ÷ StandardHoursPerDay

basicPayDays = divisor
             − (uncredited days inside the first 15)
             + (credited days beyond the first 15)

Basic Pay = effectiveDailyRate × basicPayDays
```

Three company decisions are baked into `basicPayDays`:

- The divisor always assumes a **nominal 15-day period**, never the period's real
  calendar length. In a 16-day second half (Aug 16–31), day 31 sits outside the
  nominal window and never affects the divisor.
- A day worked **beyond** the nominal 15 still earns a prorated extra day's pay
  (`basicPayDays` gains one), rather than being treated as free work the flat
  salary already covers.
- An **uncredited** day beyond the nominal 15 is neutral — it forfeits that day's
  bonus but does not additionally subtract from `basicPayDays`.

One consequence worth being explicit about: a period genuinely **shorter** than 15
real days (every month's short second half, e.g. Feb 16–28) still prices as if it
were the full nominal 15 — the days beyond the real period's end that never
happened are simply never counted against `basicPayDays` either way, the same
"forfeits the bonus, doesn't subtract" neutrality an excess-window absence gets.
A perfectly-attended 13-real-day February half reads "Basic Pay (14D)", not
"Basic Pay (13D)" — the label is `basicPayDays`, not a literal count of calendar
days that occurred.

Rest days are excluded from `basicPayDays` entirely: a rest day is neither an
absence nor a credited workday, so it only ever reduces the divisor.

The label is `Basic Pay (ND)` for both employee types now, N being `basicPayDays`
(Daily-rated) or the count above (Monthly-rated) — it used to read `Basic Pay
(Semi-Monthly[, XD deducted][, YD excess])` for Monthly-rated, spelling out the
subtract/add mechanics instead of the day count they netted out to.

## 1.3 Which days count as paid

| Day status | Credited (pays Basic Pay) |
|---|---|
| Complete | Yes |
| Official Business | Yes |
| Leave — paid | Yes |
| Leave — unpaid | No |
| Absent | No |
| Partial (an unpaired punch) | **No** |
| Rest Day | No — priced by Rest Day Pay instead |

A **Partial** day pays nothing: an odd unpaired punch means the day's hours can't
be established reliably, so it isn't credited as a work day at all.

Basic Pay is per *day*, never per hour — a day scheduled for 9 hours and a day
scheduled for 10 hours pay the same Basic Pay.

**Unscheduled days** (a calendar day with no schedule row at all) are counted and
surfaced as a caution, but don't deduct. For a Monthly-rated employee this is an
overpayment risk: a genuine day off left unscheduled instead of marked Rest Day
neither reduces the divisor nor deducts.

## 1.4 Overtime

```
Overtime pay = hourly rate × hours × (1 + overtime premium)
```

Default premium **25%** (so 125%). The premium is skipped entirely — paying
straight time — when the day's "apply rate %" toggle is off. Eligibility is
decided upstream in attendance; an ineligible day arrives with zero overtime
hours.

Rate resolution: per-day override → else company default.

Overtime is **never computed on a rest day**. Rest-day hours past the scheduled
window extend worked hours instead, and are priced by the rest-day tiers below.

## 1.5 Rest Day Pay

Applies when a day is scheduled as **Rest Day** *and* a duty was actually
recognised — a schedule window was pre-set for it, and both a clock-in and a
clock-out were matched. A rest day with no pre-set schedule pays nothing no matter
what was punched; a rest day with only one side of the pair matched also pays
nothing.

Two tiers, following Philippine labor law:

```
rest day rate = hourly rate × (1 + rest day premium)

first StandardHoursPerDay hours : rest day rate × hours
hours beyond that               : rest day rate × (1 + rest day OT premium) × hours
```

At the defaults (30% and 30%) that is **130%** for the first eight hours and
**169%** beyond. Note the second tier **compounds** — `1.30 × 1.30` — rather than
adding the two premiums to reach 160%.

**Where the premium comes from:**

| | |
|---|---|
| `Employee.RestDayWorkPremiumPercentage` | This employee's override. Blank (null) = inherit. |
| `PayrollPolicy.RestDayPremiumPercentage` | Company default, `0.30`. Set in Settings → Payroll. |

Blank and `0` mean different things: blank inherits the company's 30%, an explicit
`0` means this employee is deliberately paid straight time for rest day work.

`RestDayOvertimeRatePercentage` is global only — no per-employee or per-day
override — because a rest day never produces `AttendanceSummary.OvertimeHours` in
the first place. The split is made in Payroll, not Attendance, so there is nothing
for a per-day overtime override to attach to.

The line shows the split when there is one — `Rest Day Pay (8.00H + 3.00H OT)` —
so both tiers can be multiplied back out by hand from what's printed.

**Two separate gates:**

- `QualifiesForRestDayPay` (default **off**) — an opt-in benefit. When off, the
  Rest Day Pay line is omitted from the payslip entirely rather than shown at
  ₱0.00, and "Scheduled duty" is disabled for that employee.
- `RestDayWorkPremiumPercentage` — how much, once eligible.

Rest Day Pay is **additional to**, not a substitute for, Basic Pay: a worked rest
day still reduces the Monthly divisor exactly like an unworked one.

## 1.6 Night differential

```
Night diff pay = base rate for the day × night hours × premium
```

Default premium **10%**. Night hours are those falling between 22:00 and 06:00.
There is no "apply rate %" toggle — once eligible, the premium always applies.

The **base rate depends on the day**: on a rest day it is the 130% rest-day rate,
not the plain hourly rate. So night hours on a rest day earn 10% of 130%.

All night hours on a rest day use the **first-tier** rate, even on a day that ran
past eight hours. Attendance records only a *total* night-hours figure, with
nothing saying which of those hours fell past the eighth, so there is nothing to
allocate against; one flat rule that never over-pays beats a guess.

Rate resolution: per-day override → else company default.

## 1.7 Undertime

```
Undertime deduction = hourly rate × shortfall hours
```

No premium and no override — always the plain hourly rate. A period's undertime
can be marked **"disregarded"**, which leaves the line visible with its real
computed figure but excludes it from the deductions total.

An employee can also be marked **`ExemptFromUndertimeDeduction`** (Edit Employee →
Payroll, unchecked by default), for someone whose day doesn't need to be
*completed* to earn full pay, only *exceeded* to earn Overtime — Basic Pay was
already a flat rate per credited day regardless of hours worked, so this is the
one thing that could otherwise still cost them money for running short. When set,
every period's Undertime line behaves as if permanently "disregarded": still shown
with its real computed figure, but always excluded from the deductions total,
regardless of that period's own manual toggle. Overtime is unaffected — hours
worked past the day's scheduled Work Time are still Overtime for this employee,
exactly like any other.

Pairs naturally with **`Employee.DefaultWorkTimeHours`** (Edit Employee →
Attendance, blank = company default), a per-employee starting suggestion for
Apply Schedule's Work Time field — not a runtime resolution tier the way the
buffer defaults are (Work Time is a required, concrete value once a day is saved),
just what a brand-new entry starts showing for this employee, in place of the
company-wide default, so scheduling someone whose normal day is genuinely shorter
doesn't mean re-typing that number by hand every time.

## 1.8 Holiday Pay

Holidays are a hand-maintained list of dates. **There is no Regular-vs-Special
distinction — every listed date is treated identically.** This is the largest
remaining gap; see Part 2.

For each listed date in the period:

| Situation | Pays |
|---|---|
| Worked (Complete, or a rest day actually worked) | One extra day's pay + a duplicate of that day's overtime pay + a duplicate of that day's night diff pay |
| **Monthly-rated only**, not worked and not otherwise credited (Absent, Partial, unpaid Leave, unworked Rest Day) | One extra day's pay, flat — no overtime/night-diff copies |
| Official Business or paid Leave | Nothing extra — Basic Pay already pays that day in full |
| **Daily-rated**, not worked | Nothing |

"One extra day's pay" is `effectiveDailyRate` for Monthly-rated, `DailyRate` for
Daily-rated, times a configurable **Holiday premium**. Default `1.00` — one full
extra day, reproducing PH law's 200% worked-regular-holiday rate (Basic Pay's own
100% plus one more 100% here) exactly. Same two-tier resolution as the Rest Day
premium:

| | |
|---|---|
| `Employee.HolidayPremiumPercentage` | This employee's override. Blank (null) = inherit. |
| `PayrollPolicy.HolidayPremiumPercentage` | Company default, `1.00`. Set in Settings → Payroll. |

The premium applies to **both** rows of the table above — the worked day component
and the Monthly-rated unworked-but-still-paid one — but **not** to the OT/ND copies
next to it, which stay a flat doubling of the day's ordinary Overtime/Night Diff pay
regardless of what the Holiday premium is set to.

A holiday landing on a worked rest day **stacks additively**: Holiday Pay and Rest
Day Pay are computed independently and both paid.

Holiday Pay is not its own gross-pay line. It is surfaced through the editable
**Premium Pay** adjustment, whose amount is seeded from this figure and gated on
`QualifiesForPremiumPay`. A person can overwrite the seeded amount.

## 1.9 Adjustments

Eight manually-entered categories, in two shapes:

- **Single value** (one amount per employee/period, edited inline): Premium Pay,
  Allowance, SSS, PhilHealth, Pag-IBIG, Cash Advance.
- **Itemised** (any number of description + amount rows): Incentive, Charges.

Premium Pay, Allowance and Cash Advance carry an employee-level default seeded
automatically each period, including an explicit ₱0.00 row when the default is
zero. SSS/PhilHealth/Pag-IBIG seed only when their default is non-zero — a zero
there means "not enrolled", not "zero this period".

## 1.10 Rounding

- **Money**: 2 decimals, ties away from zero. Applied once to each final summed
  line — never to an intermediate per-day figure.
- **Hours**: rounded to 2 decimals **per day, before** being used for anything, so
  the hours printed on a line multiplied by the rate printed next to it always
  reproduce the peso amount shown. Each day loses up to ~18 seconds to rounding.
- **Net Pay**: rounded to the nearest `NetPayRoundingMultiple` after gross and
  deductions are totalled. Individual lines always foot exactly to the untouched
  totals.

Each line's displayed rate is derived as `amount ÷ hours` rather than recomputed,
so a period mixing several per-day override rates still shows a rate that
multiplies out correctly.

One consequence worth knowing: because intermediates keep full precision, a figure
sitting exactly on a half-centavo may round *down*. An 8.5-hour rest day at
₱15,000/13/8 comes to ₱1,621.87, not the ₱1,621.88 exact arithmetic would suggest —
`15,000 ÷ 13 ÷ 8` doesn't terminate in decimal, so the real total lands a hair
below the midpoint.

## 1.11 Where each number is configured

| Setting | Default | Configured in |
|---|---|---|
| Standard hours per day | 8 | Settings → Payroll |
| Overtime premium | 25% | Settings → Payroll; per-day override in Apply Schedule |
| Night diff premium | 10% | Settings → Payroll; per-day override in Apply Schedule |
| **Rest day premium** | **30%** | Settings → Payroll; **per-employee override** in Edit Employee |
| **Rest day OT premium** | **30%** | Settings → Payroll |
| Night diff window | 22:00–06:00 | Settings → Attendance |
| Net pay rounding | ₱0.01 | Settings → Payroll |
| **Holiday premium** | **100%** (one extra day) | Settings → Payroll; **per-employee override** in Edit Employee |
| Pay type, daily/monthly rate | — | Edit Employee |
| **Default work time (hours)** | **10** | Settings → Attendance; **per-employee suggestion** in Edit Employee (a scheduling starting point, not a resolved rate) |

Eligibility flags, all per employee: `QualifiesForOvertime` (default **on**),
`QualifiesForNightDiff` (default **on**), `QualifiesForRestDayPay` (default
**off**), `QualifiesForPremiumPay`, `DefaultLeaveIsPaid` (default unpaid),
`ExemptFromUndertimeDeduction` (default **off** — see §1.7).

---

# Part 2 — Remaining gaps vs Philippine labor law

| # | Rule | Today | Should be |
|---|---|---|---|
| 1 | Special non-working day | **200%** — treated identically to a regular holiday | **130%** worked; **no pay** if unworked |
| 2 | Special non-working day on a rest day | 230% | **150%** |
| 3 | Regular holiday on a rest day | 230% | **260%** |
| 4 | Overtime on a regular holiday | 250% (125% paid twice) | **260%** (200% × 1.30) |
| 5 | Holiday OT/night-diff copies on a rest day | Taken against the base hourly rate | Should follow the day's actual rate, as §1.6 now does |

**All five have the same root cause**: a holiday carries no Regular-vs-Special
classification, so there is nothing to branch on. None can be fixed in isolation.

Gap 5 is marked in the code (`CalculateHolidayPay`) rather than fixed, because
correcting it alone would move Holiday-on-Rest-Day figures without the
classification that determines what they should actually be.

**Already closed** (shipped 2026-09-08): rest day first-8-hours defaulting to 0%
instead of 130%; no 8-hour boundary on rest days; night differential taken against
the base rate on premium days.

---

# Part 3 — Planned: holiday classification

Not built. This is the design, recorded so the gaps above have an agreed
destination.

## 3.1 Target rate table

`P` = the rest-day premium (30%). Every overtime figure is that day's own
first-8-hours rate × 1.30.

| Day | First 8 hours | Beyond 8 hours |
|---|---|---|
| Ordinary day | 100% | 125% |
| Rest day | 130% | 169% |
| Special non-working day | 130% | 169% |
| Special non-working day **+ rest day** | 150% | 195% |
| Regular holiday | 200% | 260% |
| Regular holiday **+ rest day** | 260% | 338% |

**150% is statutory, not derived** — it is not `1.30 × 1.30 = 169%`. It needs its
own configurable value rather than being computed from the rest-day premium.

## 3.2 How the components divide up

Keep today's split, where Basic Pay / Rest Day Pay / Overtime / Night Diff are
computed normally and Holiday Pay adds the **extra** on top. The rule becomes:
each component pays the delta between the day's full statutory factor and what the
other lines already paid.

```
totalDayFactor:
  Regular, ordinary day  →  1 + RegularHolidayPremium                = 2.00
  Regular, rest day      → (1 + RegularHolidayPremium) × (1 + P)     = 2.60
  Special, ordinary day  →  1 + SpecialHolidayPremium                = 1.30
  Special, rest day      →  1 + SpecialHolidayOnRestDayPremium       = 1.50

alreadyPaidFactor:
  rest day   → 1 + P     (the Rest Day Pay line covers it)
  otherwise  → 1.00      (Basic Pay covers it)

holiday day component = day rate × (totalDayFactor − alreadyPaidFactor)
```

Which lands exactly on the table: regular + rest = `1.30 + 1.30 = 2.60`; special +
rest = `1.30 + 0.20 = 1.50`; special ordinary = `1.00 + 0.30 = 1.30`; regular
ordinary = `1.00 + 1.00 = 2.00`.

## 3.3 What this changes in pesos

Daily-rated ₱800/day unless noted.

| Scenario | Today | Planned |
|---|---|---|
| Regular holiday on a worked rest day | holiday component ₱800.00 | **₱1,040.00** |
| Special non-working day, worked, ordinary day | ₱800.00 | **₱240.00** ← *decrease* |
| Special non-working day, unworked, Monthly-rated | one full day | **₱0.00** ← *decrease* |
| Overtime on a regular holiday | 250% | **260%** |

⚠️ **Two of these reduce pay.** Paying a full day for an *unworked* special
non-working day is not standard ("no work, no pay" absent a company grant), and a
worked special non-working day should be 130%, not the 200% the app currently
gives it. Both are corrections, but both take money away from a scenario that
currently pays more — they need an explicit decision before they ship, not a
silent landing.

## 3.4 Scope

- `HolidayType { Regular, SpecialNonWorking }` on the `Holiday` model, defaulting
  to `Regular` so existing rows keep today's behaviour.
- A migration, plus type selection in the Holiday dialog and a Type column in
  Manage Holidays.
- `IHolidayRepository` returns holidays rather than bare dates;
  `PayrollCalculator`'s holiday parameter becomes date → type.
- Three policy settings, not two new ones: `PayrollPolicy`/`Employee`
  `HolidayPremiumPercentage` (shipped, §1.11) becomes `RegularHolidayPremiumPercentage`
  (rename, same 1.00 default and existing per-employee override — a holiday with no
  classification yet is a Regular one) plus two genuinely new fields, special holiday
  premium (0.30) and special-on-rest-day premium (0.50). The rename needs its own
  migration step (`sp_rename` or add-then-drop) to carry existing per-employee
  overrides forward under the new column name rather than silently losing them.
- A pure rate-table helper, unit-tested directly against the six rows in §3.1.
