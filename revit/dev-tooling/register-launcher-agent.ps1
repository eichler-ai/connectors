# The VM's interactive user the agent should run as. This script is run BY that user in their
# own session (the task it registers is Interactive/AtLogOn for them), so $env:USERNAME is the
# right default -- unlike redeploy-and-verify.ps1, which runs via `prlctl exec` as SYSTEM and
# must detect the console user instead.
param(
    [string]$InteractiveUser = $env:USERNAME
)

Unregister-ScheduledTask -TaskName 'MCPBridgeDevLauncherAgent' -Confirm:$false -ErrorAction SilentlyContinue

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument '-WindowStyle Hidden -ExecutionPolicy Bypass -File C:\dev\launcher-agent.ps1'
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $InteractiveUser
$principal = New-ScheduledTaskPrincipal -UserId $InteractiveUser -LogonType Interactive -RunLevel Limited

# Review finding (issue #26 PR, independent review): default TaskSettingsSet is wrong for a script
# meant to run in `while ($true)` indefinitely. ExecutionTimeLimit defaults to PT72H -- after 3 days
# of VM uptime, Task Scheduler kills the agent outright, and every signal drop after that is
# silently ignored until the next logon, which is indistinguishable from "the signal didn't work"
# (the exact diagnostic hole the agent's own logging exists to close). DisallowStartIfOnBatteries /
# StopIfGoingOnBatteries default to $true -- already a documented gotcha for THIS task specifically
# (Parallels passes the Mac's battery state through to the guest), so setting it here at registration
# time closes the gap at its source instead of relying on a later manual Set-ScheduledTask fix-up.
# RestartCount/RestartInterval give the loop a shot at recovering on its own if it ever does exit
# unexpectedly, rather than staying down until someone notices and restarts it by hand.
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName 'MCPBridgeDevLauncherAgent' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force

$verify = (Get-ScheduledTask -TaskName 'MCPBridgeDevLauncherAgent').Actions[0]
Write-Output "Registered. Execute=$($verify.Execute) Arguments=$($verify.Arguments)"
