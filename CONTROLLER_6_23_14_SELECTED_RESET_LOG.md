# Controller 6.23.14 — visible START log and selected-timer RESET

The three normal buttons remain START / STOP / RESET below Resync limit.

## Visible log

A Countdown / START log is always visible immediately below the program and
Waiting for stop confirmation text. It shows the current START availability
and retains the last 60 action messages, newest first, with local timestamps.
The history scrolls within its panel, and text can be selected and copied.

The log includes every reported START requirement and remedy, including missing
or stale STATUS, offline timers, RTC qualification, diagnostics, synchronization,
ARM/recovery failures and controller/network errors. Stop confirmation reports
which timer has not provided fresh matching STATUS / EXEC_STATUS, whether its
RESET command is confirmed, whether it is READY, and any remaining old progress
or missing build identity. It does not add network polls or run-time packets.

## RESET uses the current selection

RESET clears saved progress and creates a new reset-confirmation scope from the
timers selected when RESET is clicked. This replaces 6.23.13's original-roster
RESET rule. If TIMER01 and TIMER04 are unselected, RESET removes them from the
required waiting list; the selected TIMER02 / TIMER03 / TIMER05 can then confirm
and begin a new countdown without TIMER01 / TIMER04.

The log explicitly records timers removed from the required list. It does not
claim those excluded timers were reset. A missing selected timer still requires
confirmation. Changing checkboxes alone does not change the old pause/stop
checkpoint: press RESET to apply the new selection.

STOP still saves the remaining time and repetition for the original countdown.
START after STOP resumes that original fleet. START after RESET creates a fresh
countdown from the selected timers and current duration/repetition settings.
Selected RESET scope survives a controller restart. Repeated pending RESETs for
the same selected scope and duration retain their command ID; changing scope
uses a new ID. RESET with no selection is explained and leaves the saved state
unchanged. The invalid-checkpoint recovery fix from 6.23.13 is retained.

Unselected RUNNING replies remain visible on their cards but cannot suspend
preparation or establish a silent window for the new selected program.

## Build and apply

Use the complete ZIP, or apply the patch to controller 6.23.13 from the folder
containing FactoryTimer.slnx. Do not apply this patch directly to older versions.

```powershell
git apply --check <path-to-6.23.14-patch>
git apply <path-to-6.23.14-patch>
dotnet build FactoryTimer.slnx -c Release -p:Platform=x64
```

Full PC checks from the complete bundle root:

```powershell
powershell -ExecutionPolicy Bypass -File .\checks\run_pc_builds.ps1
```

Firmware 6.24.5-rc1, analyzer 10.1.1 and the wire protocol are unchanged. No timer
or analyzer reflash is required.

## Validation

.NET SDK 10.0.100 on Linux:

- Controller-core tests: 119 PASS.
- Protocol tests: 53 PASS.
- Actual controller adapter checks: 51 PASS.
- Production smoke: 163 assertions PASS.
- Resume faults: 11 PASS.
- Stale RUNNING regression: 9 PASS.
- Portable host checks: 85 PASS.

The adapter covers the exact ESP01 / ESP04 waiting case, current selected scope,
restart persistence, multi-device START reasons and remedies, bounded log
history, empty selection, pending command reuse, invalid checkpoint RESET, and
unselected RUNNING STATUS / EXEC_STATUS during new-program preparation.
It compiles the actual controller sources using host UI stand-ins and in-memory
UDP sockets. Native Windows WinUI/XAML build and physical timer checks remain
pending.

## Bench check

1. Select TIMER02, TIMER03 and TIMER05; leave TIMER01 / TIMER04 unselected.
2. Press RESET. Confirm TIMER01 / TIMER04 leave the current waiting list and
   appear in the log as excluded by RESET.
3. When the selected timers confirm READY, press START. If it is refused, read
   the timestamped reason and remedy in the visible log below the waiting text.
4. Check STOP / START still pauses and resumes. Restart after RESET and confirm
   only the selected reset scope is restored.
