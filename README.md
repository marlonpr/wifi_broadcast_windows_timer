# Factory countdown timer proof of concept

## Absolute-time synchronization test revision

This revision adds a 100% infrastructure-Wi-Fi synchronization experiment: the Windows controller synchronizes each ESP32 to its monotonic Master clock with multiple NTP-style timestamp exchanges, keeps the three lowest-RTT samples, applies an inverse-RTT-squared weighted offset, verifies the residual error with the same weighted low-RTT estimator, and then sends a future absolute `START_AT` timestamp.

The UI shows Master time, each device's reconstructed Master time, signed residual clock error, best/verification RTT, applied offset, the estimated worst Master error and full five-device spread. When START_AT executes, each ESP sends `STARTED` telemetry. The controller reconstructs start time with the delay-free verification offset so the displayed START error can expose a deliberately biased calibration offset.

The current experiment keeps ESP01/ESP03/ESP04/ESP05 as no-delay controls and lets ESP02 use one of three calibration paths: NONE (0/0 ms), SYMMETRIC (250/250 ms), or ASYMMETRIC (250/0 ms). Verification exchanges and START_AT delivery are always delay-free. This directly tests the NTP-style assumption that forward/reverse path delay is approximately symmetric.


This repository contains a Windows 11 WinUI 3 controller for a five-device classic ESP32 countdown-timer fleet. A START action is broadcast three times with one command ID. Each timer accepts the first copy, arms a local monotonic start about one second in the future, acknowledges later copies as duplicates, and reports its state by unicast UDP.

The default sequence is exactly `20, 19, ..., 1, 0`. The value is 20 while ARMED, is still 20 at the scheduled start, changes to 19 after one complete elapsed second, and becomes FINISHED/0 after 20 elapsed seconds.

## Project layout

- `windows-controller/FactoryTimer.Controller`: C#/.NET 10 unpackaged WinUI 3 application.
- `windows-controller/FactoryTimer.Protocol`: protocol codec shared by the controller and its tests.
- `esp32-timer-firmware`: ESP-IDF 6.x application for classic ESP32.
- `protocol/PROTOCOL.md`: FCT1 wire-format specification.
- `tests/FactoryTimer.Protocol.Tests`: Windows protocol unit tests.
- `tests/firmware-host`: host-native C++ tests for parsing, duplicates, reset, and countdown behavior.

The firmware has no Arduino dependency. `countdown_timer.*` and `protocol_codec.*` are portable C++ and do not include ESP-IDF headers. Network code supplies `esp_timer_get_time()` values to that core; the core never decrements a counter with `vTaskDelay()`.

## Windows controller

### Prerequisites

- Windows 11, x64.
- .NET 10 SDK.
- Windows App SDK 2.3.1 (NuGet restore obtains it if it is not already cached).
- The PC connected to the active Wi-Fi LAN. Allow the application on **Private networks** if Windows Defender Firewall prompts.

### Build, test, and run

Run these commands in PowerShell from the repository root:

```powershell
dotnet restore .\FactoryTimer.slnx
dotnet build .\FactoryTimer.slnx --configuration Release --no-restore
dotnet test .\tests\FactoryTimer.Controller.Core.Tests\FactoryTimer.Controller.Core.Tests.csproj --configuration Release --no-restore
dotnet test .\tests\FactoryTimer.Protocol.Tests\FactoryTimer.Protocol.Tests.csproj --configuration Release --no-restore
dotnet run --project .\windows-controller\FactoryTimer.Controller\FactoryTimer.Controller.csproj --configuration Release --no-restore
```

The app locates an operational IPv4 Wi-Fi adapter and computes its directed broadcast address from the adapter address and prefix length. It does not assume a `/24` network. Leave the manual override empty for normal use. For a lab or loopback relay test, enter a specific IPv4 destination before pressing START or RESET.

