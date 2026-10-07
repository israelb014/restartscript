using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScheduledRestart.Core;
using ScheduledRestart.Services;

namespace ScheduledRestart
{
    public partial class MainWindow : Window
    {
        private const int RestartNowSeconds = 60;
        private readonly DispatcherTimer _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        private ScheduleStatus _status;
        private bool _statusKnown;
        private ForeignScanResult _foreign;
        private bool _scanning;
        private bool _scanErrorLogged;

        /// <summary>Outcome of a finished one-time schedule; shown until the next action.</summary>
        private string _outcomeText;

        public MainWindow()
        {
            InitializeComponent();
            WindowFit.Apply(this);
            ComputerText.Text = "מחשב: " + Environment.MachineName;
            SourceInitialized += (s, e) => NativeMethods.UseDarkTitleBar(this);
            Loaded += (s, e) =>
            {
                RefreshStatus();
                _refreshTimer.Start();
            };
            _refreshTimer.Tick += (s, e) => RefreshStatus();
            Closed += (s, e) => _refreshTimer.Stop();
        }

        private TaskPresence Presence
        {
            get { return _status == null ? TaskPresence.None : _status.Presence; }
        }

        private int ForeignCount
        {
            get { return _foreign == null ? 0 : _foreign.Tasks.Count; }
        }

        private void RefreshStatus()
        {
            try
            {
                string outcome = App.Manager.ResolveFinishedOneTime();
                if (outcome != null) _outcomeText = outcome;
                _status = App.Manager.GetStatus();
                _statusKnown = true;
            }
            catch (Exception ex)
            {
                _status = null;
                _statusKnown = false;
                ShowStatus("לא ניתן לקרוא את מצב התזמון", ex.Message, null, "IconWarning", (Brush)FindResource("ErrorBrush"));
                SetText(WarningText, null);
                SetText(LastRunText, null);
                SetText(OutcomeText, null);
                MissedText.Visibility = Visibility.Collapsed;
                UpdateButtons();
                return;
            }
            ApplyStatus();
            StartForeignScan();
        }

        private void ApplyStatus()
        {
            if (!_statusKnown) return;
            Brush accent = (Brush)FindResource("AccentBrush");
            Brush neutral = (Brush)FindResource("TextFaintBrush");
            RestartSchedule schedule = _status.Schedule;
            switch (_status.Presence)
            {
                case TaskPresence.None:
                    ShowStatus(ForeignCount > 0 ? "אין תזמון של התוכנה" : "אין תזמון פעיל",
                        "לחצו על \"קבע תזמון\" כדי לקבוע הפעלה מחדש אוטומטית.", null, "IconEmpty", neutral);
                    break;
                case TaskPresence.Active:
                    ShowStatus("תזמון פעיל", HebrewText.Describe(schedule),
                        HebrewText.NextRunLine(_status.NextRun ?? schedule.GetNextRun(DateTime.Now)), "IconCheck", accent);
                    break;
                case TaskPresence.Paused:
                    ShowStatus("התזמון מושהה", HebrewText.Describe(schedule),
                        "ההפעלה מחדש לא תתבצע עד לחידוש התזמון.", "IconPausedCircle", neutral);
                    break;
                default:
                    ShowStatus("קיים תזמון בפורמט לא מוכר", "ניתן להחליף אותו בתזמון חדש או לבטל אותו.",
                        _status.NextRun.HasValue ? HebrewText.NextRunLine(_status.NextRun) : null, "IconWarning", (Brush)FindResource("ErrorBrush"));
                    break;
            }

            SetText(OutcomeText, _status.Presence == TaskPresence.None ? _outcomeText : null);
            MissedText.Visibility = _status.Missed ? Visibility.Visible : Visibility.Collapsed;
            SetText(WarningText, schedule != null && schedule.WarningMinutes > 0 ? HebrewText.WarningSummary(schedule.WarningMinutes) : null);
            SetText(LastRunText, _status.LastRun.HasValue
                ? string.Format("הרצה אחרונה: {0} ({1})", HebrewText.DateTimeText(_status.LastRun.Value), HebrewText.TaskResult(_status.LastResult))
                : null);

            ForeignStrip.Visibility = ForeignCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            ForeignText.Text = ForeignCount == 1 ? "נמצא תזמון נוסף אחד במחשב" : string.Format("נמצאו {0} תזמונים נוספים במחשב", ForeignCount);
            UpdateButtons();
        }

