<#
    Offline tests for Set-ScheduledRestart.ps1 (schedule math, parsing, XML triggers, verification,
    replace/rollback flow). The Task Scheduler COM layer is mocked, so the tests run on any OS with
    PowerShell 5.1 or 7:  pwsh -NoProfile -File tests/Test-ScheduledRestart.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\Set-ScheduledRestart.ps1'), [ref]$null, [ref]$null)
foreach ($f in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) { . ([scriptblock]::Create($f.Extent.Text)) }
# config vars (skip Windows-only ones)
foreach ($st in $ast.EndBlock.Statements) { $t = $st.Extent.Text; if ($t -like '$script:*' -and $t -notlike '*WindowsIdentity*') { . ([scriptblock]::Create($t)) } }
$script:CurrentUser = 'TEST\user'
$fail = 0
function Check($name, $actual, $expected) { if ("$actual" -ceq "$expected") { "PASS $name" } else { "FAIL $name`n   got: $actual`n  want: $expected"; $script:fail++ } }
$from = [datetime]'2026-09-24T10:00:00'   # Thursday
function T([string]$hhmm) { ConvertTo-TimeOfDay -Text $hhmm }

$s = Get-ScheduleTemplate Weekly; $s.DaysOfWeek = ConvertTo-WeekdayList '1,5'; $s.Time = T '03:00'
Check 'weekly next' (Get-NextRunTime $s $from).ToString('s') '2026-09-25T03:00:00'
Check 'weekly summary' (Get-ScheduleSummary $s) 'Every week on Monday and Friday at 03:00'
$s.DaysOfWeek = ConvertTo-WeekdayList '4'; $s.Time = T '09:00'
Check 'weekly same day passed -> next week' (Get-NextRunTime $s $from).ToString('s') '2026-10-01T09:00:00'
$s.Time = T '11:00'
Check 'weekly same day later' (Get-NextRunTime $s $from).ToString('s') '2026-09-24T11:00:00'
Check 'weekday list single elem is array' ((ConvertTo-WeekdayList '7').GetType().Name) 'DayOfWeek[]'
Check 'weekday sort/dedupe' ((ConvertTo-WeekdayList '7 3,3;1') -join ',') 'Monday,Wednesday,Sunday'
foreach ($bad in @('', '8', '0', 'x', '1,,9')) { try { ConvertTo-WeekdayList $bad; Check "weekday bad '$bad'" 'no error' 'error' } catch [FormatException] { "PASS weekday bad '$bad'" } }

$m = Get-ScheduleTemplate MonthlyDay; $m.MonthDays = @(31); $m.Time = T '02:00'
Check 'monthly 31 skips Sep' (Get-NextRunTime $m $from).ToString('s') '2026-10-31T02:00:00'
$m.MonthDays = @(); $m.LastDayOfMonth = $true
Check 'monthly last day' (Get-NextRunTime $m $from).ToString('s') '2026-09-30T02:00:00'
Check 'monthly last summary' (Get-ScheduleSummary $m) 'On the last day of every month at 02:00'
$m.LastDayOfMonth = $false; $m.MonthDays = @(24); $m.Time = T '09:00'
Check 'monthly today passed' (Get-NextRunTime $m $from).ToString('s') '2026-10-24T09:00:00'
Check 'monthly 29 Feb (non-leap skip)' (Get-NextRunTime (& { $x = Get-ScheduleTemplate MonthlyDay; $x.MonthDays=@(29); $x.Time=(T '01:00'); $x }) ([datetime]'2027-01-30')).ToString('s') '2027-03-29T01:00:00'

$w = Get-ScheduleTemplate MonthlyWeekday; $w.WeeksOfMonth = @(2); $w.DaysOfWeek = @([DayOfWeek]::Tuesday); $w.Time = T '04:00'
Check 'second Tuesday' (Get-NextRunTime $w $from).ToString('s') '2026-10-13T04:00:00'
Check 'second Tuesday summary' (Get-ScheduleSummary $w) 'On the second Tuesday of every month at 04:00'
$w.WeeksOfMonth = @(5); $w.DaysOfWeek = @([DayOfWeek]::Friday)
Check 'last Friday Sep' (Get-NextRunTime $w $from).ToString('s') '2026-09-25T04:00:00'
$w.WeeksOfMonth = @(1); $w.DaysOfWeek = @([DayOfWeek]::Thursday); $w.Time = T '23:00'
Check 'first Thursday Oct' (Get-NextRunTime $w $from).ToString('s') '2026-10-01T23:00:00'

