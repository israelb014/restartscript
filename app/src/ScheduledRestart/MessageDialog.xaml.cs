using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScheduledRestart
{
    public enum MessageKind
    {
        Info,
        Success,
        Error,
        Question
    }

    /// <summary>Dark, right-to-left message and confirmation dialog.</summary>
    public partial class MessageDialog : Window
    {
        private MessageDialog(string message, MessageKind kind, string primary, string secondary)
        {
            InitializeComponent();
            WindowFit.Apply(this);
            MessageText.Text = message;
            PrimaryButton.Content = primary;
            if (secondary == null)
            {
                SecondaryButton.Visibility = Visibility.Collapsed;
                PrimaryButton.IsCancel = true;
            }
            else
            {
                SecondaryButton.Content = secondary;
            }

            string icon;
            switch (kind)
            {
                case MessageKind.Success: icon = "IconCheck"; break;
                case MessageKind.Error: icon = "IconWarning"; break;
                case MessageKind.Question: icon = "IconInfo"; break;
                default: icon = "IconInfo"; break;
            }
            KindIcon.Data = (Geometry)FindResource(icon);
            if (kind == MessageKind.Error) KindIcon.Stroke = (Brush)FindResource("ErrorBrush");
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        }

        /// <summary>Asks a yes/no question. Returns true when the confirm button is chosen.</summary>
        public static bool Confirm(Window owner, string message, string confirmText, string cancelText)
        {
            var dialog = new MessageDialog(message, MessageKind.Question, confirmText, cancelText);
            SetOwner(dialog, owner);
            return dialog.ShowDialog() == true;
        }

        public static void Show(Window owner, string message, MessageKind kind)
        {
            var dialog = new MessageDialog(message, kind, "אישור", null);
            SetOwner(dialog, owner);
            dialog.ShowDialog();
        }

        private static void SetOwner(Window dialog, Window owner)
        {
            if (owner != null && owner.IsLoaded && owner != dialog)
            {
                dialog.Owner = owner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        private void OnPrimary(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void OnSecondary(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
