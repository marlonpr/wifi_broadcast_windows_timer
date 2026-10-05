# v6.22.6 validation performed in this environment

Completed:

- v6.22.4 pooled-sync source contract: 14/14 PASS.
- fleet-health source contract updated for 31-field STATUS: 11/11 PASS.
- qualification guard unit test: disciplined-rate sign, same-boot rejection, inferred-missing/holdover rejection, and explicit warm confirmation all PASS.
- legacy 30-minute disciplined-rate arithmetic test: PASS.
- synthetic two-device physical-instrument replay: 1801 COMMIT boundaries, expected pair slope +0.247222 ppm, fitted analyzer slope +0.247230 ppm, disagreement +0.000008 ppm, <=0.05 ppm criterion PASS.

Not run here:

- `dotnet build` / MSTest, because the .NET SDK is not installed in this environment.
- physical 30-minute ESP01/ESP02 Analyzer_v8 qualification comparison. That is the next required hardware validation before screening the new RTC batch.
