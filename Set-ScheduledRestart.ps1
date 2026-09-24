#Requires -Version 5.1
<#
.SYNOPSIS
    Interactive tool to create, view and remove a scheduled automatic restart of the local computer.

.DESCRIPTION
    Set-ScheduledRestart.ps1 is a menu-driven tool for technicians. It manages a single Windows
    Task Scheduler task (\ScheduledRestart\ScheduledRestart) that restarts the local computer with:

        C:\Windows\System32\shutdown.exe /r /f /t 0 /d p:4:1 /c "Scheduled restart"

    Supported schedules:
        - One-time  : a specific date and time.
        - Weekly    : one or more weekdays at a given time.
        - Monthly   : a specific day of the month (or the last day), or the Nth weekday
                      (First/Second/Third/Fourth/Last) at a given time.

    The task runs as SYSTEM with highest privileges whether or not a user is logged on, wakes the
    computer to run, and never runs late if a scheduled start was missed.

    The script uses the Task Scheduler COM API (Schedule.Service) and does not depend on the
    ScheduledTasks module, so it works on Windows PowerShell 5.1 on Windows 10/11 and
    Windows Server 2016 or later.

    If the script is not running elevated it relaunches itself with administrator rights.

    Every create, replace and delete action and every error is written to
    C:\ProgramData\ScheduledRestart\ScheduledRestart.log. Create and delete actions are also written
    to the Windows Application event log under the source "ScheduledRestart".

.EXAMPLE
    PS C:\Tools> .\Set-ScheduledRestart.ps1

    Starts the interactive menu. Approve the UAC prompt if the console is not elevated.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Tools\Set-ScheduledRestart.ps1

    Starts the tool from cmd.exe or a shortcut regardless of the local execution policy.

.NOTES
    Name     : Set-ScheduledRestart.ps1
    Version  : 1.0.0
    Requires : Windows PowerShell 5.1, local administrator rights
    Scope    : Local computer only
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive console tool; colored host output is intended.')]
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

#region Configuration

$script:ScriptVersion    = '1.0.0'
$script:TaskFolderName   = 'ScheduledRestart'
$script:TaskFolderPath   = '\ScheduledRestart'
$script:TaskName         = 'ScheduledRestart'
$script:SystemSid        = 'S-1-5-18'
$script:ActionPath       = 'C:\Windows\System32\shutdown.exe'
$script:ActionArguments  = '/r /f /t 0 /d p:4:1 /c "Scheduled restart"'
$script:LogDirectory     = 'C:\ProgramData\ScheduledRestart'
$script:LogFile          = 'C:\ProgramData\ScheduledRestart\ScheduledRestart.log'
$script:EventSource      = 'ScheduledRestart'
$script:EventLogName     = 'Application'
$script:EventIdCreated   = 1001
$script:EventIdDeleted   = 1002
$script:EventSourceReady = $null
$script:Culture          = [System.Globalization.CultureInfo]::InvariantCulture
$script:CurrentUser      = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

# Task Scheduler COM constants (taskschd.h)
$script:TASK_TRIGGER_TIME           = 1
$script:TASK_TRIGGER_WEEKLY         = 3
$script:TASK_TRIGGER_MONTHLY        = 4
$script:TASK_TRIGGER_MONTHLYDOW     = 5
$script:TASK_ACTION_EXEC            = 0
$script:TASK_CREATE_OR_UPDATE       = 6
$script:TASK_LOGON_SERVICE_ACCOUNT  = 5
$script:TASK_RUNLEVEL_HIGHEST       = 1
$script:TASK_INSTANCES_IGNORE_NEW   = 2
$script:TASK_ENUM_HIDDEN            = 1
$script:ALL_MONTHS                  = 0xFFF

$script:OrdinalNames = @{ 1 = 'First'; 2 = 'Second'; 3 = 'Third'; 4 = 'Fourth'; 5 = 'Last' }

$script:TaskResultText = @{
    '0x00000000' = 'The operation completed successfully'
    '0x00000001' = 'Incorrect function or unknown error'
    '0x0000045B' = 'A system shutdown is in progress (normal for a restart task)'
    '0x000004A6' = 'A system shutdown has already been scheduled'
    '0x00041300' = 'Task is ready to run at its next scheduled time'
    '0x00041301' = 'Task is currently running'
    '0x00041302' = 'Task is disabled'
    '0x00041303' = 'Task has not yet run'
    '0x00041304' = 'There are no more runs scheduled for this task'
    '0x00041306' = 'Task was terminated by the user'
    '0x8004130F' = 'Credentials for the task account are missing or invalid'
    '0x8004131F' = 'An instance of this task is already running'
    '0x80070005' = 'Access is denied'
    '0x800710E0' = 'The operator or administrator has refused the request'
    '0xC000013A' = 'The application was terminated (Ctrl+C)'
}

$script:TaskStateText = @{ 0 = 'Unknown'; 1 = 'Disabled'; 2 = 'Queued'; 3 = 'Ready'; 4 = 'Running' }

#endregion Configuration

#region Output and logging

function Write-Status {
    <# Writes a colored status line to the console. #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Message,
        [ValidateSet('Header', 'Info', 'Success', 'Warning', 'Error')][string]$Type = 'Info'
    )
    $colors = @{ Header = 'Cyan'; Info = 'Gray'; Success = 'Green'; Warning = 'Yellow'; Error = 'Red' }
    Write-Host $Message -ForegroundColor $colors[$Type]
}

function Write-LogEntry {
    <# Appends a line to the log file: yyyy-MM-dd HH:mm:ss [LEVEL] [user] message #>
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ValidateSet('INFO', 'WARN', 'ERROR')][string]$Level = 'INFO'
    )
    try {
        if (-not (Test-Path -LiteralPath $script:LogDirectory -PathType Container)) {
            $null = New-Item -Path $script:LogDirectory -ItemType Directory -Force
        }
        $timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss', $script:Culture)
        $line = '{0} [{1}] [{2}] {3}' -f $timestamp, $Level, $script:CurrentUser, $Message
        Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8
    }
    catch {
        Write-Status -Type Warning -Message ('WARNING: Unable to write to log file {0}: {1}' -f $script:LogFile, $_.Exception.Message)
    }
}

