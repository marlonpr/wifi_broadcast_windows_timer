# Factory Timer — Synthetic Path-Asymmetry Controls V9.2

## Frozen measurement fixture

Do not change device positions, analyzer placement, 1 kΩ START-edge series resistors,
twisted-pair returns, AP placement, or power during these controls.

Use fixed `8+8_BASELINE` sampling for all controls.

## Why V9.2 exists

The first ESP02 reverse +1 ms control exposed a firmware metrology fault.  The
reverse-delay UART `ESP_LOGI` ran after device T3 but before `sendto()`, adding about
4 ms of real reverse-path latency that was not included in `ActualReverseDelayUs`.
The physical sign result remains valid (reverse bias -> early START), but that run
must not be used as a quantitative +1 ms gain control.

V9.2 makes two changes:

1. The reverse-delay diagnostic log runs only after `SendPacket()` returns.
2. `ActualReverseDelayUs` is measured from T3 through a first packet-format pass,
   then written into a final packet immediately before send.  This is closer to the
   physical T3->send hold than the old busy-wait-only value.

No self-reported packet field can include the time required to format that same
field or the subsequent `sendto()` call.  Therefore the host-side standing check
below remains mandatory.

## Standing implementation checks

### 1. SyncQualityDeviationUs — primary go/no-go

For the injection target, before looking at analyzer endpoints:

- PASS: `|mean SyncQualityDeviationUs| <= 200 us`
- REVIEW: `200 us < |mean| <= 400 us`
- FAIL: `|mean| > 400 us`
- Any final synchronization rejected because of the synthetic injection: FAIL.

The hard operational acceptance band is ±400 us.  The ±200 us level is an early
warning, not a hard rejection threshold at n=30.

### 2. External reverse-path standing check

For every raw SYNC sample compute:

```
R = (MasterT4Us - DeviceT3Us) - ActualReverseDelayUs
```

`R` contains an arbitrary master/device clock offset, so never compare its absolute
value across devices.  Within the same device and short run, that offset cancels in:

```
U_i = median(R_i, CALIBRATION) - median(R_i, VERIFICATION)
```

For an injected target, compare `U_target` with the median `U` of the other four
devices.  A large target-only excess localizes an unreported hold to the reverse
path.  The analysis script emits this automatically.  The default operational
warning is 1000 us and is configurable; it is a diagnostic threshold, not an
inferential endpoint.

The broken first Control A produced approximately:

```
ESP01  -198 us
ESP02 +3971 us   <-- injected target
ESP03   -96 us
ESP04  -272 us
ESP05  -144 us
```

and ESP02 was about +4141 us above the median of the controls.

### 3. Reported delay check

For reverse controls, confirm the target's `ActualReverseDelayUs` is close to the
requested value.  In V9.2 it represents the firmware-side hold measured from T3
through a first packet-format pass, not merely the intentional wait loop.

For forward controls, use the Windows QPC-measured `ActualForwardDelayUs`.

## Physical statistic for synthetic controls

Do not fleet-center the injected device when measuring control gain.  If a single
device is shifted by delta, fleet centering attenuates its apparent shift to 4/5 of
delta.

Use:

```
C_i = t_i - mean(t_j for j != i)
```

and compare to a clean/no-injection baseline when estimating the injected gain:

```
Delta C_i = C_i(injected) - C_i(clean)
```

The four-device reference remains preferred; dropping the injected device from
other devices' references increased SE in the empirical check.

## Control matrix

| Control | Target | Injection | Role |
|---|---|---:|---|
| A | ESP02 / ESP32 | reverse +1 ms | quantitative small-signal gain; expect ~0.5 ms early |
| B | ESP02 / ESP32 | reverse +4 ms | quantitative gain; expect ~2 ms early |
| C | ESP03 / ESP32-S3 | reverse +4 ms | cross-silicon transfer check; expect ~2 ms early |
| D | ESP02 / ESP32 | forward +40 ms | coarse opposite-polarity / large-scale plumbing check; expect ~20 ms late |

Run about 30 trials per control.

### Important interpretation of Control D

D is **not** a quantitative linearity anchor.  A constant +40 ms forward delay
flattens the inverse-RTT² weights of the best-3 estimator, so the estimator operates
in a materially different weighting regime.  A and B carry the quantitative gain
evidence.  D checks opposite polarity and that the large-delay plumbing behaves in
the expected direction/order of magnitude.

## Admissibility of the failed first Control A

The original V9 Control A remains valid for its primary sign result:

- reverse-biased path produced an early physical START;
- the synchronization model tracked the real ~4.1 ms reverse excess correctly;
- the residual natural ESP02 bias remained consistent with the clean run.

Those conclusions depend on what physically occurred, not on the requested 1 ms
value.  The run must not be pooled into quantitative +1 ms gain estimates because
the injection metrology was wrong.

## Preregistered interpretation after controls

The synthetic controls must confirm:

1. reverse-biased injection -> early physical START;
2. forward-biased injection -> late physical START;
3. +4 ms reverse produces similar gain/sign on ESP02 and ESP03.

The subsequent 600-trial clean run is the replication test for the natural-path
hypothesis: fleet-centered P5 RTT floor vs B_i should be negative.  The loaded-
channel experiment remains required for causal attribution because silicon type and
clean-path floor are collinear in the five-device fleet.
