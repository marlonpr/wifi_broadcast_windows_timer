# Validation — controller TX trace source

- Based on the latest ZEIT-branded controller source package in the project library.
- `MainWindow.xaml` XML parse: PASS.
- Source sanity check confirms every application-level UDP send path routes through `SendTracedAsync`; only the single socket send inside that helper remains.
- The TX timestamp is captured from `MasterClock.NowMicroseconds` immediately before socket send.
- During a production START run, trace rows are buffered in memory and written only after `T* + duration + 2 s`.
- Python qualification tools compile and synthetic 1800-boundary validation passes.

A full `dotnet build` could not be executed in this environment because the .NET SDK is not installed. Run `dotnet restore`, `dotnet build FactoryTimer.slnx -c Release`, and `dotnet test -c Release` on the Windows development PC before using this controller build for the qualification run.