$o = Get-ScheduleTemplate Once; $o.StartDate = ConvertTo-CalendarDate '30/09/2026'; $o.Time = T '3:05'
Check 'once next' (Get-NextRunTime $o $from).ToString('s') '2026-09-30T03:05:00'
Check 'once summary' (Get-ScheduleSummary $o) 'Once on Wednesday 30/09/2026 at 03:05'
Check 'once past -> null' ($null -eq (Get-NextRunTime $o ([datetime]'2026-10-01'))) 'True'
foreach ($bad in @('31/02/2026', '2026-09-30', '13/13/2026', '')) { try { ConvertTo-CalendarDate $bad; Check "date bad '$bad'" 'ok' 'error' } catch [FormatException] { "PASS date bad '$bad'" } }
foreach ($bad in @('24:00', '3pm', '12:60', '')) { try { ConvertTo-TimeOfDay $bad; Check "time bad '$bad'" 'ok' 'error' } catch [FormatException] { "PASS time bad '$bad'" } }

# trigger decode round-trip
$tw = [pscustomobject]@{ Type = 3; StartBoundary = '2026-09-24T03:00:00'; DaysOfWeek = (Get-DayBitmask -Day ([DayOfWeek[]]@('Monday','Friday'))) }
Check 'decode weekly' (Get-ScheduleSummary (Get-ScheduleFromTrigger $tw)) 'Every week on Monday and Friday at 03:00'
$tm = [pscustomobject]@{ Type = 4; StartBoundary = '2026-09-24T02:00:00+03:00'; DaysOfMonth = (1 -shl 30); RunOnLastDayOfMonth = $false }
Check 'decode monthly 31' (Get-ScheduleSummary (Get-ScheduleFromTrigger $tm)) 'On day 31 of every month at 02:00'
$tl = [pscustomobject]@{ Type = 4; StartBoundary = '2026-09-24T02:00:00'; DaysOfMonth = 0; RunOnLastDayOfMonth = $true }
Check 'decode monthly last' (Get-ScheduleSummary (Get-ScheduleFromTrigger $tl)) 'On the last day of every month at 02:00'
$td = [pscustomobject]@{ Type = 5; StartBoundary = '2026-09-24T04:00:00'; DaysOfWeek = 32; WeeksOfMonth = 0; RunOnLastWeekOfMonth = $true }
Check 'decode last Friday' (Get-ScheduleSummary (Get-ScheduleFromTrigger $td)) 'On the last Friday of every month at 04:00'
$tt = [pscustomobject]@{ Type = 1; StartBoundary = '2026-12-31T23:59:00' }
Check 'decode once' (Get-ScheduleSummary (Get-ScheduleFromTrigger $tt)) 'Once on Thursday 31/12/2026 at 23:59'

Check 'result neg' (Get-TaskResultText -2147216609) '0x8004131F (An instance of this task is already running)'
Check 'result 0' (Get-TaskResultText 0) '0x00000000 (The operation completed successfully)'
Check 'result unknown' (Get-TaskResultText 5) '0x00000005 (Unknown result code)'
Check 'join3' (Join-EnglishList @('A','B','C')) 'A, B and C'

