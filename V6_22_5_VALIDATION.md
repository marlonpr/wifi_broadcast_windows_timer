# v6.22.5 validation performed in this environment

Performed:

- `tools/test_v6224_pooled_sync_policy.py` — 14/14 PASS
- `tools/test_v6225_health_telemetry.py` — 11/11 PASS
- `tools/test_v6225_qualification_math.py` — PASS
- deterministic synthetic 30-minute qualification replay — recovered +1/+3/+5 ppm and median-relative -2/0/+2 ppm exactly
- diff whitespace check against the v6.22.4 build-fix source — no whitespace errors

Not performed here:

- `dotnet build` / `dotnet test`, because the .NET SDK is not installed in this environment;
- physical hardware validation of the new fleet-health CSV path.

The v6.22.4 pooled med3/retry policy is intentionally unchanged. v6.22.5 adds STATUS parsing, health capture, explicit post-run health polling, CSV output, and the disciplined-rate batch qualification tool.