The app binds one asynchronous UDP socket to the selected Wi-Fi address (or all IPv4 interfaces when no Wi-Fi adapter is available). The same socket sends three command copies about 40 ms apart and receives unicast ACK/STATUS responses. A device is ONLINE after a valid response and OFFLINE after five seconds without status; firmware heartbeat status is sent every two seconds.

## ESP32 firmware

### Prerequisites

- ESP-IDF 6.x command environment. The builds reported below used ESP-IDF v6.0.
- Two original/classic ESP32 boards and their serial port names.
- A 2.4 GHz main AP and a repeater operating as a transparent bridge/extender on the same IPv4 subnet.

Open an **ESP-IDF 6.x PowerShell**, then change to the firmware directory:

```powershell
cd C:\path\to\repository\esp32-timer-firmware
```

### Configure and build ESP01

The first command explicitly selects classic ESP32 and creates an independent sdkconfig using the ESP01 defaults:

```powershell
idf.py -B build-esp01 -D SDKCONFIG=sdkconfig.esp01 -D "SDKCONFIG_DEFAULTS=sdkconfig.defaults;sdkconfig.defaults.esp01" set-target esp32
idf.py -B build-esp01 menuconfig
idf.py -B build-esp01 build
```

In `menuconfig`, open **Factory timer configuration** and set:

- `Wi-Fi SSID`: the main AP SSID.
- `Wi-Fi password`: the main AP password (or empty only for an open lab network).
- `Timer device ID`: `ESP01`.

Flash and monitor, replacing `COM5` with the first board's port:

```powershell
idf.py -B build-esp01 -p COM5 flash monitor
```

Press `Ctrl+]` to leave the ESP-IDF monitor.

### Configure and build ESP02

Use a separate build directory and sdkconfig so the two identities and Wi-Fi credentials cannot overwrite one another:

```powershell
idf.py -B build-esp02 -D SDKCONFIG=sdkconfig.esp02 -D "SDKCONFIG_DEFAULTS=sdkconfig.defaults;sdkconfig.defaults.esp02" set-target esp32
idf.py -B build-esp02 menuconfig
idf.py -B build-esp02 build
```

In **Factory timer configuration**, set the repeater SSID/password (the SSID may be the same as the main AP when the extender mirrors it) and confirm device ID `ESP02`. Flash and monitor the second board, replacing `COM6` as needed:

```powershell
idf.py -B build-esp02 -p COM6 flash monitor
```

Generated `sdkconfig.esp01` and `sdkconfig.esp02` files contain credentials and are ignored. The checked-in `sdkconfig.defaults.esp01` and `.esp02` files intentionally contain empty credentials and only establish the device profiles.

## Host firmware tests

Use an x64 Visual Studio Developer PowerShell/Command Prompt with CMake available, from the repository root:

```powershell
cmake -S .\tests\firmware-host -B .\tests\firmware-host\build
cmake --build .\tests\firmware-host\build --config Release
ctest --test-dir .\tests\firmware-host\build -C Release --output-on-failure
```

These tests run on the PC. They cover the exact 20-to-0 sequence, the scheduled-start boundary, duplicate START without schedule movement, RESET while RUNNING and after FINISHED, duplicate RESET, command parsing, response formatting, malformed input, unsupported version/type, and numeric ranges.

## Two-device AP/repeater test

1. Put the repeater in bridge/extender mode, disable wireless/client isolation, and verify it forwards IPv4 broadcast traffic. A routed/NAT guest network is not the same LAN and cannot receive the controller's directed broadcast.
2. Configure/flash ESP01 for the main AP and ESP02 for the repeater as described above. Keep both serial monitors open in separate terminals.
3. Connect the Windows 11 PC to the main AP. Start the controller and check its calculated adapter address/broadcast at the bottom of the window.
4. Confirm both serial consoles report IP addresses in the controller's subnet. If either is on a different subnet, correct the repeater mode before continuing.
5. Leave duration at 20 and click START once. Do not click repeatedly; the application itself sends the required three copies with one ID.
6. Observe the controller and both serial consoles until completion.
7. Click RESET. Verify the controller, ESP01, and ESP02 all report READY with 20 remaining.

