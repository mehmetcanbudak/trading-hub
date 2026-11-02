# Build self-contained Windows x64 and ARM64 installers using .NET SDK 10 and NSIS.
# Install NSIS with: winget install NSIS.NSIS
# Usage: .\build-windows.ps1 [-Version 1.2.0]
param([string]$Version = "")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
if (-not $Version) {
  $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
}
$makensis = Get-Command makensis -ErrorAction SilentlyContinue
$makensis = if ($makensis) { $makensis.Source } else { Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe" }
if (-not (Test-Path $makensis)) { throw "NSIS is required: install it with 'winget install NSIS.NSIS'." }
New-Item -ItemType Directory -Force dist | Out-Null
foreach ($arch in "x64", "arm64") {
  $out = "dist\win-$arch"
  if (Test-Path $out) { Remove-Item -Recurse -Force $out }
  dotnet publish src\TradingHub.App\TradingHub.App.csproj -c Release -r "win-$arch" --self-contained true -o $out -v q "-p:Version=$Version"
  if ($LASTEXITCODE) { throw "dotnet publish failed ($arch)." }
  & $makensis -V2 "-DVERSION=$Version" "-DARCH=$arch" "-DSRC=$PWD\$out" "-DOUT=$PWD\dist\TradingHub-Setup-$arch.exe" installer\TradingHub.nsi
  if ($LASTEXITCODE) { throw "makensis failed ($arch)." }
  Write-Host "Built: dist\TradingHub-Setup-$arch.exe"
}
