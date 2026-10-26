# Controller 6.23.5 — RSSI maintenance color

The dBm reading is amber when the latest available RSSI is strictly below -70 dBm. Exactly -70 dBm and stronger readings keep the normal text color. Unknown readings use the existing neutral dash placeholder. New STATUS values update the indicator in either direction; MarkOffline clears the reading and warning.

This is presentation only. RSSI does not enter START, synchronization, readiness, retry, recovery or timing-budget decisions. Existing STATUS traffic supplies the reading; polling and active-segment silence are unchanged. Health CSV values/columns are unchanged.

The two mutually exclusive TextBlocks preserve the normal themed foreground instead of overriding it with a cached brush. The warning uses amber shades for light/dark backgrounds and the user's text color in contrast themes. Its tooltip and accessibility help text identify it as a maintenance warning. Theme resources follow [Microsoft's contrast-theme guidance](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes).

The full-width, variable-height card layout from 6.23.4 is retained. Both MainWindow.xaml and DeviceViewModel.cs are needed for this change; the supplied patch applies to the full 6.23.4 bundle. Firmware remains 6.24.0 and Analyzer remains 10.1.1; neither requires a rebuild.

Rebuild from controller_v6_22_13:

```powershell
dotnet build FactoryTimer.slnx -c Release -p:Platform=x64
```

XAML/theme/binding and unchanged-gate checks are recorded in checks/rssi_warning_validation.txt. This environment has no .NET/WinUI SDK; the Windows build and visual check remain pending. Earlier portable results are retained as historical evidence.
