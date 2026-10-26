# Controller 6.23.3 — visible Wi-Fi status

Each timer card shows a Wi-Fi row below Last status: RSSI with dBm units, channel and the associated AP BSSID. All values bind to the existing view-model properties using TextBlocks. Existing discovery/STATUS handling updates the row; the last values remain visible while active segments are silent. Last status identifies the age of the reading. Unavailable values use the existing dash placeholders.

Every `controller_health_*.csv` appends these columns, in this order, after ControllerSessionId:

```
RssiDbm,WifiChannel,Bssid
```

RSSI and channel are numeric; BSSID is CSV-quoted. START values come from the captured START STATUS, and POST_RUN values from the captured POST_RUN STATUS, including when those values differ. Missing snapshots or absent legacy fields produce blank cells. The prior CSV columns retain their names and positions. This is a per-run snapshot export, not a continuous site-survey recording feature.

For an enclosure comparison, observe idle timer cards in the same location/orientation and compare readings on the same BSSID. The row requires no serial connection or separate status-request client. Retain the existing silent-segment workflow for timing tests.

Firmware remains 6.24.0 and Analyzer remains 10.1.1. STATUS packets, timing/synchronization, segmented-program recovery, polling cadence and network silence are unchanged. No firmware or analyzer rebuild is needed for this UI/CSV update.

Build the controller on the Windows PC with `dotnet build FactoryTimer.slnx -c Release` from controller_v6_22_13. Run the existing PC checks before bench use. This environment has no .NET/WinUI build tools; XAML/schema and available Python qualification checks are recorded in checks/wifi_status_validation.txt. The earlier 80-check portable report is retained and is not a new .NET build result.
