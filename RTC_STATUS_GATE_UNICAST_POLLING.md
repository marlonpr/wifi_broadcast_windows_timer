# RTC lock gate and in-run unicast STATUS polling

This qualification update adds three deliberately separate diagnostics/safety changes:

1. Current FCT2 STATUS appends the RTC discipline state (`LOCKED`, `ACQUIRING`, `HOLDOVER`, `UNINITIALIZED`, or `DISABLED`). The controller accepts the older nine-field STATUS for compatibility, but a production START gate requires a fresh `LOCKED` state.
2. Firmware independently rejects START/START_AT while DS3231 discipline is enabled but not LOCKED. The ACK remains `NOT_SYNCED`; the UART log identifies the RTC state.
3. During a traced production run, the controller polls only the frozen participant IPs by unicast. Outside the run it returns to broadcast discovery automatically.

Firmware also emits deferred `RX_NEAR_BOUNDARY` records for accepted UDP packets on port 5000 that arrive within 15 ms before a running display boundary. The receive timestamp is captured immediately after `recvfrom`; UART logging is deferred through a low-priority queue. This trace does **not** observe Wi-Fi management traffic or UDP traffic on unrelated ports, so an unexplained late commit can still originate in the AP, another LAN host, or driver work that never reaches port 5000.
