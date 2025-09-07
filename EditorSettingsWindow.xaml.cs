using Microsoft.Win32;
using System.Windows;

namespace WpfLogViewerApp
{
    public partial class EditorSettingsWindow : Window
    {
        public string EditorPath { get; private set; }
        public string ArgsTemplate { get; private set; }

        public EditorSettingsWindow(string currentPath, string currentArgs)
        {
            InitializeComponent();
            txtEditorPath.Text = currentPath ?? string.Empty;
            txtArgs.Text = currentArgs ?? string.Empty;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                txtEditorPath.Text = dlg.FileName;
            }
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            EditorPath = txtEditorPath.Text.Trim();
            ArgsTemplate = txtArgs.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
