# v6.22.8 validation performed in this environment

## Automated checks

- v6.22.4 pooled-sync policy: PASS (14/14)
- v6.22.5/v6.22.6 health telemetry: PASS
- disciplined-rate math: PASS
- v6.22.6 continuity guards: PASS
- v6.22.7 START1->START2 rate-stability guard: PASS
- v6.22.8 10-minute warm-up gate tests: PASS
- v6.22.8 module-acceptance boundary tests: PASS
- Python compile checks across `tools/*.py`: PASS

## Real ESP01/ESP02 validation replay

Using the physically validated 2026-10-05 pair:

```text
ESP01  D-master +11.306967 ppm  delta_median +0.040291 ppm  dRate +0.232167 ppm  PASS  ACCEPT
ESP02  D-master +11.226384 ppm  delta_median -0.040291 ppm  dRate +0.158593 ppm  PASS  ACCEPT
batch median +11.266675 ppm
```

Analyzer replay:

```text
ANALYZER_PAIR_SLOPE_PPM=+0.078282
TOOL_EXPECTED_ESP02_MINUS_ESP01_PHASE_SLOPE_PPM=+0.080583
TOOL_MINUS_ANALYZER_ERROR_PPM=+0.002301
TOLERANCE_PPM=0.050000
RESULT=PASS
```

The sign of `TOOL_MINUS_ANALYZER_ERROR_PPM` is corrected in v6.22.8; the magnitude
and pass/fail decision are unchanged from v6.22.7.

## Real START #2 warm-up replay

Running the stricter warm-up gate against the available START #2 serial captures
correctly returns `WAIT`:

```text
ESP01: trend +0.006862 ppm/min; dRate +0.041592 ppm; temp span 0.50 C -> WAIT
ESP02: trend +0.009084 ppm/min; dRate +0.119969 ppm; temp span 0.50 C -> WAIT
```

This is the intended behavior: the new gate would have prevented START #1 while the
RTC package temperatures/rates were still moving.

## Not run here

The .NET SDK is not installed in this environment. Controller runtime source is
unchanged by v6.22.8, but run the normal Windows build/test commands before use:

```powershell
dotnet build FactoryTimer.slnx -c Release
dotnet test tests\FactoryTimer.Protocol.Tests -c Release
dotnet test tests\FactoryTimer.Controller.Core.Tests -c Release
```