# ---- XML trigger path ----
$sampleXml = '<?xml version="1.0" encoding="UTF-16"?><Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><RegistrationInfo><Author>x</Author></RegistrationInfo><Principals><Principal id="Author"><UserId>S-1-5-18</UserId><LogonType>ServiceAccount</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><WakeToRun>true</WakeToRun></Settings><Actions Context="Author"><Exec><Command>C:\Windows\System32\shutdown.exe</Command></Exec></Actions></Task>'
$ld = Get-ScheduleTemplate MonthlyDay; $ld.LastDayOfMonth = $true; $ld.Time = T '02:00'
Check 'xml needed last day' (Test-RequiresXmlTrigger $ld) 'True'
$d15 = Get-ScheduleTemplate MonthlyDay; $d15.MonthDays = @(15); $d15.Time = T '02:00'
Check 'xml not needed day 15' (Test-RequiresXmlTrigger $d15) 'False'
$lw = Get-ScheduleTemplate MonthlyWeekday; $lw.WeeksOfMonth = @(5); $lw.DaysOfWeek = ConvertTo-WeekdayList '5'; $lw.Time = T '04:00'
Check 'xml needed last Friday' (Test-RequiresXmlTrigger $lw) 'True'
$lw2 = Get-ScheduleTemplate MonthlyWeekday; $lw2.WeeksOfMonth = @(2); $lw2.DaysOfWeek = ConvertTo-WeekdayList '2'
Check 'xml not needed second Tue' (Test-RequiresXmlTrigger $lw2) 'False'
$today = (Get-Date).ToString('yyyy-MM-dd')
$out = Get-TaskXmlWithTrigger -TaskXml $sampleXml -Schedule $ld
Check 'xml last day no empty ns' ($out -notmatch 'xmlns=""') 'True'
Check 'xml last day trigger' ([regex]::Match($out, '<Triggers>.*</Triggers>').Value) "<Triggers><CalendarTrigger><StartBoundary>${today}T02:00:00</StartBoundary><Enabled>true</Enabled><ScheduleByMonth><DaysOfMonth><Day>Last</Day></DaysOfMonth><Months><January /><February /><March /><April /><May /><June /><July /><August /><September /><October /><November /><December /></Months></ScheduleByMonth></CalendarTrigger></Triggers>"
Check 'xml Triggers after RegistrationInfo' (($out.IndexOf('</RegistrationInfo><Triggers>')) -gt 0) 'True'
$out2 = Get-TaskXmlWithTrigger -TaskXml $out -Schedule $lw
Check 'xml last Friday (replaces existing)' ([regex]::Match($out2, '<Triggers>.*</Triggers>').Value -replace '<Months>.*</Months>','M') "<Triggers><CalendarTrigger><StartBoundary>${today}T04:00:00</StartBoundary><Enabled>true</Enabled><ScheduleByMonthDayOfWeek><Weeks><Week>Last</Week></Weeks><DaysOfWeek><Friday /></DaysOfWeek>M</ScheduleByMonthDayOfWeek></CalendarTrigger></Triggers>"
$multi = Get-ScheduleTemplate MonthlyWeekday; $multi.WeeksOfMonth = @(5,1); $multi.DaysOfWeek = ConvertTo-WeekdayList '7,1'
Check 'xml multi weekdays' ([regex]::Match((Get-CalendarTriggerXml $multi), '<Weeks>.*</DaysOfWeek>').Value) '<Weeks><Week>1</Week><Week>Last</Week></Weeks><DaysOfWeek><Monday /><Sunday /></DaysOfWeek>'
$withTrig = $sampleXml -replace '</RegistrationInfo>', '</RegistrationInfo><Triggers><TimeTrigger><StartBoundary>2020-01-01T00:00:00</StartBoundary></TimeTrigger></Triggers>'
Check 'xml replaces old triggers' (([regex]::Matches((Get-TaskXmlWithTrigger -TaskXml $withTrig -Schedule $ld), 'Trigger>')).Count) '2'
Check 'xml parses back' (([xml](Get-TaskXmlWithTrigger -TaskXml $sampleXml -Schedule $lw)).Task.Triggers.CalendarTrigger.ScheduleByMonthDayOfWeek.Weeks.Week) 'Last'

