# Controller 6.23.16 — completion RESET, 10-second recovery and missing-card state

Based directly on `FactoryTimer_Controller_6_23_15_SELECTED_CONTROLS_COMPLETE`.
Firmware 6.24.5-rc1, analyzer 10.1.1 and the wire protocol are byte-unchanged.
This is a controller-only update. No timer or analyzer reflash is required.
The three-button layout, selected-timer actions and 6.23.15 pause/resume
segmentation behavior are retained.

## Automatic RESET when the whole program completes

After the final segment is confirmed FINISHED, the controller saves its program
execution audit and waits for the final controller TX/health/diagnostic CSV
collection to finish. It then sends the normal three-copy RESET transaction to
the program's executed roster. An edit to the checkboxes during the last segment
does not reset unrelated timers or exclude a timer that just ran the program.
Manual START, STOP and RESET continue to use the selection at button press.

Automatic RESET returns panels to the existing idle display and preloads the
original duration on the controller. START begins a fresh countdown using the
current duration/repetitions and selected timers. No automatic RESET happens
at an internal repetition, intermediate segment boundary, pause or HOLD.

The pending RESET command ID and roster are saved before network sends.
Freshly challenged READY evidence is required for confirmation. If replies or
connectivity are lost, the pending stop confirmation stays visible and survives
a controller restart. Completed progress cannot become a resumable pause.
START cannot race final diagnostics or automatic RESET. RESET errors are logged
and can be retried through the normal controls.

## Recovery wait: 180 -> 10 seconds

The production boundary recovery window is now 10 seconds, with one-second
poll spacing. This covers missing participants, stale RUNNING/ARMED replies
past the planned end, and RTC/post-run temperature recovery. Healthy devices
still finish their active segment; unconfirmed completion never counts as a
finished repetition. If recovery does not finish within the window, the fleet
holds at 00:00 and the log explains why. Production RTC pre-start waiting uses
the same limit. Qualification warm-up and RTC acceptance thresholds are retained.

All timeout messages and recovery-audit classifications use the new 10-second
limit. The normal ARM cancellation proofs and bounded retries are retained.

## Timer card reflects a failed active probe immediately

Once a fresh STATUS probe expires (normally about two seconds), a timer that
returned neither fresh STATUS nor request-matched execution STATUS is marked
OFFLINE / UNKNOWN immediately. Its estimated remaining time is cleared so the
card cannot keep counting from stale RUNNING data. The visible countdown log
records the timer and reason at that same point.

Fresh STATUS restores the reported state. A missing EXEC reply alone does not
make a timer offline when live STATUS proves it is online; it still blocks
START with the compatibility/fresh-evidence reason. Planned network silence
while a segment runs remains supported. Ordinary passive idle detection keeps
its existing timeout, and now also clears stale RUNNING to UNKNOWN on expiry.

## Apply and build on Windows

Replace the controller source with `controller_v6_22_13` from the complete ZIP,
or apply the incremental patch to the 6.23.15 controller root (the folder
containing FactoryTimer.slnx):

```powershell
git apply --check <path-to-6.23.16-patch>
git apply <path-to-6.23.16-patch>
dotnet build FactoryTimer.slnx -c Release -p:Platform=x64
```

From the bundle root, run all PC checks with:

```powershell
powershell -ExecutionPolicy Bypass -File .\checks\run_pc_builds.ps1
```

The complete bundle preserves the same firmware, analyzer, board identities,
network profiles, assets and folder names as the delivered 6.23.15 bundle.

## Verification

.NET SDK 10.0.100 on Linux:

- Controller-core tests: 125 PASS.
- Protocol tests: 53 PASS.
- Actual view-model/UDP adapter: 89 checks PASS.
- Production smoke: 163 assertions PASS.
- Resume fault scenarios: 11 PASS.
- Stale RUNNING regression: 9 PASS.
- Portable suite: 86 PASS, including the .NET production smoke (85 without SDK).
- Recovery audit contract: 14 PASS.

The adapter uses actual controller sources with host UI stand-ins and simulated
UDP timers. It exercises the program-loop completion hook, RESET send scope and
three copies per participant, durable intent before sends, waiting for diagnostic
CSV completion before RESET, fresh READY proofs, no second RESET on repeated
completion handling, no RESET during other stages, pending confirmation after
network loss/restart, and immediate offline/unknown card transitions. Core tests
check WAIT at 9.999 seconds and HOLD at 10 seconds for each recovery fault.
Firmware, analyzer and protocol bytes are compared against the baseline.

The full Windows WinUI/XAML build and physical button/network test are pending.
No physical timer, serial port or hardware configuration was changed here.

## Short bench check

1. Select the desired timers. Run 00:20 x 3 without pausing. At the final end,
   verify diagnostics are saved, RESET is logged for the executed timers,
   panels return to the idle display and the controller shows READY / 00:20.
2. Change the checkboxes during the final segment. Automatic RESET must still
   address the executed roster; a subsequent manual START uses the new selection.
3. With TIMER03 previously online, disconnect it, then press START while selected.
   When the fresh probe fails, expect OFFLINE / UNKNOWN on its card immediately
   and a matching reason in the countdown log. Reconnect it and confirm recovery.
4. At a segment boundary, keep one required timer disconnected: expect HOLD
   after the 10-second recovery window, without crediting missing completion.
5. Test STOP/resume and RESET normally to confirm the retained 6.23.15 behavior.
