# v6.22.7 — automatic rate-stability warm guard

This controller/tooling-only revision keeps firmware v6.23.4 and the validated v6.22.4 pooled synchronization path unchanged.

The two-START DS3231 qualification now requires both the procedural `--warm-confirmed` flag and a data-derived endpoint-rate stability guard.

## Guard

The RTC discipline fit uses a 128 s window, whose effective center is approximately 64 s behind the current endpoint. If the fitted local-vs-RTC rate changes by `DeltaRatePpm` between START #1 and START #2, the residual phase bias from fit lag is approximately:

```text
phase_bias_us ~= 64 s * DeltaRatePpm
slope_bias_ppm ~= phase_bias_us / qualification_interval_s
```

For a 1800 s interval and the default `|DeltaRatePpm| <= 0.25 ppm`:

```text
|phase_bias| <= 16 us
|slope_bias| <= 0.0089 ppm
```

A pair whose endpoint rate changes by more than 0.25 ppm is `INVALID`, even if the operator supplied `--warm-confirmed`. This catches a board that was powered late and is still on its warm-up ramp.

The output CSV adds `RateStabilityOK`, `RtcRateDeltaPpm`, `MaxRateChangePpm`, `FitLagSeconds`, `EstimatedFitLagPhaseBiasUs`, and `EstimatedFitLagSlopeBiasPpm`.

Example:

```powershell
python tools\qualify_rtc_batch.py health_start1.csv health_start2.csv --warm-confirmed --output rtc_batch_qualification.csv
```

The default 0.25 ppm limit can be changed with `--max-rate-change-ppm`, but production qualification should keep the default until physical validation indicates otherwise.
