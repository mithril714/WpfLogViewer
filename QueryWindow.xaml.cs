using System.Collections.Generic;
using System.Windows;

namespace WpfLogViewerApp
{
    public partial class QueryWindow : Window
    {
        public HashSet<string> QueryItems { get; private set; }

        public QueryWindow(HashSet<string> existing)
        {
            InitializeComponent();
            QueryItems = new HashSet<string>(existing);
            RefreshList();
        }

        private void AddQuery_Click(object sender, RoutedEventArgs e)
        {
            var text = QueryInputBox.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                QueryItems.Add(text);
                RefreshList();
                QueryInputBox.Clear();
            }
        }

        private void RemoveQuery_Click(object sender, RoutedEventArgs e)
        {
            if (QueryListBox.SelectedItem is string selected)
            {
                QueryItems.Remove(selected);
                RefreshList();
            }
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void RefreshList()
        {
            QueryListBox.Items.Clear();
            foreach (var q in QueryItems)
                QueryListBox.Items.Add(q);
        }
    }
}
