# Controller 6.23.12 — START / STOP / RESET

This complete source release is based on the uploaded RC2 archive, controller
6.23.10-rc2. Its firmware **6.24.5-rc1** and analyzer **10.1.1** are unchanged.
Rebuild the Windows controller; no timer/analyzer reflash is required for this
controller update. Existing folder names and assets are retained.

## Three controls

The only normal countdown controls are one row immediately below **Resync limit
(seconds)**, in this order:

| Button | Action |
|---|---|
| START | Begin a new countdown, or continue the countdown saved by STOP |
| STOP | Pause: save the remaining whole seconds, repetition and original participant roster, then stop those timers |
| RESET | Stop and discard the saved progress; the next START uses the configured duration/repetitions as a new countdown |

The labels stay START, STOP and RESET. Confirmation/recovery details are text,
not additional buttons. The previous separate CONTINUE, RESUME, RECONCILE and
retry/isolation controls are not shown. Existing engineering/qualification
actions are retained under a collapsed **Engineering diagnostics** section.

STOP preserves progress; repeated STOP presses do not replace the saved time.
Editing duration, repetition count or the draft selection while paused does not
change the saved countdown: START still resumes the original fleet/program.
Use RESET to discard it and use edited settings for the next countdown.

## What the panels do

This uses the controller-only approach from the supplied patches. STOP sends the
existing RESET command to the frozen timers, so their panels return to the idle
logo. The controller displays PAUSED and the saved time. The firmware does not
gain a PAUSE opcode or freeze a number on the panels.

The saved position is captured at the operator's STOP click, before asynchronous
preparation cancellation or network checks. It uses whole display seconds; the
fraction of that second can replay. STOP is not a synchronized physical edge:
boards continue until they receive RESET. START performs fresh readiness,
synchronization and the existing all-participant ARM barrier before resuming.

A partially completed repetition runs as a shortened first segment. After it
finishes, the controller automatically resynchronizes and returns to the original
full repetition duration. Example: 03:00 x 30, STOP on repeat 3 at 02:50 -> START
runs 02:50 for repeat 3, resynchronizes, then runs repeats 4-30 at 03:00. The
shortened segment is excluded from drift-estimator seeding so its short sync
interval cannot create a false long-segment frequency limit.

## Confirmation and recovery

- STOP/RESET affect the original frozen participants, even if the draft selection
  changes. A missing timer is listed; it is not silently removed.
- START cannot bypass an unconfirmed stop. It retries bounded confirmation and
  resumes/starts only after the required original fleet is confirmed stopped.
- Reset proof must be fresh, challenged READY/EXEC evidence for the exact RESET
  command. A stale reply, wrong command or missing board cannot release the gate.
- Before sending destructive RESET, the controller discovers current endpoints
  using broadcast plus last-known-address probes. A current challenged EXEC reply
  must agree with the device's current IP. Delayed STATUS from an old IP cannot
  inherit the new endpoint's proof.
- Retries retain a pending RESET transaction ID. A new RESET press after a fully
  confirmed transaction uses a fresh command ID. RESET always clears continuation
  intent, including while delivery remains unconfirmed.
- The hashed program journal persists paused time, repetition and confirmation
  state. Reopening the controller restores PAUSED. START rechecks all original
  boards before using the snapshot. Legacy journals without pause fields load.
- A saved active run is reconciled through START; an already-running segment is
  observed, not started a second time. Existing held-program completion guards
  remain. A previous controller session's clock origin is never reused.
- If position cannot be reconstructed after restarting an active controller,
  STOP still attempts to stop the fleet; continuation is blocked rather than
  inventing a position. RESET then prepares a new countdown.

Initial clearly missing/offline selections are rejected before creating a durable
new program, preserving the selection-admission fix from the supplied patch.

## Build on Windows

From the extracted `FactoryTimer_preflight_sources/controller_v6_22_13` folder:

```powershell
dotnet build FactoryTimer.slnx -c Release -p:Platform=x64
```

For the complete PC check sequence, from `FactoryTimer_preflight_sources`:

```powershell
powershell -ExecutionPolicy Bypass -File .\checks\run_pc_builds.ps1
```

The incremental patch is relative to the **controller repository root** (the
folder containing `FactoryTimer.slnx`), against the uploaded RC2 version:

```powershell
git apply --check .\FactoryTimer_Controller_6_23_12_THREE_BUTTONS_vs_RC2.patch
git apply .\FactoryTimer_Controller_6_23_12_THREE_BUTTONS_vs_RC2.patch
```

Use the actual path to the patch. It combines controller code, tests and these
notes; do not also apply the three supplied intermediate patches. Those patches
depend on a missing pause foundation and do not apply directly to the supplied
RC2. The complete ZIP also updates the bundle-level README/check script.

## Verification

- Controller.Core native MSTest suite: **116 passed**.
- Protocol native MSTest suite: **53 passed**.
- Production smoke: **163 assertions passed**.
- Countdown adapter smoke: **22 checks passed**, compiling the actual view model,
  clock, device model, UDP/serial adapters and coordinator. Host-only UI dispatcher,
  brush and interface-provider stand-ins are used; this is not WinUI rendering.
- Existing resume-fault scenarios: **11 checks passed**.
- Existing stale-RUNNING regression: **9 checks passed**.
- Portable firmware/tooling/analyzer suite: **85 passed**.

Tests cover repeat edges, segment edges, re-pausing a shortened repetition,
maximum-size continuation plans, journal restart/migration, wrong RESET proof,
lost-network STOP/RESET, cancellation during preparation, changed DHCP endpoints,
selection admission and the exact three-button placement. Native checks used
.NET SDK 10.0.100 on Linux. The full Windows WinUI/XAML build and physical
STOP/START/RESET tests remain to run on the PC/boards.

## Short bench check

1. Run 00:30 x 3. On repetition 2, press STOP and record the saved counter/time.
   Confirm the controller says PAUSED and every original timer confirms READY.
2. Wait ten seconds: saved time must not move. Press START. Confirm the remainder
   of repetition 2, automatic synchronization, then a full 30-second repetition 3.
3. STOP again, then RESET. Set 00:15 x 2 and START: it must start at repetition 1
   with 15 seconds, without the previous paused remainder.
4. Repeat with a board temporarily offline during STOP. Its name must remain
   pending. Restore connectivity, then START; verify current IP discovery and
   stop confirmation before any new ARM. Deselecting it cannot bypass confirmation.
5. Close/reopen while paused, then START: saved time, repetition and original fleet
   must survive. Also try STOP during synchronization/RTC preparation.
