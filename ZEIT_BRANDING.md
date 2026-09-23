# Zeit branding / icon integration

The complete user-provided `Assets` folder is stored in:

`windows-controller/FactoryTimer.Controller/Assets/`

The multi-resolution icon `ESP32ControllerWindows.ico` is used for:

- the embedded `FactoryTimer.Controller.exe` application icon
- the running WinUI window / taskbar icon via `AppWindow.SetIcon`
- Start Menu and Desktop shortcuts
- Windows Installed Apps / uninstall entry (through the EXE icon)
- the Inno Setup installer executable and installer window

The project copies the complete Assets folder to build and publish output.
The installer build script also validates that the icon asset exists.

The existing PerMonitorV2 DPI fix and self-contained WinUI startup settings are preserved.