# ---- verification ----
$now = [datetime]'2026-09-24T10:00:00'
$exp = Get-ScheduleTemplate Weekly; $exp.DaysOfWeek = ConvertTo-WeekdayList '1,5'; $exp.Time = T '03:00'
$act = [pscustomobject]@{ Schedule = (Get-ScheduleFromTrigger ([pscustomobject]@{ Type = 3; StartBoundary = '2026-09-24T03:00:00'; DaysOfWeek = 34 })); NextRunTime = [datetime]'2026-09-25T03:00:00' }
$d = Get-ScheduleDifference -Expected $exp -Actual $act -From $now
Check 'verify match' $d.Count 0
Check 'verify match type' ($d.GetType().Name) 'String[]'
$act.NextRunTime = [datetime]'2026-09-28T03:00:00'
Check 'verify next-run mismatch' ((Get-ScheduleDifference -Expected $exp -Actual $act -From $now) -join '|') 'Next run: expected Friday 25/09/2026 03:00, Task Scheduler reports Monday 28/09/2026 03:00'
$act = [pscustomobject]@{ Schedule = (Get-ScheduleFromTrigger ([pscustomobject]@{ Type = 3; StartBoundary = '2026-09-24T03:30:00'; DaysOfWeek = 2 })); NextRunTime = [datetime]'2026-09-25T03:00:00' }
$d = Get-ScheduleDifference -Expected $exp -Actual $act -From $now
Check 'verify days+time+next mismatch' ($d -join '|') 'Weekdays: expected "Monday, Friday", found "Monday"|Time: expected 03:00, found 03:30'
$act = [pscustomobject]@{ Schedule = (Get-ScheduleFromTrigger ([pscustomobject]@{ Type = 4; StartBoundary = '2026-09-24T02:00:00'; DaysOfMonth = 0; RunOnLastDayOfMonth = $true })); NextRunTime = [datetime]'2026-09-30T02:00:00' }
Check 'verify last day match' ((Get-ScheduleDifference -Expected $ld -Actual $act -From $now).Count) 0
$act.Schedule.LastDayOfMonth = $false
Check 'verify last day flag lost' ((Get-ScheduleDifference -Expected $ld -Actual $act -From $now) -join '|') 'Last day of month: expected "True", found "False"'
$act = [pscustomobject]@{ Schedule = (Get-ScheduleFromTrigger ([pscustomobject]@{ Type = 5; StartBoundary = '2026-09-24T04:00:00'; DaysOfWeek = 32; WeeksOfMonth = 0; RunOnLastWeekOfMonth = $true })); NextRunTime = [datetime]'2026-09-25T04:00:00' }
Check 'verify last Friday match' ((Get-ScheduleDifference -Expected $lw -Actual $act -From $now).Count) 0
Check 'verify type mismatch' ((Get-ScheduleDifference -Expected $exp -Actual $act -From $now)[0]) 'Trigger type: expected Weekly, found Monthly (day of week)'
$act.NextRunTime = $null
Check 'verify disabled/no next run' ((Get-ScheduleDifference -Expected $lw -Actual $act -From $now) -join '|') 'Next run: expected Friday 25/09/2026 04:00, Task Scheduler reports None'
$o2 = Get-ScheduleTemplate Once; $o2.StartDate = [datetime]'2026-09-30'; $o2.Time = T '03:05'
$act = [pscustomobject]@{ Schedule = (Get-ScheduleFromTrigger ([pscustomobject]@{ Type = 1; StartBoundary = '2026-10-30T03:05:00' })); NextRunTime = [datetime]'2026-10-30T03:05:00' }
Check 'verify once date mismatch' ((Get-ScheduleDifference -Expected $o2 -Actual $act -From $now)[0]) 'Date: expected "30/09/2026", found "30/10/2026"'

