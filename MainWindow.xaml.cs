using Microsoft.Win32;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;
using System.Diagnostics;

namespace WpfLogViewerApp
{
    public partial class MainWindow : Window
    {
        private List<LogEntry> logEntries = new();
        private HashSet<string> queryProcesses = new();

        // 動的列の優先順
        private static readonly string[] KnownCols =
            { "データ番号", "ポート", "名前", "場所", "処理時間", "開始時間", "終了時間" };

        // 外部エディタ関連・内部ビューア参照・ターゲットログ
        private string externalEditorPath = string.Empty;
        private string externalEditorArgs = string.Empty;
        private string lastTargetLogFile = string.Empty;
        private InternalLogViewerWindow _internalViewer;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void MenuOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "ログファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                LoadLogFile(dlg.FileName);
                RedrawTable();
            }
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void MenuHelp_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("WPF Log Viewer (.NET 5)", "バージョン情報");
        }

        private void MenuEditorSettings_Click(object sender, RoutedEventArgs e)
        {
            var w = new EditorSettingsWindow(externalEditorPath, externalEditorArgs) { Owner = this };
            if (w.ShowDialog() == true)
            {
                externalEditorPath = w.EditorPath ?? string.Empty;
                externalEditorArgs = w.ArgsTemplate ?? string.Empty;
                MessageBox.Show("設定を保存しました。\nエディタ: " + externalEditorPath + "\n引数: " + externalEditorArgs);
            }
        }

        private void MenuPickTargetLog_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "ログファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
            if (!string.IsNullOrEmpty(lastTargetLogFile) && File.Exists(lastTargetLogFile))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(lastTargetLogFile);
                dlg.FileName = Path.GetFileName(lastTargetLogFile);
            }
            if (dlg.ShowDialog() == true)
            {
                lastTargetLogFile = dlg.FileName;
                MessageBox.Show("ターゲットログ: " + lastTargetLogFile);
            }
        }

        private void ShowQueryDialog_Click(object sender, RoutedEventArgs e)
        {
            var q = new QueryWindow(queryProcesses);
            if (q.ShowDialog() == true)
            {
                queryProcesses = q.QueryItems;
            }
        }

        private void ExecuteQuery_Click(object sender, RoutedEventArgs e)
        {
            RedrawTable();
        }

        private void OpenInternalViewer_Click(object sender, RoutedEventArgs e)
        {
            var sel = GetSelectedEntry();
            if (sel == null) { MessageBox.Show("行を選択してください。"); return; }

            // ターゲットログがなければ選択
            if (string.IsNullOrEmpty(lastTargetLogFile))
            {
                var dlg = new OpenFileDialog { Filter = "ログファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
                if (dlg.ShowDialog() == true) lastTargetLogFile = dlg.FileName;
                if (string.IsNullOrEmpty(lastTargetLogFile)) return;
            }

            int line = FindLineNumberByTime(lastTargetLogFile, sel.Time);
            if (line < 0) { MessageBox.Show($"該当時刻の行が見つかりません: {sel.Time}"); return; }

            _internalViewer = new InternalLogViewerWindow(lastTargetLogFile, line) { Owner = this };
            _internalViewer.Show();
        }

        private void OpenExternalEditor_Click(object sender, RoutedEventArgs e)
        {
            var sel = GetSelectedEntry();
            if (sel == null) { MessageBox.Show("行を選択してください。"); return; }

            if (string.IsNullOrWhiteSpace(externalEditorPath) || !File.Exists(externalEditorPath))
            {
                if (MessageBox.Show("外部エディタが未設定です。設定しますか？", "確認", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    MenuEditorSettings_Click(sender, e);
                if (string.IsNullOrWhiteSpace(externalEditorPath) || !File.Exists(externalEditorPath)) return;
            }

            if (string.IsNullOrEmpty(lastTargetLogFile))
            {
                var dlg = new OpenFileDialog { Filter = "ログファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
                if (dlg.ShowDialog() == true) lastTargetLogFile = dlg.FileName;
                if (string.IsNullOrEmpty(lastTargetLogFile)) return;
            }

            int line = FindLineNumberByTime(lastTargetLogFile, sel.Time);
            if (line < 0) { MessageBox.Show($"該当時刻の行が見つかりません: {sel.Time}"); return; }

            string args = (externalEditorArgs ?? string.Empty)
                .Replace("{file}", $"\"{lastTargetLogFile}\"")
                .Replace("{line}", line.ToString());

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = externalEditorPath,
                    Arguments = args,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("外部エディタ起動に失敗しました:\n" + ex.Message);
            }
        }

        // ★ DataGrid 選択変更 → 自動同期
        private void logGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (toggleAutoSync != null && toggleAutoSync.IsChecked == false) return;
            SyncJumpToSelection(autoPromptTarget: true);
        }

        private void SyncJumpToSelection(bool autoPromptTarget)
        {
            var sel = GetSelectedEntry();
            if (sel == null) return;

            if (string.IsNullOrEmpty(lastTargetLogFile) && autoPromptTarget)
            {
                var dlg = new OpenFileDialog { Filter = "ログファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
                if (dlg.ShowDialog() == true) lastTargetLogFile = dlg.FileName;
            }
            if (string.IsNullOrEmpty(lastTargetLogFile) || !File.Exists(lastTargetLogFile)) return;

            int line = FindLineNumberByTime(lastTargetLogFile, sel.Time);
            if (line <= 0) return;

            // 内部ビューアが開いていればジャンプ
            _internalViewer?.JumpToLine(line);

            // 外部エディタ設定があればジャンプ（エディタによっては再起動扱い）
            if (!string.IsNullOrWhiteSpace(externalEditorPath))
            {
                string args = (externalEditorArgs ?? string.Empty)
                    .Replace("{file}", $"\"{lastTargetLogFile}\"")
                    .Replace("{line}", line.ToString());
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = externalEditorPath,
                        Arguments = args,
                        UseShellExecute = false
                    };
                    Process.Start(psi);
                }
                catch { /* noisy回避 */ }
            }
        }

        // ヘルパー：選択行の時刻/処理を取得
        private LogEntry GetSelectedEntry()
        {
            if (logGrid?.SelectedItem is DataRowView drv)
            {
                return new LogEntry
                {
                    Time = drv.Row.Table.Columns.Contains("時刻") ? drv.Row["時刻"]?.ToString() : "",
                    Process = drv.Row.Table.Columns.Contains("処理") ? drv.Row["処理"]?.ToString() : "",
                    Detail = ""
                };
            }
            return null;
        }

        // ヘルパー：時刻文字列で先頭一致する行番号を探す
        private int FindLineNumberByTime(string filePath, string timeText)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(timeText)) return -1;
            int i = 0;
            foreach (var line in File.ReadLines(filePath))
            {
                i++;
                // 先頭に "HH:mm:ss" で一致
                if (line.StartsWith(timeText)) return i;
            }
            return -1;
        }

        private void LoadLogFile(string path)
        {
            logEntries.Clear();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(' ', 3);
                if (parts.Length < 2) continue;
                var time = parts[0];
                var process = parts[1];
                var details = parts.Length >= 3 ? parts[2] : string.Empty;
                logEntries.Add(new LogEntry { Time = time, Process = process, Detail = details });
            }
        }

        private Dictionary<string, string> ParseDetail(string detail)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(detail)) return map;

            var parts = detail.Split(',');
            if (parts.Length > 0) map["データ番号"] = parts[0].Trim();
            if (parts.Length > 1) map["ポート"] = parts[1].Trim();
            if (parts.Length > 2) map["名前"] = parts[2].Trim();
            if (parts.Length > 3) map["場所"] = parts[3].Trim();
            if (parts.Length > 4) map["処理時間"] = parts[4].Trim();
            if (parts.Length > 5) map["開始時間"] = parts[5].Trim();
            if (parts.Length > 6) map["終了時間"] = parts[6].Trim();
            return map;
        }

        private DataTable BuildTableFromLogs(IEnumerable<LogEntry> entries)
        {
            var dt = new DataTable();
            dt.Columns.Add("時刻");
            dt.Columns.Add("処理");

            var dynamicCols = new HashSet<string>();
            foreach (var e in entries)
                foreach (var k in ParseDetail(e.Detail).Keys)
                    dynamicCols.Add(k);

            foreach (var col in KnownCols)
                if (dynamicCols.Contains(col)) dt.Columns.Add(col);
            foreach (var col in dynamicCols.Except(KnownCols))
                dt.Columns.Add(col);

            foreach (var e in entries)
            {
                var map = ParseDetail(e.Detail);
                var row = dt.NewRow();
                row["時刻"] = e.Time;
                row["処理"] = e.Process;
                foreach (DataColumn c in dt.Columns)
                {
                    var name = c.ColumnName;
                    if (name == "時刻" || name == "処理") continue;
                    row[name] = map.TryGetValue(name, out var v) ? v : "";
                }
                dt.Rows.Add(row);
            }

            return dt;
        }

        private void RedrawTable()
        {
            var filtered = logEntries
                .Where(e => queryProcesses.Count == 0 || queryProcesses.Contains(e.Process))
                .ToList();

            var table = BuildTableFromLogs(filtered);
            var view = table.DefaultView;
            logGrid.ItemsSource = view;

            // 文字列の HH:mm:ss なら文字列ソートでも時刻順に近い並び
            ICollectionView cv = CollectionViewSource.GetDefaultView(logGrid.ItemsSource);
            using (cv.DeferRefresh())
            {
                cv.SortDescriptions.Clear();
                cv.SortDescriptions.Add(new SortDescription("時刻", ListSortDirection.Ascending));
            }
        }
    }

    public class LogEntry
    {
        public string Time { get; set; }
        public string Process { get; set; }
        public string Detail { get; set; }
    }
}
