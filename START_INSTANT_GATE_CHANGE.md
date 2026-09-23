# Start-instant timing gate

This revision removes countdown duration from the production readiness calculation.

- The controller still performs fresh production 8+8 synchronization before every START.
- Each accepted residual is conservatively aged at 50 ppm only from its effective estimator epoch to the planned absolute start time `T*`.
- For two or more selected devices, the two largest per-device uncertainties must sum to `<= 20,000 us` at `T*`.
- Exactly `20,000 us` passes; values above it fail as `TimingBudgetExceeded`.
- The countdown duration is validated separately as `00:01..99:59` and is not used by the readiness gate.
- `Dmax` and `DurationExceeded` were removed from the production START path.
- START_AT packet format, target lead time, brightness, and firmware are unchanged. The later confirmed-abort revision moves final STATUS earlier and reserves a RESET-confirmation window without changing the start-instant timing gate.

The 20 ms claim now has one precise meaning: fleet scheduler uncertainty at the common start instant. It does not claim that local oscillator drift keeps all later second boundaries or the final `00:00` within 20 ms.
