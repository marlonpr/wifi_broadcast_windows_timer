# Factory Timer Protocol — FCT1 + FCT2 synchronization extension

The controller and timers use printable ASCII UDP datagrams on port `5000`.
FCT1 remains valid for legacy relative START, RESET, STATUS_REQUEST, ACK, and STATUS.
FCT2 adds clock synchronization and absolute-time START scheduling.

## Clock domains

The Windows controller owns a monotonic **Master clock** measured in microseconds from controller startup.
Each ESP32 keeps its native `esp_timer_get_time()` clock. ESP timers are never reset.
After synchronization, each device stores:

```text
MasterTime = EspLocalTime + master_minus_local_offset_us
```

A positive offset therefore means the Master clock's numeric value is ahead of that ESP's local timer.

## NTP-style synchronization exchange

### SYNC request

```text
FCT2|SYNC|0123456789ABCDEF|100000
FCT2|SYNC|0123456789ABCDEF|100000|250000
```

Fields: version, packet type, sync ID, Master transmit timestamp `t1` in microseconds, and optional artificial ESP→Master reply delay in microseconds. The original four-field form remains valid and means zero artificial reply delay.

The ESP captures `t2` immediately after `recvfrom()` returns and captures `t3` before the optional artificial reply delay. This is deliberate: the injected reverse delay must appear in `(t4-t3)` rather than being hidden as endpoint processing time.

### SYNC_REPLY

```text
FCT2|SYNC_REPLY|ESP01|0123456789ABCDEF|100000|40004|40006
```

Fields: version, packet type, device ID, sync ID, echoed Master `t1`, ESP receive `t2`, ESP transmit `t3`.
The Master captures `t4` as soon as the reply is received.

The Master estimates:

```text
RTT = (t4 - t1) - (t3 - t2)
master_minus_local_offset = ((t1 - t2) + (t4 - t3)) / 2
```

The controller takes multiple samples, retains the three valid samples with the lowest RTT, and computes a 1/RTT² weighted offset over those three. This gives the cleanest retained path the greatest influence while still using multiple measurements.

### SYNC_SET

```text
FCT2|SYNC_SET|0123456789ABCDEF|60001|10
```

Fields: version, packet type, sync ID, selected `master_minus_local_offset_us`, selected RTT in microseconds.
The ESP stores the offset and confirms:

```text
FCT2|SYNC_APPLIED|ESP01|0123456789ABCDEF|60001|10
```

The controller then runs additional verification exchanges without changing the applied offset. The displayed residual error is:

```text
device_clock_error = applied_offset - verification_offset
```

Positive means the device's reconstructed Master clock is estimated to be ahead of the Master; negative means behind.

## Absolute START_AT

```text
FCT2|CMD|START_AT|1111222233334444|20|5000000
```

Fields: version, packet type, command type, command ID, duration seconds, absolute Master target in microseconds.

The ESP converts once when it accepts the command:

```text
local_start_us = target_master_us - master_minus_local_offset_us
```

It then schedules against its own monotonic timer. Packet arrival time does not change `target_master_us`.
A duplicate with the same command ID cannot move the target.

If the ESP has no valid synchronization, it returns `NOT_SYNCED`. If the converted local target is already in the past, it returns `LATE`.

ACK examples:

```text
FCT1|ACK|ESP01|1111222233334444|START_AT|ACCEPTED
FCT1|ACK|ESP01|1111222233334444|START_AT|DUPLICATE
FCT1|ACK|ESP01|1111222233334444|START_AT|NOT_SYNCED
FCT1|ACK|ESP01|1111222233334444|START_AT|LATE
```

## STARTED telemetry

When the timer actually changes from ARMED to RUNNING, it reports:

```text
FCT2|STARTED|ESP01|1111222233334444|2000000|5000002|5000000
```

Fields: version, packet type, device ID, command ID, actual ESP local start observation, reconstructed Master time at that observation, requested Master target.

The firmware transmits both its local start timestamp and its self-reconstructed Master timestamp. For experiment analysis the controller prefers the delay-free verification offset:

```text
verification_corrected_master_start = local_start_us + verification_offset_us
actual_start_error = verification_corrected_master_start - target_master_start_us
```