Expected controller result:

- Controller immediately shows 20/ARMED, changes to RUNNING at about one second, then follows `20, 19, ..., 0` and FINISHED.
- ESP01 through ESP05 each become ONLINE and show their own IP address.
- Both show the same START command ID, an accepted ACK (with duplicate copies noted), ARMED/20, RUNNING values, then FINISHED/0.
- RESET produces a new shared command ID and both cards become READY/20.

Expected serial-console pattern on each board (addresses and IDs vary):

```text
Starting factory countdown timer device ESP01
Wi-Fi IPv4 address: 192.168.x.x
Wi-Fi gateway: 192.168.x.x
Wi-Fi subnet mask: 255.255.255.0
Listening for UDP commands on port 5000
Received START id=... duration=20 delay_ms=1000 ...
ACK transmitted ... ACCEPTED
Countdown state=ARMED remaining=20 ...
Duplicate command id=... acknowledged without changing timer
ACK transmitted ... DUPLICATE
Countdown state=RUNNING remaining=20 ...
Countdown state=RUNNING remaining=19 ...
...
Countdown state=FINISHED remaining=0 ...
Received RESET id=... duration=20 delay_ms=0 ...
Countdown state=READY remaining=20 ...
```

There may be two duplicate lines because all three copies are acknowledged. The scheduled start must not move when either duplicate arrives.

## Protocol and operational notes

The complete versioned text format and validation rules are in [protocol/PROTOCOL.md](protocol/PROTOCOL.md). UDP port 5000 must be reachable from the controller to both boards, and unicast responses to the controller's ephemeral source port must be allowed. The response format carries a general device ID, so the controller model can later expand to 15 timers and an ESP32 physical controller without changing FCT1.

This first version intentionally has no DS3231 or display driver. The serial log is the display seam for the proof of concept.

## Validation status for this synchronization revision

- Firmware portable host tests were executed in this session with both debug-log configurations: **2/2 passed**.
- The revised XAML was parsed as well-formed XML and controller bindings were statically checked against the view-model properties.
- The current environment does not include the .NET SDK or ESP-IDF toolchain, so the revised WinUI solution and full ESP-IDF firmware were **not** compiled here. Build both on the existing Windows/.NET 10 and ESP-IDF 6.x environments before flashing/running the hardware test.

The earlier baseline revision had previously passed its Windows and ESP-IDF builds, but that does not substitute for compiling this FCT2 synchronization revision.


## Controlled SYNC path-delay build

The current test UI provides NONE (0/0 ms), SYMMETRIC (250/250 ms), and ASYMMETRIC (250/0 ms) ESP02 calibration modes. ESP01 remains a no-delay control; verification and START_AT are delay-free. See `SYNC_PATH_DELAY_TEST_GUIDE.md`.


## Mode-change safety

Changing the SYNC path-delay selector now clears all five devices' synchronization state. This prevents a START_AT test from reusing an offset measured under a previous delay mode. After selecting NONE, SYMMETRIC, or ASYMMETRIC, press **SYNC CLOCKS** before START_AT.


## Five-device scaling revision

The controller now recognizes `ESP01` through `ESP05`. Discovery, ONLINE/OFFLINE tracking, RESET, START_AT ACK handling, STARTED telemetry, clock verification, and UI cards cover all five devices. `SYNC CLOCKS` requires all five devices to have discovered IP addresses and synchronizes them sequentially using 8 calibration samples plus 8 delay-free verification samples per device; each phase uses an inverse-RTT-squared weighted offset over the 3 lowest-RTT valid samples.

The synchronization summary now reports the worst absolute verified clock error across the five devices and the fleet spread (`max(error) - min(error)`). The START summary reports the analogous verification-corrected five-device start spread. The completion status also reports total fleet synchronization duration.

