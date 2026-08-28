Unregister-ScheduledTask -TaskName 'MCPBridgeDevLauncherAgent' -Confirm:$false -ErrorAction SilentlyContinue

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument '-WindowStyle Hidden -ExecutionPolicy Bypass -File C:\dev\launcher-agent.ps1'
$trigger = New-ScheduledTaskTrigger -AtLogOn -User 'nicholas'
$principal = New-ScheduledTaskPrincipal -UserId 'nicholas' -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName 'MCPBridgeDevLauncherAgent' -Action $action -Trigger $trigger -Principal $principal -Force

$verify = (Get-ScheduledTask -TaskName 'MCPBridgeDevLauncherAgent').Actions[0]
Write-Output "Registered. Execute=$($verify.Execute) Arguments=$($verify.Arguments)"

