[CmdletBinding()]
param(
  [string]$Source = (Join-Path $PSScriptRoot '..\rhino-plugin\bin\Release\net7.0\package\UrbanBridgePlugin')
)

$Source = [System.IO.Path]::GetFullPath($Source)
$Target = Join-Path $env:APPDATA 'McNeel\Rhinoceros\8.0\Plug-ins\UrbanBridgePlugin'
if (-not (Test-Path (Join-Path $Source 'UrbanBridgePlugin.rhp'))) {
  throw "UrbanBridgePlugin.rhp was not found in: $Source`nBuild the Release bundle first: dotnet build rhino-plugin/UrbanBridgePlugin.csproj -c Release"
}
New-Item -ItemType Directory -Force -Path (Split-Path $Target) | Out-Null
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $Target
Copy-Item -Recurse -Force $Source $Target
Write-Host "UrbanBridge was installed to: $Target"
Write-Host 'Restart Rhino, then run UrbanBridgeStatus in the Rhino command line.'
