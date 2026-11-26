using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ConfigEditor;

// 오른쪽 선택 항목 패널 (UI-MOCKUP-V2). 작업 목록은 오른쪽 클릭 메뉴(MakeMenu)를 그대로 펼쳐 같은 처리 함수를 쓴다.
public partial class MainWindow
{
    private const double InspectorMinWindow = 1000;
    private (string Text, Dictionary<string, string> Values)? savedValues;

    private void UpdateInspectorVisibility()
    {
        var show = Session != null && !sourceMode && Session.Format != "CSV" && ActualWidth >= InspectorMinWindow;
        Inspector.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateInspector(ConfigNode? node)
    {
        InspectorContent.Children.Clear();
        if (node == null || Session == null)
        {
            InspectorContent.Children.Add(Muted("항목을 선택하면 경로와 원래 값, 사용할 수 있는 작업이 여기에 표시됩니다.", wrap: true));
            return;
        }
        var title = new TextBlock { Text = node.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        InspectorContent.Children.Add(title);

        var path = new DockPanel { Margin = new Thickness(0, 6, 0, 14) };
        var copy = new Button { Content = "경로 복사", Style = (Style)FindResource("PanelAction"), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0), FontSize = 12 };
        copy.SetResourceReference(ForegroundProperty, "Blue");
        copy.Click += (_, _) => Copy(node.Path);
        DockPanel.SetDock(copy, Dock.Right); path.Children.Add(copy);
        var pathText = Muted(node.Path); pathText.SetResourceReference(FontFamilyProperty, "Mono"); pathText.FontSize = 12; pathText.TextTrimming = TextTrimming.CharacterEllipsis; pathText.ToolTip = node.Path; pathText.VerticalAlignment = VerticalAlignment.Center;
        path.Children.Add(pathText);
        InspectorContent.Children.Add(path);

        var facts = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        facts.ColumnDefinitions.Add(new ColumnDefinition());
        void Fact(string label, string value, bool mono = false, bool strike = false)
        {
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Muted(label); l.Margin = new Thickness(0, 0, 0, 6); Grid.SetRow(l, facts.RowDefinitions.Count - 1); facts.Children.Add(l);
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            if (mono) v.SetResourceReference(FontFamilyProperty, "Mono");
            if (strike) { v.TextDecorations = TextDecorations.Strikethrough; v.SetResourceReference(ForegroundProperty, "Muted"); }
            Grid.SetRow(v, facts.RowDefinitions.Count - 1); Grid.SetColumn(v, 1); facts.Children.Add(v);
        }
        Fact("형식", node.DisplayKind);
        if (node.Group) Fact("하위", node.Children.Count + "개");
        else Fact("값", node.Display, mono: true);
        if (node.State == "변경" && !node.Group && SavedValue(node.Path) is { } old && old != node.Value) Fact("원래 값", old.Length == 0 ? "(빈 문자열)" : old, mono: true, strike: true);
        else if (node.State.Length > 0 && node.State != "변경") Fact("상태", node.State);
        InspectorContent.Children.Add(facts);

        var line = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 6) }; line.SetResourceReference(BackgroundProperty, "Line");
        InspectorContent.Children.Add(line);
        foreach (var entry in MakeMenu(node).Items.Cast<object>().ToList())
        {
            if (entry is Separator) { var gap = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) }; gap.SetResourceReference(BackgroundProperty, "Line"); InspectorContent.Children.Add(gap); continue; }
            if (entry is not MenuItem item || item.Header is not string header || header == "경로 복사") continue;
            var row = new DockPanel();
            if (header is "값 수정" or "주석 수정") { var key = Muted("F2"); key.FontSize = 11; DockPanel.SetDock(key, Dock.Right); row.Children.Add(key); }
            row.Children.Add(new TextBlock { Text = header });
            var button = new Button { Content = row, Style = (Style)FindResource("PanelAction"), IsEnabled = item.IsEnabled };
            button.Click += (_, _) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            InspectorContent.Children.Add(button);
        }
    }

    // 경로줄: $ › server › port. 조각을 누르면 그 상위 항목을 선택한다.
    private void UpdateCrumbs(ConfigNode? node)
    {
        Crumbs.Children.Clear();
        if (node == null) return;
        var chain = new List<ConfigNode>();
        for (var n = node; n != null; n = n.Parent) chain.Insert(0, n);
        foreach (var part in chain)
        {
            if (Crumbs.Children.Count > 0) Crumbs.Children.Add(Muted("›"));
            var target = part;
            var crumb = new Button { Content = part.Name, Style = (Style)FindResource("MenuBarButton"), Padding = new Thickness(4, 1, 4, 1), ToolTip = part.Path };
            crumb.Click += (_, _) => SelectNode(target, true);
            Crumbs.Children.Add(crumb);
        }
    }

    private void EditMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        CommandItem(menu, "실행 취소    Ctrl+Z", Undo, UndoButton.IsEnabled);
        CommandItem(menu, "다시 실행    Ctrl+Y", Redo, RedoButton.IsEnabled);
        menu.Items.Add(new Separator());
        CommandItem(menu, "찾기    Ctrl+F", (_, _) => OpenSearch(), Session != null);
        if (Session?.StructureEditing != true) CommandItem(menu, "구조 편집 시작", StructureClick, Session != null);
        ShowToolbarMenu(sender, menu);
    }

    // ponytail: 저장 기준 원문을 한 번 파싱해 캐시. 50 MiB 파일에서는 첫 선택이 느릴 수 있음.
    private string? SavedValue(string path)
    {
        var text = Session?.SavedText ?? "";
        if (text.Length == 0) return null;
        if (savedValues?.Text != text)
        {
            var values = new Dictionary<string, string>();
            try { foreach (var n in new ConfigDocument(text, Session!.Format, Session.Separator).All) values.TryAdd(n.Path, n.Value); } catch { }
            savedValues = (text, values);
        }
        return savedValues.Value.Values.TryGetValue(path, out var value) ? value : null;
    }

    private static TextBlock Muted(string text, bool wrap = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        t.SetResourceReference(ForegroundProperty, "Muted");
        return t;
    }
}
