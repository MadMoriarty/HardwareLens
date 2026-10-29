# Builds the portable single-file app into artifacts\portable (requires the .NET 10 SDK).
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
if (-not $SkipTests) {
  dotnet test tests/HardwareLens.Tests/HardwareLens.Tests.csproj -c Release
  if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}
dotnet publish "$root\src\HardwareLens.App\HardwareLens.App.csproj" -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -o "$root\artifacts\portable"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
& (Join-Path $PSScriptRoot 'collect-licenses.ps1')
$packageFolder = Join-Path $root 'artifacts/package'
New-Item -ItemType Directory -Path $packageFolder -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'artifacts/portable/HardwareLens.exe') -Destination $packageFolder -Force
foreach ($name in @('README.md', 'LICENSE', 'SECURITY.md', 'THIRD-PARTY-NOTICES.md', 'docs', 'licenses')) {
  Copy-Item -LiteralPath (Join-Path $root $name) -Destination $packageFolder -Recurse -Force
}
$packageFiles = @('HardwareLens.exe', 'README.md', 'LICENSE', 'SECURITY.md', 'THIRD-PARTY-NOTICES.md', 'docs', 'licenses') | ForEach-Object { Join-Path $packageFolder $_ }
Compress-Archive -LiteralPath $packageFiles -DestinationPath (Join-Path $root 'artifacts/HardwareLens-win-x64.zip') -Force
Get-FileHash -LiteralPath (Join-Path $root 'artifacts/HardwareLens-win-x64.zip') -Algorithm SHA256
