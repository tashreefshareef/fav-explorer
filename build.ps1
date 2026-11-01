# Builds both halves of Fav Explorer into dist\.
#
#   dist\HeartOverlay.dll   the native badge handler that loads into explorer.exe
#   dist\FavExplorer.exe    the tray app that owns the shortcut and the tagging
#
# Needs: MSVC C++ toolset, Windows SDK, CMake, .NET 10 SDK. Nothing else.

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Config = 'Release',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$dist  = Join-Path $root 'dist'
$build = Join-Path $root 'build'

function Step($msg) { Write-Host "`n=== $msg" -ForegroundColor Cyan }

if ($Clean) {
    Step "Cleaning"
    foreach ($d in @($build, $dist)) {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    }
}

# dist\ is always rebuilt from scratch: the installer packs everything in it, so a
# file left over from an earlier build would ship to users.
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$installerOut = Join-Path $build 'installer'
if (Test-Path $installerOut) { Remove-Item $installerOut -Recurse -Force }

Step "Icons"
& (Join-Path $root 'tools\make-icons.ps1')

Step "Overlay handler (C++)"
& cmake -S (Join-Path $root 'src\overlay') -B (Join-Path $build 'overlay') -A x64 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }

& cmake --build (Join-Path $build 'overlay') --config $Config | ForEach-Object {
    if ($_ -match 'error|warning') { Write-Host "  $_" -ForegroundColor Yellow }
}
if ($LASTEXITCODE -ne 0) { throw "CMake build failed" }

$dll = Join-Path $build "overlay\$Config\HeartOverlay.dll"
if (-not (Test-Path $dll)) { throw "HeartOverlay.dll was not produced" }
Copy-Item $dll $dist -Force
Write-Host ("  HeartOverlay.dll  {0:N0} bytes" -f (Get-Item $dll).Length)

Step "Tray app (C#)"
$trayOut = Join-Path $build 'tray'
if (Test-Path $trayOut) { Remove-Item $trayOut -Recurse -Force }   # everything in it is copied below
& dotnet publish (Join-Path $root 'src\tray\FavExplorer.csproj') `
    -c $Config -r win-x64 --self-contained true `
    -o $trayOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# The publish folder carries pdbs and the .NET host's bookkeeping; the installer
# only needs what actually runs.
Get-ChildItem $trayOut -File |
    Where-Object { $_.Extension -in '.exe', '.dll', '.json' } |
    ForEach-Object { Copy-Item $_.FullName $dist -Force }

Copy-Item (Join-Path $root 'assets\app.ico') $dist -Force

Step "Installer scripts"
Copy-Item (Join-Path $root 'install.ps1')   $dist -Force
Copy-Item (Join-Path $root 'uninstall.ps1') $dist -Force


Step "Installer (Inno Setup)"
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc /Q (Join-Path $root 'installer\favexplorer.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed" }
    Get-ChildItem (Join-Path $build 'installer') -Filter *.exe |
        ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
} else {
    Write-Host "  Inno Setup 6 not found - skipping installer (winget install JRSoftware.InnoSetup)" -ForegroundColor Yellow
}
Step "Done"
Get-ChildItem $dist -File | Sort-Object Name |
    Format-Table @{L='Name';E={$_.Name}}, @{L='Size';E={'{0,10:N0}' -f $_.Length}} -AutoSize

Write-Host "`nNext: run dist\install.ps1 as administrator." -ForegroundColor Green