This prevents an intentionally biased calibration offset from making START telemetry look artificially perfect. The five-device controller computes the fleet spread from these verification-corrected values.

## Reliability

Normal START_AT operation keeps the existing three-copy transmission pattern, approximately 40 ms apart, with one unchanged command ID and one unchanged absolute target.
The repetitions are for packet-loss robustness; the absolute target provides synchronization.

The controlled delay experiment does not alter START_AT delivery. All configured devices receive the normal repeated broadcast with the identical absolute target. Artificial delay is confined to ESP02 calibration SYNC exchanges.

## Important limitation

The NTP-style offset calculation assumes the forward and reverse Wi-Fi path delays are approximately symmetric. Arbitrary one-way/asymmetric network delay cannot be determined exactly from two unsynchronized clocks. Multiple samples, best-3 low-RTT inverse-square weighting, Wi-Fi power-save disablement, and verification reduce this error but do not create a hard real-time guarantee.

## Controlled SYNC path-delay experiment

The Windows controller treats ESP01 as a 0/0 ms control and injects delay only into ESP02 calibration samples:

- NONE: Master→ESP 0 ms, ESP→Master 0 ms
- SYMMETRIC: Master→ESP 250 ms, ESP→Master 250 ms
- ASYMMETRIC: Master→ESP 250 ms, ESP→Master 0 ms

For forward delay, the controller captures `t1` first and waits before transmitting. For reverse delay, the ESP captures `t3` first and waits before transmitting `SYNC_REPLY`. Verification exchanges are always 0/0 ms.

Expected behavior from the NTP-style estimator is `offset_bias=(reverse-forward)/2`: symmetric 250/250 adds about 500 ms RTT with near-zero artificial offset bias; asymmetric 250/0 adds about 250 ms RTT and biases the applied Master-minus-local offset by about -125 ms.

START_AT transmission is never artificially delayed by this experiment. The controller uses the delay-free verification offset to reconstruct STARTED telemetry, so asymmetric calibration should show the resulting ~125 ms physical-start estimate instead of the self-consistent 0 ms error produced by the biased applied offset.

## Extended STATUS diagnostics

Updated firmware emits an FCT2 STATUS packet with Wi-Fi diagnostics:

```text
FCT2|STATUS|ESP03|0123456789ABCDEF|RUNNING|19|-57|6|AA:BB:CC:DD:EE:FF
```

Fields after `remaining` are RSSI in dBm, Wi-Fi primary channel, and BSSID. The updated controller still accepts the legacy six-field FCT1 STATUS packet, so older firmware remains discoverable; diagnostic fields are simply unavailable for legacy STATUS.

## Verification quality gate and retry

After the selected calibration and delay-free verification sample counts are collected (8+8 baseline or 4+4 candidate in v8), the controller forms a best-3 low-RTT 1/RTT² weighted offset for each phase and evaluates:

```text
residual_error = applied_offset - verification_offset
quality_deviation = residual_error - expected_experiment_bias
```

The default gate is:

```text
|quality_deviation| <= 3000 us
maximum synchronization attempts = 5
```

For NONE and SYMMETRIC modes, expected experiment bias is 0 us. For ASYMMETRIC 250/0 ms, expected bias is -125000 us, so the controlled experiment remains valid rather than being rejected as a bad production synchronization.

A failed quality check discards the calibration for START_AT purposes and recalibrates that device. If all five attempts miss the threshold, that device is left unsynchronized and the benchmark trial fails instead of starting with a known poor clock estimate.


## Runtime panel brightness

The controller may unicast a runtime panel-brightness command to any selected device:

```text
FCT2|CMD|BRIGHTNESS|<CommandId>|<percent>|0
```

`percent` is an integer from `0` through `100`. `0` blanks LED output; `100` is maximum output brightness. The device replies with the ordinary ACK form using command type `BRIGHTNESS`. This setting does not alter timer state, synchronization state, START_AT scheduling, or the idle-logo framebuffer. It is runtime-only: after reboot the firmware returns to its build-time `CONFIG_FACTORY_DISPLAY_BRIGHTNESS` default.
