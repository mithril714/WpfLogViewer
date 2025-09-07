using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WpfLogViewerApp
{
    public partial class InternalLogViewerWindow : Window
    {
        private string _currentPath;

        public InternalLogViewerWindow(string path, int lineNumber)
        {
            InitializeComponent();
            _currentPath = path;
            lblFile.Text = path;
            LoadFile(path, lineNumber);
        }

        private void LoadFile(string path, int targetLine)
        {
            if (!File.Exists(path))
            {
                MessageBox.Show("ファイルが見つかりません: " + path);
                return;
            }
            var all = File.ReadAllLines(path);
            linesPanel.Children.Clear();
            for (int i = 0; i < all.Length; i++)
            {
                var tb = new TextBlock
                {
                    Text = $"{i + 1,6}: {all[i]}",
                    FontFamily = new FontFamily("Consolas"),
                    Padding = new Thickness(6, 2, 6, 2),
                    Background = (i + 1 == targetLine) ? new SolidColorBrush(Color.FromRgb(255, 247, 173)) : Brushes.Transparent
                };
                linesPanel.Children.Add(tb);
            }
            // Scroll to target
            if (targetLine > 0 && targetLine <= linesPanel.Children.Count)
            {
                var fe = linesPanel.Children[targetLine - 1] as FrameworkElement;
                fe?.BringIntoView();
            }
        }

        public void JumpToLine(int targetLine)
        {
            if (targetLine <= 0 || targetLine > linesPanel.Children.Count) return;

            // clear previous highlight
            for (int i = 0; i < linesPanel.Children.Count; i++)
            {
                if (linesPanel.Children[i] is TextBlock tbi)
                    tbi.Background = Brushes.Transparent;
            }

            // highlight new line and scroll
            if (linesPanel.Children[targetLine - 1] is TextBlock tb)
            {
                tb.Background = new SolidColorBrush(Color.FromRgb(255, 247, 173));
                tb.BringIntoView();
            }
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(lblFile.Text ?? "");
        }
    }
}