# input loops with mocked Read-Host
function Write-Host { }
$script:answers = [System.Collections.Queue]::new()
function Read-Host { param($Prompt) $script:answers.Dequeue() }
function Feed { $script:answers.Clear(); foreach ($a in $args) { $script:answers.Enqueue($a) } }
Feed 'x' '9' ' 2 '; Check 'menu number reprompt' (Read-MenuNumber -Prompt p -Minimum 1 -Maximum 3) '2'
Check 'queue drained' $script:answers.Count 0
Feed 'maybe' 'y'; Check 'yes/no' (Read-YesNo -Prompt p) 'True'
Feed 'b'; try { Read-MenuNumber -Prompt p -Minimum 1 -Maximum 3; Check 'B cancels' 'no' 'cancel' } catch [OperationCanceledException] { 'PASS B cancels' }
$tomorrow = (Get-Date).Date.AddDays(1)
$yesterday = (Get-Date).Date.AddDays(-1).ToString('dd/MM/yyyy', [cultureinfo]::InvariantCulture)
$nowText = (Get-Date).AddMinutes(-1).ToString('HH:mm', [cultureinfo]::InvariantCulture)
Feed '1/1/2020' $yesterday ((Get-Date).ToString('dd/MM/yyyy', [cultureinfo]::InvariantCulture)) $nowText 'B'
try { $null = Read-OneTimeSchedule; Check 'one-time past time today rejected' 'accepted' 'rejected' } catch [OperationCanceledException] { Check 'one-time past date/time rejected' $script:answers.Count 0 }
Feed $tomorrow.ToString('dd/MM/yyyy', [cultureinfo]::InvariantCulture) '23:30'
$os = Read-OneTimeSchedule; Check 'one-time future ok' (Get-ScheduleSummary $os) ('Once on {0} at 23:30' -f $tomorrow.ToString('dddd dd/MM/yyyy', [cultureinfo]::InvariantCulture))
Feed 'abc' '5' '22:00'; $ws = Read-WeeklySchedule; Check 'weekly read single' ($ws.DaysOfWeek.GetType().Name + ' ' + (Get-ScheduleSummary $ws)) 'DayOfWeek[] Every week on Friday at 22:00'
Feed '1' '32' 'L' '01:00'; $ms = Read-MonthlySchedule; Check 'monthly read L' (Get-ScheduleSummary $ms) 'On the last day of every month at 01:00'
Feed '2' '6' '5' '0' '7' '01:30'; $ms = Read-MonthlySchedule; Check 'monthly read Nth' (Get-ScheduleSummary $ms) 'On the last Sunday of every month at 01:30'
# ---- replace / rollback flow (COM layer mocked) ----
$script:log = [System.Collections.Generic.List[string]]::new()
$script:ui = [System.Collections.Generic.List[string]]::new()
$script:st = @{}
function Write-LogEntry { param($Message, $Level = 'INFO') $script:log.Add("$Level $Message") }
function Write-Status { param($Message, $Type = 'Info') $script:ui.Add("$Type $Message") }
function Write-AppEventLog { param($Message, $EventId) $script:log.Add("EVENT $EventId") }
function New-Info($sch, [switch]$Broken) { $n = Get-NextRunTime $sch; if ($Broken) { $n = $n.AddDays(1) }; [pscustomobject]@{ Schedule = $sch; Summary = (Get-ScheduleSummary $sch); NextRunTime = $n } }
function Get-RestartTaskInfo { $script:st.Current }
function Get-RestartTaskXml { if ($script:st['ExportThrows']) { throw 'export boom' }; if ($script:st.Current) { '<Task>old</Task>' } }
function Save-TaskBackup { param($Xml) $script:st.Saved = $Xml; 'C:\ProgramData\ScheduledRestart\ScheduledRestart.previous.xml' }
function Remove-RestartTask { if ($script:st['RemoveThrows']) { $script:st.RemoveThrows = $false; throw 'delete boom' }; $script:st.Current = $null; $script:st.Removed++; $true }
function New-RestartTask { param($Schedule) if ($script:st['RegisterThrows']) { throw 'register boom' }; $script:st.Current = New-Info $Schedule -Broken:([bool]$script:st['Mismatch']) }
function Restore-RestartTask { param($Xml) $script:st.RestoredXml = $Xml; if ($script:st['RestoreThrows']) { throw 'restore boom' }; $script:st.Current = $script:st.Old }
function Scenario([hashtable]$flags, [switch]$HasOld) {
    $script:log.Clear(); $script:ui.Clear()
    $old = Get-ScheduleTemplate Weekly; $old.DaysOfWeek = ConvertTo-WeekdayList '3'; $old.Time = T '01:00'
    $script:st = @{ Current = $null; Old = (New-Info $old); Removed = 0 }
    if ($HasOld) { $script:st.Current = $script:st.Old }
    foreach ($k in $flags.Keys) { $script:st[$k] = $flags[$k] }
    $new = Get-ScheduleTemplate Weekly; $new.DaysOfWeek = ConvertTo-WeekdayList '5'; $new.Time = T '02:00'
    $existing = $null; if ($HasOld) { $existing = $script:st.Old }
    Invoke-ScheduleRegistration -Schedule $new -Existing $existing
}
$oldSummary = 'Every week on Wednesday at 01:00'

