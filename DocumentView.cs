using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ConfigEditor;
public partial class MainWindow
{
    private async Task Parse(bool preserveViewport = false, ConfigDocument? preparedDocument = null)
    {
        syntaxError = null;
        parseTimer.Stop();
        var s = Session;
        int rev = ++revision;
        var scrolls = new List<(ScrollViewer View, double X, double Y)>();
        void CaptureScrolls(DependencyObject root)
        {
            if (root is ScrollViewer view)
                scrolls.Add((view, view.HorizontalOffset, view.VerticalOffset));
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                CaptureScrolls(VisualTreeHelper.GetChild(root, i));
        }
        void ClearViews()
        {
            document = null;
            Tree.ItemsSource = null;
            Stage.ItemsSource = null;
            GroupContent.Children.Clear();
            Csv.ItemsSource = null;
        }
        if (preserveViewport)
        {
            CaptureScrolls(EditorArea);
            document = null;
        }
        else
            ClearViews();
        if (s == null)
        {
            ClearViews();
            UpdateStatus();
            return;
        }
        string text = s.Text;
        Message.Text = "구조를 읽는 중…";
        try
        {
            bool reuse = preparedDocument != null && preparedDocument.Text == text && preparedDocument.Format == s.Format && preparedDocument.Separator == s.Separator;
            var savedText = s.SavedText;
            var format = s.Format;
            var separator = s.Separator;
            var result = reuse ? (Parsed: preparedDocument!, Saved: new Dictionary<string, string>()) : await Task.Run(() =>
            {
                var parsed = new ConfigDocument(text, format, separator);
                var saved = new Dictionary<string, string>();
                try
                {
                    foreach (var n in new ConfigDocument(savedText, format, separator).All)
                        saved.TryAdd(n.Path, n.Value);
                }
                catch { }
                return (Parsed: parsed, Saved: saved);
            });
            // OLE keeps pumping Dispatcher callbacks: do not rebuild a large view mid-drag.
            while (activeTabDrags > 0 && rev == revision && s == Session)
                await Task.Delay(30);
            if (rev != revision || s != Session)
                return;
            var parsed = result.Parsed;
            document = parsed;
            if (!reuse)
            {
                foreach (var n in parsed.All)
                {
                    n.Open = !s.Collapsed.Contains(n.Path);
                    n.State = n.ReadOnly ? "원문 편집" : result.Saved.TryGetValue(n.Path, out var old) && old == n.Value ? "" : s.Dirty ? "변경" : "";
                }
                foreach (var n in parsed.All)
                {
                    for (var p = n; p != null; p = p.Parent)
                        if (s.PendingDeletes.Contains(p.Path))
                        {
                            n.PendingDelete = true;
                            n.State = "삭제 예정";
                            break;
                        }
                }
            }
            Message.Text = s.Format == "INI" ? "INI: 전체 줄 주석 지원 · 인라인 ;/#는 값으로 처리합니다." : s.Format == "YAML" ? "YAML: 기본 1.2 표기 · 앵커·별칭·블록 문자열은 원문 편집을 사용하세요." : "값 한 번 클릭/Enter · F2 Key 변경 · 오른쪽 클릭 메뉴 · Ctrl+S 저장";
            Populate();
            if (preserveViewport)
            {
                EditorArea.UpdateLayout();
                foreach (var (view, x, y) in scrolls)
                {
                    view.ScrollToHorizontalOffset(x);
                    view.ScrollToVerticalOffset(y);
                }
                EditorArea.UpdateLayout();
            }
        }
        catch (Exception ex)
        {
            if (rev != revision || s != Session)
                return;
            ClearViews();
            syntaxError = ex;
            Message.Text = "문법 오류: " + ex.Message;
            sourceMode = true;
            SetView();
        }
        UpdateStatus();
    }
    private void Populate()
    {
        if (document == null)
            return;
        if (document.Format == "CSV")
        {
            PopulateCsv();
            SetView();
            return;
        }
        string query = Search.Text;
        var list = new List<ConfigNode>();
        bool Match(ConfigNode n) => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || n.Value.Contains(query, StringComparison.OrdinalIgnoreCase) || n.Children.Any(Match);
        void Visit(IEnumerable<ConfigNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (query.Length > 0 && !Match(n))
                    continue;
                list.Add(n);
                if (n.Open || query.Length > 0)
                {
                    Visit(n.Children);
                    if (n.Group && !n.ReadOnly && !n.PendingDelete && Session?.StructureEditing == true && !Session.RawEditing && query.Length == 0)
                        list.Add(new ConfigNode { Parent = n, Name = "＋ 항목 추가", Kind = "Add", ReadOnly = true });
                }
            }
        }
        Visit(document.Roots);
        foreach (var n in list)
            n.HasVisibleChildren = false;
        foreach (var siblings in list.GroupBy(n => n.Parent))
        {
            foreach (var n in siblings)
                n.LastVisibleSibling = false;
            siblings.Last().LastVisibleSibling = true;
            if (siblings.Key != null)
                siblings.Key.HasVisibleChildren = true;
        }
        Tree.ItemsSource = list;
        RenderViews(list);
        SetView();
    }
    private void PopulateCsv()
    {
        if (document == null || Session == null)
            return;
        Csv.Columns.Clear();
        csvRows.Clear();
        var table = new DataTable();
        int columns = document.Roots.Select(n => n.Children.Count).DefaultIfEmpty(0).Max();
        for (int i = 0; i < columns; i++)
        {
            string name = "C" + i;
            table.Columns.Add(name);
            string header = Session.CsvHeader && document.Roots.Count > 0 && i < document.Roots[0].Children.Count ? document.Roots[0].Children[i].Value : $"열 {i + 1}";
            Csv.Columns.Add(new DataGridTextColumn { Header = string.IsNullOrEmpty(header) ? "제목 없음" : header, HeaderStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), (Style)FindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader))) { Setters = { new Setter(FontWeightProperty, FontWeights.SemiBold) } }, Binding = new System.Windows.Data.Binding(name) { Converter = new CsvDisplayValue() }, ElementStyle = (Style)FindResource("GridText"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        }
        foreach (var row in document.Roots.Skip(Session.CsvHeader ? 1 : 0))
        {
            if (Search.Text.Length > 0 && !row.Children.Any(n => n.Value.Contains(Search.Text, StringComparison.OrdinalIgnoreCase)))
                continue;
            csvRows.Add(row);
            var r = table.NewRow();
            for (int i = 0; i < row.Children.Count; i++)
                r[i] = row.Children[i].Value;
            table.Rows.Add(r);
        }
        Csv.ItemsSource = table.DefaultView;
    }
    private void SetView()
    {
        workspacePending = true;
        SyncStructure();
        UpdateInspectorVisibility();
        Tree.Visibility = !sourceMode && Session?.Format != "CSV" && editorView == EditorView.Tree ? Visibility.Visible : Visibility.Collapsed;
        Stage.Visibility = !sourceMode && Session?.Format != "CSV" && editorView == EditorView.Stages ? Visibility.Visible : Visibility.Collapsed;
        Groups.Visibility = !sourceMode && Session?.Format != "CSV" && editorView == EditorView.Groups ? Visibility.Visible : Visibility.Collapsed;
        Csv.Visibility = !sourceMode && Session?.Format == "CSV" ? Visibility.Visible : Visibility.Collapsed;
        Source.Visibility = sourceMode ? Visibility.Visible : Visibility.Collapsed;
        CsvHeader.Visibility = CsvSeparator.Visibility = !sourceMode && Session?.Format == "CSV" ? Visibility.Visible : Visibility.Collapsed;
        GridButton.Content = Session?.Format == "CSV" ? "▦ 표 보기" : editorView switch
        {
            EditorView.Tree => "▦ 트리 보기",
            EditorView.Stages => "▦ 단계 보기",
            _ => "▦ 그룹 보기"
        };
        GridButton.SetResourceReference(Control.BackgroundProperty, sourceMode ? "Property" : "Surface");
        SourceButton.SetResourceReference(Control.BackgroundProperty, sourceMode ? "Surface" : "Property");
        Search.IsEnabled = !sourceMode;
        GridButton.FontWeight = !sourceMode ? FontWeights.Bold : FontWeights.Normal;
        SourceButton.FontWeight = sourceMode ? FontWeights.Bold : FontWeights.Normal;
    }
    private void UpdateStatus()
    {
        UndoButton.IsEnabled = Session?.CanUndo == true;
        RedoButton.IsEnabled = Session?.CanRedo == true;
        EmptyState.Visibility = Session == null ? Visibility.Visible : Visibility.Collapsed;
        EditorArea.Visibility = Session == null ? Visibility.Hidden : Visibility.Visible;
        Status.Text = Session == null ? "" : $"{Session.Format}   ·   {Session.EncodingName}{(Session.Bom.Length > 0 ? " BOM" : "")}   ·   {(Session.Text.Contains("\r\n") ? "CRLF" : "LF")}   ·   {document?.All.Count() ?? 0}개 항목";
        var changes = document?.All.Count(n => n.State == "변경" && !n.Group) ?? 0;
        StatusChanges.Text = !Session?.Dirty ?? true ? "" : changes > 0 ? $"변경 {changes}건" : "변경됨";
    }
    private async void SourceChanged(object sender, TextChangedEventArgs e)
    {
        if (switching || Session == null)
            return;
        if (Source.IsReadOnly)
            return;
        Session.Change(Source.Text);
        UpdateStatus();
        document = null;
        revision++;
        parseTimer.Stop();
        parseTimer.Start();
        await Task.CompletedTask;
    }
    private void SourceView(object sender, RoutedEventArgs e)
    {
        sourceMode = true;
        SetView();
        Source.Focus();
    }
    private async void GridView(object sender, RoutedEventArgs e)
    {
        if (Session == null)
            return;
        var session = Session;
        if (document == null)
            await Parse();
        if (session != Session)
            return;
        if (document == null)
        {
            if (syntaxError == null)
                return;
            var dialog = new SyntaxErrorDialog(this, session.FilePath ?? session.Title, session.Text, syntaxError);
            if (dialog.ShowDialog() == true)
            {
                sourceMode = true;
                SetView();
                Source.Focus();
                Source.Select(dialog.ErrorOffset, dialog.ErrorOffset < Source.Text.Length ? 1 : 0);
                Source.ScrollToLine(Source.GetLineIndexFromCharacterIndex(dialog.ErrorOffset));
            }
            return;
        }
        if (Session.StructureEditing && Session.RawEditing)
        {
            try
            {
                Session.CompleteStructure();
            }
            catch (Exception ex) { Error(ex); return; }
        }
        sourceMode = false;
        SetView();
    }
    private void OpenSearch()
    {
        Search.Focus();
        Search.SelectAll();
    }
    private void CloseSearch(object sender, RoutedEventArgs e)
    {
        Search.Clear();
        if (Session != null)
            Session.SearchText = "";
        workspacePending = true;
        if (sourceMode)
            Source.Focus();
        else if (Session?.Format == "CSV")
            Csv.Focus();
        else if (editorView == EditorView.Tree)
            Tree.Focus();
        else if (editorView == EditorView.Stages)
            Stage.Focus();
        else
            Groups.Focus();
    }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (switching)
            return;
        SearchClear.Visibility = SearchHint.Visibility == Visibility.Visible && Search.Text.Length > 0 ? Visibility.Visible : Search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = SearchPlaceholder.Visibility = Search.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        Populate();
    }
    private async void CsvOptionChanged(object sender, RoutedEventArgs e)
    {
        if (switching || Session == null)
            return;
        Session.CsvHeader = CsvHeader.IsChecked == true;
        Session.Separator = CsvSeparator.SelectedIndex == 1 ? '\t' : CsvSeparator.SelectedIndex == 2 ? ';' : ',';
        await Parse();
    }
    private void ToggleNode(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConfigNode n || Session == null || !n.Group)
            return;
        ToggleBranch(n, (Keyboard.Modifiers & ModifierKeys.Alt) != 0);
    }
    private void ToggleBranch(ConfigNode node, bool recursive)
    {
        if (Session == null || !node.Group)
            return;
        node = document?.All.FirstOrDefault(item => item.Path == node.Path) ?? node;
        bool open = !node.Open;
        foreach (var item in (recursive ? ConfigDocument.Walk([node]) : new[] { node }).Where(n => n.Group))
        {
            item.Open = open;
            if (open)
                Session.Collapsed.Remove(item.Path);
            else
                Session.Collapsed.Add(item.Path);
        }
        Populate();
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tree.SelectedItem is ConfigNode n)
            SelectNode(n, false);
    }
    private void SetZoom(int percent)
    {
        zoomPercent = Math.Clamp(percent, 50, 200);
        EditorScale.ScaleX = EditorScale.ScaleY = zoomPercent / 100d;
        ZoomReset.Content = $"{zoomPercent}%";
    }
    private void EditorMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ReferenceEditor != null)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.Delta == 0)
            return;
        SetZoom(zoomPercent + Math.Sign(e.Delta) * 10);
        e.Handled = true;
    }
    private void ResetZoom(object sender, RoutedEventArgs e) => SetZoom(100);
}
