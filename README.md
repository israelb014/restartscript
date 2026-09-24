# Set-ScheduledRestart

An interactive PowerShell tool that lets a technician schedule an automatic restart of the local
computer (one-time, weekly or monthly), view it, and remove it again.

The restart is a Windows Task Scheduler task that runs as SYSTEM and executes:

```
C:\Windows\System32\shutdown.exe /r /f /t 0 /d p:4:1 /c "Scheduled restart"
```

The restart is immediate and forced. Users get no warning and cannot postpone it.

## Requirements

| Item | Requirement |
| --- | --- |
| OS | Windows 10, Windows 11, Windows Server 2016 or later |
| PowerShell | Windows PowerShell 5.1 (PowerShell 7 works but is not required) |
| Rights | Local administrator. The script relaunches itself elevated (UAC prompt) if needed. |
| Modules | None. The script uses the Task Scheduler COM API (`Schedule.Service`), not the `ScheduledTasks` module. |
| Scope | Local computer only |

## How to run

1. Copy `Set-ScheduledRestart.ps1` to the computer, for example to `C:\Tools`.
2. Run it with one of these:
   - Right-click the file and choose **Run with PowerShell**.
   - From a PowerShell console:
     ```powershell
     powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Tools\Set-ScheduledRestart.ps1
     ```
3. If the console is not elevated, accept the UAC prompt. The tool opens in a new elevated window.

At startup the script runs `Unblock-File` on itself to remove the "downloaded from the internet" mark.
If your execution policy stops the script before it can run, run
`Unblock-File C:\Tools\Set-ScheduledRestart.ps1` once, or start it with `-ExecutionPolicy Bypass` as shown above.

## Menu walkthrough

At the top of every screen, a header shows the computer name, the current uptime, and the current
schedule with its next run time. If no schedule exists, it says **Not configured**.

```
  1. Create / replace scheduled restart
  2. Show current schedule
  3. Delete scheduled restart
  4. View log
  5. Exit
```

Type **B** at any prompt inside an option to go back to the main menu without making changes.
If you type something invalid, the tool explains the problem and asks again.

### 1. Create / replace scheduled restart

Choose the schedule type:

| Type | Prompts | Example |
| --- | --- | --- |
| One-time | Date `dd/MM/yyyy`, time `HH:mm` (24-hour). Past dates and times are rejected. | `31/12/2026`, `03:00` |
| Weekly | One or more weekdays by number (`1` = Monday ... `7` = Sunday), separated by commas, then time | `1,5` = Monday and Friday |
| Monthly: day of month | Day `1`-`31`, or `L` for the last day of the month, then time | `15`, or `L` |
| Monthly: day of week | Occurrence (`1` First, `2` Second, `3` Third, `4` Fourth, `5` Last), weekday number, then time | `2` + `2` = second Tuesday |

Days 29-31: months that don't have that day are skipped. To restart at month end every month, use `L`.

Before anything is saved, the tool shows a summary (type, days, time, calculated next run) and asks
for **Y/N** confirmation. If a schedule already exists, the tool warns you at the start and asks for
confirmation to replace it. Nothing changes until you answer **Y**.

Task details:

- Location: `\ScheduledRestart\ScheduledRestart` in Task Scheduler
- Runs as SYSTEM (`S-1-5-18`) with highest privileges, whether or not a user is logged on
- Wakes the computer from sleep to run
- If the computer is off or asleep and misses the scheduled time, the restart is **skipped**. It never runs late.
- Runs on battery power, is not stopped when switching to battery, and has a 10-minute execution limit. A second instance is never started.
- The task description records who created it, when, and the schedule in plain English.
- "Last day of month" and "Last <weekday> of month" schedules are registered from task XML
  (`<Day>Last</Day>`, `<Week>Last</Week>`), as documented in the Task Scheduler schema. All other schedules use the COM trigger API.

After saving, the tool reads the task back from Task Scheduler. It compares the trigger type, days,
time and next run time with the values it calculated. It shows green **Verified** only when every
value matches. If a value differs or registration fails, the tool deletes the task, writes an `ERROR`
line to the log and lists the differences in red. If you were replacing a schedule, the old schedule
is gone too, so create it again.

### 2. Show current schedule

Shows the schedule type, the schedule in plain English, time, next run time, last run time, last
result code (translated to text where known), enabled state and task state. If no schedule exists,
the tool says so.

### 3. Delete scheduled restart

Shows the current schedule and asks for **Y/N** confirmation. Then it deletes the task and removes the
`\ScheduledRestart\` task folder if the folder is now empty.

### 4. View log

Shows the last 30 lines of the log file. Warnings are shown in yellow and errors in red.

### 5. Exit

Closes the tool.

## Logging

| Where | What |
| --- | --- |
| `C:\ProgramData\ScheduledRestart\ScheduledRestart.log` | Every create, replace and delete action and every error |
| Windows **Application** event log, source `ScheduledRestart` | Create/replace (Event ID 1001) and delete (Event ID 1002) |

Log line format:

```
yyyy-MM-dd HH:mm:ss [LEVEL] [DOMAIN\user] message
```

Levels are `INFO`, `WARN` and `ERROR`. The first time the tool writes an event, it registers the
event log source. If registration fails, the tool keeps working and writes a `WARN` line to the log file.

## Removing everything manually

Run these commands in an **elevated** Windows PowerShell console:

```powershell
# 1. Delete the scheduled task and its folder
schtasks.exe /Delete /TN "\ScheduledRestart\ScheduledRestart" /F
$svc = New-Object -ComObject Schedule.Service
$svc.Connect()
$svc.GetFolder('\').DeleteFolder('ScheduledRestart', 0)   # only succeeds when the folder is empty

# 2. Delete the log folder
Remove-Item -Path 'C:\ProgramData\ScheduledRestart' -Recurse -Force

# 3. Remove the event log source (existing events stay in the Application log)
Remove-EventLog -Source 'ScheduledRestart'

# 4. Delete the script itself
Remove-Item -Path 'C:\Tools\Set-ScheduledRestart.ps1'
```

You can also delete the task and folder in Task Scheduler (`taskschd.msc`) under
**Task Scheduler Library > ScheduledRestart**.

## Development notes

- The script is saved as UTF-8 with BOM, and all console text is ASCII.
- It passes PSScriptAnalyzer with no findings. It also passes the `PSUseCompatibleSyntax`,
  `PSUseCompatibleCommands` and `PSUseCompatibleTypes` rules targeting Windows PowerShell 5.1.
  To check it yourself:
  ```powershell
  Install-Module PSScriptAnalyzer -Scope CurrentUser
  Invoke-ScriptAnalyzer -Path .\Set-ScheduledRestart.ps1 -Severity Error,Warning,Information
  ```
- To test the helper functions, you can dot-source the script (`. .\Set-ScheduledRestart.ps1`)
  without starting the menu.
