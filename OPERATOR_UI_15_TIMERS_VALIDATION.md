# Operator UI 15-timer validation

- PASS — 15 configured protocol slots
- PASS — ESP15 packet routing is dynamic
- PASS — operator display name conversion exists
- PASS — select all method exists
- PASS — deselect all method exists
- PASS — individual checkbox binding exists
- PASS — operator device cards use display names
- PASS — diagnostic headings removed from visible XAML
- PASS — network interface remains operator-accessible
- PASS — start/reset remain visible
- PASS — brightness remains visible
- PASS — existing five selected by default
- PASS — new ten deselected by default

## Build limitation

The .NET/Windows App SDK toolchain is not installed in this execution environment, so the WinUI project was not compiled here. The XAML was parsed as XML and the source was statically checked. Build and run the normal Release test suite on the Windows development machine before release.

## Compatibility

Operator labels are `TIMER01..TIMER15`; wire/protocol identities remain `ESP01..ESP15`. Existing `ESP01..ESP05` firmware therefore remains compatible. To use TIMER06..TIMER15, configure those physical devices with firmware IDs `ESP06..ESP15`.
