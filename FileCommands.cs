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
    public async void OpenPath(string path) => await OpenPathAsync(path);
    public async Task OpenPathAsync(string path)
    {
        await openGate.WaitAsync();
        try
        {
            path = Path.GetFullPath(path);
            foreach (var editor in Editors)
            {
                var existing = editor.sessions.FirstOrDefault(s => string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    editor.Files.SelectedItem = existing;
                    editor.RememberFile(path);
                    editor.BringForward();
                    return;
                }
            }
            FileSession loaded;
            try
            {
                loaded = await Task.Run(() => FileSession.Load(path));
            }
            catch (DecoderFallbackException)
            {
                if (MessageBox.Show(this, "UTF-8로 읽을 수 없습니다. CP949로 다시 읽을까요?", "문자 인코딩", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    return;
                loaded = await Task.Run(() => FileSession.Load(path, Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)));
            }
            var target = IsLoaded ? this : Editors.FirstOrDefault();
            if (target != null)
            {
                target.sessions.Add(loaded);
                target.Files.SelectedItem = loaded;
                target.RememberFile(path);
            }
        }
        catch (Exception ex) { Error(ex); }
        finally { openGate.Release(); }
    }
    private void OpenFile(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "설정 파일|*.json;*.xml;*.config;*.yaml;*.yml;*.ini;*.cfg;*.csv;*.tsv|모든 파일|*.*", Multiselect = true };
        if (d.ShowDialog(this) == true)
            foreach (string p in d.FileNames)
                OpenPath(p);
    }
    private void FileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (string p in paths)
                OpenPath(p);
    }
    private void NewFile(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        PopulateNewFileMenu(menu);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }
    private void PopulateNewFileMenu(ItemsControl menu)
    {
        foreach (var format in new[] { "JSON", "XML", "YAML", "INI", "CSV" })
        {
            var item = new MenuItem { Header = format };
            item.Click += (_, _) => { var text = format switch { "JSON" => "{\n  \"server\": {\n    \"host\": \"localhost\",\n    \"port\": 8080,\n    \"enabled\": true\n  }\n}\n", "XML" => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <!-- Example Config -->\n  <server host=\"localhost\" port=\"8080\" />\n</configuration>\n", "YAML" => "# 서버 설정\nserver:\n  host: localhost\n  port: 8080\n", "INI" => "; 서버 설정\n[server]\nhost=localhost\nport=8080\n", _ => "name,host,port\nReceiver-A,localhost,08080\n" }; var s = new FileSession(format, text); sessions.Add(s); Files.SelectedItem = s; };
            menu.Items.Add(item);
        }
    }
    private async void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Files)
            return;
        EndInline(false);
        inlinePath = null;
        addPopup?.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false);
        if (!tabContentPending && e.RemovedItems.OfType<FileSession>().FirstOrDefault() is { } previous)
        {
            previous.SearchText = Search.Text;
            previous.SourceMode = sourceMode;
        }
        int generation = ++tabLoadGeneration;
        var selected = Session;
        ConfigDocument? prepared = incomingTabDocument is { } incoming && incoming.Session == selected ? incoming.Document : null;
        if (prepared != null)
            incomingTabDocument = null;
        if (deferTransferredTabLoad)
        {
            tabContentPending = true;
            revision++;
            document = null;
            if (selected != null)
                ShowTabLoading(selected);
            // Let the new window and its loading state paint before assigning a large source text.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (generation != tabLoadGeneration || selected != Session || !IsLoaded)
                return;
        }
        try
        {
            switching = true;
            sourceMode = Session?.SourceMode ?? false;
            Source.Text = Session?.Text ?? "";
            Source.IsUndoEnabled = false;
            Source.IsUndoEnabled = true;
            CsvHeader.IsChecked = Session?.CsvHeader ?? false;
            CsvSeparator.SelectedIndex = Session?.Separator == '\t' ? 1 : Session?.Separator == ';' ? 2 : 0;
            Search.Text = Session?.SearchText ?? "";
            switching = false;
            await Parse(false, prepared);
        }
        finally { if (generation == tabLoadGeneration) HideTabLoading(); }
    }
    private Exception? syntaxError;
    private async void Undo(object sender, RoutedEventArgs e)
    {
        if (Session == null)
            return;
        if (Session.Draft != null)
        {
            Message.Text = "추가 초안을 확정하거나 취소하세요.";
            return;
        }
        Session.Undo();
        await ShowSessionText();
    }
    private async void Redo(object sender, RoutedEventArgs e)
    {
        if (Session == null)
            return;
        if (Session.Draft != null)
        {
            Message.Text = "추가 초안을 확정하거나 취소하세요.";
            return;
        }
        Session.Redo();
        await ShowSessionText();
    }
    private async Task ShowSessionText()
    {
        switching = true;
        Source.Text = Session?.Text ?? "";
        switching = false;
        await Parse();
    }
    private void SaveFile(object sender, RoutedEventArgs e) => Save(Session, false);
    private void SaveAs(object sender, RoutedEventArgs e) => Save(Session, true);
    private bool Save(FileSession? s, bool saveAs)
    {
        if (s == null)
            return false;
        if (inlineBox != null && !CommitInline())
            return false;
        if (s.Input != null && FindFocusedCsv() == null)
        {
            Message.Text = "미적용 입력을 복원하거나 취소한 뒤 저장하세요.";
            return false;
        }
        if (s.Draft != null)
        {
            try
            {
                if (s != Session)
                    return false;
                var added = BuildAddDraft();
                if (added == null)
                    return false;
                s.Change(added);
                s.Draft = null;
                switching = true;
                Source.Text = added;
                switching = false;
                document = new ConfigDocument(s.Text, s.Format, s.Separator);
            }
            catch (Exception ex) { Error(ex); return false; }
        }
        if (s == Session && !Csv.CommitEdit(DataGridEditingUnit.Cell, true))
            return false;
        string? path = s.FilePath;
        if (path == null || saveAs)
        {
            var d = new SaveFileDialog { FileName = path == null ? "configuration." + s.Format.ToLowerInvariant() : Path.GetFileName(path), Filter = $"{s.Format}|*.{s.Format.ToLowerInvariant()}", OverwritePrompt = true };
            if (d.ShowDialog(this) != true)
                return false;
            path = d.FileName;
        }
        try
        {
            if (sessions.Any(other => other != s && string.Equals(other.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                throw new IOException("다른 탭에서 열려 있는 파일입니다. 그 탭을 닫거나 다른 경로를 선택하세요.");
            if (!SaveWithExternalCheck(s, path))
                return false;
            s.CompleteStructure();
            SyncStructure();
            Populate();
            RememberFile(path);
            UpdateStatus();
            Message.Text = "저장했습니다. 숨김 백업은 앱 정상 종료 시 정리됩니다.";
            return true;
        }
        catch (Exception ex) { Error(ex); return false; }
    }
    private bool MayClose(FileSession s)
    {
        if (!s.Dirty)
            return true;
        var result = MessageBox.Show(this, s.Title + "\n변경 내용을 저장할까요?", "저장 확인", MessageBoxButton.YesNoCancel);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && Save(s, false);
    }
    private void CloseTab(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is FileSession s && MayClose(s))
        {
            sessions.Remove(s);
            if (Files.SelectedItem == null && sessions.Count > 0)
                Files.SelectedIndex = 0;
            try
            {
                Checkpoint();
            }
            catch (Exception ex) { Error(ex); }
        }
    }
    private void ClosingWindow(object? sender, CancelEventArgs e)
    {
        if (ReferenceEditor != null)
        {
            e.Cancel = true;
            ReferenceEditor.Activate();
            return;
        }
        if (closingForUpdate)
            return;
        if (Workspace != null)
        {
            try
            {
                Checkpoint();
                return;
            }
            catch (Exception ex) { e.Cancel = true; Error(ex); return; }
        }
        foreach (var s in sessions)
            if (!MayClose(s))
            {
                e.Cancel = true;
                return;
            }
    }
    private void ChangeEncoding(object sender, RoutedEventArgs e)
    {
        if (Session == null)
            return;
        var menu = new ContextMenu();
        foreach (var name in new[] { "UTF-8", "UTF-8 BOM", "UTF-16 LE", "CP949" })
        {
            var i = new MenuItem { Header = name };
            i.Click += (_, _) => { Session?.SetEncoding(name); UpdateStatus(); Message.Text = "선택한 인코딩은 다음 저장에 적용됩니다."; };
            menu.Items.Add(i);
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }
    private int zoomPercent = 100;
}
