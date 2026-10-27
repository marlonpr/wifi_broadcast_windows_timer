# Controller 6.23.13 — RESET clears RECOVERY HELD

RESET now clears the recovery hold caused by an invalid saved STOP/RESET
checkpoint. It discards the old countdown progress, restores the duration
currently entered on screen, and makes START available for a new countdown.
The START / STOP / RESET layout is unchanged.

The reset is saved before the recovery error is cleared. If the journal still
contains a valid plan, the original timer list is kept even if the selection
has changed. If the journal is unreadable, RESET uses the selected timers and
any timers currently observed ARMED or RUNNING. Select the intended timers
before pressing RESET in that case.

The existing RESET confirmation and START readiness/synchronization checks
remain in place. An unavailable timer is shown as awaiting stop confirmation;
reconnect it and press RESET or START again. A storage-write failure is reported
and does not falsely clear the hold. RESET can save its cleared intent while
offline; sending commands still requires the network.

A late recovery error no longer changes a stopped or completed program back to
HELD. In particular, it cannot create the invalid HELD + STOP checkpoint state.
The repaired journal loads normally after restarting the controller.

## Build and apply

The complete ZIP includes the full source tree. Replace the source tree with
that version, then build from `controller_v6_22_13` on Windows:

```powershell
dotnet build FactoryTimer.slnx -c Release -p:Platform=x64
```

The small patch applies to controller 6.23.12, from its directory containing
`FactoryTimer.slnx`. Check it first, then apply it:

```powershell
git apply --check <path-to-6.23.13-patch>
git apply <path-to-6.23.13-patch>
```

It is not an incremental patch for RC2 or for the earlier supplied 6.23.11
patches. Use the complete ZIP if the source version is uncertain.
No timer or analyzer reflash is required. Firmware 6.24.5-rc1, analyzer 10.1.1,
and wire protocol are unchanged from the preceding complete release.

## Validation

.NET SDK 10.0.100 on Linux:

- Controller-core tests: 118 PASS.
- Protocol tests: 53 PASS.
- Actual view-model adapter: 33 checks PASS, including invalid-checkpoint
  RESET, cleared state after restart, fresh START availability, truncated
  journal fallback, original-roster preservation and failed-write handling.
- Production smoke: 163 assertions PASS.
- Resume fault scenarios: 11 PASS.
- Stale RUNNING regression: 9 PASS.
- Portable host suite: 85 PASS.

The adapter uses the actual controller sources with host UI stand-ins and
in-memory UDP sockets. Native Windows WinUI/XAML build and physical timer
checks remain pending.

## Bench check

1. With the reported recovery hold visible, press RESET.
2. Confirm RECOVERY HELD disappears and the controller shows READY at the
   configured duration. Resolve any explicitly listed stop confirmations.
3. Press START and confirm a new countdown begins at repetition 1.
4. Check STOP pauses, START resumes, and RESET clears progress as before.
5. Restart the controller after RESET and confirm the hold does not return.
