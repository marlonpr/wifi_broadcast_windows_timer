# Four-second logo and GPIO33 output — firmware 6.24.6-rc1 / controller 6.23.17

Based on the working controller 6.23.16 bundle. Update the controller and timer
firmware together. Analyzer 10.1.1 is unchanged; move its COMMIT leads to GPIO17.

## Wiring

| Function | Timer GPIO |
|---|---:|
| New production output | 33 |
| Analyzer COMMIT marker | 17 (previously 33) |
| Existing refresh-adoption diagnostic | 16 (unchanged) |
| DS3231 SQW | 27 (unchanged) |

This release targets the existing classic ESP32 fleet profiles ESP01–ESP15.
All supplied profiles and diagnostic variants now select COMMIT GPIO17.
A build that tries to use GPIO33 for both COMMIT and the new output is rejected.
Keep analyzer CH1=ESP01, CH2=ESP04, CH3=ESP03 and its production profile. No
analyzer firmware rebuild is needed; only move the timer-side wires.

## Behavior

- When a logo-enabled START_AT is accepted, GPIO33 goes HIGH for four seconds,
  then LOW. Duplicate packets do not restart the pulse. A rejected START does
  not trigger it. The existing pre-start logo and five-second scheduled ARM
  lead are retained; the initial pulse starts at acceptance, not button press.
- At the end of every repetition, display the existing logo for four seconds.
  During those seconds GPIO33 performs four cycles of 500 ms ON / 500 ms OFF.
- At an internal boundary, the next full countdown begins immediately after
  the four-second logo. GPIO33 is LOW during the countdown itself. No network
  command or synchronization is needed at that internal boundary.
- The final repetition also gets the complete four-second logo/blink period.
  FINISHED, the post-run temperature read, final diagnostics and the existing
  automatic completion RESET occur after that period. The panel then stays on
  the idle logo. No numeric 00:00 frame is inserted into the logo intervals.
- STOP/RESET cancels the signal and forces GPIO33 LOW, including when an old
  timer callback was already queued. Wi-Fi loss does not stop the local schedule.

Example: 00:20 x 3, measured from the scheduled countdown start T*:

| Interval (seconds) | Panel | GPIO33 |
|---|---|---|
| 0–20 | Repetition 1 countdown | LOW |
| 20–24 | Logo | 500 ms ON / 500 ms OFF |
| 24–44 | Repetition 2 countdown | LOW |
| 44–48 | Logo | 500 ms ON / 500 ms OFF |
| 48–68 | Repetition 3 countdown | LOW |
| 68–72 | Logo | 500 ms ON / 500 ms OFF |
| After 72 | FINISHED, health collection, automatic RESET, idle logo | LOW |

The initial four-second HIGH pulse occurs before T*, during the existing
pre-start logo window. For 00:05 x 30 the segment now takes 270 seconds.
The controller clock/cards show LOGO during the intervals and retain the current
repetition index until the next countdown starts.

## Segment planning and pause behavior

Countdown duration D is unchanged. For n repetitions with logo L=4:

```
last countdown end = n*D + (n-1)*L
segment completion = n*(D+L)
```

The configured resync cap applies through the end of the last countdown;
internal logos count against it. The final four-second logo follows that last
countdown, so 99:59 remains supported with a 6000-second cap: the countdown ends
at 5999 seconds and completion follows at 6003 seconds. The controller's silent
window, trace duration, recovery and completion checks include the final logo.
Timing-budget drift calculations cover the last countdown endpoint.

