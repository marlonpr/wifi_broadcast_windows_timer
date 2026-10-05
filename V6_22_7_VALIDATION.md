# v6.22.7 validation performed in this environment

The following checks were actually run here:

- `tools/test_v6224_pooled_sync_policy.py`: PASS (14/14 source-contract checks)
- `tools/test_v6225_health_telemetry.py`: PASS (11/11 source-contract checks)
- `tools/test_v6225_qualification_math.py`: PASS
- `tools/test_v6226_qualification_guards.py`: PASS
- `tools/test_v6227_rate_stability_guard.py`: PASS
- `python -m py_compile` on both qualification tools: PASS
- synthetic two-device 1800 s qualification: PASS
- synthetic Analyzer-vs-tool 1801-boundary comparison: PASS

The v6.22.7 rate guard was specifically checked at the boundary:

```text
DeltaRate = +0.250 ppm -> valid
DeltaRate = +0.251 ppm -> invalid
64 s * 0.250 ppm = 16 us estimated phase bias
16 us / 1800 s = 0.008889 ppm estimated slope bias
```

Not run in this environment:

- `dotnet build/test` (SDK unavailable here)
- ESP-IDF target build (firmware is unchanged v6.23.4)
- real 30-minute Analyzer_v8 hardware validation
