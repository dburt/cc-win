# Creates a Start Menu shortcut for the installed app and tries to pin it to the taskbar.
#
# Windows 11 blocks programmatic taskbar pinning for ordinary Win32 apps (the shell's
# "taskbarpin" verb is hidden), so the pin is attempted and then verified rather than
# assumed. If it does not take, the script says so and tells you the manual step.
param(
    [string]$Exe  = (Join-Path $env:LOCALAPPDATA 'ClaudeSessions\ClaudeSessions.exe'),
    [string]$Name = 'Claude Session History'
)

if (-not (Test-Path $Exe)) { throw "Not built yet: $Exe" }

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$lnk = Join-Path $startMenu "$Name.lnk"

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath       = $Exe
$sc.WorkingDirectory = Split-Path $Exe
$sc.IconLocation     = "$Exe,0"
$sc.Description      = 'Browse Claude Code session history live'
$sc.Save()
Write-Host "Start Menu shortcut: $lnk"

# Attempt the pin.
$pinned = $false
try {
    $app = New-Object -ComObject Shell.Application
    $item = $app.Namespace((Split-Path $lnk)).ParseName((Split-Path $lnk -Leaf))
    $verb = $item.Verbs() | Where-Object { $_.Name -replace '&', '' -match 'Pin to tas?k ?bar' }
    if ($verb) { $verb.DoIt(); Start-Sleep -Milliseconds 900; $pinned = $true }
} catch { }

# Verify against the folder the shell actually keeps pinned taskbar shortcuts in.
$pinDir = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
$onBar = (Test-Path $pinDir) -and (Get-ChildItem $pinDir -Filter *.lnk -ErrorAction SilentlyContinue |
    Where-Object { (New-Object -ComObject WScript.Shell).CreateShortcut($_.FullName).TargetPath -eq $Exe })

if ($onBar) {
    Write-Host "PINNED: the app is on the taskbar."
} else {
    Write-Host "NOT PINNED: Windows blocked programmatic pinning (expected on Windows 11)."
    Write-Host "To pin it: launch the app, then right-click its taskbar button -> Pin to taskbar."
}
