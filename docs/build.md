# Build and architecture

## Prerequisites

Use Windows x64, the .NET 10 SDK selected by `global.json` (10.0.400 with latest feature roll-forward), and NuGet access for the first restore. A newer compatible 10.0 feature SDK is allowed. PowerShell runs the packaging script.

```powershell
dotnet restore HardwareLens.slnx
dotnet test tests/HardwareLens.Tests/HardwareLens.Tests.csproj -c Release
dotnet run --project src/HardwareLens.App/HardwareLens.App.csproj -c Release
./tools/build.ps1
```

The portable ZIP is `artifacts/HardwareLens-win-x64.zip`; the executable is `artifacts/portable/HardwareLens.exe`. Builds are self-contained, target win-x64, and include native libraries for self-extraction. Keep the packaged licenses and documentation with the executable.

## Projects

| Project | Responsibility |
| --- | --- |
| HardwareLens.Core | Models, time series, observations, reports/diffs, settings, and recording. |
| HardwareLens.Windows | Native Windows collectors, inventory, process control, startup, and service access. |
| HardwareLens.App | Avalonia desktop UI, navigation, charts, dialogs, and mini monitor. |
| HardwareLens.Probe | Developer checks against Windows data sources; can produce private machine data. |
| HardwareLens.Tests | Core behavior tests; not a full native management or device compatibility suite. |

`tools/independent-check.ps1` and `tools/probe-windows-sources.ps1` are developer diagnostics. Inspect their parameters first and keep outputs private. Do not commit recordings, diagnostic inventories, or exports.

## Documentation screenshot

```powershell
dotnet run --project src/HardwareLens.App/HardwareLens.App.csproj -c Release -- --screenshot docs/screenshots/performance.png
```

This opens the actual app, waits for initial readings, selects Performance, renders the control tree, and closes. It collects real local data and can write normal settings/incident files. Review the PNG for private information before publishing. This is a documentation capture mode, not synthetic data.

## Verification scope

Run core tests and a release build before submitting changes. Native counters and management behavior require Windows checks on suitable test machines. Do not terminate arbitrary processes or alter services just to verify a documentation change. A passing core suite does not establish hardware compatibility or safety for all management operations.
