using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using ScheduledRestart.Core;
using ScheduledRestart.Services;

namespace ScheduledRestart
{
    /// <summary>About: version, maker and a link to the maker's website.</summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            WindowFit.Apply(this);
            VersionText.Text = "גרסה " + FileVersion;

            LicenseInfo license = License;
            LicenseText.Text = license.DisplayLine;
            LicenseIdText.Text = license.Id;
            LicenseIdText.Visibility = license.IsLicensed && license.Id.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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

        /// <summary>Licensee embedded at build time (LicenseeName / LicenseId assembly metadata).</summary>
        internal static LicenseInfo License
        {
            get
            {
                IEnumerable<KeyValuePair<string, string>> metadata = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
                    .Cast<AssemblyMetadataAttribute>()
                    .Select(a => new KeyValuePair<string, string>(a.Key, a.Value));
                return LicenseInfo.FromMetadata(metadata);
            }
        }

        private void OnRemoveAll(object sender, RoutedEventArgs e)
        {
            if (!MessageDialog.Confirm(this, "הפעולה תמחק את התזמון של התוכנה, את הלוגים ואת הגיבויים מהמחשב הזה. להמשיך?", "הסר הכל", "ביטול"))
                return;
            List<string> problems;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                problems = Uninstaller.RemoveAll(App.Manager.Client, AppConstants.LogDirectory, AppConstants.EventSource);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            if (problems.Count > 0)
            {
                MessageDialog.Show(this, "ההסרה לא הושלמה:\n\n" + string.Join("\n", problems), MessageKind.Error);
                return;
            }
            MessageDialog.Show(this, "הכל הוסר מהמחשב.", MessageKind.Success);
            Application.Current.Shutdown();
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
