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
    public async Task RunUiTest(string output)
    {
        closingForUpdate = true;
        inlineEnabled = false;
        Directory.CreateDirectory(output);
        await TestNewFiles();
        await TestKeyboardEditing(output);
        await TestScripts(output);
        await TestInline(output);
        await TestStructureUi(output);
        var examples = new Dictionary<string, string> { { "JSON", "{\n  \"server\": {\"host\": \"localhost\", \"port\": 8080, \"enabled\": true},\n  \"security\": {\"token\": \"sample-key\"},\n  \"devices\": [{\"name\": \"Receiver-A\"}, {\"name\": \"Receiver-B\"}]\n}" }, { "XML", "<configuration>\n  <!-- Example Config -->\n  <server host='localhost' port='8080'><token>sample-key</token></server>\n</configuration>" }, { "CSV", "name,host,port\r\nReceiver-A,localhost,08080\r\nReceiver-B,localhost,08081\r\n" }, { "YAML", "# comment\nserver:\n  host: localhost\n  port: 8080\n" }, { "INI", "; comment\n[server]\nhost=localhost\nport=8080\n[security]\ntoken=sample-key\n" } };
        foreach (var pair in examples)
        {
            var s = new FileSession(pair.Key, pair.Value);
            sessions.Add(s);
            Files.SelectedItem = s;
            await Parse();
            if (document == null)
                throw new Exception("UI parse failed: " + pair.Key);
        }
        // 탭이 많을 때 새로 선택한 탭이 탭 줄 안에 보여야 한다
        var extraTabs = Enumerable.Range(0, 12).Select(i => new FileSession("JSON", "{\"tab\":" + i + "}")).ToList();
        foreach (var tab in extraTabs) { sessions.Add(tab); Files.SelectedItem = tab; }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var tabScroll = (ScrollViewer)VisualTreeHelper.GetChild(Files, 0);
        var lastTab = (FrameworkElement)Files.ItemContainerGenerator.ContainerFromItem(extraTabs[^1]);
        var tabBounds = lastTab.TransformToAncestor(tabScroll).TransformBounds(new Rect(lastTab.RenderSize));
        if (tabScroll.HorizontalOffset <= 0 || tabBounds.Left < -1 || tabBounds.Right > tabScroll.ViewportWidth + 1)
            throw new Exception("New tab must scroll into view");
        foreach (var tab in extraTabs) sessions.Remove(tab);
        foreach (var session in sessions.Where(s => s.Format != "CSV").ToArray())
        {
            Files.SelectedItem = session;
            await Parse();
            foreach (var mode in Enum.GetValues<EditorView>())
            {
                ChangeView(mode, false);
                var node = document!.All.First(n => n.Editable && !n.Group && n.Value == "localhost");
                var before = session.Text;
                SelectNode(node, false);
                await Apply(document.Change(node, "ui-edited"));
                var edited = session.Text;
                var path = session.SelectedPath;
                foreach (var next in Enum.GetValues<EditorView>())
                {
                    ChangeView(next, false);
                    if (session.Text != edited || !session.CanUndo || session.SelectedPath != path)
                        throw new Exception("Shared edit/history: " + session.Format + " " + next);
                }
                session.Undo();
                await ShowSessionText();
                if (session.Text != before)
                    throw new Exception("View undo: " + session.Format);
                if (session.Format is "JSON" or "YAML" or "XML")
                {
                    ChangeView(mode, false);
                    var typed = document!.All.First(n => n.Editable && (session.Format == "XML" ? n.XmlValue != null : n.Kind == "String"));
                    session.BeginStructure();
                    var picker = (ComboBox)CreateTypePicker(typed);
                    if (session.Format != "XML")
                    {
                        picker.SelectedItem = "Number";
                        if (session.Text != before || picker.SelectedItem as string != "String")
                            throw new Exception("Invalid type rollback");
                    }
                    picker.SelectedItem = session.Format == "XML" ? "CDATA" : "Null";
                    for (int wait = 0; wait < 100 && (document == null || document.All.First(n => n.Path == typed.Path).DisplayKind == typed.DisplayKind); wait++)
                        await Task.Delay(20);
                    if (document == null || document.All.First(n => n.Path == typed.Path).DisplayKind != (session.Format == "XML" ? "CDATA" : "Null"))
                        throw new Exception("Dropdown conversion: " + mode + " " + session.Format);
                    session.Undo();
                    await ShowSessionText();
                    if (session.Text != before)
                        throw new Exception("Dropdown undo");
                    session.CompleteStructure();
                }
            }
        }
        Files.SelectedIndex = 0;
        await Parse();
        var host = document!.All.First(n => n.Name == "host");
        await Apply(document.Change(host, "changed"));
        if (!Session!.Dirty)
            throw new Exception("UI dirty state");
        Session.Undo();
        await ShowSessionText();
        if (document!.All.First(n => n.Name == "host").Value != "localhost")
            throw new Exception("UI undo");
        CloseSearch(this, new());
        if (Search.Text != "" || SearchPanel.Visibility != Visibility.Visible)
            throw new Exception("Search box always visible and empty");
        OpenSearch();
        if (!Search.IsKeyboardFocused)
            throw new Exception("Open search focus");
        Search.Text = "Receiver-A";
        if (Tree.Items.Count == 0)
            throw new Exception("UI search");
        CloseSearch(this, new());
        if (Search.Text != "" || Session!.SearchText != "" || SearchClear.Visibility != Visibility.Collapsed || !Tree.Items.Cast<ConfigNode>().Any(n => n.Name == "host"))
            throw new Exception("Close search clears filter");
        Session!.BeginStructure(true);
        sourceMode = true;
        SetView();
        Source.Text = "{";
        await Parse();
        if (document != null || !sourceMode)
            throw new Exception("UI invalid source");
        Session.Undo();
        await ShowSessionText();
        Session.CompleteStructure();
        sourceMode = false;
        SetView();
        async Task Capture(string name, FrameworkElement? target = null)
        {
            target ??= this;
            target.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            if (target is Window)
                bitmap.Render(target);
            else
            {
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(new VisualBrush(target), null, new Rect(0, 0, target.ActualWidth, target.ActualHeight));
                bitmap.Render(visual);
            }
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name));
            encoder.Save(file);
        }
        OpenSearch();
        Search.Text = "Receiver";
        SetTheme(false, false);
        await Capture("wpf-search-floating-light.png");
        SetTheme(true, false);
        await Capture("wpf-search-floating-dark.png");
        CloseSearch(this, new());
        Files.SelectedIndex = 1;
        await Parse();
        foreach (var mode in Enum.GetValues<EditorView>())
        {
            ChangeView(mode, false);
            foreach (var dark in new[] { false, true })
            {
                SetTheme(dark, false);
                await Capture($"wpf-comment-{mode}-{(dark ? "dark" : "light")}.png");
                var comment = document!.All.First(n => n.Kind == "Comment");
                if (mode != EditorView.Groups)
                {
                    var grid = mode == EditorView.Tree ? Tree : Stage;
                    var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(comment);
                    if (row == null || Math.Abs(row.ActualHeight - 26) > 1)
                        throw new Exception("Comment row height: " + mode);
                    var text = Visuals<TextBlock>(row).First(t => t.Text == comment.Display);
                    var center = text.TransformToAncestor(row).Transform(new Point(0, text.ActualHeight / 2)).Y;
                    if (Math.Abs(center - row.ActualHeight / 2) > 1.5)
                        throw new Exception("Comment vertical center: " + mode + " " + center + "/" + row.ActualHeight);
                    foreach (var guide in Visuals<TreeGuides>(row))
                    {
                        var drawing = VisualTreeHelper.GetDrawing(guide);
                        if (drawing != null && drawing.Children.OfType<GeometryDrawing>().Any(d => d.Geometry is LineGeometry line && line.StartPoint.Y == line.EndPoint.Y && line.StartPoint.X != line.EndPoint.X))
                            throw new Exception("Comment horizontal branch");
                    }
                }
            }
        }
        SetTheme(false, false);
        Files.SelectedIndex = 0;
        await Parse();
        foreach (var dark in new[] { false, true })
        {
            SetTheme(dark, false);
            var problem = new SyntaxErrorDialog(this, "sample.xml", "<root>\n  <port>8080</wrong>\n</root>", new System.Xml.XmlException("시작 태그 port와 종료 태그 wrong이 일치하지 않습니다.", null, 2, 15));
            problem.Show();
            await Capture($"wpf-syntax-error-{(dark ? "dark" : "light")}.png", problem);
            problem.Close();
            var accept = new SyntaxErrorDialog(this, "sample.xml", "<root>\n  <port>8080</wrong>\n</root>", new System.Xml.XmlException("잘못된 종료 태그", null, 2, 15));
            _ = Dispatcher.BeginInvoke(new Action(() => accept.LocateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))), DispatcherPriority.ApplicationIdle);
            if (accept.ShowDialog() != true || accept.ErrorOffset != 21)
                throw new Exception("Syntax dialog locate action");
        }
        SetTheme(false, false);
        var recoveryApp = (App)Application.Current;
        var previousStore = recoveryApp.Workspace;
        recoveryApp.Workspace = new WorkspaceStore(Path.Combine(output, "workspace"));
        var restoreWindow = new MainWindow();
        restoreWindow.Show();
        try
        {
            var draft = new FileSession("JSON", "{unfinished") { SourceMode = true, SearchText = "draft" };
            var restoredState = new WorkspaceStore.WindowState(1, Guid.NewGuid(), 1, [new FileSession("XML", "<root>unsaved</root>").Capture(), draft.Capture()]);
            await restoreWindow.RestoreWorkspace(restoredState);
            if (restoreWindow.sessions.Count != 2 || restoreWindow.Files.SelectedIndex != 1 || restoreWindow.Session!.Text != "{unfinished" || !restoreWindow.Session.Dirty || restoreWindow.Source.Text != "{unfinished" || !restoreWindow.sourceMode)
                throw new Exception("Restored window tabs, selected draft and source");
            restoreWindow.Checkpoint();
            var durable = recoveryApp.Workspace.LoadWindows([]).Single(w => w.Id == restoredState.Id);
            if (durable.Selected != 1 || durable.Tabs[1].Text != "{unfinished")
                throw new Exception("Durable update checkpoint");
            restoreWindow.Close();
            if (Editors.Contains(restoreWindow))
                throw new Exception("Unsaved hot exit");
            var reopened = new MainWindow();
            reopened.Show();
            try
            {
                await reopened.RestoreWorkspace(durable);
                if (reopened.Session?.Text != "{unfinished" || reopened.sessions.Count != 2)
                    throw new Exception("Hot exit reopen");
            }
            finally { reopened.Close(); }
            recoveryApp.Workspace.Remember(Path.Combine(output, "recent-example.json"));
            if (!recoveryApp.Workspace.Recent().Any(p => p.EndsWith("recent-example.json")))
                throw new Exception("Recent workspace path");
        }
        finally { if (Editors.Contains(restoreWindow)) { restoreWindow.closingForUpdate = true; restoreWindow.Close(); } recoveryApp.Workspace = previousStore; }
        var selected = document!.All.First(n => n.Name == "host");
        SelectNode(selected, false);
        var originalText = Session!.Text;
        var originalDirty = Session.Dirty;
        foreach (var mode in Enum.GetValues<EditorView>())
        {
            ChangeView(mode, false);
            if (Session.Text != originalText || Session.Dirty != originalDirty || Session.SelectedPath != selected.Path || Tree.SelectedItem != selected)
                throw new Exception("View switch changed document or selection: " + mode);
            Search.Text = "Receiver-A";
            if (Tree.Items.Count == 0)
                throw new Exception("View search: " + mode);
            Search.Clear();
            var group = document.All.First(n => n.Name == "server");
            group.Open = false;
            Populate();
            if (Tree.Items.Cast<ConfigNode>().Any(n => n.Parent == group))
                throw new Exception("View collapse: " + mode);
            await Capture("wpf-" + mode + "-collapsed.png");
            group.Open = true;
            Populate();
            SetTheme(false, false);
            await Capture("wpf-" + mode + "-light.png");
            await Capture("editor-" + mode + "-light.png", EditorArea);
            SetTheme(true, false);
            await Capture("wpf-" + mode + "-dark.png");
            await Capture("editor-" + mode + "-dark.png", EditorArea);
        }
        foreach (var branchView in Enum.GetValues<EditorView>())
        {
            ChangeView(branchView, false);
            Search.Clear();
            var branch = document!.Roots[0];
            branch.Open = true;
            ToggleBranch(branch, true);
            if (ConfigDocument.Walk([branch]).Where(n => n.Group).Any(n => n.Open || !Session!.Collapsed.Contains(n.Path)))
                throw new Exception("Recursive collapse");
            ToggleBranch(branch, true);
            if (ConfigDocument.Walk([branch]).Where(n => n.Group).Any(n => !n.Open || Session!.Collapsed.Contains(n.Path)))
                throw new Exception("Recursive expand");
            var child = branch.Children.First(n => n.Group);
            ToggleBranch(child, false);
            ToggleBranch(branch, false);
            ToggleBranch(branch, false);
            if (child.Open)
                throw new Exception("Ordinary toggle preserves descendants");
            branch.Open = false;
            ToggleBranch(branch, true);
        }
        if (ParseView("bad") != EditorView.Tree || ParseView("Stages") != EditorView.Stages)
            throw new Exception("View preference fallback");
        ChangeView(EditorView.Stages, false);
        var zoomText = Session!.Text;
        var zoomPath = Session.SelectedPath;
        var zoomUndo = Session.CanUndo;
        SetZoom(999);
        if (zoomPercent != 200)
            throw new Exception("Zoom upper limit");
        SetZoom(1);
        if (zoomPercent != 50)
            throw new Exception("Zoom lower limit");
        SetZoom(150);
        foreach (var mode in Enum.GetValues<EditorView>())
        {
            ChangeView(mode, false);
            await Capture("wpf-zoom-" + mode + ".png");
            if (EditorScale.ScaleX != 1.5 || EditorScale.ScaleY != 1.5 || Session.Text != zoomText || Session.SelectedPath != zoomPath || Session.CanUndo != zoomUndo)
                throw new Exception("Zoom state preservation");
        }
        sourceMode = true;
        SetView();
        await Capture("wpf-zoom-source.png");
        sourceMode = false;
        SetView();
        ResetZoom(this, new());
        if (EditorScale.ScaleX != 1 || zoomPercent != 100)
            throw new Exception("Zoom reset");
        var alignmentSession = new FileSession("JSON", System.Text.Json.JsonSerializer.Serialize(new
        {
            targets = new Dictionary<string, object>
            {
                [".NETCoreApp,Version=v8.0"] = new Dictionary<string, object>
                {
                    ["ExampleApp/1.0.0"] = new { dependencies = Enumerable.Range(0, 80).ToDictionary(i => i % 2 == 0 ? "Example." + i : "Example.Controls." + new string('X', 100) + i, i => "1.0." + i) }
                }
            }
        }));
        sessions.Add(alignmentSession);
        Files.SelectedItem = alignmentSession;
        await Parse();
        ChangeView(EditorView.Stages, false);
        SetTheme(false, false);
        static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    yield return match;
                foreach (var descendant in Visuals<T>(child))
                    yield return descendant;
            }
        }
        var referenceNode = document!.All.First(n => n.Name == "Example.0");
        SelectNode(referenceNode, false);
        Edit(referenceNode);
        var reference = ReferenceEditor as EditDialog ?? throw new Exception("Reference edit not opened");
        if (!IsEnabled || !reference.IsVisible)
            throw new Exception("Reference owner disabled");
        reference.ValueBox.Text = "edited-reference";
        var lockedClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        GridButton.RaiseEvent(lockedClick);
        if (!lockedClick.Handled)
            throw new Exception("Reference background click unlocked");
        Stage.ScrollIntoView(Stage.Items[40]);
        UpdateLayout();
        var scroll = Visuals<ScrollViewer>(Stage).First();
        var previousOffset = scroll.VerticalOffset;
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
        UpdateLayout();
        if (scroll.VerticalOffset <= previousOffset || Session!.SelectedPath != referenceNode.Path)
            throw new Exception("Reference wheel or selection");
        await Capture("wpf-reference-background.png");
        await Capture("wpf-reference-dialog.png", reference);
        Visuals<Button>(reference).First(b => b.Content as string == "적용").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int wait = 0; wait < 100 && (ReferenceEditor != null || document == null || !Session.Text.Contains("edited-reference")); wait++)
            await Task.Delay(20);
        if (ReferenceEditor != null || document == null || document.All.First(n => n.Path == referenceNode.Path).Value != "edited-reference")
            throw new Exception("Reference applies original node");
        Session.Undo();
        await ShowSessionText();
        var cancelReference = new EditDialog(this, "값 수정", "test", "original", "String");
        var cancelTask = ShowReferenceEdit(cancelReference);
        cancelReference.ValueBox.Text = "discard";
        cancelReference.Close();
        if (await cancelTask.WaitAsync(TimeSpan.FromSeconds(5)) || ReferenceEditor != null)
            throw new Exception("Reference cancel cleanup");
        var resizeHeader = (Grid)Stage.Columns[0].Header;
        var resizeGrip = resizeHeader.Children.OfType<System.Windows.Controls.Primitives.Thumb>().ElementAt(1);
        resizeGrip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(120, 0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
        ResizeStage(2, 130);
        if (stageWidths[1] != 300 || stageWidths[2] != 130 || Session!.Text != alignmentSession.Text)
            throw new Exception("Stage resize drag");
        foreach (var zoom in new[] { 100, 125, 150 })
        {
            SetZoom(zoom);
            foreach (var index in new[] { 0, 40, 75 })
            {
                Stage.ScrollIntoView(Stage.Items[index]);
                await Capture($"wpf-alignment-{zoom}-{index}.png");
                var rows = Visuals<StageRow>(Stage).ToArray();
                foreach (var row in rows)
                {
                    var layout = (Grid)((Border)row.Content).Child;
                    if (Math.Abs(layout.ColumnDefinitions[1].ActualWidth - 300) > .1 || Math.Abs(layout.ColumnDefinitions[2].ActualWidth - 130) > .1)
                        throw new Exception("Resized virtual row width");
                }
                var origins = rows.Select(r => ((Border)r.Content).Child.TransformToAncestor(Stage).Transform(new Point()).X).ToArray();
                if (origins.Length < 2 || origins.Max() - origins.Min() > .1)
                    throw new Exception("Stage row alignment: " + string.Join(", ", origins));
            }
        }
        ResizeStage(1, 1);
        if (stageWidths[1] != 100)
            throw new Exception("Stage minimum width");
        ResizeStage(1, 20000);
        if (stageWidths[1] != 10000)
            throw new Exception("Stage maximum width");
        ResizeStage(1, StageWidth);
        ResizeStage(2, StageWidth);
        sessions.Remove(alignmentSession);
        Files.SelectedIndex = 0;
        await Parse();
        SetZoom(100);
        foreach (var dark in new[] { false, true })
        {
            SetTheme(dark, false);
            var chooser = new ViewChooser(this);
            if (chooser.SelectedView != EditorView.Tree)
                throw new Exception("First-run default must be A");
            chooser.Show();
            foreach (var choice in Enum.GetValues<EditorView>())
            {
                chooser.SelectView(choice);
                await Capture($"wpf-chooser-{choice}-{(dark ? "dark" : "light")}.png", chooser);
            }
            chooser.Close();
            SetTheme(dark, false);
            var update = new UpdateDialog(this, new Version(0, 1, 7), new Version(0, 1, 8));
            update.Show();
            await Capture(dark ? "wpf-update-dark.png" : "wpf-update-light.png", update);
            update.Close();
        }
        foreach (var choice in Enum.GetValues<EditorView>())
        {
            var chooser = new ViewChooser(this);
            chooser.SelectView(choice);
            chooser.Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => chooser.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            if (chooser.ShowDialog() != true || chooser.SelectedView != choice)
                throw new Exception("View chooser selection");
        }
        var dismissed = new ViewChooser(this);
        dismissed.SelectView(EditorView.Tree);
        dismissed.Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => dismissed.LaterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        if (dismissed.ShowDialog() == true)
            throw new Exception("View chooser dismiss must not apply");
        foreach (var accept in new[] { false, true })
        {
            var update = new UpdateDialog(this, new Version(0, 1, 7), new Version(0, 1, 8));
            update.Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => (accept ? update.InstallButton : update.LaterButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            if ((update.ShowDialog() == true) != accept)
                throw new Exception("Update dialog decision");
        }
        var transform = new CryptoWindow(this, "sample-key", "SHA-256", true);
        transform.Show();
        await Capture("wpf-crypto.png", transform);
        transform.Close();
        var edit = new EditDialog(this, "값 수정", "$/server/host", "localhost", "String");
        edit.Show();
        await Capture("wpf-edit.png", edit);
        edit.Close();
        Files.SelectedIndex = 2;
        await Parse();
        if (Csv.Columns.Count != 3 || csvRows.Count != 3)
            throw new Exception("CSV grid");
        foreach (var mode in Enum.GetValues<EditorView>())
        {
            ChangeView(mode, false);
            if (Csv.Visibility != Visibility.Visible || Stage.Visibility == Visibility.Visible || Groups.Visibility == Visibility.Visible)
                throw new Exception("CSV mode isolation, zoom limits/reset/state and five editor renders");
        }
        SetZoom(150);
        await Capture("wpf-csv.png");
        SetZoom(100);
        await TestWindowTabs(output);
        File.WriteAllText(Path.Combine(output, "ui-test-result.txt"), "PASS: five formats, tabs, new tab scrolled into view, edit, undo, search, invalid source recovery, three views, selection and document preservation, collapse, six theme renders, preference fallback, CSV mode isolation, zoom limits/reset/state, concurrent file open deduplication, tab detach/merge/reorder, cross-window duplicate open, transferred unsaved state/history/search/source mode, empty window close");
        sessions.Clear();
    }
}
