# Installer prerequisites carried by the setup EXE

`build-installer.ps1` expects:

- `VC_redist.x64.exe` — official Microsoft Visual C++ Redistributable (x64).

If the file is absent, the build script downloads it from Microsoft's stable redirect:

`https://aka.ms/vs/17/release/vc_redist.x64.exe`

The redistributable is embedded into `FactoryTimerSetup-<version>-win-x64.exe` and runs silently during installation. It is not required to remain in the installed application directory.

The .NET 10 runtime is **not** stored here. It is embedded into the application publish output by `dotnet publish --self-contained true`.

The Windows App SDK runtime is likewise copied into the publish output because the app project uses `WindowsAppSDKSelfContained=true`.