        /// <summary>Scans all task folders in the background; the status card updates when it finishes.</summary>
        private void StartForeignScan()
        {
            if (_scanning) return;
            _scanning = true;
            Task.Run(() => App.Foreign.Scan()).ContinueWith(t =>
            {
                _scanning = false;
                if (t.Exception != null)
                {
                    if (!_scanErrorLogged)
                    {
                        _scanErrorLogged = true;
                        App.Manager.LogFailure("סריקת התזמונים במחשב", t.Exception.GetBaseException());
                    }
                    return;
                }
                _foreign = t.Result;
                ApplyStatus();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>A synchronous scan for decisions that must use the current state.</summary>
        private ForeignScanResult ScanNow()
        {
            try
            {
                _foreign = App.Foreign.Scan();
            }
            catch (Exception ex)
            {
                App.Manager.LogFailure("סריקת התזמונים במחשב", ex);
            }
            return _foreign;
        }

        private void ShowStatus(string title, string description, string nextRun, string iconKey, Brush color)
        {
            StatusTitle.Text = title;
            SetText(StatusDescription, description);
            SetText(NextRunText, nextRun);
            StatusIcon.Data = (Geometry)FindResource(iconKey);
            StatusIcon.Stroke = color;
            StatusRing.Stroke = color;
        }

        private static void SetText(System.Windows.Controls.TextBlock block, string text)
        {
            block.Text = text ?? string.Empty;
            block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateButtons()
        {
            TaskPresence p = Presence;
            ScheduleLabel.Text = p == TaskPresence.None ? "קבע תזמון" : "שנה תזמון";
            ScheduleButton.IsEnabled = _statusKnown;
            PauseButton.IsEnabled = _statusKnown && (p == TaskPresence.Active || p == TaskPresence.Paused);
            bool paused = p == TaskPresence.Paused;
            PauseLabel.Text = paused ? "חדש תזמון" : "השהה תזמון";
            PauseIcon.Data = (Geometry)FindResource(paused ? "IconResume" : "IconPause");
            DeleteButton.IsEnabled = _statusKnown && p != TaskPresence.None;
        }

        /// <summary>Any action ends the display of a finished one-time outcome.</summary>
        private void BeginAction()
        {
            _outcomeText = null;
        }

        private void OnSchedule(object sender, RoutedEventArgs e)
        {
            BeginAction();
            RefreshStatus();
            var dialog = new ScheduleDialog(_status == null ? null : _status.Schedule, Presence != TaskPresence.None) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            Mouse.OverrideCursor = Cursors.Wait;
            ForeignScanResult foreign = ScanNow();
            Mouse.OverrideCursor = null;
            if (foreign != null && foreign.Tasks.Any(t => t.Enabled && t.Action == PowerAction.Restart)
                && !MessageDialog.Confirm(this, "קיים במחשב תזמון הפעלה מחדש נוסף ופעיל. המחשב עלול לעלות מחדש פעמיים.", "המשך בכל זאת", "הצג תזמונים"))
            {
                ShowForeignTasks();
                return;
            }
            if (!SaveGuards.ConfirmBitLocker(this)) return;

            SaveResult result;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                result = App.Manager.Save(dialog.Result);
            }
            catch (Exception ex)
            {
                App.Manager.LogFailure("שמירת התזמון", ex);
                result = new SaveResult(SaveOutcome.Failed, ex.Message);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            RefreshStatus();

            switch (result.Outcome)
            {
                case SaveOutcome.Saved:
                    MessageDialog.Show(this, "התזמון נשמר ואומת", MessageKind.Success);
                    break;
                case SaveOutcome.FailedRestored:
                    MessageDialog.Show(this, "השמירה נכשלה. התזמון הקודם שוחזר.\n\n" + result.Error, MessageKind.Error);
                    break;
                default:
                    MessageDialog.Show(this, "השמירה נכשלה.\n\n" + result.Error, MessageKind.Error);
                    break;
            }
        }

        private void OnPauseToggle(object sender, RoutedEventArgs e)
        {
            BeginAction();
            bool resume = Presence == TaskPresence.Paused;
            try
            {
                App.Manager.SetEnabled(resume);
            }
            catch (Exception ex)
            {
                App.Manager.LogFailure(resume ? "חידוש התזמון" : "השהיית התזמון", ex);
                MessageDialog.Show(this, (resume ? "חידוש התזמון נכשל." : "השהיית התזמון נכשלה.") + "\n\n" + ex.Message, MessageKind.Error);
            }
            RefreshStatus();
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            BeginAction();
            if (!MessageDialog.Confirm(this, "לבטל את התזמון הקיים?", "בטל תזמון", "חזור")) return;
            try
            {
                App.Manager.Delete();
                RefreshStatus();
                MessageDialog.Show(this, "התזמון בוטל", MessageKind.Success);
            }
            catch (Exception ex)
            {
                App.Manager.LogFailure("ביטול התזמון", ex);
                RefreshStatus();
                MessageDialog.Show(this, "ביטול התזמון נכשל.\n\n" + ex.Message, MessageKind.Error);
            }
        }

        private void OnRestartNow(object sender, RoutedEventArgs e)
        {
            BeginAction();
            ApplyStatus();
            if (!MessageDialog.Confirm(this, "להפעיל מחדש את המחשב עכשיו?", "הפעל מחדש", "ביטול")) return;
            try
            {
                ShutdownRunner.ScheduleRestart(RestartNowSeconds);
            }
            catch (Exception ex)
            {
                App.Manager.LogFailure("הפעלה מחדש מיידית", ex);
                MessageDialog.Show(this, "לא ניתן להפעיל מחדש את המחשב.\n\n" + ex.Message, MessageKind.Error);
                return;
            }
            App.Log.Write(string.Format("הפעלה מחדש מיידית: המחשב יופעל מחדש בעוד {0} שניות.", RestartNowSeconds),
                EventLogEntryType.Warning, AppConstants.EventRestartNow);
            new CountdownWindow(RestartNowSeconds) { Owner = this }.ShowDialog();
        }

        private void OnShowLog(object sender, RoutedEventArgs e)
        {
            BeginAction();
            ApplyStatus();
            new LogWindow(App.Log) { Owner = this }.ShowDialog();
        }

        private void OnShowForeign(object sender, RoutedEventArgs e)
        {
            BeginAction();
            ShowForeignTasks();
        }

        private void ShowForeignTasks()
        {
            new ForeignTasksWindow { Owner = this }.ShowDialog();
            RefreshStatus();
        }
    }

    /// <summary>Confirmations shared by every path that saves a schedule.</summary>
    internal static class SaveGuards
    {
        /// <summary>Asks for confirmation when BitLocker protects the system drive. Returns false when the user cancels.</summary>
        public static bool ConfirmBitLocker(Window owner)
        {
            Mouse.OverrideCursor = Cursors.Wait;
            bool protectedDrive;
            try
            {
                protectedDrive = BitLocker.IsSystemDriveProtected();
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            if (!protectedDrive) return true;
            bool confirmed = MessageDialog.Confirm(owner, "ביטלוקר פועל, האם מאשר להגדיר הפעלה מחדש מתוזמנת?", "מאשר", "ביטול");
            App.Log.Write(confirmed
                ? "ביטלוקר פועל בכונן המערכת: המשתמש אישר הגדרת הפעלה מחדש מתוזמנת."
                : "ביטלוקר פועל בכונן המערכת: המשתמש ביטל את הגדרת ההפעלה מחדש המתוזמנת.");
            return confirmed;
        }
    }

    /// <summary>Keeps windows inside the screen work area; their content scrolls when capped.</summary>
    internal static class WindowFit
    {
        private const double Margin = 24;

        public static void Apply(Window window)
        {
            Rect area = SystemParameters.WorkArea;
            window.MaxHeight = Math.Max(240, area.Height - Margin);
            window.MaxWidth = Math.Max(320, area.Width - Margin);
            if (!double.IsNaN(window.Height) && window.Height > window.MaxHeight) window.Height = window.MaxHeight;
            if (!double.IsNaN(window.Width) && window.Width > window.MaxWidth) window.Width = window.MaxWidth;
        }
    }
}