function Register-EventSource {
    <# Ensures the Application event log source exists. Returns $true when it can be used. #>
    if ($null -ne $script:EventSourceReady) {
        return $script:EventSourceReady
    }
    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists($script:EventSource)) {
            New-EventLog -LogName $script:EventLogName -Source $script:EventSource
            Write-LogEntry -Level INFO -Message ('Registered event log source "{0}" in the {1} log.' -f $script:EventSource, $script:EventLogName)
        }
        $script:EventSourceReady = $true
    }
    catch {
        $script:EventSourceReady = $false
        Write-LogEntry -Level WARN -Message ('Unable to register event log source "{0}": {1}' -f $script:EventSource, $_.Exception.Message)
    }
    return $script:EventSourceReady
}

function Write-AppEventLog {
    <# Writes an entry to the Application event log. Failures are logged as WARN only. #>
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)][int]$EventId,
        [ValidateSet('Information', 'Warning', 'Error')][string]$EntryType = 'Information'
    )
    if (-not (Register-EventSource)) {
        return
    }
    try {
        Write-EventLog -LogName $script:EventLogName -Source $script:EventSource -EventId $EventId -EntryType $EntryType -Message $Message
    }
    catch {
        Write-LogEntry -Level WARN -Message ('Unable to write to the {0} event log: {1}' -f $script:EventLogName, $_.Exception.Message)
    }
}

#endregion Output and logging

#region Environment

function Test-IsAdministrator {
    <# Returns $true when the current process is elevated. #>
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-SystemUptime {
    <# Returns the uptime as readable text, or 'Unknown'. #>
    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -Verbose:$false
        $span = (Get-Date) - $os.LastBootUpTime
        return ('{0}d {1:00}h {2:00}m' -f $span.Days, $span.Hours, $span.Minutes)
    }
    catch {
        return 'Unknown'
    }
}

function Get-SystemAccountName {
    <# Resolves S-1-5-18 to its localized account name (e.g. NT AUTHORITY\SYSTEM). #>
    $sid = New-Object System.Security.Principal.SecurityIdentifier($script:SystemSid)
    return $sid.Translate([System.Security.Principal.NTAccount]).Value
}

#endregion Environment

#region Formatting helpers

function Join-EnglishList {
    <# Joins items as "A", "A and B" or "A, B and C". #>
    param([string[]]$Item)
    if ($null -eq $Item -or $Item.Count -eq 0) { return '' }
    if ($Item.Count -eq 1) { return $Item[0] }
    return ('{0} and {1}' -f ($Item[0..($Item.Count - 2)] -join ', '), $Item[-1])
}

function Get-DateText {
    <# Formats a date/time in an invariant, unambiguous way. #>
    param([Parameter(Mandatory = $true)][datetime]$Date)
    return $Date.ToString('dddd dd/MM/yyyy HH:mm', $script:Culture)
}

function Get-TimeText {
    <# Formats a time of day as HH:mm. #>
    param([Parameter(Mandatory = $true)][timespan]$Time)
    return ('{0:00}:{1:00}' -f $Time.Hours, $Time.Minutes)
}

function Get-SortedWeekday {
    <# Sorts weekdays Monday first and removes duplicates. #>
    param([System.DayOfWeek[]]$Day)
    if ($null -eq $Day) { return , ([System.DayOfWeek[]]@()) }
    $sorted = [System.DayOfWeek[]]@($Day | Sort-Object -Unique | Sort-Object -Property { ([int]$_ + 6) % 7 })
    return , $sorted
}

function Get-TaskResultText {
    <# Translates a task result code to text where known. #>
    param([int]$Code)
    $hex = '0x{0:X8}' -f $Code
    if ($script:TaskResultText.ContainsKey($hex)) {
        return ('{0} ({1})' -f $hex, $script:TaskResultText[$hex])
    }
    return ('{0} (Unknown result code)' -f $hex)
}

#endregion Formatting helpers

#region Schedule model

function Get-ScheduleTemplate {
    <# Returns an empty schedule object. Type: Once, Weekly, MonthlyDay, MonthlyWeekday, Custom. #>
    param([Parameter(Mandatory = $true)][string]$Type)
    return [pscustomobject]@{
        Type           = $Type
        StartDate      = (Get-Date).Date
        Time           = [timespan]::Zero
        DaysOfWeek     = [System.DayOfWeek[]]@()
        MonthDays      = [int[]]@()
        LastDayOfMonth = $false
        WeeksOfMonth   = [int[]]@()
        CustomText     = ''
    }
}

function Get-ScheduleTypeText {
    <# Returns a readable schedule type. #>
    param([Parameter(Mandatory = $true)]$Schedule)
    switch ($Schedule.Type) {
        'Once'           { return 'One-time' }
        'Weekly'         { return 'Weekly' }
        'MonthlyDay'     { return 'Monthly (day of month)' }
        'MonthlyWeekday' { return 'Monthly (day of week)' }
        default          { return 'Custom' }
    }
}

function Get-ScheduleDayText {
    <# Returns the "days" part of a schedule in plain English. #>
    param([Parameter(Mandatory = $true)]$Schedule)
    switch ($Schedule.Type) {
        'Once' {
            return $Schedule.StartDate.ToString('dddd dd/MM/yyyy', $script:Culture)
        }
        'Weekly' {
            $days = Get-SortedWeekday -Day $Schedule.DaysOfWeek
            return (Join-EnglishList -Item ([string[]]$days))
        }
        'MonthlyDay' {
            $parts = @()
            foreach ($d in ($Schedule.MonthDays | Sort-Object)) { $parts += ('day {0}' -f $d) }
            if ($Schedule.LastDayOfMonth) { $parts += 'the last day' }
            return ('{0} of every month' -f (Join-EnglishList -Item ([string[]]$parts)))
        }
        'MonthlyWeekday' {
            $weeks = @()
            foreach ($w in ($Schedule.WeeksOfMonth | Sort-Object)) { $weeks += $script:OrdinalNames[[int]$w].ToLowerInvariant() }
            $days = Get-SortedWeekday -Day $Schedule.DaysOfWeek
            return ('the {0} {1} of every month' -f (Join-EnglishList -Item ([string[]]$weeks)), (Join-EnglishList -Item ([string[]]$days)))
        }
        default {
            return $Schedule.CustomText
        }
    }
}

function Get-ScheduleSummary {
    <# Returns a one-line plain English description of a schedule. #>
    param([Parameter(Mandatory = $true)]$Schedule)
    $time = Get-TimeText -Time $Schedule.Time
    $days = Get-ScheduleDayText -Schedule $Schedule
    switch ($Schedule.Type) {
        'Once'           { return ('Once on {0} at {1}' -f $days, $time) }
        'Weekly'         { return ('Every week on {0} at {1}' -f $days, $time) }
        'MonthlyDay'     { return ('On {0} at {1}' -f $days, $time) }
        'MonthlyWeekday' { return ('On {0} at {1}' -f $days, $time) }
        default          { return ('Custom schedule: {0}' -f $days) }
    }
}

