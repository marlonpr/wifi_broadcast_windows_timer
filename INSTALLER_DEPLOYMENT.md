# Operator-controller deployment model

The Factory Timer operator controller is distributed as an unpackaged, self-contained x64 WinUI 3 application wrapped in a conventional setup EXE.

## Runtime model

- .NET 10: self-contained publish (`win-x64`).
- Windows App SDK: self-contained (`WindowsAppSDKSelfContained=true`).
- Microsoft Visual C++ x64 runtime: bundled as an installer prerequisite.
- Windows App Runtime: no separate target-PC installation is required by this deployment model.
- .NET SDK / Visual Studio: build-machine tools only; never required on an operator PC.

## Installer

`installer\build-installer.ps1` creates:

`artifacts\installer\FactoryTimerSetup-<version>-win-x64.exe`

using `installer\FactoryTimer.iss`.

The setup installs the complete publish folder, adds shortcuts, and optionally creates a Private-profile inbound UDP firewall rule scoped to `FactoryTimer.Controller.exe`.

## Architecture

This release is intentionally x64-only. If ARM64 operator PCs are introduced later, produce a separate `win-arm64` publish/installer rather than relying on emulation as the primary deployment path.
