<#
    Integration tests for Set-ScheduledRestart.ps1 against the REAL Task Scheduler (no COM mocks).
    Run elevated in Windows PowerShell 5.1 on a disposable machine (used by .github/workflows/windows-test.yml):

        powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Invoke-IntegrationTest.ps1

    Safety: every schedule is placed at least 60 minutes in the future and is checked against Task
    Scheduler's own NextRunTime after each registration. If any registered task would run sooner, it is
    deleted immediately and the test fails. The task is always deleted in the finally block, so the
    shutdown.exe action never runs.

    The forced verification failure (scenario 3) wraps the script's own comparison function so that it
    reports one extra difference; every Task Scheduler call is still real.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -ChildPath 'Set-ScheduledRestart.ps1'
. $scriptPath

$script:Failures = 0
$script:Passes = 0
$script:SafetyMinutes = 60
$script:TestStart = Get-Date

function Assert-That {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    if ($Condition) {
        $script:Passes++
        Write-Host ('[PASS] {0}' -f $Name) -ForegroundColor Green
    }
    else {
        $script:Failures++
        Write-Host ('[FAIL] {0} {1}' -f $Name, $Detail) -ForegroundColor Red
    }
}

function Assert-SafeNextRun {
    <# Deletes the task and throws if Task Scheduler would run it within the safety window. #>
    $info = Get-RestartTaskInfo
    if ($null -ne $info -and $null -ne $info.NextRunTime -and $info.NextRunTime -lt (Get-Date).AddMinutes($script:SafetyMinutes)) {
        $null = Remove-RestartTask
        throw ('SAFETY STOP: task next run {0} is within {1} minutes. Task deleted.' -f $info.NextRunTime, $script:SafetyMinutes)
    }
}

function Get-TriggerXml {
    <# Returns the <Triggers> element of the registered task as XML text. #>
    $xml = [xml](Get-RestartTaskXml)
    return $xml.Task.Triggers.OuterXml
}

function Get-TestTime {
    <# A whole hour at least 2 hours from now, so no occurrence can fall inside the test run. #>
    return [timespan]::FromHours(((Get-Date).Hour + 3) % 24)
}

function New-TestSchedule {
    param([string]$Kind)
    switch ($Kind) {
        'Once' {
            $s = Get-ScheduleTemplate -Type 'Once'
            $s.StartDate = (Get-Date).Date.AddDays(7)
        }
        'Weekly2' {
            $s = Get-ScheduleTemplate -Type 'Weekly'
            $s.DaysOfWeek = Get-SortedWeekday -Day ([System.DayOfWeek[]]@((Get-Date).DayOfWeek, (Get-Date).AddDays(3).DayOfWeek))
        }
        'Monthly15' {
            $s = Get-ScheduleTemplate -Type 'MonthlyDay'
            $s.MonthDays = [int[]]@(15)
        }
        'LastDay' {
            $s = Get-ScheduleTemplate -Type 'MonthlyDay'
            $s.LastDayOfMonth = $true
        }
        'LastFriday' {
            $s = Get-ScheduleTemplate -Type 'MonthlyWeekday'
            $s.WeeksOfMonth = [int[]]@(5)
            $s.DaysOfWeek = [System.DayOfWeek[]]@([System.DayOfWeek]::Friday)
        }
    }
    $s.Time = Get-TestTime
    $next = Get-NextRunTime -Schedule $s
    if ($null -eq $next -or $next -lt (Get-Date).AddMinutes($script:SafetyMinutes)) {
        throw ('SAFETY STOP: calculated next run for {0} is {1}, inside the safety window.' -f $Kind, $next)
    }
    return $s
}

function Invoke-Registration {
    param($Schedule, $Existing)
    $result = Invoke-ScheduleRegistration -Schedule $Schedule -Existing $Existing
    Assert-SafeNextRun
    return $result
}

$originalDifference = ${function:Get-ScheduleDifference}

