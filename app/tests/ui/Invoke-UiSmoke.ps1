<#
    UI smoke test for ScheduledRestart.exe (Windows PowerShell 5.1, elevated, interactive desktop).

    Starts the exe, waits for the main window and uses UI Automation to open the schedule dialog, the
    log viewer, the About window and the foreign-tasks window, saving a screenshot of each. Runs twice:
    with the normal work area, and with the work area limited to 1093x576 - what 1366x768 at 125%
    scaling leaves in device-independent pixels after the taskbar. Nothing is saved or changed: every
    dialog is closed with its cancel/close button. A disabled foreign test task (2035) is created so the
    foreign-tasks strip appears, and removed at the end.

    Usage: powershell -File Invoke-UiSmoke.ps1 -Exe <path to ScheduledRestart.exe> -OutDir <folder>
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SmokeNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int param, ref RECT rect, int winIni);
    public static RECT GetWorkArea() { var r = new RECT(); SystemParametersInfo(0x30, 0, ref r, 0); return r; }
    public static bool SetWorkArea(RECT r) { return SystemParametersInfo(0x2F, 0, ref r, 0x2); }
}
'@

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$results = New-Object System.Collections.Generic.List[string]
$failures = 0
$A = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

function Write-Result([string]$line) {
    $results.Add($line)
    Write-Host $line
}

function Save-Shot([System.Windows.Rect]$rect, [string]$name) {
    $x = [int][math]::Max(0, $rect.X); $y = [int][math]::Max(0, $rect.Y)
    $w = [int][math]::Max(1, $rect.Width); $h = [int][math]::Max(1, $rect.Height)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
        $path = Join-Path $OutDir $name
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Result ("  screenshot {0} ({1}x{2} at {3},{4})" -f $name, $w, $h, $x, $y)
    } finally {
        $g.Dispose(); $bmp.Dispose()
    }
}