function Get-MonthlyOccurrence {
    <# Returns the dates (without time) on which a monthly schedule fires in the given month. #>
    param(
        [Parameter(Mandatory = $true)]$Schedule,
        [Parameter(Mandatory = $true)][datetime]$Month
    )
    $first = New-Object DateTime($Month.Year, $Month.Month, 1)
    $daysInMonth = [DateTime]::DaysInMonth($Month.Year, $Month.Month)
    $last = $first.AddDays($daysInMonth - 1)
    $dates = @()

    if ($Schedule.Type -eq 'MonthlyDay') {
        foreach ($d in $Schedule.MonthDays) {
            if ($d -le $daysInMonth) { $dates += $first.AddDays($d - 1) }
        }
        if ($Schedule.LastDayOfMonth) { $dates += $last }
    }
    elseif ($Schedule.Type -eq 'MonthlyWeekday') {
        foreach ($dow in $Schedule.DaysOfWeek) {
            foreach ($week in $Schedule.WeeksOfMonth) {
                if ($week -eq 5) {
                    $back = (([int]$last.DayOfWeek - [int]$dow) + 7) % 7
                    $dates += $last.AddDays(-$back)
                }
                else {
                    $offset = (([int]$dow - [int]$first.DayOfWeek) + 7) % 7
                    $dates += $first.AddDays($offset + (7 * ($week - 1)))
                }
            }
        }
    }
    return $dates
}

function Get-NextRunTime {
    <# Calculates the next occurrence of a schedule after the reference time, or $null. #>
    param(
        [Parameter(Mandatory = $true)]$Schedule,
        [datetime]$From = (Get-Date)
    )
    switch ($Schedule.Type) {
        'Once' {
            $candidate = $Schedule.StartDate.Date + $Schedule.Time
            if ($candidate -gt $From) { return $candidate }
            return $null
        }
        'Weekly' {
            for ($i = 0; $i -le 7; $i++) {
                $candidate = $From.Date.AddDays($i) + $Schedule.Time
                if ($candidate -gt $From -and ($Schedule.DaysOfWeek -contains $candidate.DayOfWeek)) {
                    return $candidate
                }
            }
            return $null
        }
        { $_ -eq 'MonthlyDay' -or $_ -eq 'MonthlyWeekday' } {
            $monthStart = New-Object DateTime($From.Year, $From.Month, 1)
            for ($m = 0; $m -lt 24; $m++) {
                $month = $monthStart.AddMonths($m)
                $candidates = @(Get-MonthlyOccurrence -Schedule $Schedule -Month $month |
                        ForEach-Object { $_ + $Schedule.Time } |
                        Where-Object { $_ -gt $From } |
                        Sort-Object)
                if ($candidates.Count -gt 0) { return $candidates[0] }
            }
            return $null
        }
        default {
            return $null
        }
    }
}

function Get-DayBitmask {
    <# Converts weekdays to the Task Scheduler DaysOfWeek bitmask (Sunday = 1 ... Saturday = 64). #>
    param([System.DayOfWeek[]]$Day)
    $mask = 0
    foreach ($d in $Day) { $mask = $mask -bor (1 -shl [int]$d) }
    return $mask
}

function Get-WeekdayFromBitmask {
    <# Converts a Task Scheduler DaysOfWeek bitmask to weekdays. #>
    param([int]$Mask)
    $days = @()
    for ($i = 0; $i -le 6; $i++) {
        if ($Mask -band (1 -shl $i)) { $days += [System.DayOfWeek]$i }
    }
    return , ([System.DayOfWeek[]]$days)
}

function Get-StartBoundaryDate {
    <# Parses a Task Scheduler StartBoundary string (yyyy-MM-ddTHH:mm:ss[zone]). #>
    param([Parameter(Mandatory = $true)][string]$Boundary)
    return [datetime]::ParseExact($Boundary.Substring(0, 19), 'yyyy-MM-ddTHH:mm:ss', $script:Culture)
}

function Get-ScheduleFromTrigger {
    <# Decodes a Task Scheduler COM trigger into a schedule object. #>
    param([Parameter(Mandatory = $true)]$Trigger)

    $start = $null
    if (-not [string]::IsNullOrEmpty($Trigger.StartBoundary)) {
        $start = Get-StartBoundaryDate -Boundary $Trigger.StartBoundary
    }

    switch ([int]$Trigger.Type) {
        $script:TASK_TRIGGER_TIME {
            $schedule = Get-ScheduleTemplate -Type 'Once'
        }
        $script:TASK_TRIGGER_WEEKLY {
            $schedule = Get-ScheduleTemplate -Type 'Weekly'
            $schedule.DaysOfWeek = Get-WeekdayFromBitmask -Mask ([int]$Trigger.DaysOfWeek)
        }
        $script:TASK_TRIGGER_MONTHLY {
            $schedule = Get-ScheduleTemplate -Type 'MonthlyDay'
            $mask = [int64]$Trigger.DaysOfMonth
            $days = @()
            for ($i = 0; $i -lt 31; $i++) {
                if ($mask -band ([int64]1 -shl $i)) { $days += ($i + 1) }
            }
            $schedule.MonthDays = [int[]]$days
            $schedule.LastDayOfMonth = [bool]$Trigger.RunOnLastDayOfMonth
        }
        $script:TASK_TRIGGER_MONTHLYDOW {
            $schedule = Get-ScheduleTemplate -Type 'MonthlyWeekday'
            $schedule.DaysOfWeek = Get-WeekdayFromBitmask -Mask ([int]$Trigger.DaysOfWeek)
            $weeks = @()
            for ($i = 0; $i -lt 4; $i++) {
                if ([int]$Trigger.WeeksOfMonth -band (1 -shl $i)) { $weeks += ($i + 1) }
            }
            if ([bool]$Trigger.RunOnLastWeekOfMonth) { $weeks += 5 }
            $schedule.WeeksOfMonth = [int[]]$weeks
        }
        default {
            $schedule = Get-ScheduleTemplate -Type 'Custom'
            $schedule.CustomText = ('Trigger type {0} was not created by this tool' -f $Trigger.Type)
        }
    }

    if ($null -ne $start) {
        $schedule.StartDate = $start.Date
        $schedule.Time = $start.TimeOfDay
    }
    return $schedule
}

