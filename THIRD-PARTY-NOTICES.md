# Third-party notices

HardwareLens source is MIT licensed. Its dependencies retain their respective licenses.

- Avalonia UI (including desktop platform, Fluent theme, and DataGrid packages): MIT, https://github.com/AvaloniaUI/Avalonia
- .NET runtime and Microsoft libraries: see the applicable NuGet license metadata and the .NET runtime notices, https://github.com/dotnet/runtime
- Transitive graphics/native dependencies, including SkiaSharp and HarfBuzzSharp: see package license files and third-party notices included in `licenses/` when packaging.
- xUnit and Microsoft.NET.Test.Sdk are development/test dependencies; they are not part of the application UI.

The packaging script copies available license files and NuGet package metadata for dependencies resolved by the app. Preserve that directory in redistribution. NuGet metadata identifies upstream license expressions/URLs where a package does not include a separate license file. Review the exact resolved dependency versions when creating a release; this summary does not replace their terms.
