# v6.22.4 build fix

This package contains one source-only compile fix in `windows-controller/FactoryTimer.Controller/MainViewModel.cs`.

The original v6.22.4 declaration passed a non-nullable `ForwardSyncAttemptCandidate` directly to `Dictionary.TryGetValue`, which triggers CS8600 under nullable warnings-as-errors because the `out` value is maybe-null on failure.

The lookup now declares the out value nullable and explicitly rejects null:

```csharp
if (!latestCandidate.TryGetValue(device.DeviceId, out ForwardSyncAttemptCandidate? candidate) ||
    candidate is null)
{
    ...
}
```

No synchronization estimator, pooling, retry, readiness, degraded-start, networking, or UI behavior was changed.

The WinUI `WMC9999` shown after CS8600 is expected to be a cascade from the failed C# compilation; rebuild the complete solution to confirm it clears.
