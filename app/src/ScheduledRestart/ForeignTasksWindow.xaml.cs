using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScheduledRestart.Core;
using ScheduledRestart.Services;

namespace ScheduledRestart
{
    /// <summary>Lists restart/shutdown tasks created outside the app, with disable/enable, delete and adopt.</summary>
    public partial class ForeignTasksWindow : Window
    {
        public ForeignTasksWindow()
        {
            InitializeComponent();
            WindowFit.Apply(this);
            SourceInitialized += (s, e) => NativeMethods.UseDarkTitleBar(this);
            Loaded += (s, e) => Reload();
        }

        private void Reload()
        {
            TaskList.Children.Clear();
            ForeignScanResult scan;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                scan = App.Foreign.Scan();
            }
            catch (Exception ex)
            {
                App.Manager.LogFailure("סריקת התזמונים במחשב", ex);
                FooterText.Text = "הסריקה נכשלה: " + ex.Message;
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            foreach (ForeignTask task in scan.Tasks) TaskList.Children.Add(BuildCard(task));
            EmptyText.Visibility = scan.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FooterText.Text = scan.Unreadable > 0 ? string.Format("{0} משימות לא נקראו בגלל הרשאות", scan.Unreadable) : string.Empty;
        }

        private UIElement BuildCard(ForeignTask task)
        {
            var body = new StackPanel();

            var header = new DockPanel { LastChildFill = true };
            var badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            badges.Children.Add(Pill(task.KindText, task.Action == PowerAction.Restart ? "AccentSoftBrush" : "WarningSoftBrush",
                task.Action == PowerAction.Restart ? "AccentBrush" : "WarningBrush"));
            badges.Children.Add(Pill(task.Enabled ? "פעיל" : "מושבת", "InputBrush", task.Enabled ? "TextBrush" : "TextFaintBrush"));
            DockPanel.SetDock(badges, Dock.Left);
            header.Children.Add(badges);
            header.Children.Add(new TextBlock
            {
                Text = task.Name,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 10, 0)
            });
            body.Children.Add(header);
            body.Children.Add(new TextBlock
            {
                Text = task.Path,
                FontSize = 12,
                Foreground = Res("TextFaintBrush"),
                FlowDirection = FlowDirection.LeftToRight,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });

            body.Children.Add(Line("תזמון: " + task.ScheduleText));
            body.Children.Add(Line("הרצה הבאה: " + (task.NextRun.HasValue ? HebrewText.DateTimeText(task.NextRun.Value) : "אין")));
            if (task.LastRun.HasValue) body.Children.Add(Line("הרצה אחרונה: " + HebrewText.DateTimeText(task.LastRun.Value)));
            body.Children.Add(Line("חשבון: " + task.Account));

            body.Children.Add(new Border
            {
                Background = Res("InputBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 8, 0, 0),
                Child = new TextBlock
                {
                    Text = task.CommandLine,
                    FontFamily = new FontFamily("Consolas, Courier New"),
                    FontSize = 12,
                    Foreground = Res("TextDimBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    FlowDirection = FlowDirection.LeftToRight
                }
            });
            if (task.DetectedInScript)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "זוהה בתוך סקריפט",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res("WarningBrush"),
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }

            var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(ActionButton(task.Enabled ? "השבת" : "הפעל", "SecondaryButton", () => Toggle(task)));
            buttons.Children.Add(ActionButton("מחק", "SecondaryButton", () => Delete(task)));
            if (task.CanAdopt) buttons.Children.Add(ActionButton("העבר לניהול התוכנה", "PrimaryButton", () => Adopt(task)));
            body.Children.Add(buttons);

            return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 10), Child = body };
        }

        private Brush Res(string key)
        {
            return (Brush)FindResource(key);
        }

        private TextBlock Line(string text)
        {
            return new TextBlock { Text = text, FontSize = 13, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        }

        private Border Pill(string text, string background, string foreground)
        {
            return new Border
            {
                Background = Res(background),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 2, 10, 3),
                Margin = new Thickness(6, 0, 0, 0),
                Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res(foreground) }
            };
        }

        private Button ActionButton(string text, string style, Action onClick)
        {
            var button = new Button
            {
                Content = text,
                Style = (Style)FindResource(style),
                Height = 34,
                MinWidth = 84,
                FontSize = 13,
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(14, 0, 14, 0)
            };
            button.Click += (s, e) => onClick();
            return button;
        }

        private void Toggle(ForeignTask task)
        {
            bool enable = !task.Enabled;
            try
            {
                App.Foreign.SetEnabled(task, enable);
            }
            catch (Exception ex)
            {
                App.Manager.LogFailure(enable ? "הפעלת תזמון נוסף" : "השבתת תזמון נוסף", ex);
                MessageDialog.Show(this, (enable ? "הפעלת התזמון נכשלה." : "השבתת התזמון נכשלה.") + "\n\n" + ex.Message, MessageKind.Error);
            }
            Reload();
        }

        private void Delete(ForeignTask task)
        {
            if (!MessageDialog.Confirm(this, string.Format("למחוק את התזמון '{0}'?", task.Name), "מחק", "ביטול")) return;
            try
            {
                string backup = App.Foreign.Delete(task);
                Reload();
                MessageDialog.Show(this, "התזמון נמחק. גיבוי נשמר ב־\n" + backup, MessageKind.Success);
            }
            catch (Exception ex)
            {
                App.Manager.LogFailure("מחיקת תזמון נוסף", ex);
                Reload();
                MessageDialog.Show(this, "מחיקת התזמון נכשלה.\n\n" + ex.Message, MessageKind.Error);
            }
        }

        private void Adopt(ForeignTask task)
        {
            if (!MessageDialog.Confirm(this, string.Format("להעביר את התזמון '{0}' לניהול התוכנה? התזמון המקורי יושבת ולא יימחק.", task.Name), "העבר", "ביטול"))
                return;
            bool appHasTask;
            try { appHasTask = App.Manager.GetStatus().Presence != TaskPresence.None; }
            catch (Exception) { appHasTask = false; }
            if (appHasTask && !MessageDialog.Confirm(this, "קיים כבר תזמון של התוכנה. להחליף אותו?", "החלף", "ביטול")) return;
            if (!SaveGuards.ConfirmBitLocker(this)) return;

            AdoptResult result;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                result = App.Foreign.Adopt(task, App.Manager);
            }
            catch (Exception ex)
            {
                App.Manager.LogFailure("העברת תזמון לניהול התוכנה", ex);
                result = new AdoptResult(false, ex.Message);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            Reload();
            if (result.Success)
                MessageDialog.Show(this, "התזמון הועבר לניהול התוכנה. התזמון המקורי הושבת.", MessageKind.Success);
            else
                MessageDialog.Show(this, "ההעברה נכשלה. המצב הקודם שוחזר.\n\n" + result.Error, MessageKind.Error);
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
