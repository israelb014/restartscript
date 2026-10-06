using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ScheduledRestart.Core;
using ScheduledRestart.Services;

namespace ScheduledRestart
{
    /// <summary>Countdown after "restart now", with a ring indicator and a cancel button (shutdown.exe /a).</summary>
    public partial class CountdownWindow : Window
    {
        private const double Size = 176;
        private const double Stroke = 10;
        private readonly int _totalSeconds;
        private readonly DateTime _end;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private bool _allowClose;

        public CountdownWindow(int seconds)
        {
            InitializeComponent();
            _totalSeconds = seconds;
            _end = DateTime.Now.AddSeconds(seconds);
            _timer.Tick += (s, e) => UpdateView();
            Loaded += (s, e) =>
            {
                UpdateView();
                _timer.Start();
            };
        }

        private void UpdateView()
        {
            double remaining = Math.Max(0, (_end - DateTime.Now).TotalSeconds);
            int whole = (int)Math.Ceiling(remaining);
            SecondsText.Text = whole.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RingArc.Data = BuildArc(remaining / _totalSeconds);
            if (remaining > 0)
            {
                CountdownText.Text = string.Format("המחשב יופעל מחדש בעוד {0} שניות", whole);
                return;
            }
            _timer.Stop();
            CountdownText.Text = "המחשב מופעל מחדש כעת...";
            CancelButton.IsEnabled = false;
        }

        private static Geometry BuildArc(double fraction)
        {
            double r = (Size - Stroke) / 2;
            var center = new Point(Size / 2, Size / 2);
            if (fraction >= 0.9999) return new EllipseGeometry(center, r, r);
            if (fraction <= 0) return Geometry.Empty;
            double angle = 2 * Math.PI * fraction;
            var start = new Point(center.X, center.Y - r);
            var end = new Point(center.X + r * Math.Sin(angle), center.Y - r * Math.Cos(angle));
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            try
            {
                ShutdownRunner.Abort();
            }
            catch (Exception ex)
            {
                App.Manager.LogFailure("ביטול ההפעלה מחדש", ex);
                MessageDialog.Show(this, "ביטול ההפעלה מחדש נכשל.\n\n" + ex.Message, MessageKind.Error);
                return;
            }
            App.Log.Write("ההפעלה מחדש המיידית בוטלה.", EventLogEntryType.Information, AppConstants.EventRestartCancelled);
            _timer.Stop();
            _allowClose = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Only the cancel button closes this window while the countdown is running.
            if (!_allowClose) e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
