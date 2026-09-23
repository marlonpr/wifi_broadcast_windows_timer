# Published WinUI startup crash fix

The previous publish could terminate immediately with Windows Event Viewer reporting:

- `Microsoft.UI.Xaml.dll`
- exception `0xc000027b`
- stowed HRESULT `0x80004005`

The controller was already unpackaged and Windows App SDK self-contained, but the app project/publish profile did not enable MSIX tooling. A current Microsoft Q&A case with the same Windows App SDK 2.3.1 + .NET 10 publish-only crash was resolved by using all of these settings together:

```xml
<WindowsPackageType>None</WindowsPackageType>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<EnableMsixTooling>true</EnableMsixTooling>
<SelfContained>true</SelfContained>
```

This revision adds those settings, keeps trimming/single-file/ReadyToRun disabled, and adds a 5-second post-publish smoke test before Inno Setup is allowed to build the installer.
