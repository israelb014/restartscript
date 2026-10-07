<#
    Safety cleanup for CI: removes every task and folder created by the app's tests
    (\ScheduledRestartTest, \ScheduledRestartForeignTest, \ScheduledRestartSmoke), at any depth.
    Never touches anything else. Always exits 0.
#>
$ErrorActionPreference = 'Continue'
$service = New-Object -ComObject Schedule.Service
$service.Connect()

function Clear-Folder($folder) {
    foreach ($sub in @($folder.GetFolders(0))) {
        Clear-Folder $sub
        $folder.DeleteFolder($sub.Name, 0)
    }
    foreach ($task in @($folder.GetTasks(1))) {
        $folder.DeleteTask($task.Name, 0)
        Write-Host "Deleted leftover test task $($task.Path)"
    }
}

foreach ($name in 'ScheduledRestartTest', 'ScheduledRestartForeignTest', 'ScheduledRestartSmoke') {
    try { $folder = $service.GetFolder("\$name") } catch { continue }
    try {
        Clear-Folder $folder
        $service.GetFolder('\').DeleteFolder($name, 0)
        Write-Host "Removed test folder \$name"
    } catch { Write-Host "Cleanup of \$name failed: $($_.Exception.Message)" }
}
exit 0
