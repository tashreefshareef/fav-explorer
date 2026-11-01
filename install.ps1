# Installs Fav Explorer.
#
# Needs administrator rights for exactly one reason: Windows reads the list of icon
# overlay handlers from HKEY_LOCAL_MACHINE and nowhere else, so a per-user install
# is not possible. Everything the app does afterwards runs unelevated.
#
#   .\install.ps1                  install, then ask before restarting Explorer
#   .\install.ps1 -RestartExplorer restart Explorer without asking
#   .\install.ps1 -NoRestart       skip the restart (the heart won't draw until
#                                  the next sign-in)

[CmdletBinding()]
param(
    [switch]$RestartExplorer,
    [switch]$NoRestart,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

# ------------------------------------------------------------------ elevation --

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Fav Explorer needs administrator rights to register the overlay handler." -ForegroundColor Yellow
    Write-Host "Re-launching with elevation - approve the prompt Windows shows.`n"

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($RestartExplorer) { $argList += '-RestartExplorer' }
    if ($NoRestart)       { $argList += '-NoRestart' }
    if ($NoLaunch)        { $argList += '-NoLaunch' }

    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    return
}

# ------------------------------------------------------------------- payload --

$src = $PSScriptRoot
$dll = Join-Path $src 'HeartOverlay.dll'
$exe = Join-Path $src 'FavExplorer.exe'

# Run either from dist\ or straight from the repo root after a build.
if (-not (Test-Path $dll)) {
    $alt = Join-Path $src 'dist'
    if (Test-Path (Join-Path $alt 'HeartOverlay.dll')) {
        $src = $alt
        $dll = Join-Path $src 'HeartOverlay.dll'
        $exe = Join-Path $src 'FavExplorer.exe'
    }
}

foreach ($f in @($dll, $exe)) {
    if (-not (Test-Path $f)) { throw "Missing $f - run build.ps1 first." }
}

$target = Join-Path $env:ProgramFiles 'Fav Explorer'
Write-Host "Installing to $target" -ForegroundColor Cyan

# --------------------------------------------------------------- stop the app --

Get-Process -Name 'FavExplorer' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  stopping the running copy"
    $_.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 400
    if (-not $_.HasExited) { $_ | Stop-Process -Force }
}

New-Item -ItemType Directory -Force -Path $target | Out-Null

# --------------------------------------------------------------------- files --

function Copy-Payload {
    Get-ChildItem $src -File |
        Where-Object { $_.Extension -in '.exe', '.dll', '.json', '.ico', '.ps1' } |
        ForEach-Object { Copy-Item $_.FullName $target -Force -ErrorAction SilentlyContinue }
}

# Explorer keeps the old handler mapped and will not release it, so a reinstall has
# to bring Explorer down *before* the copy, not after. Doing this unconditionally is
# far more reliable than copying first and trying to recognise the failure: a locked
# copy does not always surface as an exception this script can catch, and the last
# time it didn't, the installer cheerfully reported success while leaving the
# previous DLL in place.
$explorerStoppedEarly = $false
$installedDll = Join-Path $target 'HeartOverlay.dll'
if (Test-Path $installedDll) {
    # Only the overlay handler is held open by Explorer. If it has not changed —
    # a tray-app-only update, say — there is nothing to unlock, and closing the
    # user's Explorer windows for no reason is just rude.
    $dllChanged = (Get-FileHash $dll).Hash -ne (Get-FileHash $installedDll).Hash
    if ($dllChanged) {
        Write-Host "  stopping Explorer so the old handler can be replaced"
        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        $explorerStoppedEarly = $true
    } else {
        Write-Host "  overlay handler unchanged - leaving Explorer alone" -ForegroundColor Green
        $script:SkipExplorerRestart = $true
    }
}

Copy-Payload

# Never take the copy on trust. Compare what landed against what we shipped, and
# fail loudly if they differ — a stale DLL is invisible from the outside and looks
# exactly like a bug in the handler.
$mismatch = @()
foreach ($name in @('HeartOverlay.dll', 'FavExplorer.exe')) {
    $a = Join-Path $src $name
    $b = Join-Path $target $name
    if (-not (Test-Path $b)) { $mismatch += "$name is missing from $target"; continue }
    if ((Get-FileHash $a).Hash -ne (Get-FileHash $b).Hash) {
        $mismatch += "$name did not get replaced (still $((Get-Item $b).Length) bytes, expected $((Get-Item $a).Length))"
    }
}

if ($mismatch.Count -gt 0) {
    Write-Host "`nINSTALL FAILED - the files on disk are not the ones being installed:" -ForegroundColor Red
    $mismatch | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host "`nSomething still has the old copy open. Sign out and back in, then re-run this script." -ForegroundColor Red
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
    throw "install verification failed"
}

Write-Host "  copied and verified $((Get-ChildItem $target -File).Count) files" -ForegroundColor Green

# ---------------------------------------------------------------- registering --

