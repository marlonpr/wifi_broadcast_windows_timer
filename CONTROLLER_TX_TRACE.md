# Controller TX timing trace for DS3231 qualification

This controller source is based on the latest ZEIT-branded Factory Timer source package.

## What changed

`UdpControllerService` now timestamps every successful application UDP send immediately before `SendAsync` using the same `MasterClock` / QPC domain used to construct T*.

During a normal production START run, `MainViewModel` begins an in-memory trace as soon as the frozen `PreparedStartRun` (and therefore T*) exists. It captures START_AT arm traffic and all later application traffic through `T* + duration + 2 s`.

No disk I/O occurs in the packet send path. At the end of the countdown the trace is written to:

```text
Documents\FactoryTimer\TimingQualification\controller_tx_YYYYMMDD-HHMMSS_<command>.csv
```

The app shows the final path in the Connection card.

## CSV fields

```text
RunCommandId
TStarMasterUs
DurationSeconds
PacketMasterUs
DeltaFromTStarUs
PhaseUs
LeadToNextBoundaryUs
PacketType
Destination
Attempt
Bytes
CorrelationId
```

`PhaseUs` is `(send - T*) mod 1 s`.

`LeadToNextBoundaryUs` is the corresponding lead to the next one-second boundary. A send 3 ms before a boundary is therefore recorded near `LeadToNextBoundaryUs=3000`.

The polling cadence is intentionally **unchanged** in this diagnostic build. First establish causality; only then move polling to mid-second if application STATUS traffic is responsible.

## 30-minute A/B/A experiment

1. Use v6.6 on the timers for the baseline causal run.
2. Wait until both devices pass the objective `rate_ppm` stability gate.
3. Arm analyzer after RESET.
4. Start 30:00 from this controller.
5. Minutes 0–10: PC network connected.
6. Minutes 10–20: disconnect only the controller PC from the LAN. Keep ESP devices and analyzer running.
7. Minutes 20–30: reconnect the PC.
8. Save both ESP serial logs and analyzer serial capture.
9. Use `tools/analyze_30min_qualification.py` with the generated controller TX CSV.

Interpretation:

- Late commit + app TX immediately before the boundary: application traffic candidate.
- Late commits vanish while PC is disconnected but no app TX aligns: Windows/background PC traffic candidate.
- Late commits persist while PC is disconnected: AP / other LAN host / local device cause.

## In-run polling mode

During an active traced production run, periodic STATUS polling is now sent by unicast to the frozen participant IPs instead of to the subnet broadcast address. Outside a run, ordinary broadcast discovery resumes automatically. This makes controller send time useful for packet-arrival correlation and avoids the simultaneous reply burst caused by broadcast STATUS_REQUEST.
