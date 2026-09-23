# External mouse wheel / mixed-DPI fix

This build adds explicit **PerMonitorV2** DPI awareness to the WinUI 3 controller manifest.

The diagnostic capture showed two important facts:

- On `DISPLAY2` at 100% scale, XAML and screen coordinates agreed.
- On the primary display, logged XAML X coordinates were almost exactly `1.25 ×` the screen X coordinate (for example `1063 -> 1329.34` and `1193 -> 1491.38`).

That is the signature of a 125% mixed-DPI coordinate mismatch. The original `app.manifest` declared OS compatibility but no DPI-awareness mode.

The fix adds both:

- legacy fallback: `dpiAware = true/pm`
- Windows 10/11 mode: `dpiAwareness = PerMonitorV2`

No timer protocol, synchronization, UDP, device-selection, brightness, or countdown behavior was changed.