Check 'create: verified' (Scenario @{}) 'Verified'
Check 'create: no export/delete' (@($script:log | Where-Object { $_ -match 'Exported|Deleted previous' }).Count) 0
Check 'create: green verified' (@($script:ui | Where-Object { $_ -like 'Success   Verified: created*' }).Count) 1

Check 'create fails: Failed, no restore' (Scenario @{ RegisterThrows = $true }) 'Failed'
Check 'create fails: no yellow restore msg' (@($script:ui -match 'previous schedule restored').Count) 0

Check 'replace ok: verified' (Scenario @{} -HasOld) 'Verified'
Check 'replace ok: log order' (($script:log | ForEach-Object { ($_ -split ' ')[0..1] -join ' ' }) -join ',') 'INFO Exported,INFO Deleted,INFO Replaced,EVENT 1001'
Check 'replace ok: xml saved' $script:st.Saved '<Task>old</Task>'
Check 'replace ok: new active' $script:st.Current.Summary 'Every week on Friday at 02:00'

Check 'replace register throws: Restored' (Scenario @{ RegisterThrows = $true } -HasOld) 'Restored'
Check 'restored: old active again' $script:st.Current.Summary $oldSummary
Check 'restored: used exported xml' $script:st.RestoredXml '<Task>old</Task>'
Check 'restored: yellow message' ($script:ui -contains 'Warning   New schedule failed - previous schedule restored') 'True'
Check 'restored: log has ERROR then WARN restore' (($script:log | ForEach-Object { ($_ -split ' ')[0] }) -join ',') 'INFO,INFO,ERROR,WARN'
Check 'restored: error lists cause' (@($script:ui -match 'register boom').Count) 1
Check 'restored: no create event' (@($script:log -match 'EVENT').Count) 0

Check 'replace verify mismatch: Restored' (Scenario @{ Mismatch = $true } -HasOld) 'Restored'
Check 'mismatch: new task was deleted first' $script:st.Removed 2
Check 'mismatch: error names next run' (@($script:ui -match 'Next run: expected').Count) 1
Check 'mismatch: old active' $script:st.Current.Summary $oldSummary

Check 'restore throws: Failed' (Scenario @{ RegisterThrows = $true; RestoreThrows = $true } -HasOld) 'Failed'
Check 'restore throws: manual schtasks hint' (@($script:ui -match 'schtasks.exe /Create /TN "\\ScheduledRestart\\ScheduledRestart" /XML').Count) 1
Check 'restore throws: logged ERROR' (@($script:log | Where-Object { $_ -like 'ERROR Unable to restore*restore boom*' }).Count) 1
Check 'restore throws: no yellow restored' (@($script:ui -match 'previous schedule restored').Count) 0

Check 'export fails: Aborted' (Scenario @{ ExportThrows = $true } -HasOld) 'Aborted'
Check 'export fails: old untouched' $script:st.Current.Summary $oldSummary
Check 'export fails: nothing deleted' $script:st.Removed 0

Check 'delete old fails: Restored' (Scenario @{ RemoveThrows = $true } -HasOld) 'Restored'
Check 'delete old fails: new never registered' (@($script:log -match 'Replaced').Count) 0
Check 'delete old fails: old active' $script:st.Current.Summary $oldSummary

"`nFailures: $fail"
if ($fail -gt 0) { exit 1 }
