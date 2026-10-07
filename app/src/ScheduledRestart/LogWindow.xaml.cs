using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ScheduledRestart.Services;

namespace ScheduledRestart
{
    /// <summary>Read-only log viewer, newest first. Lines written by the script are shown as they are.</summary>
    public partial class LogWindow : Window
    {
        private readonly RestartLog _log;
        private string[] _lines = new string[0];

        internal LogWindow(RestartLog log)
        {
            InitializeComponent();
            WindowFit.Apply(this);
            _log = log;
            PathText.Text = log.Path;
            SourceInitialized += (s, e) => NativeMethods.UseDarkTitleBar(this);
            Loaded += (s, e) => LoadLines();
        }

        private void LoadLines()
        {
            try
            {
                _lines = _log.ReadNewestFirst();
            }
            catch (Exception ex)
            {
                _lines = new string[0];
                FeedbackText.Text = "קריאת הלוג נכשלה: " + ex.Message;
            }

            LogList.Items.Clear();
            foreach (string line in _lines)
            {
                LogList.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                    // English lines from the PowerShell script read left-to-right, Hebrew lines right-to-left.
                    FlowDirection = line.Any(c => c >= '֐' && c <= '׿') ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
                });
            }
            EmptyState.Visibility = _lines.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            LogList.Visibility = _lines.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            FeedbackText.Text = string.Empty;
            LoadLines();
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            string target = File.Exists(_log.Path) ? _log.Path : Path.GetDirectoryName(_log.Path);
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                MessageDialog.Show(this, "תיקיית הלוג עדיין לא קיימת. היא תיווצר בפעולה הראשונה.", MessageKind.Info);
                return;
            }
            if (!NativeMethods.OpenFolderAndSelect(target))
                MessageDialog.Show(this, "לא ניתן לפתוח את תיקיית הלוג.", MessageKind.Error);
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            string text = string.Join(Environment.NewLine, _lines);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (text.Length == 0) Clipboard.Clear();
                    else Clipboard.SetText(text);
                    FeedbackText.Text = "הלוג הועתק ללוח";
                    return;
                }
                catch (COMException)
                {
                    Thread.Sleep(60);
                }
            }
            FeedbackText.Text = "ההעתקה ללוח נכשלה. נסו שוב.";
        }
    }
}