try {
    Write-Host ('PowerShell {0} ({1}), elevated: {2}' -f $PSVersionTable.PSVersion, $PSVersionTable.PSEdition, (Test-IsAdministrator)) -ForegroundColor Cyan
    Assert-That 'Running on Windows PowerShell 5.1' ($PSVersionTable.PSVersion.Major -eq 5 -and $PSVersionTable.PSVersion.Minor -eq 1)
    Assert-That 'Running elevated' (Test-IsAdministrator)

    if ($null -ne (Get-RestartTaskInfo)) { $null = Remove-RestartTask }

    # 1. Create each schedule type from scratch.
    Write-Host "`n=== 1. Create each schedule type ===" -ForegroundColor Cyan
    $createdCount = 0
    foreach ($kind in @('Once', 'Weekly2', 'Monthly15', 'LastDay', 'LastFriday')) {
        $schedule = New-TestSchedule -Kind $kind
        $result = Invoke-Registration -Schedule $schedule -Existing $null
        Assert-That ('{0}: returns Verified' -f $kind) ($result -is [string] -and $result -eq 'Verified') ('(got: {0})' -f ($result -join ','))
        $info = Get-RestartTaskInfo
        Assert-That ('{0}: task exists with summary "{1}"' -f $kind, (Get-ScheduleSummary -Schedule $schedule)) ($null -ne $info -and $info.Summary -eq (Get-ScheduleSummary -Schedule $schedule)) ('(found: {0})' -f $(if ($info) { $info.Summary } else { 'none' }))
        $xml = Get-RestartTaskXml
        Assert-That ('{0}: runs as SYSTEM, highest privileges' -f $kind) ($xml -match '<UserId>S-1-5-18</UserId>' -and $xml -match '<RunLevel>HighestAvailable</RunLevel>')
        Assert-That ('{0}: shutdown.exe action' -f $kind) ($xml -match [regex]::Escape('<Command>C:\Windows\System32\shutdown.exe</Command>') -and $xml -match [regex]::Escape('/r /f /t 0 /d p:4:1'))
        Assert-That ('{0}: WakeToRun, 10 min limit, IgnoreNew' -f $kind) ($xml -match '<WakeToRun>true</WakeToRun>' -and $xml -match '<ExecutionTimeLimit>PT10M</ExecutionTimeLimit>' -and $xml -match '<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>')
        Assert-That ('{0}: StartWhenAvailable is not enabled' -f $kind) ($xml -notmatch '<StartWhenAvailable>true</StartWhenAvailable>')
        if ($kind -eq 'LastDay') { Assert-That 'LastDay: XML uses <Day>Last</Day>' ($xml -match '<Day>Last</Day>') }
        if ($kind -eq 'LastFriday') { Assert-That 'LastFriday: XML uses <Week>Last</Week>' ($xml -match '<Week>Last</Week>' -and $xml -match '<Friday />') }
        if ($kind -eq 'Weekly2') { Assert-That 'Weekly2: two weekdays registered' (@($info.Schedule.DaysOfWeek).Count -eq 2) }
        if ($result -eq 'Verified') { $createdCount++ }
        $null = Remove-RestartTask
    }

    # 2. Replace an existing schedule.
    Write-Host "`n=== 2. Replace an existing schedule ===" -ForegroundColor Cyan
    $weekly = New-TestSchedule -Kind 'Weekly2'
    Assert-That 'Replace: initial weekly schedule Verified' ((Invoke-Registration -Schedule $weekly -Existing $null) -eq 'Verified')
    $monthly = New-TestSchedule -Kind 'Monthly15'
    $result = Invoke-Registration -Schedule $monthly -Existing (Get-RestartTaskInfo)
    Assert-That 'Replace: returns Verified' ($result -eq 'Verified') ('(got: {0})' -f ($result -join ','))
    Assert-That 'Replace: monthly schedule is now active' ((Get-RestartTaskInfo).Summary -eq (Get-ScheduleSummary -Schedule $monthly))
    Assert-That 'Replace: backup XML of the weekly task saved' ((Test-Path -LiteralPath $script:BackupFile) -and ((Get-Content -LiteralPath $script:BackupFile -Raw) -match '<WeeklyTrigger>|<ScheduleByWeek>'))

    # 3. Forced verification failure restores the previous task.
    Write-Host "`n=== 3. Forced verification failure ===" -ForegroundColor Cyan
    $before = Get-RestartTaskInfo
    $triggerBefore = Get-TriggerXml
    Set-Item -Path function:Get-ScheduleDifference -Value {
        param($Expected, $Actual, [datetime]$From = (Get-Date))
        $real = & $originalDifference -Expected $Expected -Actual $Actual -From $From
        return , ([string[]](@($real) + 'Forced verification failure (CI test)'))
    }
    try {
        $result = Invoke-Registration -Schedule (New-TestSchedule -Kind 'LastFriday') -Existing $before
    }
    finally {
        Set-Item -Path function:Get-ScheduleDifference -Value $originalDifference
    }
    Assert-That 'Forced failure: returns Restored' ($result -eq 'Restored') ('(got: {0})' -f ($result -join ','))
    $after = Get-RestartTaskInfo
    Assert-That 'Forced failure: previous task exists' ($null -ne $after)
    Assert-That 'Forced failure: same schedule summary' ($null -ne $after -and $after.Summary -eq $before.Summary) ('(before: {0}; after: {1})' -f $before.Summary, $(if ($after) { $after.Summary } else { 'none' }))
    $triggerAfter = Get-TriggerXml
    Assert-That 'Forced failure: identical trigger XML' ($triggerAfter -eq $triggerBefore) ("`n  before: $triggerBefore`n  after:  $triggerAfter")
    Assert-That 'Forced failure: same next run time' ($null -ne $after -and $after.NextRunTime -eq $before.NextRunTime)

    # 4. Delete through the menu action (answers Y), then check task and folder are gone.
    Write-Host "`n=== 4. Delete ===" -ForegroundColor Cyan
    function Read-Host { param([string]$Prompt) Write-Host ('{0}: Y' -f $Prompt); return 'Y' }
    try {
        Invoke-DeleteRestart
    }
    finally {
        Remove-Item -Path function:Read-Host
    }
    Assert-That 'Delete: task is gone' ($null -eq (Get-RestartTaskInfo))
    $folderGone = $false
    $service = Get-TaskService
    try {
        $folder = $service.GetFolder($script:TaskFolderPath)
        Close-ComObject -InputObject $folder
    }
    catch {
        $folderGone = Test-IsNotFoundError -ErrorRecord $_
    }
    finally {
        Close-ComObject -InputObject $service
    }
    Assert-That 'Delete: \ScheduledRestart folder is gone' $folderGone

    # 5. Log file and Application event log.
    Write-Host "`n=== 5. Logging ===" -ForegroundColor Cyan
    $lines = @(Get-Content -LiteralPath $script:LogFile)
    $format = '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \[(INFO|WARN|ERROR)\] \[[^\]]+\] .+'
    Assert-That 'Log: every line matches the format' (@($lines | Where-Object { $_ -notmatch $format }).Count -eq 0)
    foreach ($pattern in @(
            '\[INFO\] .* Created scheduled restart',
            '\[INFO\] .* Exported previous task XML',
            '\[INFO\] .* Deleted previous scheduled restart',
            '\[INFO\] .* Replaced scheduled restart',
            '\[ERROR\] .* New schedule failed registration or verification.*Forced verification failure',
            '\[WARN\] .* Previous schedule restored from exported XML and verified',
            '\[INFO\] .* Deleted scheduled restart')) {
        Assert-That ('Log: line matching "{0}"' -f $pattern) (@($lines | Where-Object { $_ -match $pattern }).Count -ge 1)
    }
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ScheduledRestart'; StartTime = $script:TestStart.AddMinutes(-1) } -ErrorAction SilentlyContinue)
    $created = @($events | Where-Object { $_.Id -eq 1001 }).Count
    $deleted = @($events | Where-Object { $_.Id -eq 1002 }).Count
    Assert-That ('Events: 1001 written for each verified create/replace (expected {0}, found {1})' -f ($createdCount + 2), $created) ($created -eq ($createdCount + 2))
    Assert-That ('Events: 1002 written for the delete (found {0})' -f $deleted) ($deleted -eq 1)

    Write-Host "`n--- Log file ---" -ForegroundColor Cyan
    $lines | ForEach-Object { Write-Host $_ }
}
catch {
    $script:Failures++
    Write-Host ('[FAIL] Unhandled error: {0}' -f $_.Exception.Message) -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
}
finally {
    try {
        if ($null -ne (Get-RestartTaskInfo)) {
            $null = Remove-RestartTask
            Write-Host 'Cleanup: leftover task deleted.' -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host ('Cleanup failed: {0}' -f $_.Exception.Message) -ForegroundColor Red
        $script:Failures++
    }
}

Write-Host ("`nIntegration: {0} passed, {1} failed" -f $script:Passes, $script:Failures) -ForegroundColor Cyan
if ($script:Failures -gt 0) { exit 1 }
exit 0