For 03:00 x 10 with a 600-second cap, the segment lengths become 552, 552, 552,
184 seconds (each three-repeat segment's last countdown ends at 548 seconds).
For 50:00 x 2 with a 6000-second cap, the internal logo would move the second
countdown end beyond the cap, so the planner uses two segments instead.
Mandatory resynchronization can extend the logo/idle pause between segments.

Existing selected START/STOP/RESET and partial-resume segmentation are retained.
STOP in an internal logo interval saves the next repetition at full duration.
There is no countdown remaining to resume once the final logo has begun.
Old saved plans retain their old timing until RESET/a new program; do not
reinterpret an already-issued command during recovery.

## Protocol and compatibility

New production START_AT commands append `LogoSeconds=4` as field 14. Legacy
13-field segments and raw qualification/manual commands retain their original
no-gap behavior. New EXEC_STATUS appends LogoSeconds as field 20 (0 when idle or
legacy). A fresh challenged reply proves support before the new controller
sends a logo command. Old timer firmware is explicitly blocked for new logo
programs before synchronization/ARM. Repetition timing is part of duplicate-ID,
final-barrier and recovery identity checks. New firmware also requires enough
START lead for the initial four-second pulse.

Production STATUS is unchanged and still fits its 511-byte envelope; the
maximum-width new EXEC_STATUS is 254 bytes. Old controllers cannot parse the
new EXEC_STATUS suffix, so update both sides. Engineering qualification and
PREFLIGHT commands keep their timing; use GPIO17 for their analyzer capture.

## Build and apply

Folder names stay `controller_v6_22_13`, `firmware_v6_23_16`, `analyzer_v10_1`.
Use the complete source bundle, or apply both incremental patches to the working
controller 6.23.16 / firmware 6.24.5-rc1 source roots. Controller patch paths are
relative to the controller root containing FactoryTimer.slnx; firmware patch
paths are relative to the firmware root. Run `git apply --check <patch>` before
`git apply <patch>` in each respective root.

From the controller folder on Windows:

```powershell
dotnet build FactoryTimer.slnx -c Release -p:Platform=x64
```

From the bundle root, run the full PC sequence:

```powershell
powershell -ExecutionPolicy Bypass -File .\checks\run_pc_builds.ps1
```

In the ESP-IDF shell, from the firmware root, build/flash each selected board
with its own profile. Example for ESP02 (replace COMxx with its actual port):

```powershell
idf.py -B build-esp02-logo -D SDKCONFIG=sdkconfig.esp02 build
idf.py -B build-esp02-logo -D SDKCONFIG=sdkconfig.esp02 -p COMxx flash
```

Keep any commissioned SSID/password, device identity and zone edits in your
local profiles; change `CONFIG_FACTORY_DISPLAY_EDGE_GPIO` to 17 there. Supplied
profile changes are limited to COMMIT 33 -> 17; network settings are unchanged.
Move analyzer leads to GPIO17 before collecting new captures.

## Verification

.NET 10 host tests: 130 core tests, 56 protocol tests, 98 actual controller
adapter checks, 163 production assertions, 11 resume fault checks and 9 stale
RUNNING checks pass. The portable suite passes 89 checks with the .NET smoke
included (88 without SDK). The recovery/audit contract also passes 14 checks.

The actual firmware render loop is compiled and checked frame by frame for
legacy runs and logo runs, including 20 x 3 (73 boundaries), 5 x 30 (271),
1 x 3, 99:59 and a deliberately 500 us late START task. Every publication keeps
the same absolute T* + n*1,000,000 us grid. The actual GPIO output module is
executed with simulated time: initial four-second pulse, four half-second cycles
per repetition, replacement/RESET, queued callback cancellation, late-callback
phase correction and failure-to-LOW all pass. A conflicting GPIO33 diagnostic
configuration is rejected by a negative compile test.

Controller tests cover phase/index arithmetic, cap packing, final-logo completion,
pause in a logo, journal reload, legacy compatibility, fresh capability evidence,
prepared run/silence duration and timer-card interpolation. Analyzer/bitmap assets
and network identity settings are compared against the working baseline.

ESP-IDF target build, full Windows WinUI/XAML build and physical timing/output
verification remain pending. Host tests do not prove target SDK/IRAM integration
or hardware GPIO timing. No boards or serial ports were operated here.

## Bench sequence

1. Update controller and selected timers. Check boot version 6.24.6-rc1,
   COMMIT=GPIO17 and the GPIO33 output-ready log. Press RESET after updating.
2. Run 00:20 x 1. Measure GPIO33 HIGH for four seconds after acceptance; verify
   a four-second logo with four 500 ms ON/OFF cycles at countdown end, then LOW.
3. Run 00:20 x 3. Expect the table above, one segment, and automatic RESET after
   the final four-second period and diagnostics. Capture GPIO17 COMMIT separately
   from GPIO33 output. Production validator: `--segment-seconds 72`; expect 73
   boundaries per COMMIT channel and the existing <=30 ms production gate.
4. STOP during the initial pulse, during a countdown and during a logo: GPIO33
   must go LOW. Resume and RESET through the normal selected-timer controls.
5. Run 03:00 x 10 with cap 600, then test a network outage during a segment.
   Validate segment lengths 552,552,552,184 and that healthy timers continue.
