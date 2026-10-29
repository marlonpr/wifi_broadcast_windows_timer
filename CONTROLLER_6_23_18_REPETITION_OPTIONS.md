# Repetition options — firmware 6.24.8-rc1 / controller 6.23.18

Based on the working active-LOW firmware 6.24.7-rc1 / controller 6.23.17.
Analyzer 10.1.1, bitmap assets, network profiles and identities are unchanged.

Two checkboxes below the resync-limit field default to OFF:

- **Start logo + output before every repetition (4 seconds).** Adds a local
  four-second logo and steady GPIO33 LOW before each internal repetition. Each
  numeric countdown retains its full duration. The existing end logo and four
  cycles of 500 ms LOW / 500 ms HIGH remain in place.
- **Sync before every repetition.** Uses one repetition per segment, with the
  normal readiness, synchronization and final ARM checks each time. The seconds
  limit is disabled and ignored while checked; it is retained for time-limit
  mode. Each countdown can still be up to 99:59. A segment start already has its
  steady four-second signal, so enabling both boxes adds no duplicate signal.

Keep the seconds-limit policy as the production default. Internal repeats
already use one continuous RTC-disciplined schedule. Syncing each repeat adds a
variable network pause and another point where a missing or unready selected
timer can hold the program. It is available when that deliberate pause and a
fresh synchronization each time are wanted; start signaling itself needs no sync.

Options are saved with the plan and survive restart, resume and selected-timer
changes. They are locked during active or paused execution. Press RESET before
changing a paused program's options. Old journals retain their original timing.

## Timing

GPIO33 stays **LOW=ON, HIGH=OFF**; COMMIT stays GPIO17. At a segment start,
the existing five-second ARM lead and four-second LOW pulse beginning at START_AT
acceptance are unchanged. No countdown seconds are consumed by a start logo.

With start signaling ON and time-limit sync, 00:20 x 3 is one 80-second segment
from the first scheduled numeric start T*:

| Time from T* | Panel | GPIO33 |
|---|---|---|
| 0–20 s | Countdown 1 | HIGH |
| 20–24 s | End logo | 500 ms LOW / 500 ms HIGH |
| 24–28 s | Start logo for repetition 2 | Steady LOW |
| 28–48 s | Countdown 2 | HIGH |
| 48–52 s | End logo | 500 ms LOW / 500 ms HIGH |
| 52–56 s | Start logo for repetition 3 | Steady LOW |
| 56–76 s | Countdown 3 | HIGH |
| 76–80 s | Final end logo | 500 ms LOW / 500 ms HIGH |
| After 80 s | Completion, diagnostics, automatic RESET, idle logo | HIGH |

The repetition index advances when its start logo begins. STOP in either
intervening logo saves the next full countdown. STOP/RESET cancels the signal
and returns HIGH, including when a callback was already queued. There is no
additional start signal after the final repetition.

For countdown D, n repeats, end logo L=4 and optional start logo S=0 or 4:

```
segment completion = n*(D+L) + (n-1)*S
last countdown end = segment completion - L
```

The last countdown endpoint must fit the resync cap. Both internal logos count
against it; the final end logo may follow it. A single 99:59 still completes at
6003 seconds including its final logo. Silent windows, temperature suppression,
final diagnostics and automatic RESET include the complete segment.

Both boxes OFF preserve 20 x 3 at 72 seconds. Sync-every-repetition uses three
24-second segments plus variable network preparation/ARM pauses. The existing
bounded retries, 10-second recovery wait and HOLD behavior remain. A healthy
board continues an active segment if another board disappears.

The existing partial-resume design is retained: a shortened saved remainder
runs as its own segment, followed by resynchronization for remaining full repeats.

## Protocol and compatibility

The new signal uses START_AT field 15, `RepeatStartSeconds=4`, following
`LogoSeconds=4`. Normal logo commands remain 14 fields; legacy segments remain
13. Firmware appends RepeatStartSeconds (0 or 4) as EXEC_STATUS field 21. The new
controller accepts 19, 20 and 21 fields. Fresh challenged feature support is
required before sending the new option; unsupported boards are blocked before
synchronization. The new duration is included in duplicate-command fences,
final ARM/recovery matching, clocks, cards and completion evidence.

STATUS and RUN_DIAG are unchanged. Worst-width EXEC_STATUS is 265 bytes, below
511. Update controller and firmware together for the new feature; older
controllers cannot parse the new execution reply. Analyzer source/configuration
and wiring are unchanged from the working active-LOW build.

## Build

Folder names remain `controller_v6_22_13`, `firmware_v6_23_16`, `analyzer_v10_1`.
Use the complete source ZIP, or apply the respective patches to controller
6.23.17 and firmware 6.24.7-rc1 source roots. Run `git apply --check` before
`git apply` for each patch. From the controller folder on Windows:

```powershell
dotnet build FactoryTimer.slnx -c Release -p:Platform=x64
```

The full Windows sequence is `checks/run_pc_builds.ps1` from the bundle root.
In an ESP-IDF shell, from the firmware folder (ESP02 example):

```powershell
idf.py -B build-esp02-repeat-options -D SDKCONFIG=sdkconfig.esp02 build
idf.py -B build-esp02-repeat-options -D SDKCONFIG=sdkconfig.esp02 -p COMxx flash
```

Use each board's commissioned profile and actual port. Keep COMMIT on GPIO17.

## Verification and bench

Host checks pass: 138 core tests, 59 protocol tests, 113 actual controller
adapter checks, 89 portable checks, 163 production assertions, 11 resume fault
checks, 9 stale-RUNNING checks and 14 audit checks. The actual renderer includes
81 exact absolute boundaries for the new 20 x 3 schedule and 387 for 5 x 30,
with a deliberately 500 us late START task. The actual GPIO module verifies
end blinking followed by four seconds steady LOW, HIGH throughout countdowns,
no final extra start signal, cancellation and late callback phase. Analyzer
replay remains 46/48/68 us PASS. See `checks/repetition_options_results.txt`.

ESP-IDF target builds, the full Windows WinUI build and physical timing are
not run here. After rebuilding:

1. Both boxes OFF: run 20 x 3 and confirm the working 72-second schedule.
2. Only start signaling ON: run 20 x 3 and confirm the 80-second table, with no
   internal resync. Analyzer: `--segment-seconds 80`, 81 boundaries per channel.
3. STOP during the end blink and during the steady start logo; confirm HIGH and
   correct selected-timer resume position.
4. After RESET, enable sync-every-repetition. Run 20 x 3: three unattended
   segments, one start signal per segment, full readiness/sync/ARM checks each
   time. Analyzer: `--segment-seconds 24,24,24`, 25 boundaries per train/channel.
   Test with both start-signal checkbox states.
5. Only start signaling ON: test 03:00 x 10 with cap 600; segment lengths are
   `560,560,560,184`. Repeat the existing ARM/network fault tests.
