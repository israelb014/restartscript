using System;
using System.Diagnostics;
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

        public MainWindow()
        {
            InitializeComponent();
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

        private void RefreshStatus()
        {
            try
            {
                _status = App.Manager.GetStatus();
            }
            catch (Exception ex)
            {
                _status = null;
                ShowStatus("לא ניתן לקרוא את מצב התזמון", ex.Message, null, "IconWarning", (Brush)FindResource("ErrorBrush"));
                WarningText.Visibility = Visibility.Collapsed;
                LastRunText.Visibility = Visibility.Collapsed;
                UpdateButtons(false);
                return;
            }

            Brush accent = (Brush)FindResource("AccentBrush");
            Brush neutral = (Brush)FindResource("TextFaintBrush");
            RestartSchedule schedule = _status.Schedule;
            switch (_status.Presence)
            {
                case TaskPresence.None:
                    ShowStatus("אין תזמון פעיל", "לחצו על \"קבע תזמון\" כדי לקבוע הפעלה מחדש אוטומטית.", null, "IconEmpty", neutral);
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

            SetText(WarningText, schedule != null && schedule.WarningMinutes > 0 ? HebrewText.WarningSummary(schedule.WarningMinutes) : null);
            SetText(LastRunText, _status.LastRun.HasValue
                ? string.Format("הרצה אחרונה: {0} ({1})", HebrewText.DateTimeText(_status.LastRun.Value), HebrewText.TaskResult(_status.LastResult))
                : null);
            UpdateButtons(true);
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

        private void UpdateButtons(bool statusKnown)
        {
            TaskPresence p = Presence;
            ScheduleLabel.Text = p == TaskPresence.None ? "קבע תזמון" : "שנה תזמון";
            ScheduleButton.IsEnabled = statusKnown;
            PauseButton.IsEnabled = statusKnown && (p == TaskPresence.Active || p == TaskPresence.Paused);
            bool paused = p == TaskPresence.Paused;
            PauseLabel.Text = paused ? "חדש תזמון" : "השהה תזמון";
            PauseIcon.Data = (Geometry)FindResource(paused ? "IconResume" : "IconPause");
            DeleteButton.IsEnabled = statusKnown && p != TaskPresence.None;
        }

        private void OnSchedule(object sender, RoutedEventArgs e)
        {
            RefreshStatus();
            var dialog = new ScheduleDialog(_status == null ? null : _status.Schedule, Presence != TaskPresence.None) { Owner = this };
            if (dialog.ShowDialog() != true) return;

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
            new LogWindow(App.Log) { Owner = this }.ShowDialog();
        }
    }
}