#endregion Schedule model

#region Task Scheduler (COM)

function Close-ComObject {
    <# Releases one or more COM objects. Safe to call with $null. #>
    param([object[]]$InputObject)
    foreach ($item in $InputObject) {
        if ($null -ne $item -and [System.Runtime.InteropServices.Marshal]::IsComObject($item)) {
            try {
                $null = [System.Runtime.InteropServices.Marshal]::ReleaseComObject($item)
            }
            catch {
                Write-Verbose ('Unable to release COM object: {0}' -f $_.Exception.Message)
            }
        }
    }
}

function Get-TaskService {
    <# Returns a connected Schedule.Service COM object. The caller must release it. #>
    $service = $null
    try {
        $service = New-Object -ComObject 'Schedule.Service'
        $service.Connect()
        return $service
    }
    catch {
        Close-ComObject -InputObject $service
        throw ('Unable to connect to the Task Scheduler service: {0}' -f $_.Exception.Message)
    }
}

function Test-IsNotFoundError {
    <# Returns $true when an error record represents "file/path not found" (0x80070002 / 0x80070003). #>
    param([Parameter(Mandatory = $true)][System.Management.Automation.ErrorRecord]$ErrorRecord)
    $notFound = @(-2147024894, -2147024893)
    $exception = $ErrorRecord.Exception
    while ($null -ne $exception) {
        if ($notFound -contains $exception.HResult) { return $true }
        $exception = $exception.InnerException
    }
    return $false
}

function Get-RestartTaskInfo {
    <# Returns details of the restart task, or $null when it does not exist. #>
    $service = $null
    $folder = $null
    $task = $null
    $definition = $null
    $triggers = $null
    $trigger = $null
    try {
        $service = Get-TaskService
        try {
            $folder = $service.GetFolder($script:TaskFolderPath)
            $task = $folder.GetTask($script:TaskName)
        }
        catch {
            if (Test-IsNotFoundError -ErrorRecord $_) { return $null }
            throw
        }

        $definition = $task.Definition
        $triggers = $definition.Triggers
        if ($triggers.Count -gt 0) {
            $trigger = $triggers.Item(1)
            $schedule = Get-ScheduleFromTrigger -Trigger $trigger
        }
        else {
            $schedule = Get-ScheduleTemplate -Type 'Custom'
            $schedule.CustomText = 'No trigger defined'
        }

        $nextRun = $null
        $lastRun = $null
        if ($task.NextRunTime.Year -ge 2000) { $nextRun = [datetime]$task.NextRunTime }
        if ($task.LastRunTime.Year -ge 2000) { $lastRun = [datetime]$task.LastRunTime }

        $state = [int]$task.State
        $stateText = 'Unknown'
        if ($script:TaskStateText.ContainsKey($state)) { $stateText = $script:TaskStateText[$state] }

        return [pscustomobject]@{
            Path        = $task.Path
            Schedule    = $schedule
            Summary     = Get-ScheduleSummary -Schedule $schedule
            Description = [string]$definition.RegistrationInfo.Description
            Enabled     = [bool]$task.Enabled
            State       = $stateText
            NextRunTime = $nextRun
            LastRunTime = $lastRun
            LastResult  = [int]$task.LastTaskResult
        }
    }
    catch {
        throw ('Unable to read the scheduled restart task: {0}' -f $_.Exception.Message)
    }
    finally {
        Close-ComObject -InputObject @($trigger, $triggers, $definition, $task, $folder, $service)
    }
}

