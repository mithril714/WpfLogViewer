using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace WpfLogViewerApp
{
    public partial class MainWindow : Window
    {
        private List<LogEntry> logEntries = new();
        private HashSet<string> queryProcesses = new();

        // 動的列の優先順
        private static readonly string[] KnownCols =
            { "詳細"};

        // 外部エディタ関連・内部ビューア参照・ターゲットログ
        private string externalEditorPath = string.Empty;
        private string externalEditorArgs = string.Empty;
        private string lastTargetLogFile = string.Empty;
        private InternalLogViewerWindow _internalViewer;

        // 非同期ロード
        private CancellationTokenSource _loadCts;

        // --- ターゲットログ用インデックス ---
        private TargetLogIndex _targetIndex;
        private CancellationTokenSource _indexCts;
        private int _lastMatchedIndex = -1; // 0-based

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void MenuOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "ログファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
            if (dlg.ShowDialog() == true)
            {
                _loadCts?.Cancel();
                _loadCts = new CancellationTokenSource();
                try
                {
                    var entries = await Task.Run(() => LoadLogFileCore(dlg.FileName, _loadCts.Token));
                    logEntries = entries;
                    RedrawTable();
                }
                catch (OperationCanceledException) { }
            }
        }

        private async void MenuPickTargetLog_Click(object sender, RoutedEventArgs e)
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
//                MessageBox.Show("ターゲットログ: " + lastTargetLogFile);

                _indexCts?.Cancel();
                _indexCts = new CancellationTokenSource();
                try { await BuildTargetLogIndexAsync(lastTargetLogFile, _indexCts.Token); }
                catch (OperationCanceledException) { }
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

/*        private void MenuPickTargetLog_Click(object sender, RoutedEventArgs e)
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
*/
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
            if (string.IsNullOrEmpty(lastTargetLogFile)) { MessageBox.Show("ツール > ターゲットログを選択 で設定してください。"); return; }

            int line = FindNearestLineUsingIndex(sel.Time, out var _);
            if (line < 0) { MessageBox.Show($"該当時刻に近い行が見つかりません: {sel.Time}"); return; }

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

        private void SyncJumpToSelection()
        {
            var sel = GetSelectedEntry();
            if (sel == null) return;
            if (string.IsNullOrEmpty(lastTargetLogFile) || _targetIndex == null) return;

            int line = FindNearestLineUsingIndex(sel.Time, out var _);
            if (line <= 0) return;
            _internalViewer?.JumpToLine(line);
        }

        private void DataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row)
            {
                row.IsSelected = true;
                logGrid.SelectedItem = row.Item;
                logGrid.Focus();
                if (toggleAutoSync?.IsChecked == true) SyncJumpToSelection();
            }
        }

