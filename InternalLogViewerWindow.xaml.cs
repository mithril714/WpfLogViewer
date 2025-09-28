using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;  // IScrollInfo
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;  // DispatcherPriority

namespace WpfLogViewerApp
{
    public partial class InternalLogViewerWindow : Window
    {
        private string _currentPath;
        private readonly ObservableCollection<string> _lines = new(); // 仮想化と相性が良い

        // 検索結果は「行インデックスの配列」だけ持つ（軽量）
        private readonly List<int> _matchIndices = new();
        private int _currentMatchPos = -1;

        // 連打対策のデバウンス
        private readonly DispatcherTimer _debounceTimer;

        // キャンセル可能な再検索
        private CancellationTokenSource? _cts;

        public InternalLogViewerWindow(string path, int lineNumber)
        {
            InitializeComponent();
            _currentPath = path;
//            lblFile.Text = path;
            LoadFile(path, lineNumber);
            linesList.ItemsSource = _lines;

            // 入力デバウンス
            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounceTimer.Tick += (s, e) => { _debounceTimer.Stop(); _ = RebuildMatchesAsync(); };

            // 検索テキスト変更を監視（TextBox の TextChanged で拾う方法でもOK）
            txtSearch.TextChanged += (_, __) => _debounceTimer.Restart();
            chkRegex.Checked += (_, __) => _debounceTimer.Restart();
            chkRegex.Unchecked += (_, __) => _debounceTimer.Restart();
            chkCase.Checked += (_, __) => _debounceTimer.Restart();
            chkCase.Unchecked += (_, __) => _debounceTimer.Restart();
            chkWord.Checked += (_, __) => _debounceTimer.Restart();
            chkWord.Unchecked += (_, __) => _debounceTimer.Restart();

            // ショートカット（ウィンドウで拾いたい場合は Window.PreviewKeyDown でもOK）
            this.PreviewKeyDown += Window_PreviewKeyDown;
        }
        public void SetLines(IEnumerable<string> lines)
        {
            _lines.Clear();
            foreach (var l in lines) _lines.Add(l);
            _ = RebuildMatchesAsync();
        }

        private async Task RebuildMatchesAsync()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _matchIndices.Clear();
            _currentMatchPos = -1;
            UpdateCounter();

            var query = txtSearch.Text ?? string.Empty;
            if (string.IsNullOrEmpty(query) || _lines.Count == 0) return;

            // マッチ関数を作る
            var matcher = BuildMatcher(query,
                                       useRegex: chkRegex.IsChecked == true,
                                       caseSensitive: chkCase.IsChecked == true,
                                       wholeWord: chkWord.IsChecked == true);

            const int chunk = 2000;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (ct.IsCancellationRequested) return;

                if (matcher(_lines[i])) _matchIndices.Add(i);

                if (i % chunk == 0) // UIをブロックしない
                    await Task.Yield();
            }

            if (_matchIndices.Count > 0)
            {
                _currentMatchPos = 0;
                SelectAndScroll(_matchIndices[_currentMatchPos]);
            }
            UpdateCounter();
        }

        private static Func<string, bool> BuildMatcher(string query, bool useRegex, bool caseSensitive, bool wholeWord)
        {
            if (useRegex)
            {
                // 正規表現は単語境界をオプションで付与
                var pattern = wholeWord ? $@"\b(?:{query})\b" : query;
                var options = caseSensitive ? RegexOptions.Compiled : RegexOptions.IgnoreCase | RegexOptions.Compiled;

                // catastrophic backtracking 対策（1秒タイムアウト）
                return s =>
                {
                    try
                    {
                        return Regex.IsMatch(s ?? string.Empty, pattern, options, TimeSpan.FromSeconds(1));
                    }
                    catch (RegexMatchTimeoutException) { return false; }
                    catch (ArgumentException) { return false; } // ユーザが壊れたパターンを入れた場合
                };
            }
            else
            {
                var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (!wholeWord)
                {
                    return s => (s ?? string.Empty).IndexOf(query, cmp) >= 0;
                }
                else
                {
                    // 簡易単語境界：英数字の前後を判定
                    return s =>
                    {
                        if (string.IsNullOrEmpty(s)) return false;
                        int idx = -1;
                        while ((idx = s.IndexOf(query, idx + 1, cmp)) >= 0)
                        {
                            bool leftOk = idx == 0 || !char.IsLetterOrDigit(s[idx - 1]);
                            int r = idx + query.Length;
                            bool rightOk = r >= s.Length || !char.IsLetterOrDigit(s[r]);
                            if (leftOk && rightOk) return true;
                        }
                        return false;
                    };
                }
            }
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e) => FindNext();
        private void BtnPrev_Click(object sender, RoutedEventArgs e) => FindPrev();

        private void FindNext()
        {
            if (_matchIndices.Count == 0) return;
            _currentMatchPos = (_currentMatchPos + 1) % _matchIndices.Count;
            SelectAndScroll(_matchIndices[_currentMatchPos]);
            UpdateCounter();
        }

        private void FindPrev()
        {
            if (_matchIndices.Count == 0) return;
            _currentMatchPos = (_currentMatchPos - 1 + _matchIndices.Count) % _matchIndices.Count;
            SelectAndScroll(_matchIndices[_currentMatchPos]);
            UpdateCounter();
        }

        private void SelectAndScroll(int index)
        {
            if (index < 0 || index >= _lines.Count) return;
            linesList.SelectedIndex = index;
            // VirtualizingStackPanelでも確実に可視化
            linesList.Dispatcher.BeginInvoke(() => linesList.ScrollIntoView(linesList.SelectedItem),
                                             DispatcherPriority.Background);
        }

        private void UpdateCounter()
        {
            lblCounter.Text = _matchIndices.Count == 0
                ? "0件"
                : $"{_currentMatchPos + 1}/{_matchIndices.Count}";
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+F で検索ボックスにフォーカス
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                txtSearch.Focus();
                txtSearch.SelectAll();
                e.Handled = true;
                return;
            }

            // F3 = 次、Shift+F3 = 前
            if (e.Key == Key.F3 && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                FindPrev(); e.Handled = true; return;
            }
            if (e.Key == Key.F3)
            {
                FindNext(); e.Handled = true; return;
            }
        }

        private void txtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                FindNext();
                e.Handled = true;
            }
        }

        private void linesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 選択変更時に自動スクロール（保険）
            if (linesList.SelectedItem != null)
                linesList.Dispatcher.BeginInvoke(() => linesList.ScrollIntoView(linesList.SelectedItem),
                                                 DispatcherPriority.Background);
        }
        public void AppendLine(string line)
        {
            _lines.Add(line);
            // 追記が頻繁なら、追記位置だけの増分チェックでもOK
            _debounceTimer.Restart();
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
//            Clipboard.SetText(lblFile.Text ?? "");
        }
    }
    static class TimerEx
    {
        public static void Restart(this DispatcherTimer timer)
        {
            timer.Stop();
            timer.Start();
        }
    }
}