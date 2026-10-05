# v6.22.6 / v6.23.4 validation plan

## A. Build

```powershell
dotnet build FactoryTimer.slnx -c Release
dotnet test tests\FactoryTimer.Protocol.Tests -c Release
dotnet test tests\FactoryTimer.Controller.Core.Tests -c Release
```

Build and flash firmware v6.23.4 on ESP01 and ESP02.

## B. Telemetry smoke test

Run a normal 30 s countdown. Confirm the health CSV contains START and POST_RUN rows for both devices and non-empty `RtcAcceptedEdges`, `RtcInferredMissingEdges`, and `RtcHoldoverEntries`. Expect LOCKED, SQW core 1, zero queue drops, and no inferred-missing/holdover increase.

## C. Physical qualification-instrument validation

Warm both devices. With Analyzer_v8 attached, run START #1 as a 30:00 countdown. When it finishes, prepare START #2. Save both health CSVs and the analyzer log.

Run:

```powershell
python tools\qualify_rtc_batch.py health1.csv health2.csv --warm-confirmed --output rtc_pair.csv
python tools\validate_rtc_qualification_vs_analyzer.py health1.csv health2.csv Analyzer_v8.log --tolerance-ppm 0.05
```

Do not use the qualification tool to screen the new batch until this comparison passes.

## D. Scale without analyzer

After the instrument is physically validated, add devices 3 -> 5 -> 10 -> 15. Keep the devices warm for the batch two-START qualification, inspect/record the RTC package marking during installation, and save controller TX + health CSVs for every run. Reconnect Analyzer_v8 only if telemetry or visual behavior identifies a suspicious subset.
