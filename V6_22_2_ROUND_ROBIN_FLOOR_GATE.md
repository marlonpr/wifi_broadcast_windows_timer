# v6.22.2 controller-only synchronization hardening

Firmware remains **v6.22**.

Production START synchronization changes in the Windows controller:

1. **med3 forward-floor estimator** remains the estimator:
   - take the three largest `t1_master_us - ingress_local_us` values;
   - use the second-fastest value and that same sample's ingress timestamp as the offset epoch.

2. **150 us top-3 spread gate**:
   - calibration top-3 spread must be <= 150 us;
   - verification top-3 spread must be <= 150 us;
   - final 16-sample top-3 spread must be <= 150 us;
   - the existing +/-3 ms calibration-vs-verification gate remains as an independent coarse guard.

3. **Retry behavior**:
   - normal first attempt is collected fleet-wide in rotating round-robin rounds;
   - if one device loses samples, exceeds the spread gate, exceeds the existing quality gate, or has a retryable SYNC_SET transport failure, only that device is retried with a fresh 8+8 block;
   - attempts 2..5 use the same med3 + 150 us spread rule;
   - failure after the retry budget blocks START rather than accepting a thin floor.

4. **Rotating round-robin first attempt**:
   - 8 calibration rounds + 8 verification rounds;
   - one request per selected timer per round;
   - 2 ms quiet spacing after each completed exchange;
   - the first device rotates every round, so AP queue position is not permanently attached to one timer;
   - Normal / Reverse / Alternate now choose only the base order used to seed the rotations.

5. **Unchanged**:
   - v6.22 firmware and SYNC_SET epoch semantics;
   - disciplined sync-to-START propagation;
   - +/-3 ms readiness gate;
   - START_AT / ACK / final barrier;
   - production-silent RUNNING;
   - manual/benchmark four-timestamp synchronization;
   - SQW ISR core and SQW fit filter (these remain a separate later change).

## Why 150 us

Across the 32 device-sync traces reviewed before this build, 30 had top-3 spread <= 77 us.
The two known thin-floor cases were about 171 us and 230 us. A 150 us threshold separates
those observed cases without tightening the ordinary population.

## Final synchronization stress test

Before freezing synchronization, run several successful 60 s STARTs while deliberately loading
the same AP (for example, sustained video/network traffic from another client).

Judge the session using separate location and scatter metrics:

- session mean: `|mean lateness-corrected b0| <= 50 us`;
- run-to-run scatter: `SD <= 25 us`;
- every accepted device sync must have calibration, verification and final top-3 spread <= 150 us;
- thin-floor attempts should retry; if the retry budget cannot obtain a supported floor, START must abort;
- `prop` should remain approximately 0 us;
- `frame_not_ready = 0`;
- Analyzer dropped edges = 0.

The purpose of the loaded-AP test is tail behavior, not improving already-invisible tens-of-us precision.
