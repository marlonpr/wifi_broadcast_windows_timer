# v6.22.10 — Fleet monitor validation and exposure accounting

The on-wire protocol is unchanged from v6.22.9. This controller revision adds two rollout safeguards.

1. `StatusFieldCount` is written to every health CSV row. A device still sending an older 9/10/14/28/31-field STATUS is therefore explicitly visible even though the parser remains backward compatible.
2. Fleet monitored hours are derived from `Cpu0MonitorSamples / 4000 / 3600`, not requested countdown duration. Rows with a missing/invalid monitor contribute zero monitored time. The summarizer also flags rows whose observed sample rate differs from 4000 Hz by more than 0.5% by default.

Use:
```powershell
python tools\summarize_fleet_cpu0_monitor.py "controller_health_*.csv"
```

For the one-board telemetry canary run:
```powershell
python tools\validate_cpu0_telemetry_canary_csv.py controller_health_....csv --device ESP01
```
