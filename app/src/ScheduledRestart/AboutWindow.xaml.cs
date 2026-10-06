using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using ScheduledRestart.Core;

namespace ScheduledRestart
{
    /// <summary>About: version, maker and a link to the maker's website.</summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            VersionText.Text = "גרסה " + FileVersion;
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        }

        private static string FileVersion
        {
            get
            {
                try { return FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion; }
                catch (Exception) { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
            }
        }

        private void OnOpenWebsite(object sender, RoutedEventArgs e)
        {
            // The one exception to "only shutdown.exe": the app runs elevated, so the browser is not
            // started directly. explorer.exe hands the constant URL to the user's (non-elevated) shell.
            try
            {
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                using (Process.Start(new ProcessStartInfo(explorer, AppConstants.WebsiteUrl) { UseShellExecute = false }))
                {
                }
                App.Log.Write("נפתח האתר ib-fix.com");
            }
            catch (Exception ex)
            {
                App.Log.Write("פתיחת האתר ib-fix.com נכשלה: " + ex.Message);
                try { Clipboard.SetText(AppConstants.WebsiteUrl); }
                catch (Exception) { }
                MessageDialog.Show(this, "לא ניתן לפתוח את הדפדפן. הכתובת הועתקה ללוח.", MessageKind.Error);
            }
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
