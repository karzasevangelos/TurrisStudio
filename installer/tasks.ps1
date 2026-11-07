# Creates (or removes) the scheduled tasks that start Turris Studio with full rights without a UAC prompt.
# Full rights are needed to read CPU temperatures through the built-in sensor driver.
#   "Turris Studio"           - at sign-in, starts hidden in the tray
#   "Turris Studio Launcher"  - on demand; the Start-menu shortcut uses it to open the app with full rights
param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$names = 'Turris Studio', 'Turris Studio Launcher'

if ($Remove) {
    foreach ($n in $names) {
        Unregister-ScheduledTask -TaskName $n -Confirm:$false -ErrorAction SilentlyContinue
    }
    exit 0
}

$user = "$env:USERDOMAIN\$env:USERNAME"
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
# No time limit (the default would stop the app after 3 days), normal priority, laptops on battery too.
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -Priority 4 -StartWhenAvailable

$startup = New-ScheduledTaskAction -Execute $AppPath -Argument '--minimized --via-task'
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$trigger.Delay = 'PT5S'
Register-ScheduledTask -TaskName $names[0] -Action $startup -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'Starts Turris Studio in the tray when you sign in, so the cooler screen shows your stats.' -Force | Out-Null

# The launcher may run while an earlier launch is still the running app, so allow parallel runs.
$launchSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -MultipleInstances Parallel -Priority 4
$launch = New-ScheduledTaskAction -Execute $AppPath -Argument '--via-task'
Register-ScheduledTask -TaskName $names[1] -Action $launch -Principal $principal -Settings $launchSettings `
    -Description 'Opens Turris Studio with access to CPU temperature sensors.' -Force | Out-Null
