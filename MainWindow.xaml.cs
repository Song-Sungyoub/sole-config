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
public partial class MainWindow : Window
{
    private readonly ObservableCollection<FileSession> sessions = [];
    private static bool updateRunning;
    private bool closingForUpdate;
    public MainWindow()
    {
        InitializeComponent();
        Tree.LoadingRow += (_, e) => { RowHover.Attach(e.Row); if (e.Row.Item is ConfigNode node) AttachMove(e.Row, node); };
        Csv.LoadingRow += (_, e) => RowHover.Attach(e.Row);
        InitializeCsvEditing();
        Files.ItemsSource = sessions;
        LoadView();
        InitializeRecovery();
        InitializeInline();
        InitializeExternalChanges();
        Activated += (_, _) => ((App)Application.Current).ActiveEditor = this;
        Closed += (_, _) => { parseTimer.Stop(); revision++; };
        parseTimer.Tick += async (_, _) => { parseTimer.Stop(); await Parse(); };
        PreviewKeyDown += Keys;
        LoadWindowButtons();
        bool dark = false;
        try
        {
            dark = File.Exists(themeFile) && File.ReadAllText(themeFile) == "dark";
        }
        catch { }
        SetTheme(dark, false);
        Status.Text = "";
    }
    private void Keys(object sender, KeyEventArgs e)
    {
        if (ReferenceEditor != null)
        {
            e.Handled = true;
            return;
        }
        if (EditorArea.IsKeyboardFocusWithin && KeyboardTypeKeys(e, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }
        if (EditorArea.IsKeyboardFocusWithin && KeyboardStructureKeys(e, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }
        if (InlineKeys(e))
        {
            e.Handled = true;
            return;
        }
        if (inlineBox != null)
            return;
        if (Csv.IsKeyboardFocusWithin && Find<TextBox>(e.OriginalSource as DependencyObject) != null && e.Key is Key.Z or Key.Y)
            return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key is Key.D0 or Key.NumPad0)
            {
                SetZoom(100);
                e.Handled = true;
            }
            else if (e.Key is Key.OemPlus or Key.Add)
            {
                SetZoom(zoomPercent + 10);
                e.Handled = true;
            }
            else if (e.Key is Key.OemMinus or Key.Subtract)
            {
                SetZoom(zoomPercent - 10);
                e.Handled = true;
            }
            else if (e.Key == Key.O)
            {
                OpenFile(this, new());
                e.Handled = true;
            }
            else if (e.Key == Key.S)
            {
                Save(Session, false);
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                OpenSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Z && !Source.IsKeyboardFocusWithin)
            {
                Undo(this, new());
                e.Handled = true;
            }
            else if (e.Key == Key.Y && !Source.IsKeyboardFocusWithin)
            {
                Redo(this, new());
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && (Search.IsKeyboardFocusWithin || Search.Text.Length > 0))
        {
            CloseSearch(this, new());
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !Csv.IsKeyboardFocusWithin && EditorArea.IsKeyboardFocusWithin && Tree.SelectedItem is ConfigNode n)
        {
            Edit(n);
            e.Handled = true;
        }
    }
    private void LightTheme(object sender, RoutedEventArgs e) => SetTheme(false);
    private void DarkTheme(object sender, RoutedEventArgs e) => SetTheme(true);
    private bool darkTheme;
    public void SetTheme(bool dark, bool persist = true)
    {
        // UI-MOCKUP-V2 토큰: 단색 표면, 그룹은 Band, 값은 자료형 색
        var keys = new[] { "Canvas", "Surface", "Text", "Muted", "Line", "Property", "Selected", "Blue", "Band", "Changed", "NumberText", "BoolText", "NullText" };
        var colors = dark ? new[] { "#1C1F24", "#24282E", "#E2E5EA", "#949DAA", "#353A43", "#2B3037", "#2B3C55", "#5AA2F2", "#2A2F36", "#E2A245", "#56C2B4", "#BB97EE", "#7D8691" }
                       : new[] { "#EEF0F3", "#FFFFFF", "#1F2733", "#68717F", "#E1E4E9", "#F3F5F8", "#E4EEFB", "#0A66D8", "#F5F7FA", "#B86E00", "#0B7468", "#7340B0", "#8A93A0" };
        for (int i = 0; i < keys.Length; i++)
            Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        darkTheme = dark;
        ThemeToggle.Content = dark ? "밝은 테마" : "어두운 테마";
        if (persist)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(themeFile)!);
                File.WriteAllText(themeFile, dark ? "dark" : "light");
            }
            catch { }
    }
    private void Error(Exception ex) => MessageBox.Show(this, ex.Message, "SoleConfig", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void ToggleTheme(object sender, RoutedEventArgs e) => SetTheme(!darkTheme);
    private void WindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateInspectorVisibility();
    private void CloseApp(object sender, RoutedEventArgs e) => Close();
    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
