using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sim.App.Help;
using Sim.App.Update;

namespace Sim.App;

/// <summary>
/// 자동 업데이트(GitHub Releases) 화면: 시작 몇 초 뒤 하루 한 번 자동 확인(도움말 메뉴에서 끄기) · 도움말 → 업데이트 확인… ·
/// 새 버전 대화상자([지금 업데이트] [나중에] [이 버전 건너뛰기]) → 진행 막대로 내려받기 · SHA-256 검증 → 설치 프로그램 /SILENT → 앱 종료
/// (설치 프로그램이 다시 띄움) · 도움말 → 정보(버전). 판정 · 내려받기는 <see cref="UpdateService"/>(Avalonia 없음).
/// </summary>
public partial class MainWindow
{
    private UpdateService? _updates;
    private bool _updateBusy;

    /// <summary>앱 시작 뒤 자동 확인까지 기다리는 시간 — 첫 실행 · 3D 초기화와 겹치지 않게.</summary>
    private static readonly TimeSpan AutoCheckDelay = TimeSpan.FromSeconds(5);

    private UpdateService Updates => _updates ??= new UpdateService(
        UpdateChecker.CreateClient(UpdateChecker.Current), new UpdateSettingsStore(UpdateSettingsStore.DefaultPath),
        UpdateChecker.Current, UpdateChecker.LatestUrl(UpdateChecker.ApiBase),
        Path.Combine(Path.GetTempPath(), "OpenMS-Update"), UpdateChecker.LaunchInstaller, UpdateLog);

    private void UpdateLog(string message)
    {
        if (Dispatcher.UIThread.CheckAccess()) Log(LogLevel.Info, "업데이트", message);
        else Dispatcher.UIThread.Post(() => Log(LogLevel.Info, "업데이트", message));
    }

    /// <summary>앱 버전 표시(릴리스 = 태그 버전, 개발 빌드 = 0.0.0 (개발 빌드)).</summary>
    public static string VersionText
    {
        get
        {
            var v = UpdateChecker.Current.ToString(3);
            return UpdateChecker.IsDevBuild(UpdateChecker.Current) ? $"{v} (개발 빌드)" : v;
        }
    }

    private void WireUpdate()
    {
        MiCheckUpdate.Click += async (_, _) => await CheckForUpdates(manual: true);
        MiAutoUpdate.IsChecked = Updates.Store.Load().AutoCheck;
        MiAutoUpdate.Click += (_, _) =>
        {
            bool on = !Updates.Store.Load().AutoCheck;
            Updates.SetAutoCheck(on);
            MiAutoUpdate.IsChecked = on;
            Log(LogLevel.Info, "업데이트", on ? "시작할 때 업데이트 자동 확인: 켬 (하루 한 번)" : "시작할 때 업데이트 자동 확인: 끔");
        };
        MiAbout.Click += async (_, _) => await AboutWindow().ShowDialog(this);
        Log(LogLevel.Info, "앱", $"Open M&S {VersionText}");
        if (_smoke) return;
        Opened += async (_, _) =>
        {
            await Task.Delay(AutoCheckDelay);
            if (Updates.ShouldAutoCheck(DateTime.UtcNow)) await CheckForUpdates(manual: false);
        };
    }

