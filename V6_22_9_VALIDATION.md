# v6.22.9 validation

Validated in the assistant environment:

- 42-field STATUS parser/source contract: PASS (12/12)
- legacy 31-field STATUS compatibility retained by source inspection: PASS
- fleet CSV aggregation tool synthetic replay: PASS
- Python tools compile: PASS

The assistant environment does not contain the .NET SDK, so the normal Windows
validation remains required:

```powershell
dotnet restore
dotnet test tests\FactoryTimer.Protocol.Tests\FactoryTimer.Protocol.Tests.csproj -c Release
dotnet test tests\FactoryTimer.Controller.Core.Tests\FactoryTimer.Controller.Core.Tests.csproj -c Release
dotnet build FactoryTimer.slnx -c Release
```

The new protocol regression test is `ParsesFleetCpu0MonitorStatus`.
