# Production sync-on-start

This document defines the production START path. It intentionally excludes BG-1/BG-2, rate fitting, target pre-compensation, DS3231, persistent controller ownership, two-phase commit, adaptive sampling, and continuous deadline-liveness monitoring.

## Sequence

1. Freeze the selected participant IDs and reserve the complete set atomically.
2. Obtain a fresh STATUS preflight for the selected set. A fresh `ARMED` or `RUNNING` participant blocks as `ParticipantBusy` before any `SYNC_SET` is sent.
3. Stop periodic STATUS traffic and drain it from the timing path.
4. Synchronize selected participants serially with production `8+8`, best-3 inverse-RTT-squared consensus, no artificial path delay, and the existing ±3 ms verification quality gate/retry policy.
5. Resume STATUS and require a fresh post-sync STATUS from each selected participant.
6. Compute provisional `T_candidate = master_now + 5 s`.
7. Evaluate the readiness gate against `T_candidate`. A failed gate releases the reservation immediately. No prepared run exists.
8. On gate success, create one immutable prepared run containing a new `CommandId`, `T* = T_candidate`, duration, selected set, addresses, and accepted sync/session snapshots.
9. Unicast `START_AT(T*)` to every still-missing participant. `ACCEPTED` and `DUPLICATE` both prove that the device accepted the arm command. Retry missing devices while the ARMING window remains open.
10. Once all ARM ACKs are present, begin the mandatory final STATUS barrier at approximately `T* - 3.5 s`. Unicast STATUS_REQUEST to every frozen participant, retry only missing replies for up to 3 rounds with a 150 ms reply window per round, and require normal ARM preparation to complete before `T* - 2.5 s`.
11. Each final-barrier reply must be newly observed after the barrier began, belong to the same observed device session, report `State = ARMED`, and report the prepared START `CommandId`. A missing or inconsistent reply aborts the preparation.
12. After a successful final barrier, the existing session guard remains active until `T* - 2 s`; after that point a successful prepared run owns execution.
13. Any ARMING/final-barrier failure enters a dedicated cancellation-confirmation window. The controller creates one RESET CommandId, unicasts/retries RESET to the full frozen set, collects RESET ACKs, and requires a fresh `READY` STATUS proving the prepared START_AT is gone. Cancellation must be confirmed before `T* - 1 s`.
14. Only after RESET ACK + fresh safe STATUS are confirmed for every frozen participant does the controller report `ABORT CONFIRMED`. If proof is incomplete by the cancellation deadline, the controller reports `AbortUnconfirmed` and explicitly warns that one or more devices may still start at the abandoned `T*`. Otherwise the armed devices run independently.

Every operator retry is a new preparation: new reservation, new foreground synchronization, new provisional target, new `CommandId`, and new prepared run.

## Why the final barrier needs no firmware change

The existing STATUS packet does not contain uptime or a local timestamp. It already contains the fields required by v1:

- timer state;
- last applied timer `CommandId`;
- device identity and source IP observed by the controller.

After an accepted `START_AT`, firmware keeps that command ID in RAM and STATUS reports `ARMED` with the prepared command ID. After a reboot, the in-memory timer returns to its boot state (`READY`, command ID `0`). Therefore a device that rebooted and returned before the final barrier cannot pass `ARMED + matching CommandId`, even if it reacquires the same IP. Existing session-generation checks provide an additional rejection path for observed IP/reconnect/timestamp/command-session changes.

The barrier is a finite one-shot readiness check, not continuous deadline-liveness monitoring.

## Readiness gate

For each selected participant:

- present in the configured roster;
- online;
- STATUS age strictly less than 5 seconds;
- STATUS is not `ARMED` or `RUNNING` for a new START;
- synchronization accepted;
- synchronization session generation still equals the current device session generation.

`StatusStale` has precedence over a stale last-known `RUNNING`/`ARMED` state. The UI may display the last-known state separately, but the formal block reason remains `StatusStale`.

The accepted residual error is aged from the estimator's effective weighted Master epoch to the absolute start instant `T*`, never from synchronization-call completion time. All timing arithmetic is in microseconds.

For participant `i` under the uniform 50 ppm fallback:

```
b_i(T*) = |e_i| + ceil(50 ppm * A_i_to_T*)
```

For `N >= 2`, sort `b_i(T*)` descending and use the two largest:

```
start_pair_bound = b_(1)(T*) + b_(2)(T*)
start_pair_bound <= 20,000 us
```

An exact 20,000 us bound passes; only a value above 20,000 us is rejected as `TimingBudgetExceeded`. `N=1` skips the pairwise fleet-spread calculation entirely.

**Countdown duration is not an input to this readiness gate.** Once the devices reach the common `T*`, the start event has already occurred. The production 20 ms claim therefore applies to the scheduler start instant only. The configured countdown may be any duration accepted by the normal UI/protocol range (`00:01..99:59` in this controller build). No `Dmax` is computed and there is no `DurationExceeded` readiness result.

