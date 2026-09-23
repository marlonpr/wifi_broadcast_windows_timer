# Manual absolute-time START_AT proof

This controller build adds a diagnostic-only manual proof. The normal production START path is unchanged.

## Purpose

Demonstrate that three synchronized ESP32 devices can receive the same absolute `START_AT(T*)` command at clearly different controller times and still report starts clustered around the same `T*`.

This validates scheduler/master-clock behavior. It does not by itself prove simultaneous visible LED-panel refresh; that still requires physical output instrumentation.

## Procedure

1. Select exactly three devices, normally ESP01, ESP02 and ESP03.
2. Choose a normal countdown duration, for example 20 s.
3. Click **PREPARE MANUAL TEST**.
4. The controller performs fresh STATUS validation and production 8+8 synchronization with no injected path delay.
5. After the gate passes, the controller freezes one CommandId, one duration and one target `T* = MasterNow + 30 s`.
6. The normal START button and other send controls remain disabled while this manual session is active.
7. Click the enabled per-device START_AT buttons at visibly different times. Example:
   - ESP01 with about 24 s remaining
   - ESP02 with about 16 s remaining
   - ESP03 with about 8 s remaining
8. Do not wait until the last two seconds. The diagnostic has a safety cutoff at `T* - 2 s`.
9. At the cutoff the controller requires all three commands to have been sent and ACKed. Otherwise it unicasts RESET to the frozen participant set and aborts, preventing an intentional partial-start test from accidentally continuing.
10. If all three are armed, wait for `STARTED` telemetry. The result panel reports each manual send lead time, start timing error, scheduler lateness, and the fleet reported start spread.

## Evidence to capture

A successful result should show one common T* and one common CommandId while the three recorded send lead times differ by several seconds. Example:

```text
Common T*=123,456,789 us; command=ABCDEF...
ESP01: sent 23.8 s before T*, ACK accepted
ESP02: sent 15.4 s before T*, ACK accepted
ESP03: sent 7.2 s before T*, ACK accepted

STARTED telemetry:
ESP01: error=-0.180 ms, scheduler_lateness=...
ESP02: error=+0.031 ms, scheduler_lateness=...
ESP03: error=+0.104 ms, scheduler_lateness=...
Fleet reported start spread=0.284 ms
```

The important proof is the combination of **different packet-delivery times** and **the same absolute target with a small reported start spread**.

## One serial cable

Only one USB serial cable is required. Leave it connected to one ESP32 if desired to capture that device's firmware-side `START_AT accepted` and `Countdown started` messages. ESP02 and ESP03 evidence is still collected through their UDP ACK/STARTED telemetry in the Windows controller.

## Cancel behavior

`CANCEL / RESET` unicasts RESET to the frozen three-device set and releases the manual-test reservation. The normal production controls become available again afterward.

## Evidence-v4 applied-offset consistency check

The final result additionally prints `identity_delta` for each participant and requires it to be exactly zero:

`start error = scheduler lateness - sync residual`.

This relation is an **applied-offset consistency** check. The verification offset cancels algebraically, so `identity_delta = 0` means the firmware start used the same applied offset the controller recorded at `SYNC_APPLIED`. It does not say that the verification estimate is correct.

The minimum-send-gap calculation is performed after sorting by the recorded master send timestamp, not by DeviceId. For a strong positive run, press the three device buttons out of DeviceId order with >1 s gaps. For the negative control, press ESP01 and ESP03 <1 s apart and ESP02 well apart; the result must fail the separation criterion.
