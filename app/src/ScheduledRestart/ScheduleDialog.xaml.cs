using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduledRestart.Core;

namespace ScheduledRestart
{
    /// <summary>Create / edit a schedule. Returns the chosen schedule in <see cref="Result"/>.</summary>
    public partial class ScheduleDialog : Window
    {
        private static readonly TimeSpan DefaultTime = new TimeSpan(3, 0, 0);
        private readonly bool _taskExists;
        private readonly CheckBox[] _dayBoxes = new CheckBox[7];
        private bool _loading = true;

        public ScheduleDialog(RestartSchedule current, bool taskExists)
        {
            InitializeComponent();
            WindowFit.Apply(this);
            _taskExists = taskExists;
            SourceInitialized += (s, e) => NativeMethods.UseDarkTitleBar(this);
            HeaderText.Text = Title = taskExists ? "שינוי תזמון" : "קביעת תזמון";

            var chipStyle = (Style)FindResource("DayChip");
            for (int d = 0; d < 7; d++)
            {
                var box = new CheckBox { Content = HebrewText.DayName((DayOfWeek)d), Style = chipStyle };
                box.Checked += OnInputChanged;
                box.Unchecked += OnInputChanged;
                _dayBoxes[d] = box;
                DaysPanel.Children.Add(box);
            }
            for (int day = 1; day <= 31; day++) DayOfMonthInput.Items.Add(day.ToString(CultureInfo.InvariantCulture));
            for (int d = 0; d < 7; d++) WeekdayInput.Items.Add(HebrewText.DayName((DayOfWeek)d));
            foreach (int minutes in RestartSchedule.WarningOptions) WarningInput.Items.Add(HebrewText.WarningOption(minutes));

            // Defaults for a new schedule.
            TypeDaily.IsChecked = true;
            DateInput.SelectedDate = DateTime.Today.AddDays(1);
            TimeInput.Text = HebrewText.Time(DefaultTime);
            DayOfMonthInput.SelectedIndex = 0;
            WeekdayInput.SelectedIndex = (int)DayOfWeek.Friday;
            MonthFixed.IsChecked = true;
            WarningInput.SelectedIndex = 0;

            if (current != null) Prefill(current);
            _loading = false;
            UpdateView();
        }

        public RestartSchedule Result { get; private set; }

        private void Prefill(RestartSchedule s)
        {
            TimeInput.Text = HebrewText.Time(s.Time);
            WarningInput.SelectedIndex = Array.IndexOf(RestartSchedule.WarningOptions, s.WarningMinutes);
            switch (s.Kind)
            {
                case ScheduleKind.Once:
                    TypeOnce.IsChecked = true;
                    DateInput.SelectedDate = s.Date;
                    break;
                case ScheduleKind.Daily:
                    TypeDaily.IsChecked = true;
                    break;
                case ScheduleKind.Weekly:
                    TypeWeekly.IsChecked = true;
                    foreach (DayOfWeek d in s.Days) _dayBoxes[(int)d].IsChecked = true;
                    break;
                case ScheduleKind.MonthlyDay:
                    TypeMonthly.IsChecked = true;
                    MonthFixed.IsChecked = true;
                    DayOfMonthInput.SelectedIndex = s.DayOfMonth - 1;
                    break;
                case ScheduleKind.MonthlyLastDay:
                    TypeMonthly.IsChecked = true;
                    MonthLast.IsChecked = true;
                    break;
                case ScheduleKind.MonthlyLastWeekday:
                    TypeMonthly.IsChecked = true;
                    MonthLastWeekday.IsChecked = true;
                    WeekdayInput.SelectedIndex = (int)s.Weekday;
                    break;
            }
        }

        private void OnInputChanged(object sender, RoutedEventArgs e)
        {
            if (!_loading) UpdateView();
        }

        private void OnDayOfMonthChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            MonthFixed.IsChecked = true;
            UpdateView();
        }

        private void OnWeekdayChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            MonthLastWeekday.IsChecked = true;
            UpdateView();
        }

        private void UpdateView()
        {
            OncePanel.Visibility = TypeOnce.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            WeeklyPanel.Visibility = TypeWeekly.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            MonthlyPanel.Visibility = TypeMonthly.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ShortMonthNote.Visibility = MonthFixed.IsChecked == true && DayOfMonthInput.SelectedIndex >= 28
                ? Visibility.Visible : Visibility.Collapsed;

            DateTime typed;
            bool nightTime = DateTime.TryParseExact((TimeInput.Text ?? string.Empty).Trim(), new[] { "HH:mm", "H:mm" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out typed)
                && typed.Hour >= 1 && typed.Hour < 3;
            ClockNote.Visibility = nightTime ? Visibility.Visible : Visibility.Collapsed;

            RestartSchedule schedule;
            string error;
            if (TryBuild(out schedule, out error))
            {
                ErrorText.Visibility = Visibility.Collapsed;
                PreviewText.Text = HebrewText.NextRunLine(schedule.GetNextRun(DateTime.Now));
            }
            else
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                PreviewText.Text = "ההפעלה מחדש הבאה: —";
            }
        }

        private bool TryBuild(out RestartSchedule schedule, out string error)
        {
            schedule = null;
            error = null;

            DateTime parsedTime;
            if (!DateTime.TryParseExact((TimeInput.Text ?? string.Empty).Trim(), new[] { "HH:mm", "H:mm" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedTime))
            {
                error = "יש להזין שעה תקינה בפורמט HH:mm (לדוגמה 03:00)";
                return false;
            }
            TimeSpan time = parsedTime.TimeOfDay;
            int warning = RestartSchedule.WarningOptions[Math.Max(0, WarningInput.SelectedIndex)];

            if (TypeOnce.IsChecked == true)
            {
                if (!DateInput.SelectedDate.HasValue)
                {
                    error = "יש לבחור תאריך";
                    return false;
                }
                schedule = RestartSchedule.Once(DateInput.SelectedDate.Value, time, warning);
                if (DateInput.SelectedDate.Value.Date + time <= DateTime.Now)
                {
                    error = "המועד שנבחר כבר עבר";
                    return false;
                }
            }
            else if (TypeWeekly.IsChecked == true)
            {
                DayOfWeek[] days = Enumerable.Range(0, 7).Where(d => _dayBoxes[d].IsChecked == true).Select(d => (DayOfWeek)d).ToArray();
                if (days.Length == 0)
                {
                    error = "יש לבחור לפחות יום אחד";
                    return false;
                }
                schedule = RestartSchedule.Weekly(days, time, warning);
            }
            else if (TypeMonthly.IsChecked == true)
            {
                if (MonthLast.IsChecked == true)
                    schedule = RestartSchedule.MonthlyLastDay(time, warning);
                else if (MonthLastWeekday.IsChecked == true)
                    schedule = RestartSchedule.MonthlyLastWeekday((DayOfWeek)Math.Max(0, WeekdayInput.SelectedIndex), time, warning);
                else
                    schedule = RestartSchedule.MonthlyDay(Math.Max(0, DayOfMonthInput.SelectedIndex) + 1, time, warning);
            }
            else
            {
                schedule = RestartSchedule.Daily(time, warning);
            }
            return true;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            RestartSchedule schedule;
            string error;
            if (!TryBuild(out schedule, out error))
            {
                UpdateView();
                return;
            }
            if (_taskExists && !MessageDialog.Confirm(this, "קיים כבר תזמון. להחליף אותו?", "החלף", "ביטול")) return;
            Result = schedule;
            DialogResult = true;
        }
    }
}