After `T*`, each device runs its countdown autonomously. The controller does not claim that later displayed second boundaries or the final `00:00` remain within 20 ms across the fleet for the entire run.

## Structured block reasons

- `Offline`
- `StatusStale`
- `ParticipantBusy`
- `SyncFailed`
- `SyncInvalidated`
- `TimingBudgetExceeded`
- `ArmAckMissing`
- `AbortUnconfirmed`
- `MissingExpectedDevice`

The final barrier reuses these reasons:

- no fresh final reply by the normal ARM deadline -> `StatusStale`;
- changed session or a reply that no longer reports this prepared command as `ARMED` -> `SyncInvalidated`;
- `ARMED/RUNNING` for another command -> `ParticipantBusy`.

## Session invalidation

The controller session generation changes on:

- observed device IP change;
- an observed disconnect followed by reconnect while ordinary STATUS monitoring is active;
- local monotonic timestamp rollback observed in SYNC replies;
- a STATUS transition from a known nonzero prior CommandId to boot-state CommandId `0`.

The intentional STATUS pause used during foreground synchronization suppresses disconnect inference so the quiet period cannot invalidate its own sync.

No controller reservation or prepared-run state is persisted. After a controller restart, fresh STATUS is authoritative. A fresh `ARMED` or `RUNNING` device therefore blocks a new START as `ParticipantBusy`; the normal manual RESET action is the recovery mechanism.

## Final barrier, successful-run cutoff, and confirmed abort

The final barrier is scheduled to begin at approximately:

```
T* - 3.5 s
```

and normal ARM preparation must complete by:

```
T* - 2.5 s
```

The implementation uses up to three unicast STATUS_REQUEST rounds for participants still missing a qualifying reply, with a 150 ms reply window per round. A qualifying reply must be observed after the barrier began and must report the prepared command as `ARMED`.

After the final barrier succeeds, the existing in-memory session guard remains active until the established successful-run cutoff:

```
T* - 2.0 s
```

At or after that cutoff, a successful prepared run is no longer cancelled because of newly observed liveness/session changes.

If ARMING or the final barrier fails, the controller does **not** use a one-way best-effort RESET anymore. It enters a separate confirmed-cancellation interval for at most 1.5 seconds and never later than:

```
T* - 1.0 s
```

During that interval the controller:

1. freezes one new RESET CommandId;
2. unicasts RESET only to the frozen participant set;
3. retries participants whose RESET ACK is still missing;
4. sends STATUS_REQUEST when RESET is ACKed but its immediate STATUS was lost;
5. requires every participant to produce both an `ACCEPTED`/`DUPLICATE` RESET ACK and a fresh `READY` STATUS whose CommandId is either the RESET CommandId or `0` after a reboot.

If all participants satisfy those conditions, the UI reports `ABORT CONFIRMED`. If not, the UI reports `AbortUnconfirmed` and warns that the abandoned START_AT may still execute. A final redundant RESET burst is still attempted for unconfirmed devices, but it is not represented as proof.

This still does **not** create a true 0-or-N commit guarantee through `T*`. A device can fail silently after the last successful final-barrier reply in a run that otherwise passed. Eliminating that final autonomous interval would require a firmware commit phase.

## Known limits

- The bound applies to the scheduler start instant, not the visible HUB75 presentation transition.
- The 50 ppm fallback is used only to age accepted synchronization error up to `T*`; it does not limit countdown duration after the start.
- Every START requires a fresh successful foreground synchronization.
- The final barrier reduces the silent-failure exposure to the interval after the last qualifying reply; it does not provide a 0-or-N commit guarantee through `T*`.

## Acceptance

Host tests cover start-instant gate arithmetic, the exact 20 ms pass boundary, >20 ms rejection, exact 5-second freshness, busy-state precedence, effective-epoch ageing, `N=1`, duration independence, session invalidation, ACK semantics, final-barrier freshness/state/CommandId/session checks, and atomic overlapping reservations.

Hardware acceptance:

- powered-off selected participant blocks;
- powered-off nonparticipant does not block;
- observed reboot/session change after sync invalidates;
- reboot after ARM ACK but before the final barrier returns as `READY/0` and aborts;
- missing final-barrier STATUS from any selected participant enters confirmed cancellation;
- a forced final-barrier failure with one or more already-armed devices must show `ABORT CONFIRMED`, and none of those displays may start at the abandoned `T*`;
- if RESET ACK or fresh `READY` STATUS cannot be proved before `T* - 1 s`, the UI must show `AbortUnconfirmed` rather than a normal safe-abort message;
- final STATUS for every selected device on a successful run reports `ARMED` with the prepared `CommandId`;
- a long duration such as `99:59` is allowed when the fleet passes the readiness gate at `T*`;
- two overlapping reserves: exactly one succeeds, no partial reservation;
- frozen 30-trial analyzer run after adding the final-barrier traffic: 30/30 `0x1F`, 150/150 accepted, scheduler lateness <= 1 us, median physical spread <= 1.7 ms.
