# Controller/tooling 6.22.18: frozen qualification policy

Firmware remains 6.23.19 and Analyzer remains 10.1. This release changes the
controller, qualification tools and monitor-only Python logger. It adds no
STATUS fields and does not change either device's binary source.

## START diagnostics

Production START displays the worst board-reported `StartErrorUs` and
`SchedulerLatenessUs` from RUNNING/FINISHED STATUS for the current command.
Until that STATUS arrives, the values are PENDING. STARTED alone cannot prove
the scheduler woke on time: its timestamps can describe the scheduled target.

`SyncUncertaintyUs` is explicitly an estimate and names the board with the
largest estimate. `UncorrectedLocalClockPhaseAtTStarUs` retains the old raw
local-clock projection per board, labeled informational only. It is not a
physical START error or analyzer measurement and never enters the readiness,
degraded-sync or START ANYWAY decision. Controlled benchmark/manual experiment
CSV calculations remain separate from the production display.

## Warm-up rule, frozen before START #1

Every selected/frozen device, including devices without serial captures, needs:

- LOCKED, DiagnosticFlags=0 and a full 129-point fit.
- Latest fit RMS <=1.0 us and three consecutive good normal discovery polls.
- At least 600 s of controller history using only full 129-point fits.
- Absolute rate change from the most recent sample at/before latest−600 s
  <=0.25 ppm. At the 16 s cadence, the first full history is normally 608 s.
- Monotonic accepted edges, unchanged missing/holdover counters, zero queue
  drops since boot and valid temperature.

Bad RMS resets the good-poll streak while retaining the rate history. Partial
fits, reboot/build change, lock loss, SQW faults or a >=48 s polling gap restart
the history. Manual/post-sync STATUS cannot manufacture three normal polls.
The existing post-sync history refresh and final ARM continuation checks remain.

Health and warm-up CSVs record `WarmupMaxRateChangePpm=0.25`,
`WarmupRequiredFitPoints=129` and a process-specific `ControllerSessionId`.
Warm-up CSVs also include each sample's `RtcFitPoints`. Modern qualification
files must carry that policy/proof; `--warm-confirmed` cannot supply it.

The archived `rtc_warmup_gate.py` is an offline serial inspection aid. It uses
the revised full-fit/history/RMS/rate criteria, with trend and temperature span
informational. It prints `SERIAL_WARMUP_CHECK` and directs qualification to the
controller STATUS gate. Serial logs cannot prove flags or the whole frozen fleet.

## Verify attaching a logger did not reset a warm board

Read this board's `accepted=` count from a fresh controller STATUS/warm-up row
immediately before attaching. Substitute its actual build, port and count:

```powershell
cd .\firmware_v6_23_16
python .\tools\monitor_board.py --board ESP01 --build .\build-esp01 --port COM14 --accepted-before-open 12000 --log .\ESP01.log
```

The port is constructed unopened. DTR, RTS, RTS/CTS flow control and DSR/DTR
flow control are disabled before `open()`. The first complete RTC_QUAL must
match the requested board and have accepted edges >= the pre-open count.
Success reports both counts and `PORT_OPEN_CONTINUITY=VERIFIED`. A decreased
first count aborts with `PORT_OPEN_RESET_DETECTED`; a later decrease also aborts.
Without `--accepted-before-open`, identity can be verified, but open continuity
is explicitly UNVERIFIED. `--check-only` checks the build/ELF, not the live port.

This comparison is the hardware authority: modem-control settings alone do not
prove a USB driver avoided a reset pulse. A board running for hours should still
show thousands of accepted edges, not 16/32. After any reset, obtain fresh
controller history. The included serial mocks verify the software behavior,
not the physical adapter.

For an ESP-IDF monitor alternative, use the matching board's build and a
monitor-only command with `--no-reset`:

```powershell
idf.py -B build-esp01 -p COM14 monitor --no-reset
```

Do not combine this with `flash` on a warm qualification participant. Verify
accepted-edge continuity with this monitor too. The controller's analyzer
SerialPort already disables DTR/RTS and sets Handshake.None before opening.

## Long-run order and validation

1. Finish the normal production 30 s preflight and confirm zero diagnostic flags.
2. Open every board logger and the analyzer port; verify live identity and
   accepted-edge continuity. Open all capture ports before collecting warm-up.
3. Press RESTART WARM-UP HISTORY after all ports are stable. Keep the same
   controller process and network interface open. Wait for all five READY.
4. Start one analyzer capture with CH1 ESP01, CH2 ESP04, CH3 ESP03. Set >=40:00
   and press START #1 immediately when READY.
5. After completion, press START #2 within the existing 120 s continuation
   window. Keep all ports and the controller open; do not RESET between STARTs.
6. Keep the analyzer recording through the physical second START, then send
   END/save. Save both health CSVs and their warm-up evidence.
7. Run the two-START batch and three-pair analyzer validators with a minimum
   2400 s interval (both now default to 2400 s).

```powershell
python .\controller_v6_22_13\tools\qualify_rtc_batch.py .\first_health.csv .\second_health.csv --min-interval-seconds 2400
python .\controller_v6_22_13\tools\validate_rtc_qualification_vs_analyzer.py .\first_health.csv .\second_health.csv .\analyzer.txt --warm-confirmed --min-interval-seconds 2400 --devices ESP01 ESP04 ESP03
```

Both validators audit every frozen endpoint, including unwired ESP02/ESP05.
They require unchanged/nonempty firmware build IDs; unchanged controller
session for modern files; full fits and endpoint health; DiagnosticFlags=0;
accepted-edge increase within ±2 of elapsed **controller capture** seconds;
unchanged missing/holdover counters; and zero queue drops. Legacy CSVs without
controller warm-up columns still need explicit `--warm-confirmed`, full fits
and build continuity; they do not provide modern controller-session proof.

`RtcRateChangeSinceStart1Ppm`, the endpoint local-crystal movement, and approximate
fit-lag bias remain informational. They cannot make a qualification FAIL or
prevent START #2. `--max-rate-change-ppm` is retained as an informational band
only. This does not relax the disciplined RTC module or physical slope gates.

The analyzer reports all three raw slope errors plus the measured change of
physical START offset divided by the interval and the sync-corrected residual.
The default raw verdict remains <=0.05 ppm. The corrected residual explains a
narrow raw FAIL; it does not change that verdict automatically. A qualification
using `--gate sync-corrected` must select that policy before collecting data.
Long-train range growth from RTC drift is a measurement, not start degradation.

Monitor validation retains the exact elapsed grid convention and permits only
`abs(sample_callbacks + missed_periods - expected_periods) <=1` at shutdown.

## PC checks still required

Build the Windows controller and run its included .NET tests. The portable
suite executes Python tools and native firmware/analyzer host checks, and
checks C# integration source; it does not execute C#. Test accepted-edge
continuity on the actual USB adapters before starting the warm-up clock.
