# Foreign restart tasks

## Root cause

Up to version 2.0.5 the app looked only at its own task, `\ScheduledRestart\ScheduledRestart`.
A computer that already had a restart task from another source showed "אין תזמון פעיל",
even though it was going to restart. Other sources include another tool, `schtasks`,
PowerShell, or a task created by hand in Task Scheduler. A technician could then add a second
schedule and the machine would restart twice.

## What the app does now

On start, after every action and every 30 seconds, the app scans the whole Task Scheduler
library in the background. It walks every folder recursively and includes hidden tasks.

Skipped:
- everything under `\Microsoft\`
- the app's own task
- the app's integration-test folder (`\ScheduledRestartTest`)

Tasks that cannot be read, usually because of permissions, are skipped and counted.

Each `Exec` action is classified. Matching is case-insensitive, and accepts `/` or `-`
switches, quoted or full paths, and names with or without `.exe`:

| Kind | Detected commands |
| --- | --- |
| Restart | `shutdown /r`, `shutdown /g`, `Restart-Computer`, `psshutdown -r`, `wmic ... reboot`, `Win32_OperatingSystem` + `Reboot` in PowerShell |
| Shutdown | `shutdown /s`, `shutdown /p`, `Stop-Computer`, `psshutdown -s`, `psshutdown -k` |
| Ignored | `shutdown /a`, `/l`, `/h`, `/?`, and anything else |

Wrappers are looked into:

- `cmd.exe /c ...` and `/k`, including `&`, `&&`, `||` chains and `start`.
- `powershell` / `pwsh` with `-Command`, positional commands, `-File`, and
  `-EncodedCommand`. Encoded commands are Base64-decoded from UTF-16LE first.
- `Start-Process <program> -ArgumentList ...` inside PowerShell.
- `.bat`, `.cmd` and `.ps1` targets. The file is read when it exists and is under 1 MB, and the
  same rules are applied line by line, skipping comments. A hit is marked "זוהה בתוך סקריפט".
  A missing or unreadable file is not flagged.

The main window shows "נמצאו N תזמונים נוספים במחשב" with a "הצג" button. The list can:
- **Disable or enable** a task, verified by reading it back.
- **Delete** a task, after saving its XML to
  `C:\ProgramData\ScheduledRestart\backups\<name>-<yyyyMMdd-HHmmss>.xml`.
- **Move a restart task under the app's management**, when its trigger fits the app's model.
  The app creates its own task with the same schedule and verifies it. Then it disables the
  original but does not delete it. Any failure restores the previous state.

Saving a new schedule while an enabled foreign restart task exists asks for confirmation first,
because the computer could restart twice.

## What cannot be detected

- **A pending one-off restart that is not a task**, for example someone ran
  `shutdown /r /t 3600` in a console. Windows keeps no task for it.
- **Restarts from other mechanisms:** Windows Update or Intune/MDM maintenance windows,
  Group Policy, third-party agents and services that call the restart API directly, and
  `ComHandler` (COM) task actions.
- **Indirect commands:** commands assembled at run time, for example from variables,
  `Invoke-Expression`, or downloaded scripts; scripts larger than 1 MB; scripts on paths the
  app cannot read; and executables that restart the machine internally.
- **Tasks the elevated app is not allowed to read.** They are counted in the list footer as
  "N משימות לא נקראו בגלל הרשאות".
