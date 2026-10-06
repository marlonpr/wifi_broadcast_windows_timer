# v6.22.9 — fleet CPU0 monitor telemetry

This controller revision is v6.22.8 plus parsing/logging support for the v6.23.11
42-field FCT2 STATUS packet.

Backward compatibility is retained for the existing 9/10/14/28/31-field STATUS
forms. The appended CPU0 counters are hexadecimal on the network wire and are parsed
to normal decimal values before entering controller state or CSV logs.

`controller_health_*.csv` appends:

- Cpu0MonitorValid
- Cpu0MonitorSamples
- Cpu0MonitorEventCount
- Cpu0MonitorWorstUs
- Cpu0MonitorWorstTask
- Cpu0CommitLateCount
- Cpu0CommitWorstUs
- Cpu0CommitOverlap
- Cpu0WrongCoreCallbacks
- Cpu0MonitorOverflow
- Cpu0InterruptLevelMatch

Use `tools/summarize_fleet_cpu0_monitor.py` against one or more health CSV files to
aggregate board-hours and monitor/COMMIT event counts by device.

Example:

```powershell
python tools\summarize_fleet_cpu0_monitor.py "controller_health_*.csv"
```
