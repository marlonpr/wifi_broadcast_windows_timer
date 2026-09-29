# RTC lock gate + unicast in-run STATUS

- FCT2 STATUS accepts a tenth field containing RTC discipline state: `LOCKED`, `ACQUIRING`, `HOLDOVER`, `UNINITIALIZED`, or `DISABLED`.
- The nine-field FCT2 STATUS and legacy FCT1 STATUS remain parseable for compatibility, but a production START gate requires a fresh `LOCKED` state.
- Preflight, post-sync gate, ARM session guard, and final barrier all reject a participant that is no longer `LOCKED`.
- During an active production TX-trace session, periodic STATUS polling switches from broadcast to unicast for the frozen participant IPs. Discovery returns to broadcast after the run trace closes.
- Every controller send remains timestamped in master/QPC time.