# The DLL registers itself: it writes its own CLSID and the overlay identifier key
# whose name decides where Fav Explorer ranks against OneDrive and friends.
$reg = Start-Process regsvr32.exe -ArgumentList '/s', "`"$(Join-Path $target 'HeartOverlay.dll')`"" -Wait -PassThru
if ($reg.ExitCode -ne 0) { throw "regsvr32 failed with exit code $($reg.ExitCode)" }
Write-Host "  registered the overlay handler" -ForegroundColor Green

# ---------------------------------------------------------------- diagnostics --

$overlayRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers'
$names = (Get-ChildItem $overlayRoot -ErrorAction SilentlyContinue | Select-Object -Expand PSChildName)
$sorted = $names | Sort-Object -CaseSensitive
$position = ([array]::IndexOf($sorted, '   FavExplorer')) + 1
$usable = 11

Write-Host "`nOverlay handlers Windows will consider, in order:" -ForegroundColor Cyan
for ($i = 0; $i -lt $sorted.Count; $i++) {
    $name = $sorted[$i]
    $mark = if ($name -eq '   FavExplorer') { '  <-- Fav Explorer' } else { '' }
    $note = if ($i -ge $usable) { '  (ignored - past the limit)' } else { '' }
    $colour = if ($name -eq '   FavExplorer') { if ($i -lt $usable) { 'Green' } else { 'Red' } }
              elseif ($i -ge $usable) { 'DarkGray' } else { 'Gray' }
    Write-Host ("  {0,2}. {1}{2}{3}" -f ($i + 1), $name, $mark, $note) -ForegroundColor $colour
}

if ($position -gt 0 -and $position -le $usable) {
    Write-Host "`nFav Explorer has slot $position of $($sorted.Count). The heart will draw." -ForegroundColor Green
} else {
    Write-Host "`nFav Explorer is ranked $position of $($sorted.Count), past the ~$usable Windows honours." -ForegroundColor Red
    Write-Host "Tagging will still work and your tags are safe, but no heart will appear" -ForegroundColor Red
    Write-Host "until one of the handlers listed above it is removed." -ForegroundColor Red
}

# ------------------------------------------------------------------- explorer --

# Explorer reads the handler list once, at startup. Until it restarts, the freshly
# registered handler may as well not exist.
if (-not $explorerStoppedEarly -and -not $NoRestart -and -not $SkipExplorerRestart) {
    $doRestart = $RestartExplorer
    if (-not $doRestart) {
        Write-Host "`nExplorer has to restart to pick up the new handler." -ForegroundColor Yellow
        Write-Host "This closes any open File Explorer windows. The desktop and taskbar come straight back."
        $answer = Read-Host "Restart Explorer now? [Y/n]"
        $doRestart = ($answer -eq '' -or $answer -match '^[Yy]')
    }

    if ($doRestart) {
        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        $explorerStoppedEarly = $true
    } else {
        Write-Host "Skipped. The heart will start drawing after your next sign-in." -ForegroundColor Yellow
    }
}

if ($explorerStoppedEarly) {
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        Start-Process explorer.exe
    }
    Write-Host "  Explorer restarted" -ForegroundColor Green
}

# ---------------------------------------------------------------------- start --

if (-not $NoLaunch) {
    # Launched unelevated on purpose. The tray app has no business running as
    # administrator, and a hook installed from an elevated process behaves oddly
    # against normal-integrity windows. Handing the path to explorer.exe is what
    # drops the privilege.
    $launcher = Join-Path $target 'FavExplorer.exe'

    # But explorer.exe has to be up to accept that hand-off, and we may have only
    # just restarted it. Launching into a shell that is still starting fails
    # silently - which is exactly what happened twice during development, leaving
    # the app installed but not running.
    for ($i = 0; $i -lt 20; $i++) {
        if (Get-Process -Name explorer -ErrorAction SilentlyContinue) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Milliseconds 1500

    $started = $false
    foreach ($attempt in 1..3) {
        Start-Process explorer.exe -ArgumentList "`"$launcher`""
        for ($i = 0; $i -lt 10; $i++) {
            Start-Sleep -Milliseconds 500
            if (Get-Process -Name 'FavExplorer' -ErrorAction SilentlyContinue) { $started = $true; break }
        }
        if ($started) { break }
        Write-Host "  launch attempt $attempt didn't take, retrying" -ForegroundColor Yellow
    }

    if ($started) {
        Write-Host "`nFav Explorer is running - look for the heart in the notification area." -ForegroundColor Green
    } else {
        Write-Host "`nInstalled, but Fav Explorer did not start. Launch it yourself from:" -ForegroundColor Yellow
        Write-Host "  $launcher" -ForegroundColor Yellow
    }
}

Write-Host "`nSelect photos in File Explorer and press Ctrl+Shift+F." -ForegroundColor Green
Write-Host "To remove it later: $target\uninstall.ps1"
