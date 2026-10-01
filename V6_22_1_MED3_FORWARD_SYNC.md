# v6.22.1 controller-only forward-sync estimator refinement

Firmware remains **v6.22**.

Production START changes only the forward-floor estimator used by the Windows controller:

- v6.22: arithmetic mean of the three largest `t1_master_us - ingress_local_us` values.
- v6.22.1: median of those three values, i.e. the **second-fastest** forward-ingress sample.
- The estimator epoch is the ingress timestamp of that same second-fastest sample.
- Calibration, verification and final 16-sample consensus all use the same med3 rule.
- Existing ±3 ms calibration-vs-verification quality gate is unchanged.
- `SYNC_SET` and disciplined sync-to-START propagation are unchanged.
- Manual/benchmark four-timestamp synchronization is unchanged.
- No new top-3 dispersion gate is added yet; spread remains diagnostic.

Post-v6.22 evidence:
- 10-run actual mean3 b0 range: -29..+72 us, RMS 27.6 us.
- Same 10 runs evaluated as med3: -22.4..+20.5 us, RMS 15.9 us.
- The +72 us mean3 run had ESP02 top-3 spread ~230 us; med3 predicts ~+13.7 us.
- Across 15 auditable starts (5 pre-build + 10 post-build), med3 mean bias is ~0 us with max absolute residual ~25.9 us.

Recommended confirmation before the SQW-core change:
5 successful 60 s starts with Alternate sync order and Analyzer v8.
Acceptance target: lateness-corrected |b0| <= 50 us on all 5.
