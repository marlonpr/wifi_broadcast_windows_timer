# v6.22.6 — guarded DS3231 fleet qualification

This revision keeps the validated v6.22.4 pooled-forward synchronization and v6.22.5 health capture behavior unchanged. It adds validity guards around the two-START disciplined-rate measurement.

## Qualification observable

For each START, the firmware reports at the same selected sync sample:

```text
Q = Master - Disciplined
  = SyncSourceOffsetUs + SyncEpochLocalUs - SyncEpochDisciplinedUs
```

Between two warm STARTs:

```text
DisciplinedMinusMasterPpm = -(Q2 - Q1) / (M2 - M1) * 1e6
```

The raw `Master - Local` slope is not used for RTC qualification.

## New continuity fields

The health CSV now records:

- `RtcAcceptedEdges`
- `RtcInferredMissingEdges`
- `RtcHoldoverEntries`

They are cumulative since boot.

## Validity guards

A pair is valid only when all of the following hold:

1. Both START endpoints are `LOCKED`, have >=64 fit points, fit RMS <=3 us, zero queue drops, valid temperature, SQW core 1, and valid sync-epoch telemetry.
2. The interval is long enough (`--min-interval-seconds`, default 1200 s; 1800 s recommended).
3. Same boot: the change in `SyncEpochLocalUs` tracks the master sync-epoch interval within `--same-boot-tolerance-ppm` (default 100 ppm).
4. `RtcAcceptedEdges` advances at approximately 1 Hz over the health-capture interval (`--accepted-edge-tolerance`, default +/-2 edges).
5. `RtcInferredMissingEdges` does not increase.
6. `RtcHoldoverEntries` does not increase.
7. The operator explicitly supplies `--warm-confirmed`; two endpoint snapshots alone cannot prove thermal equilibrium.

Only valid devices contribute to the batch median. Invalid pairs are printed with the exact failed guard and the command exits nonzero.

Example:

```powershell
python tools\qualify_rtc_batch.py health_start1.csv health_start2.csv --warm-confirmed --output rtc_batch_qualification.csv
```

No DS3231 ppm acceptance threshold is hard-coded. Use the observed batch distribution and the system requirement.

## Hardware marking check

The room-temperature two-START rate test only flags a module that is measurably off during the test. It cannot prove that a package marked/constructed as DS3231M will meet TCXO behavior over temperature or aging if it happens to be accurate at room temperature. Inspect and record the RTC package marking as each module is installed.