function Get-TaskFolder {
    <# Returns the \ScheduledRestart folder, creating it when missing. The caller must release it. #>
    param([Parameter(Mandatory = $true)]$Service)
    $root = $null
    try {
        try {
            return $Service.GetFolder($script:TaskFolderPath)
        }
        catch {
            if (-not (Test-IsNotFoundError -ErrorRecord $_)) { throw }
        }
        $root = $Service.GetFolder('\')
        return $root.CreateFolder($script:TaskFolderName)
    }
    catch {
        throw ('Unable to open or create task folder {0}: {1}' -f $script:TaskFolderPath, $_.Exception.Message)
    }
    finally {
        Close-ComObject -InputObject $root
    }
}

function Add-TaskTrigger {
    <# Adds a trigger matching the schedule to the given (in-memory) trigger collection. #>
    param(
        [Parameter(Mandatory = $true)]$Triggers,
        [Parameter(Mandatory = $true)]$Schedule
    )
    $trigger = $null
    try {
        switch ($Schedule.Type) {
            'Once' {
                $trigger = $Triggers.Create($script:TASK_TRIGGER_TIME)
                $startDate = $Schedule.StartDate.Date
            }
            'Weekly' {
                $trigger = $Triggers.Create($script:TASK_TRIGGER_WEEKLY)
                $trigger.DaysOfWeek = [int16](Get-DayBitmask -Day $Schedule.DaysOfWeek)
                $trigger.WeeksInterval = 1
                $startDate = (Get-Date).Date
            }
            'MonthlyDay' {
                $trigger = $Triggers.Create($script:TASK_TRIGGER_MONTHLY)
                $mask = 0
                foreach ($d in $Schedule.MonthDays) { $mask = $mask -bor (1 -shl ($d - 1)) }
                $trigger.DaysOfMonth = [int]$mask
                $trigger.MonthsOfYear = [int16]$script:ALL_MONTHS
                $trigger.RunOnLastDayOfMonth = [bool]$Schedule.LastDayOfMonth
                $startDate = (Get-Date).Date
            }
            'MonthlyWeekday' {
                $trigger = $Triggers.Create($script:TASK_TRIGGER_MONTHLYDOW)
                $trigger.DaysOfWeek = [int16](Get-DayBitmask -Day $Schedule.DaysOfWeek)
                $weekMask = 0
                foreach ($w in $Schedule.WeeksOfMonth) {
                    if ($w -ge 1 -and $w -le 4) { $weekMask = $weekMask -bor (1 -shl ($w - 1)) }
                }
                $trigger.WeeksOfMonth = [int16]$weekMask
                $trigger.RunOnLastWeekOfMonth = ($Schedule.WeeksOfMonth -contains 5)
                $trigger.MonthsOfYear = [int16]$script:ALL_MONTHS
                $startDate = (Get-Date).Date
            }
            default {
                throw ('Unsupported schedule type "{0}".' -f $Schedule.Type)
            }
        }
        $start = $startDate + $Schedule.Time
        $trigger.StartBoundary = $start.ToString('yyyy-MM-ddTHH:mm:ss', $script:Culture)
        $trigger.Enabled = $true
    }
    finally {
        Close-ComObject -InputObject $trigger
    }
}

function New-RestartTask {
    <# Creates or replaces the restart task. Returns the next run time reported by Task Scheduler. #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    [OutputType([datetime])]
    param([Parameter(Mandatory = $true)]$Schedule)

    $service = $null
    $folder = $null
    $definition = $null
    $regInfo = $null
    $principal = $null
    $settings = $null
    $triggers = $null
    $actions = $null
    $action = $null
    $registered = $null
    try {
        $service = Get-TaskService
        $definition = $service.NewTask(0)

        $summary = Get-ScheduleSummary -Schedule $Schedule
        $created = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss', $script:Culture)
        $regInfo = $definition.RegistrationInfo
        $regInfo.Author = $script:CurrentUser
        $regInfo.Description = ('Automatic restart created by {0} on {1} with Set-ScheduledRestart.ps1 v{2}. Schedule: {3}.' -f $script:CurrentUser, $created, $script:ScriptVersion, $summary)

        $principal = $definition.Principal
        $principal.UserId = $script:SystemSid
        $principal.LogonType = $script:TASK_LOGON_SERVICE_ACCOUNT
        $principal.RunLevel = $script:TASK_RUNLEVEL_HIGHEST

        $settings = $definition.Settings
        $settings.Enabled = $true
        $settings.WakeToRun = $true
        $settings.StartWhenAvailable = $false
        $settings.DisallowStartIfOnBatteries = $false
        $settings.StopIfGoingOnBatteries = $false
        $settings.ExecutionTimeLimit = 'PT10M'
        $settings.MultipleInstances = $script:TASK_INSTANCES_IGNORE_NEW
        $settings.AllowDemandStart = $true
        $settings.Hidden = $false

        $triggers = $definition.Triggers
        Add-TaskTrigger -Triggers $triggers -Schedule $Schedule

        $actions = $definition.Actions
        $action = $actions.Create($script:TASK_ACTION_EXEC)
        $action.Path = $script:ActionPath
        $action.Arguments = $script:ActionArguments

        if (-not $PSCmdlet.ShouldProcess(('{0}\{1}' -f $script:TaskFolderPath, $script:TaskName), 'Register scheduled restart task')) {
            return $null
        }

        $folder = Get-TaskFolder -Service $service
        $registered = $folder.RegisterTaskDefinition(
            $script:TaskName,
            $definition,
            $script:TASK_CREATE_OR_UPDATE,
            (Get-SystemAccountName),
            $null,
            $script:TASK_LOGON_SERVICE_ACCOUNT)

        if ($registered.NextRunTime.Year -ge 2000) { return [datetime]$registered.NextRunTime }
        return $null
    }
    catch {
        throw ('Unable to register the scheduled restart task: {0}' -f $_.Exception.Message)
    }
    finally {
        Close-ComObject -InputObject @($registered, $action, $actions, $triggers, $settings, $principal, $regInfo, $definition, $folder, $service)
    }
}

function Remove-RestartTask {
    <# Deletes the restart task and removes the task folder when it is empty. Returns $true when the folder was removed. #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    [OutputType([bool])]
    param()

    $service = $null
    $folder = $null
    $root = $null
    $tasks = $null
    $subFolders = $null
    try {
        $service = Get-TaskService
        $folder = $service.GetFolder($script:TaskFolderPath)
        if (-not $PSCmdlet.ShouldProcess(('{0}\{1}' -f $script:TaskFolderPath, $script:TaskName), 'Delete scheduled restart task')) {
            return $false
        }
        $folder.DeleteTask($script:TaskName, 0)

        $tasks = $folder.GetTasks($script:TASK_ENUM_HIDDEN)
        $subFolders = $folder.GetFolders(0)
        if ($tasks.Count -eq 0 -and $subFolders.Count -eq 0) {
            $root = $service.GetFolder('\')
            $root.DeleteFolder($script:TaskFolderName, 0)
            return $true
        }
        return $false
    }
    catch {
        throw ('Unable to delete the scheduled restart task: {0}' -f $_.Exception.Message)
    }
    finally {
        Close-ComObject -InputObject @($subFolders, $tasks, $root, $folder, $service)
    }
}

#endregion Task Scheduler (COM)

#region Input

function Read-UserInput {
    <# Reads a trimmed line. Typing B throws OperationCanceledException to return to the main menu. #>
    param([Parameter(Mandatory = $true)][string]$Prompt)
    $value = Read-Host -Prompt ('{0} (B = back)' -f $Prompt)
    if ($null -eq $value) { $value = '' }
    $value = $value.Trim()
    if ($value -ieq 'B') {
        throw (New-Object System.OperationCanceledException('Returned to the main menu.'))
    }
    return $value
}

function Read-ValidatedInput {
    <#
        Prompts until the validator accepts the input. The validator receives the raw text and either
        returns the parsed value or throws a FormatException whose message is shown to the user.
        Values in ArgumentList are passed to the validator after the raw text.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [Parameter(Mandatory = $true)][scriptblock]$Validator,
        [object[]]$ArgumentList = @()
    )
    while ($true) {
        $raw = Read-UserInput -Prompt $Prompt
        try {
            $result = & $Validator $raw @ArgumentList
            return , $result
        }
        catch [System.FormatException] {
            Write-Status -Type Warning -Message ('  {0}' -f $_.Exception.Message)
        }
    }
}

function Read-YesNo {
    <# Prompts for Y or N and returns $true for Y. #>
    param([Parameter(Mandatory = $true)][string]$Prompt)
    return (Read-ValidatedInput -Prompt ('{0} [Y/N]' -f $Prompt) -Validator {
            param($s)
            if ($s -ieq 'Y' -or $s -ieq 'YES') { return $true }
            if ($s -ieq 'N' -or $s -ieq 'NO') { return $false }
            throw (New-Object System.FormatException('Please enter Y or N.'))
        })
}

function Read-MenuNumber {
    <# Prompts for an integer between Minimum and Maximum. #>
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [Parameter(Mandatory = $true)][int]$Minimum,
        [Parameter(Mandatory = $true)][int]$Maximum
    )
    $validator = {
        param($s, $Minimum, $Maximum)
        $n = 0
        if ([int]::TryParse($s, [System.Globalization.NumberStyles]::Integer, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$n) -and $n -ge $Minimum -and $n -le $Maximum) {
            return $n
        }
        throw (New-Object System.FormatException(('Please enter a number from {0} to {1}.' -f $Minimum, $Maximum)))
    }
    return (Read-ValidatedInput -Prompt $Prompt -Validator $validator -ArgumentList $Minimum, $Maximum)
}

