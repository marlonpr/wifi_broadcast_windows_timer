# v6.22.7 / v6.23.4 qualification validation plan

Firmware remains v6.23.4. Controller runtime synchronization remains v6.22.4 pooled med3; v6.22.7 changes only qualification tooling/version labeling.

## Windows build

```powershell
dotnet build FactoryTimer.slnx -c Release
dotnet test tests\FactoryTimer.Protocol.Tests -c Release
dotnet test tests\FactoryTimer.Controller.Core.Tests -c Release
```

## Short telemetry smoke test

Run one 30 s countdown and confirm the health CSV contains START and POST_RUN rows for every participant, including `RtcRatePpmVsRtc`, the disciplined sync epoch, accepted-edge/missing/holdover counters, and display-health summary.

## Physical qualification-instrument validation

1. Warm ESP01 and ESP02 until both are stably LOCKED.
2. Keep Analyzer_v8 attached.
3. START #1: 30:00 countdown.
4. Keep Analyzer_v8 recording all 1801 COMMIT boundaries.
5. Immediately after the countdown finishes, perform START #2.
6. Save both health CSVs and the analyzer log.

```powershell
python tools\qualify_rtc_batch.py health_start1.csv health_start2.csv `
    --warm-confirmed `
    --output rtc_pair.csv

python tools\validate_rtc_qualification_vs_analyzer.py `
    health_start1.csv health_start2.csv Analyzer_v8.log `
    --device-a ESP01 --device-b ESP02 `
    --warm-confirmed `
    --tolerance-ppm 0.05
```

Required conditions per device include:

- same boot;
- LOCKED/healthy endpoints;
- SQW continuity with no holdover or inferred-missing increment;
- `|RtcRatePpmVsRtc(START2)-RtcRatePpmVsRtc(START1)| <= 0.25 ppm`;
- procedural `--warm-confirmed`.

At 1800 s, the 0.25 ppm rate-change guard bounds the approximate 64 s fit-lag slope bias to 0.0089 ppm.

Physical pass criterion:

```text
|Analyzer pair slope - qualification pair slope| <= 0.05 ppm
Analyzer dropped = 0
>= 1200 contiguous 1-Hz COMMIT boundaries (1801 expected for 30:00)
```
