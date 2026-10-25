#Requires -Version 5.1
<#
  Збирає Razer Island: self-contained публікація (win-x64) і інсталятор Inno Setup.
    .\build.ps1                # публікація + інсталятор
    .\build.ps1 -SkipInstaller # лише публікація в artifacts\publish
#>
param([switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\RazerIsland\RazerIsland.csproj') -c Release -r win-x64 --self-contained -o $publish -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершився з помилкою' }

if ($SkipInstaller) { return }

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Не знайдено ISCC.exe. Встанови Inno Setup 6: winget install JRSoftware.InnoSetup' }

& $iscc /Qp (Join-Path $root 'installer\RazerIsland.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC завершився з помилкою' }
Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter *.exe | Select-Object Name, @{ n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 1) } }
