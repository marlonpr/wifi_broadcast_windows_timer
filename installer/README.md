# Factory Timer Windows installer

This installer setup is for the x64 operator controller.

## Target PC

The installed controller is designed to run without separately installing the .NET 10 runtime or the Windows App Runtime. The setup EXE also carries the Microsoft Visual C++ x64 Redistributable.

Minimum application target: Windows 10 build 19041 (version 2004) or newer, x64. Windows 11 x64 is the normal factory target.

## Build PC requirements

Install on the development/build PC only:

1. .NET 10 x64 SDK.
2. Inno Setup 6.
3. Internet access the first time the build script downloads `VC_redist.x64.exe`, or manually put the official Microsoft file at `installer\prereqs\VC_redist.x64.exe`.
4. Optional: Windows SDK `signtool.exe` and a code-signing certificate if the setup EXE will be signed.

Visual Studio is optional; command-line builds are sufficient.

## Build one setup EXE

From the repository root in PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\installer\build-installer.ps1 -Version 1.0.0
```

Output:

```text
artifacts\installer\FactoryTimerSetup-1.0.0-win-x64.exe
```

The script performs:

1. restore;
2. Protocol tests;
3. Controller.Core tests;
4. self-contained `win-x64` publish;
5. validation that .NET runtime and Windows App SDK files are present;
6. Inno Setup compilation;
7. SHA-256 output.

Use `-SkipTests` only for an iterative local packaging check, not for a release installer.

## Optional signing

If `signtool.exe` is in PATH and you have a certificate in the Windows certificate store:

```powershell
.\installer\build-installer.ps1 `
  -Version 1.0.0 `
  -SigningCertificateThumbprint YOUR_CERT_THUMBPRINT
```

For deployment outside a tightly managed internal environment, code-sign the release setup EXE to reduce Windows reputation/SmartScreen warnings.

## What the installer does

- installs under `C:\Program Files\Factory Timer`;
- creates a Start Menu shortcut;
- offers an optional Desktop shortcut;
- installs/repairs the Microsoft Visual C++ x64 runtime silently;
- creates an application-specific inbound UDP firewall rule for **Private** network profiles;
- removes that firewall rule during uninstall;
- offers to launch the controller when setup completes.

## Clean-PC acceptance test

Use a Windows 11 x64 PC that does not have the .NET 10 Desktop Runtime or Windows App Runtime intentionally installed for this application.

1. Verify the PC is connected to the factory LAN and the connection is marked **Private**.
2. Run `FactoryTimerSetup-<version>-win-x64.exe` as administrator.
3. Launch **Factory Timer** from the Start Menu.
4. Confirm the 15 TIMER cards load.
5. Confirm online timers are discovered.
6. Select one timer and perform a short START/RESET functional test.
7. Select multiple timers and verify synchronized START.
8. Reboot the PC and verify the controller still launches normally.
9. Uninstall Factory Timer and verify its firewall rule is removed.

No .NET SDK, Visual Studio, or separate Windows App Runtime installation should be required on the operator PC.
