# Local Windows 10 version 2004 build

The Windows desktop executable built from this fork has `<CETCompat>false</CETCompat>` in `QuiverLauncher.Desktop.csproj`. This opts its .NET apphost out of hardware-enforced shadow stack compatibility. It addresses the .NET 9+ CET-related startup failure seen on some Windows 10 systems; it does not change the Windows version or other dependencies required by the application. CET protection for this process is reduced.

## Build with Visual Studio 2022 installed

Quiver targets `net10.0`. Visual Studio 2022 does not officially support targeting .NET 10 inside the IDE. You can keep VS 2022 for editing and use the **standalone .NET 10 SDK** from a regular PowerShell terminal for the build. Check that `dotnet --version` reports `10.x` before proceeding. Do not use the VS 2022 `MSBuild.exe` or rely on Build Solution for this project.

From the repository root in PowerShell:

```powershell
dotnet --version
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\publish-win10-2004.ps1
$env:QuiverLauncher_SKIP_UPDATES = "1"
.\artifacts\win10-2004\QuiverLauncher.exe
```

The helper works around a separate build-time CET failure in the SDK's compiler apphost. It copies the selected SDK's Roslyn directory to a temporary folder, removes the compiler EXE hosts from that copy, and selects the copied compiler tasks with shared compilation disabled. Roslyn then invokes its managed compiler DLL through `dotnet.exe`. The installed SDK is not modified. `ExecutionPolicy Bypass` applies only to this PowerShell process. This workaround has been checked against Roslyn's compiler-selection source but still requires a build test on the affected Windows installation.

Run the `QuiverLauncher.exe` **inside the publish directory**. The published folder needs its accompanying DLLs and runtime files; keep the whole folder together. `QuiverLauncher_SKIP_UPDATES=1` keeps this local build from replacing itself with an upstream release. If you open a new PowerShell window to run the app, set that variable again in the new window.

Do not test the change using the release ZIP's top-level `QuiverLauncher.exe`: that is a separate Velopack launcher stub, built outside this project. If the direct published executable starts but the packaged release still fails, its stub or another native component needs separate investigation.

The .NET 10 supported-Windows list does not include the consumer Windows 10 version 2004 release. This build is intended for a practical compatibility test on that system, not a guarantee that all components will work. If it still fails, record the exact dialog text or Event Viewer application error and whether `QuiverLauncher.Desktop.exe` (also in the publish folder) behaves differently.