    /// <summary>확인 → 새 버전이면 대화상자. 자동 확인은 새 버전일 때만 화면에 나오고, 나머지(최신 · 실패 · 건너뜀)는 로그만.</summary>
    private async Task CheckForUpdates(bool manual)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            var r = await Updates.CheckAsync(manual);
            if (r.Status == UpdateStatus.Available && r.Release is { } rel) await UpdateWindow(rel).ShowDialog(this);
            else if (manual)
                await MessageWindow("업데이트 확인", r.Status switch
                {
                    UpdateStatus.UpToDate => "최신 버전입니다.",
                    UpdateStatus.DevBuild => "개발 빌드입니다.",
                    UpdateStatus.NoInstaller => "새 버전에 설치 프로그램이 없습니다.",
                    _ => "업데이트를 확인하지 못했습니다.",
                }, $"현재 버전 {VersionText}\n\n{r.Message}").ShowDialog(this);
        }
        finally { _updateBusy = false; }
    }

    /// <summary>새 버전 대화상자. <paramref name="preview"/> 가 있으면(스모크 캡처) 내려받지 않고 그 진행률 상태로 그린다.</summary>
    private Window UpdateWindow(ReleaseInfo rel, double? preview = null)
    {
        var versions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,16,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        void Row(int row, string label, string value, bool strong = false)
        {
            var l = new TextBlock { Text = label, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
            var v = new SelectableTextBlock { Text = value, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, row); Grid.SetRow(v, row); Grid.SetColumn(v, 2);
            versions.Children.Add(l); versions.Children.Add(v);
        }
        Row(0, "현재 버전", VersionText);
        Row(1, "새 버전", $"{rel.Version.ToString(3)}  ({rel.Tag}{(rel.Name != rel.Tag ? " — " + rel.Name : "")})", strong: true);
        Row(2, "설치 프로그램", $"{rel.InstallerName}{(rel.InstallerSize > 0 ? $" · {rel.InstallerSize / 1048576.0:F1} MB" : "")} · {(rel.Sha256Url is null ? "해시 검증 자산 없음" : "SHA-256 검증")}");
        foreach (var c in versions.Children) c.Margin = new Thickness(0, 2);

        var notes = new SelectableTextBlock { Text = UpdateChecker.NotesPreview(rel.Notes), TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        var notesCard = new Border { Classes = { "card" }, Padding = new Thickness(14), Child = new ScrollViewer { Content = notes, MaxHeight = 240 } };
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, IsVisible = preview is not null, Value = preview ?? 0 };
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, IsVisible = preview is not null,
            Text = preview is { } p ? $"내려받는 중… {p:P0}" : "" };

        var now = new Button { Content = "지금 업데이트", Classes = { "primary" }, IsDefault = true, MinWidth = 120 };
        var later = new Button { Content = "나중에", IsCancel = true, MinWidth = 90 };
        var skip = new Button { Content = "이 버전 건너뛰기", Classes = { "ghost" } };
        AutomationProperties.SetAutomationId(now, "Update.Now");
        AutomationProperties.SetAutomationId(later, "Update.Later");
        AutomationProperties.SetAutomationId(skip, "Update.Skip");
        foreach (var b in new[] { now, later, skip }) b.HorizontalContentAlignment = HorizontalAlignment.Center;
        var buttons = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(skip, Dock.Left);
        buttons.Children.Add(skip);
        buttons.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { later, now } });

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "새 버전의 Open M&S 가 있습니다. 지금 업데이트하면 내려받아 확인한 뒤 앱을 닫고 설치하고, 설치가 끝나면 다시 실행합니다.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(versions);
        body.Children.Add(new TextBlock { Text = "릴리스 노트", Classes = { "caption" } });
        body.Children.Add(notesCard);
        body.Children.Add(progress);
        body.Children.Add(status);
        body.Children.Add(buttons);
        var win = DialogWindow($"업데이트 — Open M&S {rel.Version.ToString(3)}", HelpKeys.Update, body, 640);

        CancellationTokenSource? cts = null;
        later.Click += (_, _) => win.Close();
        skip.Click += (_, _) =>
        {
            Updates.Skip(rel);
            Log(LogLevel.Info, "업데이트", $"버전 {rel.Version.ToString(3)} 건너뛰기 — 자동 확인에서 다시 묻지 않습니다(도움말 → 업데이트 확인은 계속 보임).");
            win.Close();
        };
        win.Closed += (_, _) => cts?.Cancel();
        now.Click += async (_, _) =>
        {
            now.IsEnabled = skip.IsEnabled = false;
            progress.IsVisible = status.IsVisible = true;
            progress.Value = 0;
            status.Text = "내려받는 중…";
            cts = new CancellationTokenSource();
            try
            {
                var prog = new Progress<double>(f => { progress.Value = f; status.Text = $"내려받는 중… {f:P0}"; });
                await Updates.DownloadAndInstallAsync(rel, prog, cts.Token);
                status.Text = "확인 완료 — 설치 프로그램을 실행했습니다. 앱을 닫습니다(설치가 끝나면 다시 실행됩니다).";
                Log(LogLevel.Info, "업데이트", $"{rel.Version.ToString(3)} 설치 프로그램 실행 — 앱 종료");
                await Task.Delay(800);
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
                else Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                status.Text = ex is UpdateVerificationException ? ex.Message : $"업데이트 실패: {ex.Message}";
                Log(LogLevel.Warn, "업데이트", status.Text);
                now.Content = "다시 시도";
                now.IsEnabled = skip.IsEnabled = true;
            }
        };
        return win;
    }

    /// <summary>짧은 알림 창(제목 · 굵은 한 줄 · 설명 · 닫기).</summary>
    private Window MessageWindow(string title, string headline, string detail)
    {
        var close = new Button { Content = "닫기", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = headline, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new SelectableTextBlock { Text = detail, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(close);
        var win = DialogWindow(title, HelpKeys.Update, body, 480);
        close.Click += (_, _) => win.Close();
        return win;
    }

    /// <summary>도움말 → 정보: 앱 이름 · 버전 · 런타임 · 업데이트 저장소 · 설정 파일.</summary>
    private Window AboutWindow()
    {
        var info = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var s = Updates.Store.Load();
        var commit = info.Contains('+') ? info[(info.IndexOf('+') + 1)..] : "";
        var detail = string.Join("\n",
            $"버전 {VersionText}" + (commit.Length > 0 ? $"  (커밋 {commit[..Math.Min(10, commit.Length)]})" : ""),
            $".NET {System.Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
            $"설치 위치 {ShownPath(AppContext.BaseDirectory)}",
            "",
            $"업데이트: github.com/{UpdateChecker.Repository} 릴리스",
            $"자동 확인 {(s.AutoCheck ? "켬 (하루 한 번)" : "끔")}" + (s.LastCheckUtc is { } lc ? $" · 마지막 확인 {lc.ToLocalTime():yyyy-MM-dd HH:mm}" : "")
                + (s.SkippedVersion is { } sv ? $" · 건너뛴 버전 {sv}" : ""),
            $"설정 파일 {ShownPath(Updates.Store.FilePath)}");
        return MessageWindow("정보 — Open M&S", $"Open M&S {VersionText}", "공중투하 6DOF M&S 시뮬레이터\n\n" + detail);
    }

    /// <summary>스모크 --updateshot: 새 버전 대화상자(기본 · 내려받는 중) · 최신 알림 · 정보 창을 results/smoke/update_*.png 로.</summary>
    private async Task UpdateShots(System.Text.StringBuilder log, string dir)
    {
        static async Task Shot(Window w, string path)
        {
            await Task.Delay(700);
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height));
            rtb.Render(w);
            rtb.Save(path);
        }
        Directory.CreateDirectory(dir);
        var cur = UpdateChecker.IsDevBuild(UpdateChecker.Current) ? new Version(0, 9, 0) : UpdateChecker.Current;
        var next = new Version(cur.Major, cur.Minor, cur.Build + 1);
        var rel = new ReleaseInfo(next, $"v{next.ToString(3)}", $"Open M&S {next.ToString(3)}",
            "## 바뀐 점\n- 업데이트 확인 · 설치 프로그램 자동 실행\n- 3D 뷰 장면 환경 개선\n- 개방 충격 응답 계산 속도 향상\n\n## 고친 점\n- 결과 불러오기에서 FEA 색이 사라지던 문제",
            "", UpdateChecker.InstallerFileName(next), "http://127.0.0.1/none", 86_000_000, "http://127.0.0.1/none.sha256");
        foreach (var (name, w) in new (string, Window)[]
        {
            ("update_dialog", UpdateWindow(rel)),
            ("update_dialog_progress", UpdateWindow(rel, preview: 0.42)),
            ("update_latest", MessageWindow("업데이트 확인", "최신 버전입니다.", $"현재 버전 {VersionText}\n\n최신 버전입니다 (현재 {cur.ToString(3)} · 최신 릴리스 {cur.ToString(3)}).")),
            ("update_about", AboutWindow()),
        })
        {
            w.Show(this);
            await Shot(w, Path.Combine(dir, name + ".png"));
            log.AppendLine(string.Create(Inv, $"ui.update {name} size={w.Bounds.Width:F0}x{w.Bounds.Height:F0}"));
            w.Close();
        }
        log.AppendLine($"ui.update version={VersionText} api={UpdateChecker.LatestUrl(UpdateChecker.ApiBase)} autoCheck={Updates.Store.Load().AutoCheck}");
    }
}
