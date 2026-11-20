# Builds a release copy into %LocalAppData%\Programs\WindowRestorer and puts a shortcut on the desktop.
# Run again after code changes to update the installed copy: pwsh tools/install.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\WindowRestorer'
$exe = Join-Path $target 'WindowRestorer.exe'

# The installed exe can't be replaced while it runs (and an admin copy can't be stopped from here).
$running = Get-Process WindowRestorer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe -or -not $_.Path }
if ($running) {
    throw "Window Restorer is running. Exit it from its tray icon menu first, then run this again."
}

dotnet publish (Join-Path $root 'src/WindowRestorer') -c Release -o $target -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }

$desktop = [Environment]::GetFolderPath('Desktop')
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $desktop 'Window Restorer.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $target
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'Remembers and restores window positions across monitors'
$shortcut.Save()

Write-Output "Installed to $target"
Write-Output "Shortcut: $(Join-Path $desktop 'Window Restorer.lnk')"
