using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;  // DispatcherPriority
using System.Windows.Controls.Primitives;  // IScrollInfo

namespace WpfLogViewerApp
{
    public partial class InternalLogViewerWindow : Window
    {
        private string _currentPath;
        private readonly ObservableCollection<string> _lines = new(); // 仮想化と相性が良い

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

            _lines.Clear();
            int i = 0;
            // File.ReadLines はストリーミングでメモリ効率が良い
            foreach (var line in File.ReadLines(path))
            {
                i++;
                _lines.Add($"{i,6}: {line}");
            }

            linesList.ItemsSource = _lines;
            JumpToLine(targetLine);
        }

        public void JumpToLine(int targetLine)
        {
            if (targetLine <= 0 || targetLine > _lines.Count) return;

            // 対象アイテムを選択し、スクロールして表示
            var obj = _lines[targetLine - 1];

            // 選択変更（これだけでスタイルトリガが効いて背景が変わる）
            linesList.SelectedItem = obj;

            // スクロール（非同期で一度だけ）
            linesList.Dispatcher.InvokeAsync(() =>
            {
                linesList.ScrollIntoView(obj);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(lblFile.Text ?? "");
        }
    }
}