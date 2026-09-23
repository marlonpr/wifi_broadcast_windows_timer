# Installer implementation validation

Static validation completed in this environment:

- controller project XML parses;
- self-contained publish profile XML parses;
- x64 RID and `SelfContained=true` are present;
- `WindowsAppSDKSelfContained=true` remains enabled;
- trimming is disabled for the WinUI application;
- installer is per-machine/admin and requires Windows build 19041 or newer;
- setup embeds `VC_redist.x64.exe`;
- setup creates a program-scoped inbound UDP firewall rule for Private profiles and removes it at uninstall;
- build script restores, tests, publishes, validates runtime files, builds the setup EXE, and emits SHA-256;
- build script refuses to proceed while `FactoryTimer.Controller.exe` is running, preventing the DLL-lock issue previously observed.

Not executable in this Linux container:

- .NET/WinUI Windows publish;
- Inno Setup compilation;
- Authenticode signing;
- clean Windows-PC installation test.

Those steps must be run on the normal Windows development PC using `installer\build-installer.ps1`.
