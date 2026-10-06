using System.Windows;
using System.Windows.Controls;

namespace ScheduledRestart
{
    /// <summary>"IB-Fix · ישראל בורוכוב" at the bottom of a window; opens the About window.</summary>
    public partial class SignatureLine : UserControl
    {
        public SignatureLine()
        {
            InitializeComponent();
        }

        private void OnClick(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        }
    }
}