function Find-ById($root, [string]$id, [int]$timeoutSec = 15) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    do {
        $el = $root.FindFirst($Scope::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw "Element '$id' not found"
}

function Invoke-Element($el) {
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$script:MainElement = $null

# Owned (modal) windows are exposed as children of their owner window, not of the desktop root.
function Get-ProcessWindows([int]$processId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $processId)
    $windows = @($A::RootElement.FindAll($Scope::Children, $cond))
    if ($script:MainElement) {
        $isWindow = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
        try { $windows += @($script:MainElement.FindAll($Scope::Children, $isWindow)) } catch { }
    }
    return $windows
}

function Wait-NewWindow([int]$processId, [int[]]$known, [int]$timeoutSec = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    do {
        foreach ($w in (Get-ProcessWindows $processId)) {
            if ($known -notcontains $w.Current.NativeWindowHandle) { return $w }
        }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw 'Dialog window did not appear'
}

function Wait-Closed([int]$processId, [int]$handle, [int]$timeoutSec = 10) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    do {
        if (-not ((Get-ProcessWindows $processId) | Where-Object { $_.Current.NativeWindowHandle -eq $handle })) { return }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw 'Dialog did not close'
}

function Test-Dialog($main, [int]$processId, [string]$opener, [string]$closer, [string]$shot, $workArea) {
    try {
        $known = @((Get-ProcessWindows $processId) | ForEach-Object { $_.Current.NativeWindowHandle })
        Invoke-Element (Find-ById $main $opener)
        $dialog = Wait-NewWindow $processId $known
        Start-Sleep -Milliseconds 900   # entrance animation
        $rect = $dialog.Current.BoundingRectangle
        Write-Result ("  {0}: '{1}' {2}x{3}" -f $opener, $dialog.Current.Name, [int]$rect.Width, [int]$rect.Height)
        Save-Shot $rect $shot
        if ($workArea -and $rect.Height -gt ($workArea.Bottom - $workArea.Top) + 1) {
            Write-Result "  FAIL: $opener window is taller than the work area"
            $script:failures++
        }
        if ($closer) {
            Invoke-Element (Find-ById $dialog $closer)
        } else {
            $dialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        }
        Wait-Closed $processId $dialog.Current.NativeWindowHandle
    } catch {
        Write-Result "  FAIL: $opener - $($_.Exception.Message)"
        $script:failures++
    }
}

function New-SmokeForeignTask {
    $xml = @'
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Author>UI smoke test</Author></RegistrationInfo>
  <Triggers><TimeTrigger><StartBoundary>2035-06-01T03:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><Enabled>false</Enabled><StartWhenAvailable>false</StartWhenAvailable></Settings>
  <Actions Context="Author"><Exec><Command>C:\Windows\System32\shutdown.exe</Command><Arguments>/r /f /t 0</Arguments></Exec></Actions>
</Task>
'@
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    $root = $service.GetFolder('\')
    try { $folder = $service.GetFolder('\ScheduledRestartSmoke') } catch { $folder = $root.CreateFolder('ScheduledRestartSmoke') }
    $null = $folder.RegisterTask('Legacy nightly reboot', $xml, 6, 'SYSTEM', $null, 5)
}

function Remove-SmokeForeignTask {
    try {
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $folder = $service.GetFolder('\ScheduledRestartSmoke')
        try { $folder.DeleteTask('Legacy nightly reboot', 0) } catch { }
        $service.GetFolder('\').DeleteFolder('ScheduledRestartSmoke', 0)
    } catch { }
}

$WinlogonKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'

# The taskbar (explorer.exe) resets the work area, so it is stopped for the limited pass and restarted after.
function Stop-Shell {
    Set-ItemProperty -Path $WinlogonKey -Name AutoRestartShell -Value 0 -Type DWord
    Get-Process explorer -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

function Start-Shell {
    Set-ItemProperty -Path $WinlogonKey -Name AutoRestartShell -Value 1 -Type DWord
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
}

function Invoke-Pass([string]$label, $limit) {
    Write-Result "=== $label ==="
    $original = [SmokeNative]::GetWorkArea()
    $workArea = $null
    if ($limit) {
        Stop-Shell
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $r = New-Object SmokeNative+RECT
        $r.Left = 0; $r.Top = 0
        $r.Right = [math]::Min($limit[0], $screen.Width); $r.Bottom = [math]::Min($limit[1], $screen.Height)
        if (-not [SmokeNative]::SetWorkArea($r)) { Write-Result '  WARN: could not set work area' }
        $workArea = [SmokeNative]::GetWorkArea()
        Write-Result ("  work area limited to {0}x{1}" -f ($workArea.Right - $workArea.Left), ($workArea.Bottom - $workArea.Top))
        if (($workArea.Bottom - $workArea.Top) -gt $limit[1]) {
            Write-Result '  FAIL: the work area could not be limited'
            $script:failures++
        }
    }
    $proc = $null
    try {
        $proc = Start-Process -FilePath $Exe -PassThru
        $deadline = (Get-Date).AddSeconds(45)
        while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500; $proc.Refresh() }
        if ($proc.MainWindowHandle -eq 0) { throw 'Main window did not appear' }
        $main = $A::FromHandle($proc.MainWindowHandle)
        $script:MainElement = $main
        $null = Find-ById $main 'ForeignShowButton' 30   # wait for the background scan to show the strip
        Start-Sleep -Milliseconds 800
        $rect = $main.Current.BoundingRectangle
        Write-Result ("  main window '{0}' {1}x{2}" -f $main.Current.Name, [int]$rect.Width, [int]$rect.Height)
        Save-Shot $rect "$label-1-main.png"
        if ($workArea -and $rect.Height -gt ($workArea.Bottom - $workArea.Top) + 1) {
            Write-Result '  FAIL: main window is taller than the work area'
            $script:failures++
        }
        Test-Dialog $main $proc.Id 'ScheduleButton' 'CancelButton' "$label-2-schedule.png" $workArea
        Test-Dialog $main $proc.Id 'LogButton' $null "$label-3-log.png" $workArea
        Test-Dialog $main $proc.Id 'SignatureButton' 'CloseButton' "$label-4-about.png" $workArea
        Test-Dialog $main $proc.Id 'ForeignShowButton' 'CloseButton' "$label-5-foreign.png" $workArea
        $screenRect = New-Object System.Windows.Rect 0, 0, ([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width), ([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
        Save-Shot $screenRect "$label-0-screen.png"
    } catch {
        Write-Result "  FAIL: $($_.Exception.Message)"
        $script:failures++
        try { Save-Shot (New-Object System.Windows.Rect 0, 0, ([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width), ([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)) "$label-error-screen.png" } catch { }
    } finally {
        if ($proc -and -not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(10000) | Out-Null }
        if ($limit) {
            [SmokeNative]::SetWorkArea($original) | Out-Null
            Start-Shell
        }
    }
}

try {
    New-SmokeForeignTask
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Write-Result ("Screen {0}x{1}" -f $screen.Width, $screen.Height)
    Invoke-Pass 'normal' $null
    Invoke-Pass 'small-1366x768-125' @(1093, 576)
} finally {
    Remove-SmokeForeignTask
    $results | Set-Content -Path (Join-Path $OutDir 'summary.txt') -Encoding UTF8
}

Write-Host "UI smoke failures: $failures"
if ($failures -gt 0) { exit 1 }
exit 0
