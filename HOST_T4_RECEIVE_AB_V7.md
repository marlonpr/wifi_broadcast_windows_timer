# Windows T4 Receive-Path A/B Experiment (v7)

This build isolates one question: does the Windows async receive continuation add measurable delay to NTP-style `t4`?

No ESP32 firmware change is required. The clock estimator, 8+8 samples, inverse-RTT² best-3 estimator, ±3 ms quality gate, retries, START_AT behavior, and protocol are unchanged from v6.

## Receive modes

The controller UI adds **Master receive timestamp path**:

- `ASYNC_AWAIT` — v6 baseline. `t4` is captured immediately after `await UdpClient.ReceiveAsync(...)` resumes.
- `BLOCKING_THREAD_T4` — experimental path. A dedicated OS thread at `ThreadPriority.AboveNormal` blocks in `Socket.ReceiveFrom(...)` and captures `MasterClock.NowMicroseconds` immediately after the socket call returns, before packet parsing, Task scheduling, or UI dispatch.

Changing the receive mode recreates the selected UDP socket and invalidates the previous clock synchronization. Press **SYNC CLOCKS** again before START_AT.

Benchmark CSVs keep the existing schema. The `SyncMode` field now includes the receive path, for example:

`NONE (0/0 ms) | RX=ASYNC_AWAIT`

or

`NONE (0/0 ms) | RX=BLOCKING_THREAD_T4`

The raw sync CSV uses the same label, so every calibration/verification sample is self-identifying.

## Test design

Freeze all ESP positions, orientations, power sources, AP, PC, selected Windows interface, and firmware.

Run interleaved 10-trial blocks:

1. `ASYNC_AWAIT` — 10 trials
2. `BLOCKING_THREAD_T4` — 10 trials
3. `ASYNC_AWAIT` — 10 trials
4. `BLOCKING_THREAD_T4` — 10 trials
5. `ASYNC_AWAIT` — 10 trials
6. `BLOCKING_THREAD_T4` — 10 trials

Do not change GC settings, ThreadPool minimums, retry spacing, estimator, sample counts, or ESP firmware during this A/B. Those would make the host-receive effect non-identifiable.

## Primary analysis

Use raw attempt-1 samples first. Compare each blocking block against its adjacent async block, then pool the three A and three B blocks.

Primary metrics:

- RTT median / P90 / P95 / P99
- forward excess = `(t2 - t1)` after accounting for clock offset only when using a suitable within-sample/asymmetry analysis
- reverse-vs-forward asymmetry using the existing four timestamps
- offset dispersion within each 8-sample phase
- first-attempt quality-gate pass rate
- fleet synchronization duration

The key signal is not merely lower total RTT. A host-side T4 scheduling delay should preferentially reduce the apparent reverse-path excess and improve offset stability.

## Interpretation

- If `BLOCKING_THREAD_T4` repeatedly reduces reverse excess / asymmetry while the ESP-side `t3-t2` turnaround stays unchanged, Windows receive scheduling was contaminating T4.
- If RTT and asymmetry are indistinguishable between modes, the large reverse tail is more likely below the application receive continuation (network/AP/device path).
- If only one block improves and the four control devices move with it, treat it as ambient Wi-Fi variation rather than a receive-mode effect.

## Deliberately not included yet

`ThreadPool.SetMinThreads(...)` and `GCSettings.LatencyMode = SustainedLowLatency` are intentionally excluded from this version. If the dedicated blocking receiver wins, those can be tested separately instead of bundling multiple host changes into one treatment.
