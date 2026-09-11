[CmdletBinding()]
param(
  [string]$Source = (Join-Path $PSScriptRoot '..\rhino-plugin\bin\Release\net7.0\package\UrbanBridgePlugin')
)

$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$DefaultSource = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'rhino-plugin\bin\Release\net7.0\package\UrbanBridgePlugin'))
$Source = [System.IO.Path]::GetFullPath($Source)
$Target = Join-Path $env:APPDATA 'McNeel\Rhinoceros\8.0\Plug-ins\UrbanBridgePlugin'

if (-not (Test-Path (Join-Path $Source 'UrbanBridgePlugin.rhp')) -and $Source -eq $DefaultSource) {
  if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Write-Host 'UrbanBridge bundle not found; building the Release bundle...'
    & dotnet build (Join-Path $RepositoryRoot 'rhino-plugin\UrbanBridgePlugin.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'UrbanBridge build failed.' }
  } else {
    throw "UrbanBridgePlugin.rhp was not found in: $Source`nInstall .NET SDK 7.x and run this installer again, or pass the folder containing UrbanBridgePlugin.rhp with -Source."
  }
}

if (-not (Test-Path (Join-Path $Source 'UrbanBridgePlugin.rhp'))) {
  throw "UrbanBridgePlugin.rhp was not found in: $Source`nPass the folder that contains UrbanBridgePlugin.rhp, not the .rhp file itself."
}

New-Item -ItemType Directory -Force -Path (Split-Path $Target) | Out-Null
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $Target
Copy-Item -Recurse -Force $Source $Target
Write-Host "UrbanBridge was installed to: $Target"
Write-Host 'Restart Rhino, then run UrbanBridgeStatus in the Rhino command line.'