For the first five-device performance baseline, select **NONE — 0 / 0 ms**. The path-delay modes are retained only for controlled experiments; when enabled, artificial calibration delay is applied only to ESP02.

## Automatic multi-run benchmark revision

The five-device controller now includes an **Automatic benchmark** panel. A benchmark trial is a fresh independent synchronization measurement, not merely another START using an old offset. Each trial performs 8 calibration samples plus 8 delay-free verification samples for each of ESP01 through ESP05, using the best-3 inverse-RTT-squared weighted offset in both phases, broadcasts one common future `START_AT`, waits until all five `STARTED` packets for that command ID arrive, records the result, and then resets the countdown so the next trial can begin without waiting for the full display duration.

The default is 10 trials and the accepted range is 1-100. The selected SYNC path-delay mode is recorded in every row, so NONE, SYMMETRIC, and ASYMMETRIC experiments can all be benchmarked; for fleet scaling use **NONE — 0 / 0 ms**.

At completion the controller automatically writes a CSV under `Documents\FactoryTimerBenchmarks`. Each device produces one row per trial. Numeric fields are stored in microseconds and include calibration RTT, verification RTT, applied and verification offsets, verified clock error, verification-corrected START error, locally reconstructed scheduler lateness, fleet synchronization duration, worst absolute clock/START errors, and five-device clock/START spreads. The UI reports success count, mean/P95/max START spread, mean worst START error, and mean fleet synchronization duration.

The benchmark waits for STARTED telemetry rather than the full countdown to finish. Once the START edge has been measured, it sends RESET and advances to the next trial. The STOP button cancels the benchmark and still saves all completed/failed rows collected so far.

## Synchronization quality retry and timing-quiet revision

The ±3 ms quality gate is retained. Each device now has up to **5 total synchronization attempts**, with a **150 ms quiet interval** before a quality retry. Periodic `STATUS_REQUEST` discovery is stopped and fully drained before timing-critical synchronization, followed by a **100 ms drain interval** before the first SYNC sample. During an automatic benchmark trial discovery remains paused through synchronization, START_AT, STARTED telemetry, and RESET, then resumes between trials.

The v5 offset estimator collects **8 samples** for calibration and verification, retains the **3 lowest-RTT valid samples**, and computes a **1/RTT² weighted offset** over those three. The minimum-RTT sample carries the real `SyncId` used by `SYNC_SET`; the offset itself is the weighted estimate. `BestSyncRttUs` and `VerifyRttUs` continue to report the minimum RTT in the retained set.

The current hardware mapping used by the CSV is ESP01/ESP02/ESP04 = classic ESP32 and ESP03/ESP05 = ESP32-S3. See `SYNC_QUALITY_RETRY_GUIDE.md` for details.


## Raw synchronization sample diagnostics revision

The automatic benchmark now writes a second CSV next to the normal five-row-per-trial summary:

```text
factory_timer_5_device_benchmark_YYYYMMDD_HHMMSS.csv
factory_timer_5_device_sync_samples_YYYYMMDD_HHMMSS.csv
```

Both files share the same run timestamp. The raw SYNC file contains one row for every completed calibration or verification timestamp exchange. v5 also preserves partial attempts: successful samples collected before an error are written, followed by an error marker row identifying the failing phase/sample, `SyncId` when known, `AttemptOutcome`, `FailureKind`, and `FailureMessage`. Complete rows record the four NTP-style timestamps, RTT/offset, low-RTT selection, the representative minimum-RTT sample, weighted consensus offset, and final quality result.

This diagnostic file is intended to distinguish isolated outliers from multi-packet latency/asymmetry bursts and transport failures without changing the ±3 ms quality gate or five-attempt retry policy. See `RTT_WEIGHTED_ESTIMATOR_V5.md` and `SYNC_PARTIAL_DIAGNOSTICS_V5.md`.
