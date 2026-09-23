# Manual START_AT controller validation notes

Source baseline: `wifi_broadcast_timer_sync_on_start_v1_1_stagger_test.zip`, which the user confirmed works.

Changes are limited to the Windows controller diagnostic UI/orchestration:

- Replaced automatic T*-9/T*-6/T*-3 diagnostic sending with operator-driven per-device buttons.
- PREPARE performs the same fresh STATUS / production 8+8 no-delay synchronization and readiness gate used by the prior diagnostic.
- PREPARE freezes one CommandId, duration, participant set, and `T* = MasterNow + 30 s`.
- Exactly the three frozen participant buttons are enabled.
- Each button sends the same `START_AT` payload only once and records its actual controller master-time send instant and lead to T*.
- Normal controller send controls remain disabled while a manual proof session is reserved.
- At `T* - 2 s`, all three commands must have been sent and ACKed. Otherwise the controller unicasts RESET to the frozen set and aborts.
- After a successful arm, the controller waits for STARTED telemetry and reports each start error plus the fleet spread.
- `CANCEL / RESET` explicitly resets the frozen set.
- The existing production `SendStartAtAsync()` through the production arm/barrier region was compared against the working stagger-test baseline and is unchanged.
- Firmware and protocol files are unchanged.

Validation possible in this environment:

- MainWindow.xaml parses as XML.
- All XAML Click handlers have matching code-behind methods.
- Production START orchestration region is byte-for-byte unchanged from the working baseline.
- Package/patch generation and source-level consistency checks completed.

Not available in this environment:

- .NET SDK / Windows App SDK, so the WinUI project could not be compiled here. Run the normal Windows `dotnet test` / `dotnet build` checks after extraction.
