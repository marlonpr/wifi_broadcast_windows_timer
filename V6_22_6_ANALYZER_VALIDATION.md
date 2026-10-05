# v6.22.6 — first physical validation of the qualification instrument

Before using the two-START tool to judge new RTC modules, validate it against Analyzer_v8 on the already-characterized ESP01/ESP02 pair.

## Procedure

1. Warm both devices until the RTC fits are stable and `LOCKED`.
2. Start Analyzer_v8.
3. Make START #1 with a 30:00 countdown and save `controller_health_...csv`.
4. Leave Analyzer_v8 recording for the full 30 minutes.
5. When the countdown finishes, immediately prepare START #2 and save its health CSV. The second countdown duration is irrelevant; only its START sync epoch is needed.
6. Run:

```powershell
python tools\validate_rtc_qualification_vs_analyzer.py health_start1.csv health_start2.csv Analyzer_v8.log --device-a ESP01 --device-b ESP02 --tolerance-ppm 0.05
```

The analyzer fits the physical slope of `ESP02_COMMIT - ESP01_COMMIT`. The network qualification predicts that slope as:

```text
DisciplinedMinusMasterPpm(ESP01) - DisciplinedMinusMasterPpm(ESP02)
```

Pass criterion for this instrument-validation run: absolute disagreement <=0.05 ppm, Analyzer `dropped=0`, and at least 1200 contiguous one-second COMMIT boundaries (1801 expected for 30:00).

The previous grounded runs measured about 0.24-0.25 ppm for this pair, so a result in that neighborhood is expected, but the pass/fail comparison is against the simultaneous analyzer capture, not against the historical number.
