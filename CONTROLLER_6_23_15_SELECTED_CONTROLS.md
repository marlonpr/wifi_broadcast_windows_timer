# Controller 6.23.15 — all countdown controls use the selection

START, STOP and RESET use the timer checkboxes selected when the button is
pressed. The three-button layout and visible START/action log are retained.
This supersedes 6.23.14's rule that STOP/resume kept the old participant list.

| Action | Selected timers receive | Saved countdown |
|---|---|---|
| START after RESET | A new synchronized countdown | Uses the configured duration and repetitions |
| STOP | Stop/pause preparation | Retains remaining whole seconds and global repetition |
| START after STOP | The saved countdown position, synchronized again | Retains the original countdown duration and total repeats |
| RESET | Reset preparation | Clears the paused progress |

A different resume selection is allowed. For example, pause with 02:50 remaining
in repetition 3, change the selection to TIMER04 and TIMER05, then press START.
Only TIMER04 and TIMER05 are required to confirm ready and resume at 02:50 in
repetition 3. The old pause participants are not required. Future full repetitions
keep the original programmed duration and numbering.

The selected list can include timers that were not in the paused run. Changed
selections are saved with the pause position before network actions. Selected
timers are prepared and confirmed with a fresh reset identity, then pass the
normal RTC readiness, synchronization and ARM checks. A missing selected timer
still blocks and is named in the log. Deselect it and press START again to apply
the revised selection; RESET is not required merely to change the resume group.

Timer checkboxes and SELECT ALL / DESELECT ALL are now available while counting,
so STOP can target a chosen subset. An action snapshots its list before any
asynchronous waits. Selection is locked while that action is applying; the
repetition/resync settings retain their separate active-program lock. Merely
changing checkboxes sends no countdown command and does not change the active
segment. Each submitted segment keeps its action's selected roster until a
subsequent operator action changes it.

STOP during preparation, repeated STOP after a pause, START recovery with a
changed selection, and RESET all follow the same selection rule. Operator STOP
or RESET cancellation of an in-progress ARM is restricted to selected prepared
timers; normal automatic failed-START cancellation still checks its complete
prepared set. Operator cancellation does not authorize a whole-fleet auto-retry.

With no timers selected, each action reports the reason and preserves progress.
The chosen pause/resume scope survives controller restart. Old ARM identities
are cleared from the stopped checkpoint before a new timer can join it. The
invalid-checkpoint RESET recovery fix remains included.

The controller retains one shared saved countdown position. Unselected boards
are not sent START or RESET commands by the action; their existing autonomous
segment can continue. This change does not add separate simultaneous controller
programs. Pause precision, idle-logo display while paused, and synchronization
after a resumed partial repetition remain as documented in 6.23.12.

## Build and apply

Use the complete source ZIP, or apply the incremental patch to controller
6.23.14 from the folder containing FactoryTimer.slnx:

```powershell
git apply --check <path-to-6.23.15-patch>
git apply <path-to-6.23.15-patch>
dotnet build FactoryTimer.slnx -c Release -p:Platform=x64
```

Use the complete ZIP for any older source version. The full PC check sequence
from the bundle root is:

```powershell
powershell -ExecutionPolicy Bypass -File .\checks\run_pc_builds.ps1
```

Firmware 6.24.5-rc1, analyzer 10.1.1 and the wire protocol are unchanged. Rebuild
the Windows controller; no timer or analyzer reflash is required.

## Validation

.NET SDK 10.0.100 on Linux:

- Controller-core tests: 123 PASS.
- Protocol tests: 53 PASS.
- Actual view-model adapter: 71 checks PASS.
- Production smoke: 163 assertions PASS.
- Resume faults: 11 PASS.
- Stale RUNNING regression: 9 PASS.
- Portable host suite: 85 PASS.

New regressions cover selected STOP, new members at resume, preserved saved time
and repeat geometry, dropping old missing participants, fresh proof for a changed
scope, paused-scope persistence, repeated pauses, all three empty-selection
cases, click-time snapshots across cancellation waits, held recovery with another
selection, and selected ARM cancellation versus normal full-set cancellation.
The adapter compiles actual controller sources with host UI stand-ins and
in-memory UDP sockets. Native Windows WinUI/XAML build and physical testing
remain pending.

## Bench check

1. Start a short repeated countdown on a selected group.
2. Change checkboxes while running, then press STOP. Check only those timers
   pause and the controller retains remaining time/repetition.
3. Choose a different group, including a timer absent from the original run.
4. Press START. Check the selected timers resume the saved position and old
   unselected timers do not appear as required confirmations.
5. Press RESET on another selection; check its progress is cleared for a new
   countdown. Confirm action messages appear in the visible log.
