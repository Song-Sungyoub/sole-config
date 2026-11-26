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
    public async Task CheckUpdates(bool manual = false)
    {
        if (updateRunning || Editors.Any(w => w.ReferenceEditor != null))
            return;
        if (!UpdateService.IsInstalled)
        {
            if (manual)
                MessageBox.Show(this, "자동 업데이트는 기본 경로에 설치한 앱에서 사용할 수 있습니다. GitHub Releases의 최신 MSI를 설치하세요.", "업데이트");
            return;
        }
        updateRunning = true;
        var accepted = false;
        try
        {
            if (manual)
                Message.Text = "업데이트 확인 중…";
            var package = await UpdateService.FindLatest();
            if (!IsLoaded || Editors.Any(w => w.ReferenceEditor != null))
                return;
            if (package == null)
            {
                if (manual)
                    Message.Text = $"SoleConfig {UpdateService.Current} · 최신 버전입니다.";
                return;
            }
            if (new UpdateDialog(this, UpdateService.Current, package.Version).ShowDialog() != true)
                return;
            accepted = true;
            Message.Text = "업데이트 파일 다운로드·검증 중…";
            var local = await UpdateService.Prepare(package);
            if (!IsLoaded || Editors.Any(w => w.ReferenceEditor != null))
                return;
            var windows = Editors.ToArray();
            if (Workspace == null)
                throw new IOException("작업 복원 저장소를 사용할 수 없어 업데이트를 중단했습니다. 파일을 저장한 뒤 다시 실행하세요.");
            foreach (var editor in windows)
                editor.Checkpoint();
            UpdateService.Install(local, package.Version);
            foreach (var editor in windows)
            {
                editor.closingForUpdate = true;
                editor.Close();
            }
        }
        catch (Exception ex) { if ((manual || accepted) && IsLoaded) Message.Text = "업데이트를 확인하거나 준비하지 못했습니다: " + ex.Message; }
        finally { updateRunning = false; }
    }
    private async void UpdateClick(object sender, RoutedEventArgs e) => await CheckUpdates(true);
    public void ShowUpdateResult()
    {
        try
        {
            var result = UpdateService.Result();
            if (result != null)
                Message.Text = result;
        }
        catch { }
    }
    private FileSession? Session => Files.SelectedItem as FileSession;
    private ConfigDocument? document;
    private bool switching, sourceMode;
    private int revision;
    private readonly DispatcherTimer parseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly List<ConfigNode> csvRows = [];
    private readonly string themeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoleConfig", "theme.txt");
}
