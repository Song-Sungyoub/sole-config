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
    private async Task Apply(string text)
    {
        if (Session == null)
            return;
        string? path = (Tree.SelectedItem as ConfigNode)?.Path;
        Session.Change(text);
        switching = true;
        Source.Text = text;
        switching = false;
        await Parse(true);
        if (path != null && document != null)
        {
            Tree.SelectedItem = document.All.FirstOrDefault(n => n.Path == path);
            if (Tree.SelectedItem is ConfigNode selected)
                SelectNode(selected, false);
        }
    }
    private void EditDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (inlineEnabled || Session?.StructureEditing == true)
            return;
        if (Tree.SelectedItem is ConfigNode { Kind: "Add" } add)
        {
            OpenAddPalette(add.Parent!, Tree);
            return;
        }
        if (Find<Button>(e.OriginalSource as DependencyObject) != null || Find<CheckBox>(e.OriginalSource as DependencyObject) != null || Find<ComboBox>(e.OriginalSource as DependencyObject) != null)
            return;
        if (Tree.SelectedItem is ConfigNode n)
            Edit(n);
    }
    private void EditCsv(object sender, MouseButtonEventArgs e)
    {
        Csv.BeginEdit();
    }
    private ConfigNode? CsvNode()
    {
        int row = Csv.Items.IndexOf(Csv.CurrentCell.Item), column = Csv.CurrentCell.Column?.DisplayIndex ?? -1;
        return row >= 0 && row < csvRows.Count && column >= 0 && column < csvRows[row].Children.Count ? csvRows[row].Children[column] : null;
    }
    private async void Edit(ConfigNode n, bool typeChange = false, bool forcePopup = false)
    {
        if ((inlineEnabled || Session?.StructureEditing == true) && !typeChange && !forcePopup && Session?.Format != "CSV")
        {
            BeginInline(n);
            return;
        }
        if (ReferenceEditor != null || Session?.RawEditing == true)
            return;
        if (document == null || !n.Editable)
        {
            GoSource(n);
            return;
        }
        var originalDocument = document;
        var originalSession = Session;
        var originalText = Session?.Text;
        var d = new EditDialog(this, typeChange ? "자료형 변경" : n.Kind == "Comment" ? "주석 수정" : "값 수정", n.Path, n.Value, n.DisplayKind, typeChange && Session?.Format is "JSON" or "YAML" ? new[] { "String", "Number", "Boolean", "Null" } : null);
        if (Session?.Input is { } pending && pending.Path == n.Path && pending.Baseline == Session.Text)
            d.ValueBox.Text = pending.Text;
        var inputSession = Session!;
        void TrackInput(object sender, TextChangedEventArgs e)
        {
            inputSession.Input = new(n.Path, d.ValueBox.Text, originalText!, d.Kind);
            inputSession.Notify();
        }
        d.ValueBox.TextChanged += TrackInput;
        bool accepted = await ShowReferenceEdit(d);
        d.ValueBox.TextChanged -= TrackInput;
        inputSession.Input = null;
        inputSession.Notify();
        SyncStructure();
        if (!accepted)
            return;
        if (Session != originalSession || Session?.Text != originalText)
        {
            Message.Text = "편집 중 문서가 변경되어 적용하지 않았습니다.";
            return;
        }
        try
        {
            await Apply(originalDocument.Change(n, d.Value, typeChange ? d.Kind : null));
        }
        catch (Exception ex) { inputSession.Input = new(n.Path, d.Value, originalText!, d.Kind); inputSession.Notify(); SyncStructure(); Error(ex); }
    }
    private async void ToggleBool(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConfigNode n || document == null)
            return;
        n = document.All.FirstOrDefault(item => item.Path == n.Path) ?? n;
        try
        {
            SelectNode(n, false);
            await Apply(document.Change(n, n.Value == "true" ? "false" : "true"));
        }
        catch (Exception ex) { Error(ex); }
    }
    private static T? Find<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T found)
                return found;
            child = child is Visual ? VisualTreeHelper.GetParent(child) : null;
        }
        return null;
    }
    private void SelectContextRow(object sender, MouseButtonEventArgs e)
    {
        var row = Find<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row != null)
            Tree.SelectedItem = row.Item;
    }
    private void SelectCsvCell(object sender, MouseButtonEventArgs e)
    {
        var cell = Find<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell != null)
            Csv.CurrentCell = new DataGridCellInfo(cell);
    }
    private void TreeMenu(object sender, ContextMenuEventArgs e)
    {
        if (Tree.SelectedItem is ConfigNode n)
            Tree.ContextMenu = MakeMenu(n);
        else
            e.Handled = true;
    }
    private void CsvMenu(object sender, ContextMenuEventArgs e)
    {
        if (CsvNode() is ConfigNode n)
            Csv.ContextMenu = MakeMenu(n);
        else
            e.Handled = true;
    }
    private ContextMenu MakeMenu(ConfigNode n)
    {
        n = document?.All.FirstOrDefault(item => item.Path == n.Path) ?? n;
        var menu = new ContextMenu();
        void Item(string title, Action action, bool enabled = true)
        {
            var i = new MenuItem { Header = title, IsEnabled = enabled && (Session?.StructureEditing == true || !(title.Contains("추가") || title.Contains("삭제") || title.Contains("이름 변경"))) };
            i.Click += (_, _) => action();
            menu.Items.Add(i);
        }
        if (Session?.Format == "CSV")
        {
            Item("선택 범위 복사", () => CopyCsv(false));
            Item("헤더 포함 복사", () => CopyCsv(true));
            Item("붙여넣기", async () => await PasteCsv(false));
            Item("내용 지우기", async () => await PasteCsv(true));
            menu.Items.Add(new Separator());
        }
        if (!n.Group)
        {
            Item(n.Kind == "Comment" ? "주석 수정" : "값 수정", () => Edit(n), n.Editable);
            if (inlineEnabled)
                Item("팝업에서 수정…", () => Edit(n, false, true), n.Editable);
            Item("값 복사", () => Copy(n.Value));
        }
        Item("경로 복사", () => Copy(n.Path));
        Item("원문 위치로 이동", () => GoSource(n));
        if (n.Editable && n.Kind != "Null")
        {
            menu.Items.Add(new Separator());
            if (ScriptEnabled)
                Item("스크립트로 변환…", () => OpenScript(n));
            Item("암호화…", () => Transform(n, "암호화"));
            Item("복호화…", () => Transform(n, "복호화"));
            Item("해시 계산…", () => Transform(n, "SHA-256"));
        }
        menu.Items.Add(new Separator());
        if (Session?.StructureEditing != true)
        {
            Item("구조 편집 시작", () => StructureClick(this, new()));
            return menu;
        }
        Item("이름 변경…", () => Rename(n), n.KeyStart >= 0 && !n.ReadOnly);
        Item(Session?.Format == "CSV" ? "행 추가…" : "하위 항목 추가…", () => Insert(n.Group || n.Kind == "Element" ? n : n.Parent), n.Group || n.Kind == "Element" || n.Parent != null);
        Item(n.PendingDelete ? "삭제 복구" : Session?.Format == "CSV" ? "행 삭제…" : "삭제…", () => Delete(n), !n.ReadOnly);
        if (Session?.Format == "INI")
            Item("새 섹션 추가…", AddSection);
        if (Session?.Format == "CSV")
        {
            Item("헤더 이름 변경…", () => RenameCsvHeader(n), Session.CsvHeader);
            Item("마지막에 열 추가…", () => CsvColumn(n, false));
            Item("이 열 삭제…", () => CsvColumn(n, true));
        }
        return menu;
    }

    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex) { Error(ex); }
    }
    private void GoSource(ConfigNode n)
    {
        sourceMode = true;
        SetView();
        Source.Focus();
        Source.Select(Math.Min(n.Start, Source.Text.Length), Math.Max(0, Math.Min(n.End - n.Start, Source.Text.Length - n.Start)));
        Source.ScrollToLine(Source.GetLineIndexFromCharacterIndex(Math.Min(n.Start, Source.Text.Length)));
    }
    private async void Rename(ConfigNode n)
    {
        if (document == null || !RequireStructure())
            return;
        var d = new EditDialog(this, "이름 변경", n.Path, n.Name.Trim('[', ']').TrimStart('@'), "String");
        if (d.ShowDialog() != true)
            return;
        try
        {
            await Apply(document.Rename(n, d.Value));
        }
        catch (Exception ex) { Error(ex); }
    }
    private void Insert(ConfigNode? n)
    {
        if (n == null || document == null || !RequireStructure())
            return;
        if (Session?.Format == "CSV")
        {
            AddCsvRow();
            return;
        }
        OpenAddPalette(n, Tree);
    }
    private async void Delete(ConfigNode n)
    {
        if (document == null || Session?.StructureEditing != true || Session.Draft != null || Session.RawEditing)
            return;
        string path = Session.Format == "CSV" ? (n.Parent ?? n).Path : n.Path;
        if (!Session.PendingDeletes.Contains(path))
        {
            try
            {
                _ = document.Delete(n);
            }
            catch (Exception ex) { Error(ex); return; }
        }
        Session.ToggleDeletion(path);
        await Parse();
        Message.Text = "삭제 예정 · 같은 메뉴에서 복구 · 완료 시 반영";
    }
    private async void AddSection()
    {
        if (document == null || !RequireStructure())
            return;
        var d = new EditDialog(this, "섹션 추가", "새 섹션 이름", "section", "String");
        if (d.ShowDialog() != true)
            return;
        try
        {
            await Apply(document.AddSection(d.Value));
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void CsvColumn(ConfigNode n, bool delete)
    {
        if (document == null || n.Parent == null || Session == null || !RequireStructure())
            return;
        if (delete && MessageBox.Show(this, "모든 행에서 이 열을 삭제할까요?", "열 삭제", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        string name = "";
        if (!delete)
        {
            var d = new EditDialog(this, "열 추가", "마지막 열에 추가합니다.", "newColumn", "String");
            if (d.ShowDialog() != true)
                return;
            name = d.Value;
        }
        try
        {
            await Apply(document.CsvColumn(n.Parent.Children.IndexOf(n), delete, name, Session.CsvHeader));
        }
        catch (Exception ex) { Error(ex); }
    }
    public void ShowCrypto(string text) => new CryptoWindow(this, text, "복호화").ShowDialog();
    private void OpenTools(object sender, RoutedEventArgs e) => new CryptoWindow(this, "").ShowDialog();
    private async void Transform(ConfigNode n, string action)
    {
        if (document == null || Session == null)
            return;
        var s = Session;
        var before = s.Text;
        var doc = document;
        var window = new CryptoWindow(this, n.Value, action, true);
        if (window.ShowDialog() != true || window.Result == null)
            return;
        if (s != Session || s.Text != before)
        {
            Message.Text = "대상 문서가 바뀌었습니다. 변환을 다시 실행하세요.";
            return;
        }
        string? kind = null;
        if (n.Kind is "Number" or "Boolean")
        {
            if (MessageBox.Show(this, "결과를 적용하면 자료형이 String으로 바뀝니다. 적용할까요?", "자료형 변경", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            kind = "String";
        }
        try
        {
            await Apply(doc.Change(n, window.Result, kind));
        }
        catch (Exception ex) { Error(ex); }
    }
}
