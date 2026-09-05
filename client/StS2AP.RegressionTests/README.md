# C# integration and packaging tests

This is an xUnit project. Run it from the repository root:

```powershell
dotnet test client/StS2AP.RegressionTests/StS2AP.RegressionTests.csproj -c Release
```

Three interop cases check a successful C# adapter call and both exception conversions.
Domain rules and generated-input tests live in `StS2AP.Domain.Tests` (F#).
The existing C# replica-construction regression is also preserved here.
These tests reference the domain library and link the small production C# helpers;
they do not build the game client or require a game installation.

Packaging tests need built artifacts. Without the corresponding environment variable,
xUnit reports them as skipped. A supplied path that is missing or invalid fails the test.

```powershell
# Check an intermediate or output client DLL. Repeat for each compatibility variant.
$env:STS2AP_TEST_ASSEMBLY = (Resolve-Path 'client/StS2AP/obj/0.111.0/Debug/net9.0/Archipelago.dll').Path
dotnet test client/StS2AP.RegressionTests/StS2AP.RegressionTests.csproj -c Release --filter 'Category=Manifest'

# Check a complete bundle from a full client build, including both variants and the loader.
$env:STS2AP_TEST_BUNDLE = (Resolve-Path 'artifacts/fsharp-trial-game/mods/Archipelago').Path
dotnet test client/StS2AP.RegressionTests/StS2AP.RegressionTests.csproj -c Release --filter 'Category=Bundle'

Remove-Item Env:STS2AP_TEST_ASSEMBLY, Env:STS2AP_TEST_BUNDLE
```

The manifest test opens both embedded JSON manifests and checks their version fields.
The bundle test also checks the external mod version and uses the shipped loader to call
the C# adapter in both variants. It verifies that FSharp.Core and the domain DLL load from
the bundle, so the test runner cannot hide a missing packaged dependency.
Neither test starts Godot or proves in-game behavior.

CI runs the interop/construction tests on Windows and Linux. The compatibility workflow
runs the manifest test after compile-only validation and again after an incremental build
for both game API versions. The complete bundle test runs locally against a full build.