function ConvertTo-TimeOfDay {
    <# Parses HH:mm (24-hour) using the invariant culture. Throws FormatException when invalid. #>
    param([string]$Text)
    try {
        $parsed = [datetime]::ParseExact($Text, [string[]]@('HH:mm', 'H:mm'), [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None)
        return $parsed.TimeOfDay
    }
    catch {
        throw (New-Object System.FormatException('Invalid time. Use 24-hour HH:mm, for example 03:00 or 22:30.'))
    }
}

function ConvertTo-CalendarDate {
    <# Parses dd/MM/yyyy using the invariant culture. Throws FormatException when invalid. #>
    param([string]$Text)
    try {
        $parsed = [datetime]::ParseExact($Text, [string[]]@('dd/MM/yyyy', 'd/M/yyyy'), [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None)
        return $parsed.Date
    }
    catch {
        throw (New-Object System.FormatException('Invalid date. Use dd/MM/yyyy, for example 31/12/2026.'))
    }
}

function ConvertTo-WeekdayList {
    <# Parses a list such as "1,5" or "1 3 5" (1 = Monday ... 7 = Sunday). Throws FormatException when invalid. #>
    param([string]$Text)
    $tokens = @($Text -split '[,;\s]+' | Where-Object { $_ -ne '' })
    if ($tokens.Count -eq 0) {
        throw (New-Object System.FormatException('Enter at least one weekday number, for example 1,5.'))
    }
    $days = @()
    foreach ($token in $tokens) {
        $n = 0
        if (-not [int]::TryParse($token, [System.Globalization.NumberStyles]::Integer, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$n) -or $n -lt 1 -or $n -gt 7) {
            throw (New-Object System.FormatException(('"{0}" is not a valid weekday. Use numbers 1 (Monday) to 7 (Sunday), separated by commas.' -f $token)))
        }
        $days += [System.DayOfWeek]($n % 7)
    }
    return , (Get-SortedWeekday -Day $days)
}

function Show-WeekdayList {
    <# Prints the weekday numbering used by all weekday prompts. #>
    Write-Host '    1 = Monday     2 = Tuesday    3 = Wednesday   4 = Thursday'
    Write-Host '    5 = Friday     6 = Saturday   7 = Sunday'
}

function Read-TimeOfDay {
    <# Prompts for a time of day (HH:mm). #>
    return (Read-ValidatedInput -Prompt 'Restart time (HH:mm, 24-hour)' -Validator { param($s) ConvertTo-TimeOfDay -Text $s })
}

function Read-OneTimeSchedule {
    <# Prompts for a one-time schedule; rejects dates/times in the past. #>
    $schedule = Get-ScheduleTemplate -Type 'Once'
    $schedule.StartDate = Read-ValidatedInput -Prompt 'Restart date (dd/MM/yyyy)' -Validator {
        param($s)
        $date = ConvertTo-CalendarDate -Text $s
        if ($date -lt (Get-Date).Date) {
            throw (New-Object System.FormatException('That date is in the past. Enter today or a future date.'))
        }
        return $date
    }
    $schedule.Time = Read-ValidatedInput -Prompt 'Restart time (HH:mm, 24-hour)' -ArgumentList $schedule.StartDate -Validator {
        param($s, $startDate)
        $time = ConvertTo-TimeOfDay -Text $s
        if (($startDate + $time) -le (Get-Date)) {
            throw (New-Object System.FormatException('That time has already passed. Enter a time in the future.'))
        }
        return $time
    }
    return $schedule
}

function Read-WeeklySchedule {
    <# Prompts for a weekly schedule (one or more weekdays). #>
    $schedule = Get-ScheduleTemplate -Type 'Weekly'
    Write-Host ''
    Show-WeekdayList
    $schedule.DaysOfWeek = Read-ValidatedInput -Prompt 'Weekday number(s), e.g. 1,5' -Validator { param($s) ConvertTo-WeekdayList -Text $s }
    $schedule.Time = Read-TimeOfDay
    return $schedule
}

function Read-MonthlySchedule {
    <# Prompts for a monthly schedule: a day of month (or last day) or an Nth weekday. #>
    Write-Host ''
    Write-Host '    1. Specific day of the month (1-31 or last day)'
    Write-Host '    2. Specific weekday (e.g. second Tuesday, last Friday)'
    $mode = Read-MenuNumber -Prompt 'Monthly type' -Minimum 1 -Maximum 2

    if ($mode -eq 1) {
        $schedule = Get-ScheduleTemplate -Type 'MonthlyDay'
        $day = Read-ValidatedInput -Prompt 'Day of month (1-31, or L for last day)' -Validator {
            param($s)
            if ($s -ieq 'L' -or $s -ieq 'LAST') { return 0 }
            $n = 0
            if ([int]::TryParse($s, [System.Globalization.NumberStyles]::Integer, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$n) -and $n -ge 1 -and $n -le 31) {
                return $n
            }
            throw (New-Object System.FormatException('Enter a day from 1 to 31, or L for the last day of the month.'))
        }
        if ($day -eq 0) {
            $schedule.LastDayOfMonth = $true
        }
        else {
            $schedule.MonthDays = [int[]]@($day)
            if ($day -ge 29) {
                Write-Status -Type Warning -Message ('  Note: months with fewer than {0} days will be skipped. Choose L to always restart on the last day.' -f $day)
            }
        }
    }
    else {
        $schedule = Get-ScheduleTemplate -Type 'MonthlyWeekday'
        Write-Host ''
        Write-Host '    1 = First   2 = Second   3 = Third   4 = Fourth   5 = Last'
        $week = Read-MenuNumber -Prompt 'Which occurrence' -Minimum 1 -Maximum 5
        Write-Host ''
        Show-WeekdayList
        $dayNumber = Read-MenuNumber -Prompt 'Weekday number' -Minimum 1 -Maximum 7
        $schedule.WeeksOfMonth = [int[]]@($week)
        $schedule.DaysOfWeek = [System.DayOfWeek[]]@([System.DayOfWeek]($dayNumber % 7))
    }
    $schedule.Time = Read-TimeOfDay
    return $schedule
}

#endregion Input

#region Menu actions

function Show-Header {
    <# Prints the header: computer name, uptime and current schedule state. #>
    Write-Host ''
    Write-Status -Type Header -Message '================================================================'
    Write-Status -Type Header -Message ('  Scheduled Restart Manager v{0}' -f $script:ScriptVersion)
    Write-Status -Type Header -Message '================================================================'
    Write-Host ('  Computer : {0}' -f $env:COMPUTERNAME)
    Write-Host ('  Uptime   : {0}' -f (Get-SystemUptime))
    try {
        $info = Get-RestartTaskInfo
        if ($null -eq $info) {
            Write-Host '  Schedule : ' -NoNewline
            Write-Status -Type Warning -Message 'Not configured'
        }
        else {
            Write-Host '  Schedule : ' -NoNewline
            Write-Status -Type Success -Message $info.Summary
            $next = 'None'
            if ($null -ne $info.NextRunTime) { $next = Get-DateText -Date $info.NextRunTime }
            if (-not $info.Enabled) { $next = '{0} (task is disabled)' -f $next }
            Write-Host ('  Next run : {0}' -f $next)
        }
    }
    catch {
        Write-Host '  Schedule : ' -NoNewline
        Write-Status -Type Error -Message 'Unable to read (see log)'
        Write-LogEntry -Level ERROR -Message $_.Exception.Message
    }
    Write-Status -Type Header -Message '================================================================'
}

function Show-MainMenu {
    <# Prints the main menu. #>
    Write-Host ''
    Write-Host '  1. Create / replace scheduled restart'
    Write-Host '  2. Show current schedule'
    Write-Host '  3. Delete scheduled restart'
    Write-Host '  4. View log'
    Write-Host '  5. Exit'
    Write-Host ''
}

function Show-ScheduleConfirmation {
    <# Prints the summary of a schedule that is about to be saved. #>
    param([Parameter(Mandatory = $true)]$Schedule)
    $next = Get-NextRunTime -Schedule $Schedule
    $nextText = 'Unable to calculate'
    if ($null -ne $next) { $nextText = Get-DateText -Date $next }
    Write-Host ''
    Write-Status -Type Header -Message '  Summary'
    Write-Status -Type Header -Message '  -------'
    Write-Host ('  Type     : {0}' -f (Get-ScheduleTypeText -Schedule $Schedule))
    Write-Host ('  Days     : {0}' -f (Get-ScheduleDayText -Schedule $Schedule))
    Write-Host ('  Time     : {0}' -f (Get-TimeText -Time $Schedule.Time))
    Write-Host ('  Next run : {0}' -f $nextText)
    Write-Host ('  Action   : {0} {1}' -f $script:ActionPath, $script:ActionArguments)
    Write-Host '  Account  : SYSTEM, highest privileges, runs whether a user is logged on or not'
    Write-Host '  Notes    : Wakes the computer; a missed run is skipped, not run late.'
    Write-Host ''
}

function Invoke-CreateRestart {
    <# Menu option 1: create or replace the scheduled restart. #>
    Write-Host ''
    Write-Status -Type Header -Message '  Create / replace scheduled restart'
    Write-Status -Type Header -Message '  ----------------------------------'

    $existing = Get-RestartTaskInfo
    if ($null -ne $existing) {
        Write-Status -Type Warning -Message ('  WARNING: A scheduled restart already exists: {0}' -f $existing.Summary)
        Write-Status -Type Warning -Message '  It will be replaced only if you confirm at the end.'
    }

    Write-Host ''
    Write-Host '    1. One-time'
    Write-Host '    2. Weekly'
    Write-Host '    3. Monthly'
    $type = Read-MenuNumber -Prompt 'Schedule type' -Minimum 1 -Maximum 3

    switch ($type) {
        1 { $schedule = Read-OneTimeSchedule }
        2 { $schedule = Read-WeeklySchedule }
        3 { $schedule = Read-MonthlySchedule }
    }

    Show-ScheduleConfirmation -Schedule $schedule

    if ($schedule.Type -eq 'Once' -and ($schedule.StartDate + $schedule.Time) -le (Get-Date)) {
        Write-Status -Type Error -Message '  The selected time has passed while you were entering it. Nothing was saved.'
        return
    }

    if ($null -ne $existing) {
        $prompt = 'Replace the existing scheduled restart with this schedule?'
    }
    else {
        $prompt = 'Save this scheduled restart?'
    }
    if (-not (Read-YesNo -Prompt $prompt)) {
        Write-Status -Type Warning -Message '  Cancelled. No changes were made.'
        return
    }

    $summary = Get-ScheduleSummary -Schedule $schedule
    $nextRun = New-RestartTask -Schedule $schedule
    $nextText = 'None'
    if ($null -ne $nextRun) { $nextText = Get-DateText -Date $nextRun }

    if ($null -ne $existing) {
        $verb = 'Replaced'
    }
    else {
        $verb = 'Created'
    }
    $message = '{0} scheduled restart {1}\{2}. Schedule: {3}. Next run: {4}. Previous schedule: {5}.' -f $verb, $script:TaskFolderPath, $script:TaskName, $summary, $nextText, $(if ($null -ne $existing) { $existing.Summary } else { 'none' })
    Write-LogEntry -Level INFO -Message $message
    Write-AppEventLog -EventId $script:EventIdCreated -Message ('{0} (by {1})' -f $message, $script:CurrentUser)

    Write-Host ''
    Write-Status -Type Success -Message ('  {0} successfully.' -f $verb)
    Write-Status -Type Success -Message ('  Schedule : {0}' -f $summary)
    Write-Status -Type Success -Message ('  Next run : {0}' -f $nextText)
}

function Show-RestartSchedule {
    <# Menu option 2: show the current schedule. #>
    Write-Host ''
    Write-Status -Type Header -Message '  Current scheduled restart'
    Write-Status -Type Header -Message '  -------------------------'
    $info = Get-RestartTaskInfo
    if ($null -eq $info) {
        Write-Status -Type Warning -Message '  No scheduled restart is configured on this computer.'
        return
    }

    $next = 'None (no future run scheduled)'
    if ($null -ne $info.NextRunTime) { $next = Get-DateText -Date $info.NextRunTime }
    $last = 'Never'
    if ($null -ne $info.LastRunTime) { $last = Get-DateText -Date $info.LastRunTime }
    $enabled = 'No'
    if ($info.Enabled) { $enabled = 'Yes' }

    Write-Host ('  Task        : {0}' -f $info.Path)
    Write-Host ('  Type        : {0}' -f (Get-ScheduleTypeText -Schedule $info.Schedule))
    Write-Host ('  Schedule    : {0}' -f $info.Summary)
    Write-Host ('  Time        : {0}' -f (Get-TimeText -Time $info.Schedule.Time))
    Write-Host ('  Next run    : {0}' -f $next)
    Write-Host ('  Last run    : {0}' -f $last)
    Write-Host ('  Last result : {0}' -f (Get-TaskResultText -Code $info.LastResult))
    Write-Host '  Enabled     : ' -NoNewline
    if ($info.Enabled) {
        Write-Status -Type Success -Message $enabled
    }
    else {
        Write-Status -Type Warning -Message $enabled
    }
    Write-Host ('  State       : {0}' -f $info.State)
    Write-Host ('  Description : {0}' -f $info.Description)
}

function Invoke-DeleteRestart {
    <# Menu option 3: delete the scheduled restart. #>
    Write-Host ''
    Write-Status -Type Header -Message '  Delete scheduled restart'
    Write-Status -Type Header -Message '  ------------------------'
    $info = Get-RestartTaskInfo
    if ($null -eq $info) {
        Write-Status -Type Warning -Message '  No scheduled restart is configured. Nothing to delete.'
        return
    }
    Write-Host ('  Current schedule: {0}' -f $info.Summary)
    if (-not (Read-YesNo -Prompt 'Delete this scheduled restart?')) {
        Write-Status -Type Warning -Message '  Cancelled. No changes were made.'
        return
    }

    $folderRemoved = Remove-RestartTask
    $message = 'Deleted scheduled restart {0}\{1}. Schedule was: {2}.' -f $script:TaskFolderPath, $script:TaskName, $info.Summary
    if ($folderRemoved) {
        $message = '{0} Removed empty task folder {1}.' -f $message, $script:TaskFolderPath
    }
    Write-LogEntry -Level INFO -Message $message
    Write-AppEventLog -EventId $script:EventIdDeleted -Message ('{0} (by {1})' -f $message, $script:CurrentUser)

    Write-Status -Type Success -Message '  Scheduled restart deleted.'
    if ($folderRemoved) {
        Write-Status -Type Success -Message ('  Empty task folder {0} removed.' -f $script:TaskFolderPath)
    }
}

function Show-LogTail {
    <# Menu option 4: show the last 30 log lines. #>
    Write-Host ''
    Write-Status -Type Header -Message ('  Last 30 log lines - {0}' -f $script:LogFile)
    Write-Status -Type Header -Message '  ------------------------------------------------------------'
    if (-not (Test-Path -LiteralPath $script:LogFile -PathType Leaf)) {
        Write-Status -Type Warning -Message '  The log file does not exist yet.'
        return
    }
    $lines = @(Get-Content -LiteralPath $script:LogFile -Tail 30 -Encoding UTF8)
    if ($lines.Count -eq 0) {
        Write-Status -Type Warning -Message '  The log file is empty.'
        return
    }
    foreach ($line in $lines) {
        if ($line -match '\[ERROR\]') {
            Write-Status -Type Error -Message $line
        }
        elseif ($line -match '\[WARN\]') {
            Write-Status -Type Warning -Message $line
        }
        else {
            Write-Host $line
        }
    }
}

function Invoke-MenuAction {
    <# Runs a menu action with uniform error handling so bad input or failures never end the session. #>
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )
    try {
        & $Action
    }
    catch [System.OperationCanceledException] {
        Write-Status -Type Warning -Message '  Returned to the main menu. No changes were made.'
    }
    catch {
        $message = '{0} failed: {1}' -f $Name, $_.Exception.Message
        Write-Status -Type Error -Message ('  ERROR: {0}' -f $message)
        Write-LogEntry -Level ERROR -Message $message
    }
}

function Invoke-MainMenu {
    <# Displays the menu until the user chooses Exit. #>
    while ($true) {
        try { [System.Console]::Clear() } catch { Write-Verbose 'Console clearing is not supported by this host.' }
        Show-Header
        Show-MainMenu
        $choice = (Read-Host -Prompt 'Select an option (1-5)')
        if ($null -eq $choice) { $choice = '' }
        switch ($choice.Trim()) {
            '1' { Invoke-MenuAction -Name 'Create / replace scheduled restart' -Action { Invoke-CreateRestart } }
            '2' { Invoke-MenuAction -Name 'Show current schedule' -Action { Show-RestartSchedule } }
            '3' { Invoke-MenuAction -Name 'Delete scheduled restart' -Action { Invoke-DeleteRestart } }
            '4' { Invoke-MenuAction -Name 'View log' -Action { Show-LogTail } }
            '5' { return }
            default {
                Write-Status -Type Warning -Message '  Invalid choice. Enter a number from 1 to 5.'
            }
        }
        Write-Host ''
        $null = Read-Host -Prompt 'Press Enter to return to the menu'
    }
}

#endregion Menu actions

#region Entry point

function Invoke-ScheduledRestartTool {
    <# Entry point: verifies the platform and elevation, then runs the menu. #>
    if ($env:OS -ne 'Windows_NT') {
        Write-Status -Type Error -Message 'This script supports Windows only.'
        exit 1
    }

    if (-not (Test-IsAdministrator)) {
        if ([string]::IsNullOrEmpty($PSCommandPath)) {
            Write-Status -Type Error -Message 'Administrator rights are required. Save the script to a file and run it again, or start PowerShell as Administrator.'
            exit 1
        }
        Write-Status -Type Warning -Message 'Administrator rights are required. Relaunching elevated...'
        $powershell = Join-Path -Path $env:SystemRoot -ChildPath 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $PSCommandPath
        try {
            Start-Process -FilePath $powershell -ArgumentList $arguments -Verb RunAs
        }
        catch {
            Write-Status -Type Error -Message ('Unable to relaunch elevated: {0}' -f $_.Exception.Message)
            exit 1
        }
        exit 0
    }

    try {
        Invoke-MainMenu
    }
    catch {
        Write-Status -Type Error -Message ('Unexpected error: {0}' -f $_.Exception.Message)
        Write-LogEntry -Level ERROR -Message ('Unexpected error: {0}' -f $_.Exception.Message)
        $null = Read-Host -Prompt 'Press Enter to exit'
        exit 1
    }
}

# Run only when executed; allows dot-sourcing for testing without starting the menu.
if ($MyInvocation.InvocationName -ne '.') {
    Invoke-ScheduledRestartTool
}

#endregion Entry point
