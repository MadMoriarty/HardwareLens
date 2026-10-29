$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/HardwareLens.App/obj/project.assets.json') -Raw | ConvertFrom-Json
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
foreach ($library in $assets.libraries.PSObject.Properties) {
  if ($library.Value.type -ne 'package') { continue }
  $source = Join-Path $packageRoot $library.Value.path
  $destination = Join-Path $projectRoot ('licenses/packages/' + $library.Name.Replace('/', '-'))
  foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    if ($file.Name -match '(?i)license|licence|notice|copying|\.nuspec$') {
      $target = Join-Path $destination $file.FullName.Substring($source.Length).TrimStart('\', '/')
      New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
      Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
  }
}
$runtimeRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
  $source = Join-Path $runtimeRoot $name
  if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $projectRoot ('licenses/dotnet-' + $name)) -Force }
}
