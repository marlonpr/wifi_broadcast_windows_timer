# BG-1 — Background clock model in shadow mode

BG-1 adds continuous clock-model acquisition without changing the proven foreground
`SYNC CLOCKS -> START_AT` behavior.

## Frozen behavior

The existing foreground path remains authoritative:

- `BLOCKING_THREAD_T4` remains the production receive timestamp path.
- Foreground synchronization keeps its selected sample profile, quality gate, retry
  policy, `SYNC_SET`, and `START_AT` behavior.
- BG-1 never sends `SYNC_SET`.
- BG-1 never marks a device synchronized in the UI and its fitted rate is never used
  to schedule a countdown.
- Foreground synchronization, benchmark, START/RESET and timing quiet periods take
  priority over BG-1. A foreground action actively cancels an in-flight shadow sample
  and takes the shared send lock; BG-1 never disables the operator controls while it
  is sampling. Missed or preempted background slots are skipped rather than burst-replayed.

This makes BG-1 a measurement-only milestone.

## Background acquisition

Each device is scheduled once per **60 s**. The five devices are staggered, so one
maintenance slot occurs approximately every 12 s. The scheduler is structured so a
15-device fleet can keep the same 60 s per-device cadence by shortening the slot
spacing rather than changing the model cadence.

One observation consists of:

1. 8 ordinary delay-free `SYNC` exchanges;
2. the unchanged best-3 low-RTT `1/RTT^2` estimator;
3. a weighted effective Master epoch formed with the same estimator weights;
4. optional die-temperature telemetry requested only by BG-1 shadow packets.

Temperature is opt-in at the protocol level (`...|0|TEMP`), so ordinary foreground
SYNC packets retain their previous byte-for-byte form and timing. ESP32-S3 builds use
the supported ESP-IDF temperature-sensor driver. Targets without that telemetry leave
the CSV temperature field blank.

A >100 ms step in the estimated Master-minus-local offset resets that device's
in-memory fit history. This is a reboot/discontinuity guard: a reset of `esp_timer`
must never be interpreted as oscillator rate.

## Model

The live shadow model fits

```text
offset(t) = a + rate * (t - t0)
```

over the most recent 30 minutes. Timestamp arithmetic is centered before least
squares so large absolute monotonic epochs do not reduce floating-point precision.
Slope units are directly microseconds/second, i.e. ppm.

The statistical candidate gate is frozen at:

```text
observation span >= 10 min
|rate| / SE(rate) >= 3
```

Passing this gate produces `RATE_CANDIDATE`, not `RATE_QUALIFIED`. The additional
predictive-benefit gate remains false throughout BG-1 and can only be enabled after
offline causal replay shows that rate correction actually lowers prediction MSE.

## Logs

The controller writes two files under:

```text
Documents\FactoryTimerBackgroundClock
```

`factory_timer_background_clock_<run>.csv` contains one row per accepted background
observation, including effective epoch, offset, RTT diagnostics, temperature, fitted
rate, slope SE/SNR, fit span, fit residual SD, exact mean-prediction SE, and model
state.

`factory_timer_background_clock_samples_<run>.csv` preserves all eight raw NTP-style
samples per observation and flags the three samples selected by the estimator.

## Offline validation

Run:

```powershell
python tools\analyze_background_clock.py `
  "$env:USERPROFILE\Documents\FactoryTimerBackgroundClock\factory_timer_background_clock_<run>.csv" `
  --reference-noise-us 402 `
  --scores-csv bg1_replay_scores.csv
```

The replay is strictly causal: a model at cutoff `t0` is fit only from observations
at or before `t0`, then scored against later observations at 60, 120, 180, 300, 600
and 900 s horizons.

The primary comparison is:

```text
DeltaMSE(h) = MSE_offset_only(h) - MSE_rate_corrected(h)
```

Both models are scored against the same noisy future reference, so the future
reference variance cancels in `DeltaMSE`. Positive values favor the rate model.

For absolute prediction variance the script also reports:

```text
MSE_observed(h) - sigma_reference^2
```

using the independently measured reference noise (currently about 402 us SD). The
subtracted variance is intentionally allowed to remain slightly negative in finite
samples rather than being silently clipped.

The same script also emits an overlapping phase-ADEV table at cadence multiples
(roughly 60, 120, 240, 480, 960 s by default). Long foreground pauses are excluded
from ADEV triples rather than treated as oscillator noise. This turns the first
overnight BG-1 run into the previously planned soak/ADEV experiment automatically.

## Physical regression gate

After flashing the BG-1 firmware/controller build, rerun the frozen 30-trial analyzer
regression. Primary/hard checks are:

```text
median physical fleet spread <= 1.7 ms
30/30 analyzer summaries == 0x1F
150/150 device synchronizations accepted
scheduler lateness <= 1 us
```

Record P95 physical spread as a secondary trend statistic with a loose ~4 ms alarm;
at n=30 it is too noisy to serve as the primary regression metric.

The automatic benchmark owns the foreground send lock, so BG-1 intentionally skips
maintenance slots while that timing-critical benchmark is active. This is the desired
production arbitration policy, not a missing test path.

## Pre-flash review clarifications

- `BackgroundClockFit.PredictOffsetMicroseconds()` applies `RatePpm` only in
  `RATE_QUALIFIED`. `RATE_CANDIDATE` retains the regression-smoothed endpoint
  estimate but freezes it at the reference epoch.
- Offline replay uses two independent arms: both share that fitted endpoint at
  cutoff `t0`; offset-only forces the future slope to zero, while rate-corrected
  extrapolates the fitted slope.
- The otherwise unreachable-in-live-BG1 `RATE_CANDIDATE -> RATE_QUALIFIED`
  transition is unit-tested with `predictiveBenefitConfirmed=true`. Live BG-1
  intentionally keeps that flag false until offline replay establishes benefit.
- `ExchangeSyncAsync` removes its `SyncId` waiter in a `finally` block. If a
  background exchange is cancelled, any late UDP reply therefore has no waiter
  to satisfy and cannot populate a later exchange. Foreground preemptions are
  counted per device and exported in `PreemptionsTotal` and
  `PreemptionsSincePreviousObservation`.

### Qualification arithmetic

With exact 60 s spacing, a 30 minute inclusive window contains about 31
observations. For residual SD `sigma = 830 us`,
`Sxx = 8,928,000 s^2`, so the expected slope SE is approximately
`830 / sqrt(Sxx) = 0.278 ppm`. The previously quoted 0.58 ppm corresponds more
closely to a ~15 minute / ~30-observation example, not this 30 minute / 60 s
policy. At the full window the approximate SNRs are ESP01 12.5, ESP02 12.6,
ESP03 122, ESP04 1.8, and ESP05 21.5. The policy verdict is unchanged: ESP04
should remain statistically unqualified at the measured noise level. At the
10 minute minimum span, ESP03 and ESP05 are already expected to pass SNR 3;
ESP01 and ESP02 reach it at roughly 11 minutes for sigma near 830 us.
