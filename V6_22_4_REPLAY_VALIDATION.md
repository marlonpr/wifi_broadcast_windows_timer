# v6.22.4 counterfactual replay — 2026-10-01 five-run capture

The latest Analyzer_v8 + UART capture was replayed with the existing audit parser using `med3` over all samples associated with each synchronization window.

Four runs had complete UART/analyzer alignment. With the audit's `rate_ppm` common-epoch correction, the counterfactual fixed residuals had approximately:

- mean: -0.6 us
- RMS: 13.5 us
- max absolute: 16 us

A second replay kept the firmware's disciplined sync->START propagation but deliberately omitted only the per-sample rate normalization in the pooled floor estimator. That produced approximately:

- mean: +6.4 us
- RMS: 13.2 us
- max absolute: 19 us

This supports using native-epoch pooled med3 in the controller-only v6.22.4 change for this capture. It does not prove that rate normalization is universally unnecessary. Exact audit-equivalent common-epoch normalization would require the firmware's fitted rate to be carried over the network protocol; v6.22.4 intentionally leaves firmware/protocol unchanged.

The key observed retry cases were the same ones identified manually: retaining earlier low-delay evidence substantially improves the inferred b0 compared with replacing a failed attempt with the first later attempt that passes.