/*        private void DataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row)
            {
                // その行を選択状態に
                row.IsSelected = true;
                logGrid.SelectedItem = row.Item;
                logGrid.Focus();

                // ★ SelectionChanged に頼らず、右クリックのタイミングで即同期する
//                if (toggleAutoSync?.IsChecked == true)
                {
                    SyncJumpToSelection();  // ← ダイアログを出さず、設定されていなければ何もしない実装にしておく
                }

                // コンテキストメニューはそのまま出す
                e.Handled = false;
            }
        }
*/
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

        private int FindNearestLineNumberByTimeFlexible(string filePath, string selectedTimeText, out string matchedTimeText)
        {
            matchedTimeText = null;
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(selectedTimeText)) return -1;

            DateTime refDt;
            var nowYear = DateTime.Now.Year;

            string[] selFormats = {
        "yy/M/d H:m:s", "yyyy/M/d H:m:s",
        "M/d H:m:s",
        "H:m:s"
    };
            if (!DateTime.TryParseExact(selectedTimeText, selFormats, CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out refDt))
            {
                if (TimeSpan.TryParse(selectedTimeText, out var ts))
                    refDt = new DateTime(nowYear, DateTime.Now.Month, DateTime.Now.Day).Date + ts;
                else
                    return -1;
            }

            var rxFull = new Regex(@"^(?<full>\d{1,4}/\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}:\d{2})");
            var rxMD = new Regex(@"^(?<md>\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}:\d{2})");
            var rxT = new Regex(@"^(?<t>\d{1,2}:\d{2}:\d{2})");

            int bestLine = -1;
            TimeSpan bestAbs = TimeSpan.MaxValue;

            int lineNo = 0;
            foreach (var line in File.ReadLines(filePath))
            {
                lineNo++;
                DateTime? dtLine = null;
                string thisMatchedText = null;

                // a) 年あり
                var mFull = rxFull.Match(line);
                if (mFull.Success)
                {
                    if (DateTime.TryParseExact(mFull.Groups["full"].Value,
                                               new[] { "yy/M/d H:m:s", "yyyy/M/d H:m:s" },
                                               CultureInfo.InvariantCulture, DateTimeStyles.None,
                                               out var dt))
                    {
                        dtLine = dt;
                        thisMatchedText = mFull.Groups["full"].Value;
                    }
                }
                else
                {
                    // b) 年なし
                    var mMD = rxMD.Match(line);
                    if (mMD.Success)
                    {
                        if (DateTime.TryParseExact(mMD.Groups["md"].Value,
                                                   "M/d H:m:s",
                                                   CultureInfo.InvariantCulture, DateTimeStyles.None,
                                                   out var md))
                        {
                            var cands = new[] {
                        new DateTime(refDt.Year,  md.Month, md.Day, md.Hour, md.Minute, md.Second),
                        new DateTime(refDt.Year-1,md.Month, md.Day, md.Hour, md.Minute, md.Second),
                        new DateTime(refDt.Year+1,md.Month, md.Day, md.Hour, md.Minute, md.Second)
                    };
                            dtLine = cands.OrderBy(c => (c - refDt).Duration()).First();
                            thisMatchedText = mMD.Groups["md"].Value;
                        }
                    }
                    else
                    {
                        // c) 時刻のみ
                        var mT = rxT.Match(line);
                        if (mT.Success && TimeSpan.TryParseExact(mT.Groups["t"].Value, @"h\:m\:s",
                                                                 CultureInfo.InvariantCulture, out var ts))
                        {
                            var sameDay = refDt.Date + ts;
                            var prevDay = refDt.Date.AddDays(-1) + ts;
                            var nextDay = refDt.Date.AddDays(+1) + ts;
                            var cands = new[] { sameDay, prevDay, nextDay };
                            dtLine = cands.OrderBy(c => (c - refDt).Duration()).First();
                            thisMatchedText = mT.Groups["t"].Value;
                        }
                    }
                }

                if (dtLine == null) continue;

                var diffAbs = (dtLine.Value - refDt).Duration();
                if (diffAbs < bestAbs)
                {
                    bestAbs = diffAbs;
                    bestLine = lineNo;
                    matchedTimeText = thisMatchedText;

                    if (bestAbs == TimeSpan.Zero) break; // 完全一致なら即決
                }
            }

            return bestLine;
        }

        private void LoadLogFile(string path)
        {
            logEntries.Clear();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(' ','\t');
                if (parts.Length < 3) continue;
                var time = parts[0] + ' ' + parts[1];
                var process = parts[2];
                var details = parts.Length >= 4 ? parts[3] : string.Empty;
                logEntries.Add(new LogEntry { Time = time, Process = process, Detail = details });
            }
        }

        private Dictionary<string, string> ParseDetail(string detail,string process)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(detail)) return map;

            if (process == "A")
            {
                var parts = detail.Split(',');
                var datas = parts[0].Split('_');
                var carrierId = datas[0];
                var slotNo = datas[1];
                string colName = "Port:" + parts[1] + " Slot:" + slotNo;
                map[colName] = parts[3];
            }
            else
            {
                map["詳細"] = detail;
            
            }
                return map;
        }

        private DataTable BuildTableFromLogs(IEnumerable<LogEntry> entries)
        {
            var dt = new DataTable();
            dt.Columns.Add("時刻");
            dt.Columns.Add("処理");
            /*            dt.Columns.Add("詳細");
            */
            var dynamicCols = new HashSet<string>();
            foreach (var e in entries)
                foreach (var k in ParseDetail(e.Detail, e.Process).Keys)
                    dynamicCols.Add(k);
            foreach (var col in KnownCols)
                if (dynamicCols.Contains(col)) dt.Columns.Add(col);
            foreach (var col in dynamicCols.Except(KnownCols))
                dt.Columns.Add(col);
            /*            foreach (var col in dynamicCols)
                            dt.Columns.Add(col);
            */
            foreach (var e in entries)
            {
                var map = ParseDetail(e.Detail, e.Process);
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
        private List<LogEntry> LoadLogFileCore(string path, CancellationToken ct)
        {
            var rxFullY = new Regex(@"^(?<dt>(?:\d{4}|\d{2})/\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}:\d{2})\s+(?<proc>\S+)\s*(?<detail>.*)$");
            var rxMD = new Regex(@"^(?<dt>\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}:\d{2})\s+(?<proc>\S+)\s*(?<detail>.*)$");
            var rxT = new Regex(@"^(?<dt>\d{1,2}:\d{2}:\d{2})\s+(?<proc>\S+)\s*(?<detail>.*)$");

            var list = new List<LogEntry>(capacity: 10000);
            foreach (var line in File.ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;

                string dt = null, proc = null, detail = null;
                var m = rxFullY.Match(line);
                if (m.Success)
                {
                    dt = m.Groups["dt"].Value; proc = m.Groups["proc"].Value; detail = m.Groups["detail"].Value;
                }
                else if ((m = rxMD.Match(line)).Success)
                {
                    dt = m.Groups["dt"].Value; proc = m.Groups["proc"].Value; detail = m.Groups["detail"].Value;
                }
                else if ((m = rxT.Match(line)).Success)
                {
                    dt = m.Groups["dt"].Value; proc = m.Groups["proc"].Value; detail = m.Groups["detail"].Value;
                }
                else continue;

                list.Add(new LogEntry { Time = dt, Process = proc, Detail = detail ?? string.Empty });
            }
            return list;
        }

        private sealed class TargetLogIndex
        {
            public string FilePath { get; }
            public List<DateTime> Times { get; } = new List<DateTime>(capacity: 10000);
            public TargetLogIndex(string path) => FilePath = path;
        }

        private async Task BuildTargetLogIndexAsync(string filePath, CancellationToken ct)
        {
            if (_targetIndex != null && string.Equals(_targetIndex.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                return;

            var idx = new TargetLogIndex(filePath);
            var rxFullY = new Regex(@"^(?<dt>(?:\d{4}|\d{2})/\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}:\d{2})");
            var rxMD = new Regex(@"^(?<dt>\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}:\d{2})");
            var rxT = new Regex(@"^(?<dt>\d{1,2}:\d{2}:\d{2})");

            int refYear = DateTime.Now.Year;
            DateTime? prev = null;

            await Task.Run(() =>
            {
                foreach (var line in File.ReadLines(filePath))
                {
                    ct.ThrowIfCancellationRequested();
                    DateTime? dtLine = null;

                    var mFull = rxFullY.Match(line);
                    if (mFull.Success)
                    {
                        if (DateTime.TryParseExact(mFull.Groups["dt"].Value,
                            new[] { "yyyy/M/d H:m:s", "yy/M/d H:m:s" },
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        {
                            dtLine = d;
                        }
                    }
                    else
                    {
                        var mMD = rxMD.Match(line);
                        if (mMD.Success)
                        {
                            if (DateTime.TryParseExact(mMD.Groups["dt"].Value, "M/d H:m:s",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out var md))
                            {
                                var cands = new[]
                                {
                                    new DateTime(refYear,   md.Month, md.Day, md.Hour, md.Minute, md.Second),
                                    new DateTime(refYear-1, md.Month, md.Day, md.Hour, md.Minute, md.Second),
                                    new DateTime(refYear+1, md.Month, md.Day, md.Hour, md.Minute, md.Second)
                                };
                                var basis = prev ?? cands[0];
                                var best = cands.OrderBy(c => (c - basis).Duration()).First();
                                dtLine = best;
                            }
                        }
                        else
                        {
                            var mT = rxT.Match(line);
                            if (mT.Success && TimeSpan.TryParseExact(mT.Groups["dt"].Value, @"h\:m\:s",
                                CultureInfo.InvariantCulture, out var ts))
                            {
                                var basis = (prev ?? DateTime.Today).Date;
                                var cands = new[] { basis + ts, basis.AddDays(-1) + ts, basis.AddDays(+1) + ts };
                                var best = cands.OrderBy(c => (c - (prev ?? c)).Duration()).First();
                                dtLine = best;
                            }
                        }
                    }

                    if (dtLine != null)
                    {
                        if (prev != null && dtLine <= prev) dtLine = prev.Value.AddMilliseconds(1);
                        idx.Times.Add(dtLine.Value);
                        prev = dtLine;
                    }
                }
            }, ct);

            _targetIndex = idx;
            _lastMatchedIndex = -1;
        }

        private static bool TryParseFlexibleDateTime(string text, out DateTime dt)
        {
            dt = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] formats = { "yyyy/M/d H:m:s", "yy/M/d H:m:s", "M/d H:m:s", "H:m:s" };
            if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                if (text.IndexOf('/') < 0 && text.Count(c => c == ':') == 2)
                {
                    var today = DateTime.Today;
                    dt = new DateTime(today.Year, today.Month, today.Day, dt.Hour, dt.Minute, dt.Second);
                }
                return true;
            }
            return false;
        }

        private int FindNearestLineUsingIndex(string selectedTimeText, out string matchedTime)
        {
            matchedTime = null;
            if (_targetIndex == null || _targetIndex.Times.Count == 0) return -1;
            if (!TryParseFlexibleDateTime(selectedTimeText, out var refDt)) return -1;

            var times = _targetIndex.Times;

            // 二分探索
            int lo = 0, hi = times.Count - 1, mid = 0;
            while (lo <= hi)
            {
                mid = (lo + hi) >> 1;
                int cmp = times[mid].CompareTo(refDt);
                if (cmp == 0) { _lastMatchedIndex = mid; matchedTime = times[mid].ToString("yyyy/MM/dd HH:mm:ss"); return mid + 1; }
                if (cmp < 0) lo = mid + 1; else hi = mid - 1;
            }
            int cand = Math.Clamp(lo, 0, times.Count - 1);

            // 近傍探索（前回ヒット近辺を優先）
            int bestIdx = cand;
            TimeSpan bestAbs = (times[cand] - refDt).Duration();
            if (_lastMatchedIndex >= 0)
            {
                const int Window = 600; // 必要に応じて調整（行数）
                int s = Math.Max(0, _lastMatchedIndex - Window);
                int e = Math.Min(times.Count - 1, _lastMatchedIndex + Window);
                for (int i = s; i <= e; i++)
                {
                    var abs = (times[i] - refDt).Duration();
                    if (abs < bestAbs) { bestAbs = abs; bestIdx = i; }
                }
            }

            _lastMatchedIndex = bestIdx;
            matchedTime = times[bestIdx].ToString("yyyy/MM/dd HH:mm:ss");
            return bestIdx + 1;
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

        private void logGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (toggleAutoSync != null && toggleAutoSync.IsChecked == false) return;
            SyncJumpToSelection();
        }

    }

    public class LogEntry
    {
        public string Time { get; set; }
        public string Process { get; set; }
        public string Detail { get; set; }
    }
}
