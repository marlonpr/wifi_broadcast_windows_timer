# STATUS lead sweep diagnostic

This is a bench-only controller build for the 20 s scheduler investigation.

After the production ARM/final-status barrier succeeds, ordinary discovery is stopped for the run. The controller sends one unicast `STATUS_REQUEST` per participant per boundary with target **ESP receive** leads:

`12, 9, 7, 6, 5, 4, 3, 2, 1.5, 1, 0.5, 0.2, -0.2, -0.5, -1.0 ms`

Negative lead means target arrival after the boundary.

For the current two-device bench the controller compensates the measured send-to-`recvfrom()` transit:

- ESP01: 2558 us
- ESP02: 2344 us

Other devices use 2500 us only as a diagnostic default. Device-side `STATUS_PATH` / `RX_NEAR_BOUNDARY` records remain the authoritative arrival timestamp.

The final <=20 ms before each scheduled send uses the QPC-backed `MasterClock` in a spin loop so the default Windows scheduler tick cannot consume the precision window. Every send remains in the normal controller TX CSV and is tagged, e.g.:

`STATUS_SWEEP_B07_TARGET_RX_LEAD_3000`

This build is not intended as the production polling policy.
