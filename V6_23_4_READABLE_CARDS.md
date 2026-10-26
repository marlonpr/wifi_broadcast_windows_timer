# Controller 6.23.4 — readable timer cards

Timer cards are now full-width rows with automatic height. The former ItemsWrapGrid allocated uniform slots; longer warm-up/diagnostic text could be clipped when the first card needed less height. A vertical StackPanel lets each card grow with its own contents.

The existing page ScrollViewer scrolls the whole list, including all 15 configured timers. Cards have no fixed width, fixed height or nested scroll viewport. Warm-up and start diagnostics wrap. RSSI/channel remain together; AP BSSID has its own wrapping row so it cannot run off the right edge.

All existing bindings, view-model/status handling, health CSV additions, network silence and timer behavior are unchanged. Firmware remains 6.24.0 and Analyzer remains 10.1.1. This is a controller presentation change.

For the minimal fix, replace windows-controller/FactoryTimer.Controller/MainWindow.xaml in the existing 6.23.3 controller folder with the supplied MainWindow.xaml. The complete bundle also updates the controller project version to 6.23.4. Rebuild from controller_v6_22_13:

```powershell
dotnet build FactoryTimer.slnx -c Release -p:Platform=x64
```

XAML structure/binding checks and unchanged-code comparisons are recorded in checks/readable_cards_validation.txt. The .NET/WinUI build and visual check on Windows remain pending here; no SDK is available in this environment. Historical portable and Wi-Fi CSV checks are retained.
