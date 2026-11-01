using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sim.App.Controls;
using Sim.App.Help;
using Sim.App.Rendering;
using Sim.App.Theme;
using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Scenario;
using Sim.Core.Structures;
using Sim.Gpu;

namespace Sim.App;

public partial class MainWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string AutoNose = "자동(형상 제안)";
    private const int MaxLog = 5000;
    private const int MaxEngineLogLines = 400;

    private readonly string _repo;
    private readonly List<string> _scenarioFiles = new();
    private ScenarioDoc? _doc;
    private World? _world;
    private JsonObject? _summary;
    private double _time;
    private bool _playing;
    private double _rate = 1;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;
    private bool _suppressSlider;
    private GpuDropBackend? _gpu;
    private string _gpuMsg = "";
    private int _selected = -1;
    private readonly bool _smoke;
    private bool _continuousRender;

    // 작업(진행·단계·취소)
    private sealed class Job(string name, bool cancellable)
    {
        public string Name { get; } = name;
        public bool Cancellable { get; } = cancellable;
        public CancellationTokenSource Cts { get; } = new();
        public Stopwatch Sw { get; } = Stopwatch.StartNew();
    }
    private Job? _job;

    // 로그
    private readonly List<LogItem> _logAll = new();
    private readonly ObservableCollection<LogItem> _logView = new();
    private int _warnCount, _errCount;
    private bool _scrollPending;
    private StreamWriter? _logWriter;

    // 어셈블리 트리
    private AsmItem? _treeRoot;
    private AsmItem?[] _bodyItems = [];
    private bool _syncTree;

    // 보기 메뉴 · 패널
    private enum Pane { Input, Tree, Telemetry, Plot, Log }
    private static readonly (Pane Pane, string Label)[] Panes =
    [
        (Pane.Input, "입력 패널 (시나리오 · 조건 · JSON)"),
        (Pane.Tree, "브라우저 표시 (어셈블리 · 3D 위)"),
        (Pane.Telemetry, "텔레메트리 대시보드 · 요약 · 이벤트"),
        (Pane.Plot, "그래프 섹션"),
        (Pane.Log, "로그 섹션"),
    ];
    private readonly bool[] _paneOn = [true, true, true, true, true];
    private readonly Dictionary<ViewLayer, MenuItem> _layerItems = new();
    private readonly Dictionary<Pane, MenuItem> _paneItems = new();
    private readonly Dictionary<AppTheme, MenuItem> _themeItems = new();
    private readonly Dictionary<object, GridLength> _savedSizes = new();
    private double _hudDist = double.NaN;

    public MainWindow()
    {
        InitializeComponent();
        _repo = FindRepo();
        _smoke = Program.Args.Contains("--smoke");
        _continuousRender = _smoke;
        if (_smoke) s_maskRepo = _repo;
        // 기본 크기(1680×980)가 작업 영역 아래로 나가 상태 표시줄이 작업 표시줄에 가려지지 않게(보조 창과 같은 규칙)
        if (!_smoke) this.KeepOnScreen();
        OpenLogFile();

        TurbBox.ItemsSource = new[] { "none", "light", "moderate", "severe" };
        // 0.001× ~ 0.02× = 충격 슬로모션(1 ms 충격을 1 s 에 걸쳐 보기 — 충격 가속도·응력 색이 퍼지고 줄어드는 모습)
        RateBox.ItemsSource = new[] { "0.001×", "0.005×", "0.02×", "0.1×", "0.25×", "0.5×", "1×", "2×", "5×", "10×", "20×", "50×" };
        RateBox.SelectedIndex = 6;
        PlotSel.ItemsSource = new[] { "고도·속도", "자세(롤·피치·요)", "하중(G)·대기속도", "MC 착탄 산포" };
        PlotSel.SelectedIndex = 0;
        NoseAxisBox.ItemsSource = new[] { AutoNose }.Concat(NoseGeometry.AxisNames).ToList();
        NoseAxisBox.SelectedIndex = 0;
        LogLevelBox.ItemsSource = new[] { "전체", "경고 · 오류", "오류만" };
        LogLevelBox.SelectedIndex = 0;
        LogList.ItemsSource = _logView;

        var dir = Path.Combine(_repo, "config", "scenarios");
        if (Directory.Exists(dir)) _scenarioFiles.AddRange(Directory.GetFiles(dir, "*.json").OrderBy(f => f));
        ScenarioBox.ItemsSource = _scenarioFiles.Select(Path.GetFileNameWithoutExtension).ToList();

        WireInputs();
        WireMenu();
        WireView();
        WireTreeAndLists();
        WireAnalysis();
        BuildDashboard();
        BuildViewMenu();
        WireSplit();
        WireTheme();
        WireShell();
        WireUpdate();
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => View.StopFlying();
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log(LogLevel.Error, "UI", e.Exception.Message);
            _logWriter?.WriteLine(e.Exception.ToString());
            e.Handled = true;
        };
        // UI 스레드 밖 예외로 프로세스가 끝나도 로그에 남긴다(조용히 사라진 경우 정상 종료 · 외부 종료와 구분)
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { _logWriter?.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  치명  [앱]  처리 안 된 예외로 종료: {e.ExceptionObject}"); _logWriter?.Flush(); } catch { }
        };
        Closed += (_, _) =>
        {
            try { _logWriter?.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  정보  [앱]  창 닫힘 — 정상 종료"); } catch { }
            _job?.Cts.Cancel(); _logWriter?.Dispose(); _logWriter = null;
            ThemeService.Changed -= OnThemeChanged;
            HelpTip.GuideRequested -= OnGuideRequested;
        };

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();

        Log(LogLevel.Info, "앱", $"저장소 {ShownPath(_repo)} · 시나리오 {_scenarioFiles.Count}개");
        if (_scenarioFiles.Count > 0) ScenarioBox.SelectedIndex = 0;
        SyncChecks();
        UpdateHud();
        Opened += async (_, _) =>
        {
            if (_smoke) await SmokeTest();
            else await RunSimulation();
        };
    }

    private static string? s_maskRepo;

    /// <summary>
    /// 화면에 보일 경로. 스모크(문서 · README 촬영)에서는 저장소 안 경로를 저장소 상대로, 사용자 폴더를 %LOCALAPPDATA% · %APPDATA% · %USERPROFILE% 로 바꿔
    /// 개발 PC 의 절대 경로 · 사용자 이름이 그림에 남지 않게 한다. 대화형 실행에서는 그대로.
    /// </summary>
    internal static string ShownPath(string path)
    {
        if (s_maskRepo is null || string.IsNullOrEmpty(path)) return path;
        string full;
        try { full = Path.GetFullPath(path); } catch { return path; }
        static bool Under(string p, string root, out string rest)
        {
            root = root.TrimEnd('\\', '/');
            rest = "";
            if (root.Length == 0 || !p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            if (p.Length == root.Length) return true;
            if (p[root.Length] is not ('\\' or '/')) return false;
            rest = p[(root.Length + 1)..];
            return true;
        }
        if (Under(full, s_maskRepo, out var r)) return r.Length == 0 ? "." : r;
        foreach (var v in new[] { "LOCALAPPDATA", "APPDATA", "TEMP", "USERPROFILE" })
            if (System.Environment.GetEnvironmentVariable(v) is { Length: > 0 } root && Under(full, root, out var rr))
                return rr.Length == 0 ? $"%{v}%" : $"%{v}%\\{rr}";
        return path;
    }

    private static string FindRepo()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var d = new DirectoryInfo(start);
            while (d != null)
            {
                if (Directory.Exists(Path.Combine(d.FullName, "config", "scenarios"))) return d.FullName;
                d = d.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    // ================= 연결 =================

    private void WireInputs()
    {
        ScenarioBox.SelectionChanged += (_, _) => { if (ScenarioBox.SelectedIndex >= 0) LoadScenario(_scenarioFiles[ScenarioBox.SelectedIndex]); };
        OpenBtn.Click += async (_, _) => await OpenScenario();
        ImportBtn.Click += async (_, _) => await ImportModel();
        ScenarioInfoBtn.Click += async (_, _) => await ShowScenarioInfo();
        HelpBtn.Click += async (_, _) => await ShowHelp();
        RunBtn.Click += async (_, _) => await RunSimulation();
        SaveBtn.Click += async (_, _) => await SaveResults();
        GpuMcBtn.Click += async (_, _) => await RunGpuMc();
        CpuMcBtn.Click += async (_, _) => await RunCpuMc();
        ApplyJsonBtn.Click += (_, _) => ApplyJson();
        CancelBtn.Click += (_, _) => CancelJob();
        WarnBadge.Click += (_, _) => ShowLog(1);
        ErrBadge.Click += (_, _) => ShowLog(2);
        PlayBtn.Click += (_, _) => TogglePlay();
        PrevEventBtn.Click += (_, _) => JumpEvent(-1);
        NextEventBtn.Click += (_, _) => JumpEvent(+1);
        RateBox.SelectionChanged += (_, _) => _rate = double.Parse(((string)RateBox.SelectedItem!).TrimEnd('×'), Inv);
        PlotSel.SelectionChanged += (_, _) => UpdatePlot();
        Plot.TimeSeek += Seek;
        TimeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_suppressSlider) { _time = TimeSlider.Value; Refresh(); }
        };
        LogLevelBox.SelectionChanged += (_, _) => RebuildLogView();
        LogSearch.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) RebuildLogView(); };
        LogClearBtn.Click += (_, _) =>
        {
            _logAll.Clear(); _logView.Clear(); _warnCount = _errCount = 0; UpdateBadges();
        };
        LogCopyBtn.Click += async (_, _) =>
        {
            if (Clipboard is { } cb) await cb.SetTextAsync(string.Join(System.Environment.NewLine, _logView.Select(l => l.Text)));
        };
        LogSaveBtn.Click += async (_, _) => await SaveLog();
    }

    private void WireMenu()
    {
        MiOpen.Click += async (_, _) => await OpenScenario();
        MiImport.Click += async (_, _) => await ImportModel();
        MiSave.Click += async (_, _) => await SaveResults();
        MiLoadResult.Click += async (_, _) => await PickAndLoadResult();
        MiExport.Click += async (_, _) => await ExportResultsFolder();
        MiShot.Click += async (_, _) => await SaveViewShot();
        MiSaveLog.Click += async (_, _) => await SaveLog();
        MiExit.Click += (_, _) => Close();
        MiRun.Click += async (_, _) => await RunSimulation();
        MiCancel.Click += (_, _) => CancelJob();
        MiGpuMc.Click += async (_, _) => await RunGpuMc();
        MiCpuMc.Click += async (_, _) => await RunCpuMc();
        MiApplyJson.Click += (_, _) => ApplyJson();
        MiFollow.Click += (_, _) => SetFollow(!View.Follow);
        MiFocus.Click += (_, _) => FocusSelected();
        MiFit.Click += (_, _) => View.FitScene();
        MiResetCam.Click += (_, _) => ResetCamera();
        MiZoomIn.Click += (_, _) => View.Zoom(1 / 1.25);
        MiZoomOut.Click += (_, _) => View.Zoom(1.25);
        MiTop.Click += (_, _) => View.SetPreset(ViewPreset.Top);
        MiNorth.Click += (_, _) => View.SetPreset(ViewPreset.North);
        MiEast.Click += (_, _) => View.SetPreset(ViewPreset.East);
        MiIso.Click += (_, _) => View.SetPreset(ViewPreset.Iso);
        MiPlay.Click += (_, _) => TogglePlay();
        MiRewind.Click += (_, _) => Seek(0);
        MiStepBack.Click += (_, _) => StepFrame(-1);
        MiStepFwd.Click += (_, _) => StepFrame(+1);
        MiPrevEvent.Click += (_, _) => JumpEvent(-1);
        MiNextEvent.Click += (_, _) => JumpEvent(+1);
        MiHelp.Click += async (_, _) => await ShowHelp();
        MiScenarioInfo.Click += async (_, _) => await ShowScenarioInfo();
        MiCancel.IsEnabled = false;
    }

    private void WireView()
    {
        View.Info += s => Dispatcher.UIThread.Post(() =>
        {
            GlText.Text = s;
            ToolTip.SetTip(GlText, s);
            Log(LogLevel.Info, "OpenGL", s);
        });
        View.BodyPicked += i => SelectBody(i);
        View.CameraChanged += SyncChecks;
        View.Layers.Changed += () =>
        {
            Overlay.IsVisible = View.Layers[ViewLayer.Hud];
            for (int p = 0; p < _paneLegends.Length; p++)
                _paneLegends[p].IsVisible = View.Layers[ViewLayer.Legend] && p < View.PaneCount && PaneMode(p) != ColorMode.Off;
            SyncChecks();
            Redraw();
        };
        View.SizeChanged += (_, _) => View.RequestNextFrameRendering();
        FollowToggle.Click += (_, _) => SetFollow(FollowToggle.IsChecked == true);
        ZoomInBtn.Click += (_, _) => View.Zoom(1 / 1.25);
        ZoomOutBtn.Click += (_, _) => View.Zoom(1.25);
        FocusBtn.Click += (_, _) => FocusSelected();
        FitBtn.Click += (_, _) => View.FitScene();
        ResetCamBtn.Click += (_, _) => ResetCamera();
    }

    private void WireTreeAndLists()
    {
        AsmTree.SelectionChanged += (_, _) =>
        {
            if (_syncTree) return;
            if (AsmTree.SelectedItem is AsmItem { IsBody: true } it) SelectBody(it.BodyIndex, fromTree: true);
        };
        AsmTree.DoubleTapped += (_, _) => { if (AsmTree.SelectedItem is AsmItem { IsBody: true }) FocusSelected(); };
        TreeExpandBtn.Click += (_, _) => { foreach (var it in AllTreeItems()) it.IsExpanded = true; };
        TreeCollapseBtn.Click += (_, _) => { foreach (var it in AllTreeItems()) it.IsExpanded = it == _treeRoot || it.Children.Any(c => c.IsBody); };
        TreeShowAllBtn.Click += (_, _) => ShowAllBodies();
        WireBrowser();

        void SeekSelectedEvent() { if (EventsList.SelectedItem is EventItem ev) Seek(ev.Time); }
        EventsList.SelectionChanged += (_, _) => SeekSelectedEvent();
        EventsList.Tapped += (_, _) => SeekSelectedEvent();
    }

    private IEnumerable<AsmItem> AllTreeItems() => _treeRoot?.SelfAndDescendants() ?? [];

    // ================= 로그 =================

    private void OpenLogFile()
    {
        try
        {
            var dir = Path.Combine(_repo, "results", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"ui_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _logWriter = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
            LogFileText.Text = $"파일: results/logs/{Path.GetFileName(path)}";
        }
        catch (Exception ex) { LogFileText.Text = $"로그 파일 없음 ({ex.Message})"; }
    }

    /// <summary>로그 한 줄 추가(UI 스레드). 작업이 없으면 상태 표시줄에도 표시.</summary>
    private void Log(LogLevel level, string source, string message)
    {
        var item = new LogItem { Time = DateTime.Now, Level = level, Source = source, Message = message };
        _logAll.Add(item);
        _logWriter?.WriteLine(item.Text);
        if (level == LogLevel.Warn) _warnCount++;
        if (level == LogLevel.Error) _errCount++;
        UpdateBadges();
        if (_logAll.Count > MaxLog + 500)
        {
            _logAll.RemoveRange(0, _logAll.Count - MaxLog);
            RebuildLogView();
        }
        else if (LogMatches(item))
        {
            _logView.Add(item);
            ScheduleLogScroll();
        }
        if (_job is null)
        {
            JobText.Text = message;
            SetJobIcon(level switch { LogLevel.Error => "err", LogLevel.Warn => "warn", _ => "ok" });
        }
    }

    /// <summary>기존 호출부 호환: 정보 로그.</summary>
    private void Status(string s) => Log(LogLevel.Info, "앱", s);

    /// <summary>상태 표시줄 점 색 — 클래스(ok · warn · err · info) → 토큰.</summary>
    private void SetJobIcon(string cls)
    {
        foreach (var c in new[] { "ok", "warn", "err", "info" }) JobIcon.Classes.Set(c, c == cls);
    }

    // ================= 테마 =================

    private void WireTheme()
    {
        ThemeBtn.Click += (_, _) => ThemeService.Toggle();
        ThemeService.Changed += OnThemeChanged;
        HelpTip.GuideRequested += OnGuideRequested;
        OnThemeChanged(ThemeService.Current);
    }

    /// <summary>테마가 바뀜 — 토큰 밖에서 그리는 것(3D GL 팔레트 · 그래프)만 다시 칠하고 메뉴 · 단추 표시를 맞춘다.</summary>
    private void OnThemeChanged(AppTheme theme)
    {
        View.Palette = ThemeService.ViewPalette();
        View.RequestNextFrameRendering();
        ThemeIcon.Kind = theme == AppTheme.Dark ? Material.Icons.MaterialIconKind.WeatherSunny : Material.Icons.MaterialIconKind.WeatherNight;
        ToolTip.SetTip(ThemeBtn, theme == AppTheme.Dark ? "라이트 테마로 (지금: 다크) — 다음 실행에도 유지" : "다크 테마로 (지금: 라이트) — 다음 실행에도 유지");
        foreach (var (t, mi) in _themeItems) mi.IsChecked = t == theme;
        Plot.InvalidateTheme();
    }

    private void OnGuideRequested(string guideKey) => _ = ShowHelp(guideKey);

    private bool LogMatches(LogItem it)
    {
        int lvl = LogLevelBox.SelectedIndex;
        if (lvl == 1 && it.Level == LogLevel.Info) return false;
        if (lvl == 2 && it.Level != LogLevel.Error) return false;
        var q = LogSearch.Text;
        return string.IsNullOrWhiteSpace(q)
            || it.Message.Contains(q, StringComparison.OrdinalIgnoreCase)
            || it.Source.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildLogView()
    {
        _logView.Clear();
        foreach (var it in _logAll) if (LogMatches(it)) _logView.Add(it);
        ScheduleLogScroll();
    }

    private void ScheduleLogScroll()
    {
        if (_scrollPending || LogAutoScroll.IsChecked != true) return;
        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            if (_logView.Count > 0) LogList.ScrollIntoView(_logView[^1]);
        }, DispatcherPriority.Background);
    }

    private void UpdateBadges()
    {
        WarnCount.Text = _warnCount.ToString(Inv);
        ErrCount.Text = _errCount.ToString(Inv);
        Avalonia.Automation.AutomationProperties.SetName(WarnBadge, $"경고 {_warnCount}");
        Avalonia.Automation.AutomationProperties.SetName(ErrBadge, $"오류 {_errCount}");
        WarnBadge.Opacity = _warnCount > 0 ? 1 : 0.45;
        ErrBadge.Opacity = _errCount > 0 ? 1 : 0.45;
    }

    /// <summary>로그 탭 열기(필터: 0 전체 · 1 경고 이상 · 2 오류만).</summary>
    private void ShowLog(int level = -1)
    {
        if (!_paneOn[(int)Pane.Log]) { _paneOn[(int)Pane.Log] = true; ApplyPanes(); SyncChecks(); }
        if (level >= 0) LogLevelBox.SelectedIndex = level;
    }

    private async Task SaveLog()
    {
        var f = await PickSaveAsync(new FilePickerSaveOptions
        {
            Title = "로그 저장", SuggestedFileName = $"mns_log_{DateTime.Now:yyyyMMdd_HHmmss}.log", DefaultExtension = "log",
        });
        if (f?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllLinesAsync(path, _logAll.Select(l => l.Text));
        Log(LogLevel.Info, "앱", $"로그 저장: {path} ({_logAll.Count}줄)");
    }

    // ================= 작업(진행 · 단계 · 취소) =================

    private Job? BeginJob(string name, bool cancellable)
    {
        if (_job is not null)
        {
            Log(LogLevel.Warn, "작업", $"'{_job.Name}' 실행 중 — 끝나거나 취소(Esc)한 뒤 다시 시도하세요.");
            return null;
        }
        var job = new Job(name, cancellable);
        _job = job;
        Log(LogLevel.Info, name, "시작");
        SetJobIcon("info");
        JobText.Text = name;
        JobProgress.IsVisible = true;
        JobProgress.IsIndeterminate = true;
        CancelBtn.IsVisible = cancellable;
        CancelBtn.IsEnabled = cancellable;
        BusyOverlay.IsVisible = true;
        BusyTitle.Text = name + (cancellable ? "   (Esc 취소)" : "");
        BusyStage.Text = "";
        BusyProgress.IsIndeterminate = true;
        SetBusy(true, cancellable);
        return job;
    }

    /// <summary>처리 단계와 진행률(null = 불확정) 표시.</summary>
    private void Stage(Job job, string stage, double? fraction)
    {
        if (!ReferenceEquals(_job, job)) return;
        JobText.Text = $"{job.Name} — {stage}";
        BusyStage.Text = stage;
        foreach (var pb in new[] { JobProgress, BusyProgress })
        {
            pb.IsIndeterminate = fraction is null;
            if (fraction is { } f) pb.Value = Math.Clamp(f, 0, 1);
        }
    }

    private void EndJob(Job job, LogLevel level, string message)
    {
        if (!ReferenceEquals(_job, job)) return;
        _job = null;
        job.Sw.Stop();
        JobProgress.IsVisible = false;
        CancelBtn.IsVisible = false;
        BusyOverlay.IsVisible = false;
        JobElapsed.Text = $"{job.Sw.Elapsed.TotalSeconds:F1} s";
        SetBusy(false, false);
        Log(level, job.Name, message);
        if (level == LogLevel.Error) ShowLog();
    }

    private void CancelJob()
    {
        if (_job is not { Cancellable: true } job) return;
        job.Cts.Cancel();
        CancelBtn.IsEnabled = false;
        Stage(job, "취소 요청 — 현재 단계 마무리 중…", null);
        Log(LogLevel.Warn, job.Name, "취소 요청");
    }

    private void SetBusy(bool busy, bool cancellable)
    {
        foreach (var c in new Control[] { RunBtn, GpuMcBtn, CpuMcBtn, ImportBtn, OpenBtn, ApplyJsonBtn, ScenarioBox,
                     MiRun, MiGpuMc, MiCpuMc, MiImport, MiOpen, MiApplyJson, MiLoadResult, MiStructural, MiFea, MiLoadCases,
                     TbOpen, TbLoad, TbRun, TbStruct })
            c.IsEnabled = !busy;
        MiCancel.IsEnabled = busy && cancellable;
    }

    // ================= 보기 메뉴 · 패널 =================

    private void BuildViewMenu()
    {
        ViewMenu.Items.Add(new MenuItem { Header = "3D 표시 레이어", IsEnabled = false });
        string? group = null;
        foreach (var (layer, label, g) in ViewLayers.All)
        {
            if (group is not null && g != group) ViewMenu.Items.Add(new Separator());
            group = g;
            var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.CheckBox, IsChecked = View.Layers[layer] };
            mi.Click += (_, _) => View.Layers[layer] = !View.Layers[layer];
            _layerItems[layer] = mi;
            ViewMenu.Items.Add(mi);
        }
        ViewMenu.Items.Add(new Separator());
        ViewMenu.Items.Add(new MenuItem { Header = "패널", IsEnabled = false });
        foreach (var (pane, label) in Panes)
        {
            var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
            if (pane == Pane.Tree) mi.InputGesture = BrowserGesture;
            mi.Click += (_, _) => { _paneOn[(int)pane] = !_paneOn[(int)pane]; ApplyPanes(); SyncChecks(); };
            _paneItems[pane] = mi;
            ViewMenu.Items.Add(mi);
        }
        ViewMenu.Items.Add(new Separator());
        ViewMenu.Items.Add(BuildColorMenu());
        ViewMenu.Items.Add(new Separator());
        // 테마 — 다크 · 라이트(도구 막대 해/달 단추와 같음, 다음 실행에도 유지)
        var themeMenu = new MenuItem { Header = "테마" };
        foreach (var (theme, label) in new[] { (AppTheme.Dark, "다크"), (AppTheme.Light, "라이트") })
        {
            var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, GroupName = "theme", IsChecked = ThemeService.Current == theme };
            mi.Click += (_, _) => ThemeService.Apply(theme);
            _themeItems[theme] = mi;
            themeMenu.Items.Add(mi);
        }
        ViewMenu.Items.Add(themeMenu);
        ViewMenu.Items.Add(BuildSceneMenu());
        ViewMenu.Items.Add(new Separator());
        var def =new MenuItem { Header = "기본 — 모두 표시 (레이어 · 패널 · 숨긴 물체)", InputGesture = new KeyGesture(Key.D0, KeyModifiers.Control) };
        def.Click += (_, _) => ResetViewDefaults();
        ViewMenu.Items.Add(def);
    }

    private readonly List<(MenuItem Item, Func<SceneSettings, bool> IsOn)> _sceneItems = new();
    private readonly MenuItem _sceneMenu = new() { Header = "장면 환경" };

    /// <summary>
    /// 보기 → 장면 환경. 환경 목록 = config/environments(내장, environment.json) + 불러온 사용자 환경. 장면 설정(Fusion 장면 설정 항목:
    /// 밝기 · 노출 · 회전 · 배경 · 지면 표시 · 평탄화 · 반사 · 거칠기 · 높이 오프셋 · 그림자 · 안개)은 사용자 설정 파일에 저장 — 다음 실행에도 유지.
    /// </summary>
    private MenuItem BuildSceneMenu()
    {
        EnvironmentLoader.ImageDecoder = DecodeImage;
        var reg = EnvironmentRegistry.FromDirectory(Path.Combine(_repo, "config", "environments"));
        foreach (var e in reg.Errors) Log(LogLevel.Warn, "환경", e);
        View.Environments = reg;
        if (!_smoke) View.SceneEnv = SceneSettings.Load(ThemeStore.DefaultPath) ?? new SceneSettings();
        foreach (var path in View.SceneEnv.UserEnvironments)
        {
            try { reg.Add(EnvironmentLoader.Load(path)); }
            catch (Exception ex) { Log(LogLevel.Warn, "환경", $"사용자 환경을 다시 불러오지 못함 — {ex.Message}"); }
        }
        FillSceneMenu();
        return _sceneMenu;
    }

    private void FillSceneMenu()
    {
        var root = _sceneMenu;
        root.Items.Clear();
        _sceneItems.Clear();
        void Add(MenuItem parent, string header, MenuItemToggleType type, Func<SceneSettings, bool> isOn, Func<SceneSettings, SceneSettings> apply, string? group = null)
        {
            var mi = new MenuItem { Header = header, ToggleType = type, GroupName = group };
            mi.Click += (_, _) => SetSceneEnv(apply(View.SceneEnv));
            _sceneItems.Add((mi, isOn));
            parent.Items.Add(mi);
        }
        const MenuItemToggleType radio = MenuItemToggleType.Radio, check = MenuItemToggleType.CheckBox;
        var reg = View.Environments;
        var analysis = reg.Find("analysis") ?? EnvironmentDef.Analysis;
        Add(root, analysis.DisplayName, radio, s => s.EnvironmentId == analysis.Id, s => s with { EnvironmentId = analysis.Id }, "envId");
        Add(root, "자동 (시나리오 토양)", radio, s => s.EnvironmentId == EnvironmentRegistry.Auto, s => s with { EnvironmentId = EnvironmentRegistry.Auto }, "envId");
        foreach (var env in reg.All.Where(e => !e.IsAnalysis))
            Add(root, env.DisplayName, radio, s => s.EnvironmentId == env.Id, s => s with { EnvironmentId = env.Id }, "envId");
        var load = new MenuItem { Header = "사용자 환경 불러오기… (environment.json · .hdr · .png)" };
        load.Click += async (_, _) => await LoadUserEnvironment();
        root.Items.Add(load);
        root.Items.Add(new Separator());
        var bright = new MenuItem { Header = "밝기" };
        foreach (var b in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
            Add(bright, string.Create(Inv, $"{b * 100:0} %"), radio, s => Math.Abs(s.Brightness - b) < 1e-6, s => s with { Brightness = b }, "envBright");
        root.Items.Add(bright);
        var ev = new MenuItem { Header = "카메라 노출 (EV)" };
        foreach (var e in new[] { -2.0, -1.0, 0.0, 1.0, 2.0 })
            Add(ev, string.Create(Inv, $"{e:+0;-0;0} EV"), radio, s => Math.Abs(s.ExposureEV - e) < 1e-6, s => s with { ExposureEV = e }, "envEv");
        root.Items.Add(ev);
        var rot = new MenuItem { Header = "환경 회전 (태양 방위)" };
        for (int d = 0; d < 360; d += 45)
        {
            int deg = d;
            Add(rot, $"{deg}°", radio, s => Math.Abs(s.RotationDeg - deg) < 1e-6, s => s with { RotationDeg = deg }, "envRot");
        }
        root.Items.Add(rot);
        var bg = new MenuItem { Header = "배경" };
        Add(bg, "환경 (하늘)", radio, s => s.SkyBackground, s => s with { BackgroundType = "environment" }, "envBg");
        Add(bg, "단색 (테마 배경)", radio, s => !s.SkyBackground, s => s with { BackgroundType = "solid" }, "envBg");
        root.Items.Add(bg);
        root.Items.Add(new Separator());
        Add(root, "지면 표시", check, s => s.GroundDisplayed, s => s with { GroundDisplayed = !s.GroundDisplayed });
        Add(root, "지면 평탄화 (지평선까지)", check, s => s.GroundFlattened, s => s with { GroundFlattened = !s.GroundFlattened });
        Add(root, "지면 반사", check, s => s.GroundReflections, s => s with { GroundReflections = !s.GroundReflections });
        var rough = new MenuItem { Header = "지면 반사 거칠기" };
        foreach (var (r, label) in new[] { (0.1, "매끈 (0.1)"), (0.5, "보통 (0.5)"), (0.9, "거침 (0.9)") })
            Add(rough, label, radio, s => Math.Abs(s.GroundRoughness - r) < 1e-6, s => s with { GroundRoughness = r }, "envRough");
        root.Items.Add(rough);
        var off = new MenuItem { Header = "지면 높이 오프셋" };
        foreach (var o in new[] { -1.0, -0.5, 0.0, 0.5, 1.0 })
            Add(off, string.Create(Inv, $"{o:+0.0;-0.0;0} m"), radio, s => Math.Abs(s.GroundOffset - o) < 1e-6, s => s with { GroundOffset = o }, "envOff");
        root.Items.Add(off);
        Add(root, "지면 그림자", check, s => s.Shadows, s => s with { Shadows = !s.Shadows });
        Add(root, "거리 안개", check, s => s.Fog, s => s with { Fog = !s.Fog });
        SyncSceneChecks();
    }

    /// <summary>사용자 환경 불러오기(Fusion '사용자 설정 환경 부착'): environment.json 또는 정거원통 영상 한 장 → 목록에 추가 · 선택 · 다음 실행에도.</summary>
    private async Task LoadUserEnvironment()
    {
        var files = await PickOpenAsync(new FilePickerOpenOptions
        {
            Title = "사용자 환경 불러오기", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("환경 · 정거원통 영상") { Patterns = ["environment.json", "*.json", "*.hdr", "*.png", "*.jpg"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        AddUserEnvironment(path);
    }

    private bool AddUserEnvironment(string path)
    {
        try
        {
            var env = EnvironmentLoader.Load(path);
            View.Environments.Add(env);
            var users = View.SceneEnv.UserEnvironments.Where(p => p != path).Append(path).ToList();
            SetSceneEnv(View.SceneEnv with { EnvironmentId = env.Id, UserEnvironments = users });
            FillSceneMenu();
            return true;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, "환경", $"불러오기 실패 — {ex.Message}");
            return false;
        }
    }

    /// <summary>PNG · JPG → 선형 RGB(sRGB 감마 2.2 근사) — 사용자 정거원통 영상용(.hdr 은 로더 내장).</summary>
    private static FloatImage DecodeImage(string path)
    {
        using var bmp = new Avalonia.Media.Imaging.Bitmap(path);
        int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
        using var wb = new Avalonia.Media.Imaging.WriteableBitmap(bmp.PixelSize, new Vector(96, 96), Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
        using (var fb = wb.Lock())
        {
            bmp.CopyPixels(fb, Avalonia.Platform.AlphaFormat.Unpremul);
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, buf, y * w * 4, w * 4);
            var rgb = new float[w * h * 3];
            for (int i = 0; i < w * h; i++)
                for (int c = 0; c < 3; c++) rgb[3 * i + c] = MathF.Pow(buf[4 * i + c] / 255f, 2.2f);
            return new FloatImage(w, h, rgb);
        }
    }

    /// <summary>장면 환경 바꾸기 — 3D 다시 그림 · 메뉴 체크 · 저장(스모크는 저장 안 함).</summary>
    private void SetSceneEnv(SceneSettings s)
    {
        View.SceneEnv = s;
        if (!_smoke) s.Save(ThemeStore.DefaultPath);
        SyncSceneChecks();
        View.RequestNextFrameRendering();
        Log(LogLevel.Info, "보기", $"장면 환경: {s.EnvironmentId} → {View.ActiveEnvironment.DisplayName}");
    }

    private void SyncSceneChecks()
    {
        void Apply() { foreach (var (mi, on) in _sceneItems) mi.IsChecked = on(View.SceneEnv); }
        Apply();
        Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>보기 기본값: 3D 레이어 · 패널 · 트리에서 숨긴 물체를 모두 표시.</summary>
    private void ResetViewDefaults()
    {
        View.Layers.ResetDefaults();
        Array.Fill(_paneOn, true);
        SetBrowserCollapsed(false);
        ApplyPanes();
        ShowAllBodies();
        SyncChecks();
        Log(LogLevel.Info, "보기", "기본값 — 모든 레이어 · 패널 · 물체 표시");
    }

    /// <summary>메뉴 체크 표시를 모델 상태에 맞춘다(자동 토글 유무와 관계없이 클릭 처리 뒤 적용).</summary>
    private void SyncChecks()
    {
        void Apply()
        {
            foreach (var (l, mi) in _layerItems) mi.IsChecked = View.Layers[l];
            foreach (var (p, mi) in _paneItems) mi.IsChecked = _paneOn[(int)p];
            MiFollow.IsChecked = View.Follow;
            FollowToggle.IsChecked = View.Follow;
            SyncShellChecks();
            UpdateHud();
        }
        Apply();
        Dispatcher.UIThread.Post(Apply);
    }

    private static bool IsZero(GridLength g) => g.IsAbsolute && g.Value == 0;

    private void SetCol(ColumnDefinition c, bool on, GridLength def)
    {
        if (!on) { if (!IsZero(c.Width)) _savedSizes[c] = c.Width; c.Width = new GridLength(0); }
        else if (IsZero(c.Width)) c.Width = _savedSizes.TryGetValue(c, out var g) ? g : def;
    }

    private void SetFollow(bool on)
    {
        View.Follow = on;
        SyncChecks();
        Redraw();
    }

    private void FocusSelected()
    {
        if (_world is null || _selected < 0) { Log(LogLevel.Warn, "카메라", "선택된 물체가 없습니다."); return; }
        View.FocusSelected();
        SyncChecks();
    }

    private void ResetCamera()
    {
        View.ResetCamera(_world is { } w && w.Bodies.Count > 6 ? 40 : 12);
        SetFollow(true);
    }

    private void Redraw()
    {
        View.RequestNextFrameRendering();
        UpdateHud();
    }

    private void UpdateHud()
    {
        _hudDist = View.Distance;
        if (!View.Layers[ViewLayer.Hud]) return;
        var sb = new StringBuilder();
        if (_world is not null && _selected >= 0 && _selected < _world.Bodies.Count)
        {
            var b = _world.Bodies[_selected];
            sb.Append(Inv, $"선택: {b.Name}  ·  t = {_time:F2} s");
        }
        else sb.Append("선택 없음");
        sb.AppendLine();
        sb.Append(Inv, $"카메라: {(View.Follow ? "추적" : "자유")} · 거리 {View.Distance:G4} m");
        if (View.HiddenBodies.Count > 0) sb.Append($" · 숨김 {View.HiddenBodies.Count}");
        Overlay.Text = sb.ToString();
    }

    // ================= 시나리오 =================

    private void LoadScenario(string path)
    {
        try
        {
            _doc = ScenarioDoc.Load(path);
            int k = _scenarioFiles.FindIndex(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (k < 0)
            {
                // FX-06: 목록 밖 파일을 열어도 콤보가 이전 이름을 보이지 않게 목록에 추가해 선택
                _scenarioFiles.Add(path);
                ScenarioBox.ItemsSource = _scenarioFiles.Select(Path.GetFileNameWithoutExtension).ToList();
                k = _scenarioFiles.Count - 1;
            }
            if (ScenarioBox.SelectedIndex != k) ScenarioBox.SelectedIndex = k;
            ShowDoc();
            Log(LogLevel.Info, "시나리오", $"로드: {Path.GetFileName(path)} — F5 로 실행");
        }
        catch (Exception ex) { Log(LogLevel.Error, "시나리오", $"로드 실패 ({Path.GetFileName(path)}): {ex.Message}"); ShowLog(); }
    }

    private void ShowDoc()
    {
        if (_doc is null) return;
        var r = _doc.Root;
        ToolTip.SetTip(ScenarioInfoBtn, $"시나리오 설명 — {_doc.Name}");
        UpdateCredibilityCard(_doc.Name, _doc.FilePath);
        string F(string path) => JsonPath.GetNumberSafe(r, path) is { } v ? v.ToString("G6", Inv) : "";
        AltBox.Text = F("carrier.altitude");
        SpeedBox.Text = F("carrier.speed");
        HeadingBox.Text = F("carrier.heading");
        WindBox.Text = F("environment.wind.speed");
        WindDirBox.Text = F("environment.wind.directionFrom");
        DtBox.Text = F("sim.dt");
        TurbBox.SelectedItem = (r["environment"] as JsonObject) is { } env ? J.Str(env, "turbulence", "none") : "none";
        JsonBox.Text = _doc.ToJson();
    }

    private void ApplyFields()
    {
        if (_doc is null) return;
        var r = _doc.Root;
        void Set(string path, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!double.TryParse(text, NumberStyles.Float, Inv, out var v)) throw new FormatException($"{path}: 숫자가 아님 '{text}'");
            EnsureParents(r, path);
            JsonPath.SetNumber(r, path, v);
        }
        if (r["carrier"] is JsonObject)
        {
            Set("carrier.altitude", AltBox.Text);
            Set("carrier.speed", SpeedBox.Text);
            Set("carrier.heading", HeadingBox.Text);
        }
        Set("environment.wind.speed", WindBox.Text);
        Set("environment.wind.directionFrom", WindDirBox.Text);
        Set("sim.dt", DtBox.Text);
        EnsureParents(r, "environment.turbulence");
        ((JsonObject)r["environment"]!)["turbulence"] = (string?)TurbBox.SelectedItem ?? "none";
        JsonBox.Text = _doc.ToJson();
    }

    private static void EnsureParents(JsonObject root, string path)
    {
        var parts = path.Split('.');
        JsonObject cur = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (cur[parts[i]] is not JsonObject next) { next = new JsonObject(); cur[parts[i]] = next; }
            cur = next;
        }
    }

    private void ApplyJson()
    {
        try
        {
            var doc = ScenarioDoc.Parse(JsonBox.Text ?? "", _doc?.BaseDir ?? _repo);
            var w = doc.Build();
            _doc = new ScenarioDoc { Root = doc.Root, BaseDir = doc.BaseDir, FilePath = _doc?.FilePath };
            ShowDoc();
            Log(LogLevel.Info, "JSON", $"검증 OK — 물체 {w.Bodies.Count}, 연결 {w.Connectors.Count}, 이벤트 {w.Events.Count} (F5 로 실행)");
        }
        catch (Exception ex) { Log(LogLevel.Error, "JSON", ex.Message); ShowLog(); }
    }

    private async Task OpenScenario()
    {
        var files = await PickOpenAsync(new FilePickerOpenOptions
        {
            Title = "시나리오 JSON 열기", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("시나리오") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } p) return;
        LoadScenario(p);
    }

    // ================= 모델 가져오기 (Fusion) =================

    private async Task ImportModel()
    {
        var files = await PickOpenAsync(new FilePickerOpenOptions
        {
            Title = "Fusion 내보내기 모델 (*.massprops.json / *.stl)", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Fusion 내보내기") { Patterns = ["*.massprops.json", "*.stl", "*.json"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            ImportModelFromPath(path);
            await RunSimulation();
        }
        catch (Exception ex) { Log(LogLevel.Error, "가져오기", ex.Message); ShowLog(); }
    }

    /// <summary>
    /// 선택된 Body 가 있으면 그 Body 의 형상·질량특성을 교체, 없으면 단일 낙하 시나리오를 새로 만든다.
    /// massprops.json 이 없으면 STL + '가져오기 질량' 으로 균일밀도 질량특성 계산(PRD S절 대체 경로).
    /// </summary>
    public void ImportModelFromPath(string path)
    {
        string? mpPath = null, stlPath = null;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) mpPath = path;
        else
        {
            stlPath = path;
            var guess = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".massprops.json");
            if (File.Exists(guess)) mpPath = guess;
        }
        // 공력: 메시 측면 투영 CP(FX-03) + 템플릿 04 와 같은 감쇠 추정값(시험·CFD 전 가정)
        var def = new JsonObject
        {
            ["modelFrame"] = "fusion", ["color"] = "#d08040",   // data-color: 가져온 물체의 시나리오 고유 색(테마 무관)
            ["aero"] = new JsonObject { ["cp"] = "auto", ["damping"] = new JsonArray(-2.0, -10.0, -10.0) },
        };
        if (mpPath is not null)
        {
            def["massProps"] = mpPath;
            // Fusion 외관 기본색이 있으면 가져오기 고정색 대신 그 색(물체 색 기본값 자리 — ScenarioLoader)
            try { if (MassPropsFile.Load(mpPath).AppearanceColor is not null) def.Remove("color"); }
            catch (InvalidDataException) { /* 읽기 오류는 시나리오 적용 때 그대로 보고된다 */ }
        }
        else
        {
            double mass = double.Parse(ImportMassBox.Text ?? "1", Inv);
            def["mass"] = mass;
            def["shape"] = new JsonObject { ["type"] = "mesh", ["file"] = stlPath, ["unit"] = "mm" };
        }
        // FX-01: 기수축 — 사용자가 고른 값, '자동'이면 형상으로 제안(로그에 표시, JSON 에서 수정 가능)
        string nose = (string?)NoseAxisBox.SelectedItem ?? AutoNose;
        if (nose == AutoNose)
        {
            try
            {
                var mp = mpPath is not null ? MassPropsFile.Load(mpPath) : null;
                var stl = mp?.Stl is { } s ? Path.Combine(Path.GetDirectoryName(mpPath!)!, s) : stlPath;
                if (stl is not null && File.Exists(stl))
                {
                    nose = NoseGeometry.SuggestNoseAxis(TriMesh.LoadStl(stl));
                    Log(LogLevel.Info, "가져오기", $"기수축 제안: {nose} (최장축 끝 단면이 작은 쪽) — 다르면 '가져오기 기수축'에서 골라 다시 가져오기");
                }
                else nose = "+Z";
            }
            catch (Exception ex) { nose = "+Z"; Log(LogLevel.Warn, "가져오기", $"기수축 제안 실패({ex.Message}) — +Z 사용"); }
        }
        def["noseAxis"] = nose;
        string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));

        var bodies = _doc?.Root["bodies"] as JsonArray;
        var target = _world is not null && _selected >= 0 && _selected < _world.Bodies.Count && _world.Bodies[_selected].Mode != BodyMode.Kinematic
            ? bodies?.OfType<JsonObject>().FirstOrDefault(b => J.StrOpt(b, "name") == _world.Bodies[_selected].Name) : null;
        if (target is not null && _doc is not null)
        {
            foreach (var k in new[] { "mass", "cg", "inertia", "shape", "massProps", "modelFrame", "modelToBody", "density", "noseAxis", "penetrator" }) target.Remove(k);
            foreach (var (k, v) in def) if (k != "aero" && k != "color") target[k] = v?.DeepClone();
            Log(LogLevel.Info, "가져오기", $"'{J.StrOpt(target, "name")}' 에 모델 적용: {Path.GetFileName(path)}");
        }
        else
        {
            def["name"] = name;
            def["parent"] = "drone_hover";
            def["mount"] = new JsonArray(0, 0, 0.3);
            var root = new JsonObject
            {
                ["schema"] = ScenarioLoader.Schema,
                ["name"] = $"import_{name}",
                ["description"] = $"가져온 모델 단일 낙하: {path}",
                ["sim"] = new JsonObject { ["dt"] = 0.001, ["tEnd"] = 300, ["outputRate"] = 100, ["seed"] = 1 },
                ["environment"] = new JsonObject
                {
                    ["wind"] = new JsonObject { ["speed"] = 3.0, ["directionFrom"] = 270.0, ["refHeight"] = 10.0, ["shear"] = "log", ["roughness"] = 0.03 },
                    ["turbulence"] = "none",
                },
                ["carrier"] = new JsonObject { ["name"] = "drone_hover", ["altitude"] = 100.0, ["speed"] = 0.0, ["heading"] = 0.0,
                    ["shape"] = new JsonObject { ["type"] = "box", ["size"] = new JsonArray(0.35, 0.35, 0.12) } },
                ["bodies"] = new JsonArray(def),
                ["events"] = new JsonArray(new JsonObject
                {
                    ["name"] = "drop", ["when"] = new JsonObject { ["type"] = "time", ["t"] = 0.5 },
                    ["do"] = new JsonArray(new JsonObject { ["type"] = "separate", ["body"] = name }),
                }),
                ["monteCarlo"] = new JsonObject { ["runs"] = 100, ["impactBody"] = name },
                ["dispersions"] = new JsonArray(
                    new JsonObject { ["path"] = "environment.wind.speed", ["dist"] = "truncnormal", ["sd"] = 1.5, ["min"] = -3.0, ["max"] = 5.0 },
                    new JsonObject { ["path"] = "environment.wind.directionFrom", ["dist"] = "normal", ["sd"] = 20.0 }),
            };
            _doc = new ScenarioDoc { Root = root, BaseDir = Path.GetDirectoryName(path)!, FilePath = null };
            ScenarioBox.SelectedIndex = -1; // FX-06: 파일 없는 새 시나리오 — 이전 이름 표시 안 함
            Log(LogLevel.Info, "가져오기", $"가져온 모델로 단일 낙하 시나리오 생성: {name}");
        }
        ShowDoc();
    }

    // ================= 실행 =================

    private async Task RunSimulation()
    {
        if (_doc is null) { Log(LogLevel.Warn, "시뮬레이션", "시나리오가 없습니다 — 열기 또는 모델 가져오기"); return; }
        try { ApplyFields(); }
        catch (Exception ex) { Log(LogLevel.Error, "입력", ex.Message); ShowLog(); return; }
        var job = BeginJob("시뮬레이션", cancellable: true);
        if (job is null) return;
        var ct = job.Cts.Token;
        var doc = _doc.Clone();
        Stage(job, "1/3 시나리오 검증 · 물체 · 형상 생성", null);
        try
        {
            var (world, tree) = await Task.Run(() =>
            {
                var w = doc.Build();
                var t = AssemblyTree.Build(w); // 분리로 계층이 끊기기 전에
                double tEnd = Math.Max(w.TEnd, 1e-9);
                Dispatcher.UIThread.Post(() => Stage(job, $"2/3 적분 (6DOF RK4, dt {w.Dt * 1000:G3} ms) — 물체 {w.Bodies.Count}", 0));
                var throttle = Stopwatch.StartNew();
                w.Run(ct, tt =>
                {
                    if (throttle.ElapsedMilliseconds < 80) return;
                    throttle.Restart();
                    Dispatcher.UIThread.Post(() => Stage(job, $"2/3 적분  t = {tt:F1} / {tEnd:F0} s (상한)", tt / tEnd));
                });
                return (w, t);
            }, ct);
            bool cancelled = ct.IsCancellationRequested;
            double calc = job.Sw.Elapsed.TotalSeconds;
            Stage(job, "3/3 결과 요약 · 3D 장면 생성", null);
            var (scene, summary) = await Task.Run(() => (SceneData.Build(world), Results.Summary(doc, world)));
            SetResult(doc, world, tree, scene, summary);
            LogEngine(world);
            string msg = cancelled
                ? $"취소됨 — t = {world.T:F1} s 까지 부분 결과 표시"
                : $"완료: 시뮬레이션 {world.T:F1} s / 계산 {calc:F2} s (실시간 ×{world.T / Math.Max(calc, 1e-6):F0}), {world.StepCount:N0} 스텝 · {world.StopReason}";
            EndJob(job, cancelled || world.Suspect ? LogLevel.Warn : LogLevel.Info, msg);
        }
        catch (OperationCanceledException) { EndJob(job, LogLevel.Warn, "취소됨"); }
        catch (Exception ex)
        {
            _logWriter?.WriteLine(ex.ToString());
            EndJob(job, LogLevel.Error, $"실행 실패: {ex.Message}");
        }
    }

    /// <summary>엔진 이벤트 로그 · 경고 · 수치 건전성을 로그 패널로.</summary>
    private void LogEngine(World w)
    {
        int n = 0;
        foreach (var e in w.EventLog)
        {
            if (++n > MaxEngineLogLines) { Log(LogLevel.Info, "엔진", $"… 이벤트 {w.EventLog.Count - MaxEngineLogLines}개 더 (오른쪽 이벤트 목록 참조)"); break; }
            Log(LogLevel.Info, "엔진", string.Format(Inv, "t = {0,8:F3} s  {1} — {2}", e.Time, e.Name, e.Message));
        }
        foreach (var s in w.Warnings) Log(LogLevel.Warn, "엔진", s);
        if (w.Suspect) Log(LogLevel.Warn, "엔진", "수치 건전성 감시: 결과가 '의심(suspect)'으로 표시됨 (FR-NUM-06) — 경고 내용 확인");
    }

    private void SetResult(ScenarioDoc doc, World world, AssemblyNode tree, SceneData scene, JsonObject summary)
    {
        _world = world;
        _runDoc = doc;
        _summary = summary;
        View.Scene = scene;
        View.Mc = null;
        ResetAnalysis();
        BuildTree(tree, world);
        var mcBody = (doc.Root["monteCarlo"] as JsonObject) is { } mc ? J.StrOpt(mc, "impactBody") : null;
        int sel = mcBody is not null && world.BodyMap.TryGetValue(mcBody, out var mb) ? mb.Index
            : world.Bodies.FindIndex(b => b.Mode != BodyMode.Kinematic);
        View.ResetCamera(world.Bodies.Count > 6 ? 40 : 12);
        _suppressSlider = true;
        TimeSlider.Maximum = Math.Max(world.T, 0.01);
        TimeSlider.Value = 0;
        _suppressSlider = false;
        _time = 0;
        SummaryText.Text = Results.SummaryText(_summary);
        BuildSummaryPanel(_summary);
        EventsList.ItemsSource = world.EventLog.Select(e => new EventItem(e.Time, e.Name, e.Message)).ToList();
        SelectBody(sel);
        _playing = true;
        SetPlayIcon(true);
        Refresh();
    }

    private void BuildTree(AssemblyNode tree, World world)
    {
        _treeRoot = AsmItem.From(tree, world);
        _bodyItems = new AsmItem?[world.Bodies.Count];
        foreach (var it in _treeRoot.SelfAndDescendants().Where(i => i.IsBody))
        {
            _bodyItems[it.BodyIndex] = it;
            it.ShownChanged += _ => UpdateHidden();
        }
        AsmTree.ItemsSource = new[] { _treeRoot };
        View.HiddenBodies = new HashSet<int>();
    }

    private void UpdateHidden()
    {
        View.HiddenBodies = _bodyItems.Where(i => i is { Shown: false }).Select(i => i!.BodyIndex).ToHashSet();
        Redraw();
    }

    private void ShowAllBodies()
    {
        // 장면(묶음) 눈도 다시 켠다 — 묶음 눈을 꺼 숨긴 뒤 '모두 보기' 해도 눈이 꺼진 채 남지 않게
        foreach (var it in AllTreeItems().Where(i => i.HasEye)) it.Shown = true;
        UpdateHidden();
    }

    /// <summary>물체 선택(트리 · 3D 클릭 · 스모크) → 추적 대상 · 텔레메트리 · 그래프 · 트리 동기화.</summary>
    private void SelectBody(int i, bool fromTree = false)
    {
        if (_world is null || i < 0 || i >= _world.Bodies.Count) return;
        _selected = i;
        View.SelectedBody = i;
        if (!fromTree && i < _bodyItems.Length && _bodyItems[i] is { } it)
        {
            _syncTree = true;
            for (var p = it.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
            AsmTree.SelectedItem = it;
            _syncTree = false;
        }
        UpdatePlot();
        Refresh();
    }

    // ================= Monte Carlo =================

    private async Task RunGpuMc()
    {
        if (_doc is null) return;
        try { ApplyFields(); } catch (Exception ex) { Log(LogLevel.Error, "입력", ex.Message); ShowLog(); return; }
        if (_doc.Root["gpuMonteCarlo"] is not JsonObject)
        {
            Log(LogLevel.Warn, "GPU MC", "이 시나리오에는 gpuMonteCarlo(단순 모델) 절이 없습니다 → CPU 6DOF MC 를 쓰세요.");
            return;
        }
        if (!int.TryParse(RunsBox.Text, NumberStyles.Integer, Inv, out int runs) || runs <= 0) { Log(LogLevel.Error, "GPU MC", $"실행 수가 올바르지 않음: '{RunsBox.Text}'"); ShowLog(); return; }
        var doc = _doc.Clone();
        var job = BeginJob("GPU Monte Carlo", cancellable: false);
        if (job is null) return;
        Stage(job, $"GPU 준비 · 커널 실행 ({runs:N0}회, LHS)", null);
        try
        {
            var res = await Task.Run(() =>
            {
                _gpu ??= GpuDropBackend.TryCreate(allowCpu: false, out _gpuMsg);
                IDropBackend be = _gpu is not null ? _gpu : new CpuDropBackend();
                return DropMonteCarlo.Run(doc, runs, be, 7, "lhs");
            });
            if (_gpu is null && _gpuMsg.Length > 0) Log(LogLevel.Warn, "GPU MC", $"GPU 없음 → CPU 대체: {_gpuMsg}");
            var pts = res.Results.Where(r => r.Landed == 1).Select(r => (r.N, r.E)).ToArray();
            ShowMc(new McOverlay { Points = pts, Stats = res.Stats, GroundAltitude = J.Num(doc.Root["environment"] as JsonObject ?? new(), "groundAltitude", 0), Label = res.Backend });
            EndJob(job, LogLevel.Info, $"완료: {res.Backend} — {runs:N0}회, 커널 {res.KernelSeconds:F2} s ({runs / Math.Max(res.KernelSeconds, 1e-9):N0} 회/s), CEP {res.Stats.Cep50:F1} m");
        }
        catch (Exception ex) { _logWriter?.WriteLine(ex.ToString()); EndJob(job, LogLevel.Error, $"실패: {ex.Message}"); }
    }

    private async Task RunCpuMc()
    {
        if (_doc is null) return;
        try { ApplyFields(); } catch (Exception ex) { Log(LogLevel.Error, "입력", ex.Message); ShowLog(); return; }
        if (!int.TryParse(FullRunsBox.Text, NumberStyles.Integer, Inv, out int runs) || runs <= 0) { Log(LogLevel.Error, "6DOF MC", $"실행 수가 올바르지 않음: '{FullRunsBox.Text}'"); ShowLog(); return; }
        var doc = _doc.Clone();
        string body = (doc.Root["monteCarlo"] as JsonObject) is { } mc && J.StrOpt(mc, "impactBody") is { } b ? b
            : _world is not null && _selected >= 0 ? _world.Bodies[_selected].Name : "";
        if (body == "") { Log(LogLevel.Warn, "6DOF MC", "monteCarlo.impactBody 또는 선택 물체가 필요합니다."); return; }
        var job = BeginJob("6DOF Monte Carlo", cancellable: true);
        if (job is null) return;
        Stage(job, $"0/{runs} 실행 (CPU {System.Environment.ProcessorCount} 스레드, 물체 {body})", 0);
        try
        {
            var progress = new Progress<int>(n => Stage(job, $"{n}/{runs} 실행 (CPU {System.Environment.ProcessorCount} 스레드, 물체 {body})", (double)n / runs));
            var ct = job.Cts.Token;
            var res = await Task.Run(() => MonteCarlo.Run(doc, runs, body, "lhs", 1, progress, ct), ct);
            var pts = res.Runs.Where(r => r.Landed && r.Error is null).Select(r => (r.N, r.E)).ToArray();
            ShowMc(new McOverlay { Points = pts, Stats = res.Stats, GroundAltitude = J.Num(doc.Root["environment"] as JsonObject ?? new(), "groundAltitude", 0), Label = $"6DOF CPU · {body}" });
            int failed = res.Runs.Count(r => r.Error is not null || !r.Landed);
            foreach (var r in res.Runs.Where(r => r.Error is not null).Take(20)) Log(LogLevel.Warn, "6DOF MC", $"실행 실패: {r.Error}");
            EndJob(job, failed > 0 ? LogLevel.Warn : LogLevel.Info, $"완료: {runs}회 {res.ElapsedSeconds:F1} s, 실패 {failed}, CEP {res.Stats.Cep50:F1} m (물체 {body})");
        }
        catch (OperationCanceledException) { EndJob(job, LogLevel.Warn, "취소됨"); }
        catch (Exception ex) { _logWriter?.WriteLine(ex.ToString()); EndJob(job, LogLevel.Error, $"실패: {ex.Message}"); }
    }

    private void ShowMc(McOverlay mc)
    {
        View.Mc = mc;
        if (!View.Layers[ViewLayer.McPoints] || !View.Layers[ViewLayer.McRings])
        {
            View.Layers[ViewLayer.McPoints] = true;
            View.Layers[ViewLayer.McRings] = true;
            Log(LogLevel.Info, "보기", "MC 결과 표시를 위해 MC 레이어를 켰습니다.");
        }
        View.FitMc();
        PlotSel.SelectedIndex = 3;
        if (!_paneOn[(int)Pane.Plot]) { _paneOn[(int)Pane.Plot] = true; ApplyPanes(); }
        UpdatePlot();
        SyncChecks();
        Redraw();
    }

    // ================= 저장 =================

    /// <summary>결과 폴더 내보내기(CSV · scene.json · 메시 — Blender 가져오기·외부 도구용). 앱에서 다시 여는 파일은 .mnsr.</summary>
    private async Task ExportResultsFolder()
    {
        if (_world is null || _doc is null) { Log(LogLevel.Warn, "저장", "저장할 결과가 없습니다."); return; }
        var folders = await PickFolderAsync(new FolderPickerOpenOptions { Title = "결과 내보내기 폴더", AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } dir) return;
        var outDir = Path.Combine(dir, $"{_doc.Name}_{DateTime.Now:yyyyMMdd_HHmmss}");
        try
        {
            Results.WriteAll(_doc, _world, outDir);
            Log(LogLevel.Info, "저장", $"결과 저장: {outDir} (Blender: tools/blender/import_mns_result.py)");
        }
        catch (Exception ex) { Log(LogLevel.Error, "저장", ex.Message); ShowLog(); }
    }

    private async Task SaveViewShot()
    {
        var f = await PickSaveAsync(new FilePickerSaveOptions
        {
            Title = "3D 화면 캡처", SuggestedFileName = $"view_{DateTime.Now:yyyyMMdd_HHmmss}.png", DefaultExtension = "png",
        });
        if (f?.TryGetLocalPath() is not { } path) return;
        View.RequestSnapshot(path);
        Log(LogLevel.Info, "보기", $"3D 화면 캡처: {path}");
    }

    // ================= 재생 · 표시 =================

    private void TogglePlay()
    {
        if (_world is null) return;
        if (!_playing && _time >= _world.T - 1e-9) _time = 0;
        _playing = !_playing;
        SetPlayIcon(_playing);
    }

    private void Seek(double t)
    {
        if (_world is null) return;
        _time = Math.Clamp(t, 0, _world.T);
        _suppressSlider = true;
        TimeSlider.Value = _time;
        _suppressSlider = false;
        Refresh();
    }

    private void StepFrame(int d)
    {
        if (_world is null) return;
        var frames = _world.Recorder.Frames;
        if (frames.Count == 0) return;
        _playing = false;
        SetPlayIcon(false);
        int i = Math.Clamp(_world.Recorder.IndexAt(_time) + d, 0, frames.Count - 1);
        Seek(frames[i].T);
    }

    /// <summary>평균 기록 간격 n 칸만큼 시각 이동(일시정지). 이벤트 근처의 촘촘한 추가 프레임에 걸려 거의 안 움직이는 일이 없게
    /// 프레임 대신 일정 시간 간격 — 화면은 보간되므로 어느 시각이든 그릴 수 있다.</summary>
    private void StepTime(int n)
    {
        if (_world is null) return;
        var frames = _world.Recorder.Frames;
        if (frames.Count < 2) return;
        _playing = false;
        SetPlayIcon(false);
        double dt = (frames[^1].T - frames[0].T) / (frames.Count - 1);
        Seek(_time + n * dt);
    }

    private void JumpEvent(int dir)
    {
        if (_world is null || _world.EventLog.Count == 0) return;
        var times = _world.EventLog.Select(e => e.Time);
        double? t = dir > 0 ? times.Where(x => x > _time + 1e-6).Cast<double?>().FirstOrDefault()
                            : times.Where(x => x < _time - 1e-6).Cast<double?>().LastOrDefault();
        if (t is { } tt) Seek(tt);
    }

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastTick;
        _lastTick = now;
        if (_job is not null) JobElapsed.Text = $"{_job.Sw.Elapsed.TotalSeconds:F1} s";
        if (_playing && _world is not null)
        {
            _time += dt * _rate;
            if (_time >= _world.T) { _time = _world.T; _playing = false; SetPlayIcon(false); }
            _suppressSlider = true;
            TimeSlider.Value = _time;
            _suppressSlider = false;
            Refresh();
        }
        else if (_continuousRender) View.RequestNextFrameRendering();
        // 유휴 시에는 렌더 요청 없음(입력·변경 때만 다시 그림). 휠 확대 등 카메라 거리 변화만 HUD 에 반영.
        if (View.Distance != _hudDist) UpdateHud();
    }

    private void Refresh()
    {
        View.PlaybackTime = _time;
        TimeText.Text = _rate < 0.1 ? $"t = {_time,9:F4} s" : $"t = {_time,8:F2} s"; // 슬로모션(충격)은 0.1 ms 단위
        Plot.CursorTime = _time;
        UpdateTelemetry();
        UpdateTreeStates();
        UpdateColors();
        UpdateHud();
        View.RequestNextFrameRendering();
    }

    private void UpdateTreeStates()
    {
        if (_world is null || _bodyItems.Length == 0) return;
        int idx = _world.Recorder.IndexAt(_time);
        if (idx < 0) return;
        var f = _world.Recorder.Frames[idx];
        for (int i = 0; i < _bodyItems.Length && i < f.Bodies.Length; i++)
            _bodyItems[i]?.SetMode(_world.Bodies[i].Mode == BodyMode.Kinematic ? BodyMode.Kinematic : f.Bodies[i].Mode);
    }


    private void UpdatePlot()
    {
        if (PlotSel.SelectedIndex == 3)
        {
            Plot.SetMc(View.Mc, View.Mc is null ? "MC 결과 없음 — 'GPU MC' 또는 'CPU MC' 실행" : "MC 착탄 산포 (CEP 원: 초록, 95% 타원: 빨강)");
            return;
        }
        if (_world is null || _selected < 0) return;
        var frames = _world.Recorder.Frames;
        int i = _selected;
        var t = frames.Select(f => f.T).ToArray();
        var name = _world.Bodies[i].Name;
        switch (PlotSel.SelectedIndex)
        {
            case 0:
                Plot.SetSeries([
                    new PlotView.Series("고도 AGL [m]", t, frames.Select(f => _world.Env.Agl(f.Bodies[i].Cg.Z)).ToArray(), 1),
                    new PlotView.Series("속도 [m/s]", t, frames.Select(f => f.Bodies[i].Velocity.Norm).ToArray(), 2, true),
                ], $"{name}: 고도·속도");
                break;
            case 1:
                var eul = frames.Select(f => f.Bodies[i].Q.ToEuler() * (180 / Math.PI)).ToArray();
                Plot.SetSeries([
                    new PlotView.Series("Roll", t, eul.Select(e => e.X).ToArray(), 3),
                    new PlotView.Series("Pitch", t, eul.Select(e => e.Y).ToArray(), 4),
                    new PlotView.Series("Yaw", t, eul.Select(e => e.Z).ToArray(), 5),
                ], $"{name}: 자세 [deg]");
                break;
            default:
                Plot.SetSeries([
                    new PlotView.Series("하중 [G]", t, frames.Select(f => f.Bodies[i].LoadG).ToArray(), 6),
                    new PlotView.Series("대기속도 [m/s]", t, frames.Select(f => f.Bodies[i].Airspeed).ToArray(), 7, true),
                ], $"{name}: 하중·대기속도");
                break;
        }
        Plot.CursorTime = _time;
    }

    // ================= 키보드 =================

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement();
        bool inText = focused is TextBox;
        // 화살표·Space·PageUp/Down·Home 은 목록·슬라이더·메뉴가 쓰므로 3D 뷰/버튼/무포커스일 때만
        bool navOk = focused is null or GlView or Button or Window;
        var m = e.KeyModifiers;
        bool ctrl = (m & KeyModifiers.Control) != 0, shift = (m & KeyModifiers.Shift) != 0, alt = (m & KeyModifiers.Alt) != 0;
        bool handled = true;
        // 열린 "?" 말풍선이 있으면 Esc 는 그것만 닫는다(작업 취소보다 먼저 — 열려 있을 때만 먹음)
        if (e.Key == Key.Escape && HelpTip.Current is { } tip)
        {
            tip.Close();
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.F5: _ = RunSimulation(); break;
            case Key.F1: _ = ShowHelp(); break;
            case Key.Escape when _job is not null: CancelJob(); break;
            // 겹쳐 뜬(고정 안 한) 서랍은 Esc 로 닫힘(참고 HamburgerMenu CompactOverlay)
            case Key.Escape when _paneOn[(int)Pane.Input] && !_leftPinned: SetPanes(false, Pane.Input); break;
            case Key.O when ctrl: _ = OpenScenario(); break;
            case Key.I when ctrl: _ = ImportModel(); break;
            case Key.S when ctrl: _ = SaveResults(); break;
            case Key.L when ctrl: _ = PickAndLoadResult(); break;
            case Key.F6: _ = RunStructural(); break;
            case Key.F7: _ = RunCcx(); break;
            case Key.F8 when shift: OpenAirflow(); break;
            case Key.F8: OpenWindTunnel(); break;
            case Key.D0 or Key.NumPad0 when ctrl: ResetViewDefaults(); break;
            case Key.B when ctrl && !shift && !alt: ToggleBrowser(); break;
            case Key.Home when ctrl && !inText: Seek(0); break;
            case Key.OemCloseBrackets when ctrl && !inText: SeekShock(1); break;
            case Key.OemOpenBrackets when ctrl && !inText: SeekShock(-1); break;
            // Ctrl+화살표: 제자리 시점 회전(카메라 위치 고정)
            case Key.Left when ctrl && !inText && navOk: View.Look(-LookStep, 0); break;
            case Key.Right when ctrl && !inText && navOk: View.Look(LookStep, 0); break;
            case Key.Up when ctrl && !inText && navOk: View.Look(0, -LookStep); break;
            case Key.Down when ctrl && !inText && navOk: View.Look(0, LookStep); break;
            // 그래프에 포커스: ←/→ = 기록 간격 한 칸씩(Shift = 10칸) 시각 이동
            case Key.Left or Key.Right when focused is PlotView && !ctrl && !alt:
                StepTime((e.Key == Key.Right ? 1 : -1) * (shift ? 10 : 1));
                Plot.KeepCursorInView();
                break;
            default:
                // 메뉴가 열려 있으면 메뉴 탐색 키를 가로채지 않는다
                handled = !inText && !ctrl && !alt && focused is not MenuItem && HandlePlainKey(e.Key, shift, navOk);
                break;
        }
        if (handled) e.Handled = true;
    }

    /// <summary>Ctrl+화살표 시점 회전 한 번 [rad](2°).</summary>
    private const double LookStep = 0.035;

    /// <summary>누르는 동안 공간 이동하는 키: W/S 전진·후진, A/D 좌·우, E/Q 상승·하강.</summary>
    private static FlyDir? FlyDirOf(Key k) => k switch
    {
        Key.W => FlyDir.Forward,
        Key.S => FlyDir.Back,
        Key.A => FlyDir.Left,
        Key.D => FlyDir.Right,
        Key.E => FlyDir.Up,
        Key.Q => FlyDir.Down,
        _ => null,
    };

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        // 어디서 떼든 멈춘다(포커스가 옮겨가도 키가 눌린 채로 남지 않게)
        if (FlyDirOf(e.Key) is { } d && View.IsFlying) View.FlyKey(d, false);
    }

    private bool HandlePlainKey(Key k, bool shift, bool navOk)
    {
        switch (k)
        {
            case Key.T: SetFollow(!View.Follow); return true;
            case Key.C: FocusSelected(); return true;
            case Key.F: View.FitScene(); return true;
            case Key.Add or Key.OemPlus: View.Zoom(1 / 1.25); return true;
            case Key.Subtract or Key.OemMinus: View.Zoom(1.25); return true;
            case Key.D1 or Key.NumPad1: View.SetPreset(ViewPreset.Top); return true;
            case Key.D2 or Key.NumPad2: View.SetPreset(ViewPreset.North); return true;
            case Key.D3 or Key.NumPad3: View.SetPreset(ViewPreset.East); return true;
            case Key.D4 or Key.NumPad4: View.SetPreset(ViewPreset.Iso); return true;
            case Key.OemComma: StepFrame(-1); return true;
            case Key.OemPeriod: StepFrame(+1); return true;
        }
        if (!navOk) return false;
        if (FlyDirOf(k) is { } fly) { View.FlyKey(fly, true); return true; }
        switch (k)
        {
            case Key.Space: TogglePlay(); return true;
            case Key.Home: ResetCamera(); return true;
            case Key.PageUp: JumpEvent(-1); return true;
            case Key.PageDown: JumpEvent(+1); return true;
            case Key.Left: if (shift) View.Orbit(0.08, 0); else View.PanPixels(40, 0); return true;
            case Key.Right: if (shift) View.Orbit(-0.08, 0); else View.PanPixels(-40, 0); return true;
            case Key.Up: if (shift) View.Orbit(0, 0.06); else View.PanPixels(0, 40); return true;
            case Key.Down: if (shift) View.Orbit(0, -0.06); else View.PanPixels(0, -40); return true;
        }
        return false;
    }

    /// <summary>도움말 창(F1 · 도구 막대 ? · 도움말 메뉴) — 목차 · 검색. 모달이 아니라 옆에 띄워 두고 조작할 수 있다.</summary>
    private Task ShowHelp(string? chapter = null)
    {
        if (_helpWin is not { IsVisible: true })
        {
            _helpWin = new HelpWindow { Icon = Icon };
            _helpWin.Closed += (_, _) => _helpWin = null;
            _helpWin.Show(this);
        }
        else _helpWin.Activate();
        if (chapter is not null) _helpWin.ShowChapter(chapter);
        return Task.CompletedTask;
    }

    private HelpWindow? _helpWin;

    /// <summary>
    /// 보조 창 · 대화상자 공통 틀 — 머리(제목 + 섹션 "?") · 내용 · 여백 24, 작업 영역 안(KeepOnScreen), Esc 로 닫기. 색은 창 스타일 토큰.
    /// </summary>
    private Window DialogWindow(string title, string helpKey, Control content, double width, double? height = null)
    {
        // 머리 띠(참고 ConsoleShell 머리 — 바탕 SurfaceAlt · 아래 구분선 · 제목 + 섹션 "?")
        var head = new DockPanel();
        var tip = new HelpTip { HelpKey = helpKey, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(tip, Dock.Right);
        head.Children.Add(tip);
        head.Children.Add(new TextBlock { Text = title, Classes = { "dialogtitle" }, TextWrapping = TextWrapping.Wrap });
        var headBar = new Border { Classes = { "dialoghead" }, Child = head };
        var root = new DockPanel();
        DockPanel.SetDock(headBar, Dock.Top);
        root.Children.Add(headBar);
        root.Children.Add(new Border { Padding = new Thickness(24, 16, 24, 24), Child = content });
        var win = new Window
        {
            Title = title, Width = width, WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = Icon, Content = root,
            MinWidth = 420, MinHeight = 240,
        };
        if (height is { } h) win.Height = h;
        else win.SizeToContent = SizeToContent.Height;
        win.KeepOnScreen();
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        return win;
    }

    /// <summary>현재 시나리오 설명(description 원문)을 별도 창으로.</summary>
    private async Task ShowScenarioInfo()
    {
        if (_doc is null) { Log(LogLevel.Warn, "시나리오", "열린 시나리오가 없습니다."); return; }
        var desc = J.Str(_doc.Root, "description", "");
        var sub = _doc.FilePath is { } fp ? Path.GetRelativePath(_repo, fp) : "(파일 없음 — 가져오기 · 결과에서 만든 시나리오)";
        var win = new InfoWindow(_doc.Name, sub, desc.Length > 0 ? desc : "이 시나리오에는 설명(description)이 없습니다.") { Icon = Icon };
        await win.ShowDialog(this);
    }

    // ================= 스모크 테스트 (--smoke) =================

    /// <summary>
    /// 창 캡처(FX-06): RenderTargetBitmap 은 OpenGL 뷰포트를 그리지 못하므로, GL 스냅숏(glPng)을 3D 뷰 위치에 합성한다.
    /// </summary>
    private async Task SaveWindowShot(string path, string? glPng = null, params Control?[] popups)
    {
        var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        rtb.Render(this);
        if (glPng is not null && File.Exists(glPng) && View.TranslatePoint(new Point(0, 0), this) is { } origin)
        {
            using var gl = new Avalonia.Media.Imaging.Bitmap(glPng);
            using (var ctx = rtb.CreateDrawingContext(false))
            {
                ctx.DrawImage(gl, new Rect(gl.Size), new Rect(origin, View.Bounds.Size));
            }
            // 3D 위 셸 층 전체(레일 · 서랍 · 패널 · 브라우저 · HUD · 레전드 · 떠 있는 막대 — 자식 포함, 별도 비트맵으로 렌더해 합성)
            foreach (var layer in new Control[] { ShellLayer })
                if (layer.IsVisible && layer.TranslatePoint(new Point(0, 0), this) is { } po && layer.Bounds.Width >= 1 && layer.Bounds.Height >= 1)
                {
                    using var over = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Math.Ceiling(layer.Bounds.Width), (int)Math.Ceiling(layer.Bounds.Height)));
                    over.Render(layer);
                    using var ctx = rtb.CreateDrawingContext(false);
                    ctx.DrawImage(over, new Rect(over.Size), new Rect(po, over.Size));
                }
            // 열린 "?" 말풍선이 3D 뷰 위에 걸치면 GL 그림이 덮으므로 맨 위에 다시
            if (HelpTip.Current?.Callout is { IsVisible: true } callout && callout.TranslatePoint(new Point(0, 0), this) is { } co
                && callout.Bounds.Width >= 1 && callout.Bounds.Height >= 1)
            {
                using var over = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Math.Ceiling(callout.Bounds.Width), (int)Math.Ceiling(callout.Bounds.Height)));
                over.Render(callout);
                using var ctx = rtb.CreateDrawingContext(false);
                ctx.DrawImage(over, new Rect(over.Size), new Rect(co, over.Size));
            }
        }
        // 팝업(오른쪽 클릭 메뉴 · 메인 메뉴와 그 하위 메뉴)은 별도 창 — 화면 좌표 차이로 창 안 위치를 구해 맨 위에 차례로 합성
        foreach (var popup in popups)
        {
            if (popup is not { IsEffectivelyVisible: true } || popup.Bounds.Width < 1 || popup.Bounds.Height < 1) continue;
            var ps = popup.PointToScreen(new Point(0, 0));
            var ws = this.PointToScreen(new Point(0, 0));
            var o = new Point((ps.X - ws.X) / RenderScaling, (ps.Y - ws.Y) / RenderScaling);
            using var over = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Math.Ceiling(popup.Bounds.Width), (int)Math.Ceiling(popup.Bounds.Height)));
            over.Render(popup);
            using var ctx = rtb.CreateDrawingContext(false);
            ctx.DrawImage(over, new Rect(over.Size), new Rect(o, over.Size));
        }
        rtb.Save(path);
        await Task.CompletedTask;
    }

    /// <summary>
    /// 스모크 --helpshot: 섹션 "?" 말풍선(대표 섹션 — 창 위 · 오른쪽 끝 · 아래 끝 · 3D 위)을 연 메인 창, 도움말 창(처음 · 검색 · 장 이동),
    /// 시나리오 설명 창을 results/smoke/*.png 로 캡처. 말풍선마다 창 안에 들어갔는지 기록한다.
    /// </summary>
    private async Task HelpShots(StringBuilder log)
    {
        var dir = Path.Combine(_repo, "results", "smoke");
        static async Task Shot(Window w, string path)
        {
            await Task.Delay(600);
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height));
            rtb.Render(w);
            rtb.Save(path);
        }
        // 섹션 "?" 말풍선 — 화면에 걸린 모든 "?" 를 하나씩 열어 창 안 배치를 확인하고, 대표 몇 개는 그림으로
        // 화면에 보이는 "?" 만(오른쪽 패널에서 스크롤 밖으로 밀린 것은 누를 수 없다)
        List<HelpTip> VisibleTips(Visual root) => root.GetVisualDescendants().OfType<HelpTip>()
            .Where(t => t.IsEffectivelyVisible && t.Bounds.Width > 0 && t.TranslatePoint(new Point(t.Bounds.Width / 2, t.Bounds.Height / 2), this) is { } c
                        && c.X > 0 && c.Y > 0 && c.X < Bounds.Width && c.Y < Bounds.Height)
            .ToList();
        var tips = VisibleTips(this);
        // 왼쪽 서랍 페이지(입력 · Monte Carlo · JSON)의 "?" — 서랍을 그 페이지로 열고 확인(새 위치마다 다시 연결됐는지)
        var drawerTips = new List<(DrawerPage Page, HelpTip Tip)>();
        bool drawerWasOpen = _paneOn[(int)Pane.Input];
        var pageWas = _page;
        foreach (var page in new[] { DrawerPage.Input, DrawerPage.MonteCarlo, DrawerPage.Json })
        {
            OpenDrawer(page);
            await Task.Delay(350);
            drawerTips.AddRange(VisibleTips(LeftDrawer).Where(t => !tips.Contains(t)).Select(t => (page, t)));
        }
        var shots = new HashSet<string> { HelpKeys.Toolbar, HelpKeys.Tree, HelpKeys.Dashboard, HelpKeys.Status, HelpKeys.View3D, HelpKeys.Plot, HelpKeys.Rail, HelpKeys.Scenario, HelpKeys.MonteCarlo };
        int inside = 0;
        DrawerPage? openPage = null;
        foreach (var (page, tip) in tips.Select(t => ((DrawerPage?)null, t)).Concat(drawerTips.Select(d => ((DrawerPage?)d.Page, d.Tip))))
        {
            if (page is { } pg && openPage != pg) { OpenDrawer(pg); openPage = pg; await Task.Delay(350); }
            else if (page is null && _paneOn[(int)Pane.Input] && !drawerWasOpen) { SetPanes(false, Pane.Input); await Task.Delay(350); }
            tip.Open();
            await Task.Delay(120);
            if (tip.Callout is { } c && c.TranslatePoint(new Point(0, 0), this) is { } p)
            {
                bool ok = p.X >= 0 && p.Y >= 0 && p.X + c.Bounds.Width <= Bounds.Width + 0.5 && p.Y + c.Bounds.Height <= Bounds.Height + 0.5 && c.Bounds.Width > 40;
                if (ok) inside++;
                log.AppendLine(string.Create(Inv, $"ui.callout key={tip.HelpKey} side={c.Side} rect={p.X:F0},{p.Y:F0},{c.Bounds.Width:F0}x{c.Bounds.Height:F0} window={Bounds.Width:F0}x{Bounds.Height:F0} inside={ok}"));
                if (shots.Contains(tip.HelpKey ?? ""))
                {
                    await Task.Delay(300);
                    await SaveWindowShot(Path.Combine(dir, $"callout_{tip.HelpKey!.Split('.').Last().ToLowerInvariant()}.png"), Path.Combine(dir, "view.png"));
                }
            }
            else log.AppendLine($"ui.callout key={tip.HelpKey} open=false");
            tip.Close();
        }
        log.AppendLine($"ui.callouts tips={tips.Count + drawerTips.Count} drawerTips={drawerTips.Count} inside={inside} keys={HelpCatalog.WithPrefix("App.").Count}");
        _page = pageWas;
        SetPanes(drawerWasOpen, Pane.Input);
        await Task.Delay(300);
        var help = new HelpWindow { Icon = Icon };
        help.Show(this);
        await Shot(help, Path.Combine(dir, "help.png"));
        help.ShowChapter(HelpContent.KeyTheme);
        await Shot(help, Path.Combine(dir, "help_chapter.png"));
        var search = help.FindDescendantOfType<TextBox>();
        if (search is not null) search.Text = "휠";
        await Shot(help, Path.Combine(dir, "help_search.png"));
        if (search is not null) search.Text = "";
        // 도움말 창 안의 "?"(찾기 설명)
        if (help.GetVisualDescendants().OfType<HelpTip>().FirstOrDefault() is { } hTip)
        {
            hTip.Open();
            await Shot(help, Path.Combine(dir, "help_callout.png"));
            hTip.Close();
        }
        log.AppendLine(string.Create(Inv, $"ui.helpwindow size={help.Bounds.Width:F0}x{help.Bounds.Height:F0}"));
        help.Close();
        if (_doc is not null)
        {
            var info = new InfoWindow(_doc.Name, ShownPath(_doc.FilePath ?? ""), J.Str(_doc.Root, "description", "")) { Icon = Icon };
            info.Show(this);
            await Shot(info, Path.Combine(dir, "scenario_info.png"));
            info.Close();
        }
        // 대화상자(공통 틀 — 머리 제목 + "?", 카드 내용)
        foreach (var (name, make) in new (string, Func<Window>)[] { ("dialog_credibility", CredibilityWindow), ("dialog_materials", MaterialsWindow) })
        {
            var w = make();
            w.Show(this);
            await Shot(w, Path.Combine(dir, name + ".png"));
            log.AppendLine(string.Create(Inv, $"ui.dialog {name} size={w.Bounds.Width:F0}x{w.Bounds.Height:F0}"));
            w.Close();
        }
        log.AppendLine($"ui.help sections={HelpCatalog.WithPrefix("Guide.").Count}");
    }

    /// <summary>스모크 --menu: 메인 메뉴 하나(와 하위 메뉴 하나)를 펼쳐 창 캡처에 팝업을 합성한다.</summary>
    private async Task<string> MainMenuShot(string name, string? sub, string path, string glPng)
    {
        static MenuItem? Find(IEnumerable<object?> items, string n) =>
            items.OfType<MenuItem>().FirstOrDefault(m => (m.Header?.ToString() ?? "").StartsWith(n, StringComparison.Ordinal));
        static Control? PopupOf(MenuItem m) => m.GetVisualDescendants().OfType<Popup>().FirstOrDefault()?.Child;
        if (Find(MainMenu.Items, name) is not { } top) return $"ui.menu name={name} found=false";
        top.Open();
        await Task.Delay(500);
        var popups = new List<Control?> { PopupOf(top) };
        var subItem = sub is null ? null : Find(top.Items, sub);
        if (subItem is not null)
        {
            subItem.Open();
            await Task.Delay(500);
            popups.Add(PopupOf(subItem));
        }
        await SaveWindowShot(path, glPng, popups.ToArray());
        string s = $"ui.menu name={name} open={top.IsSubMenuOpen} sub={subItem?.Header} popups={popups.Count(p => p is not null)} items={top.Items.OfType<MenuItem>().Count()}";
        subItem?.Close();
        top.Close();
        await Task.Delay(200);
        return s;
    }

    /// <summary>스모크 --frames: 시각 목록(t0:t1:n 구간들)마다 같은 카메라로 창을 캡처한다(데모 GIF 프레임).</summary>
    private async Task<string> FrameShots(string spec, string dir)
    {
        var times = new List<double>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (part.Split(':') is [var a0, var a1, var an])
            {
                double t0 = double.Parse(a0, Inv), t1 = double.Parse(a1, Inv);
                int n = int.Parse(an, Inv);
                for (int k = 0; k < n; k++) times.Add(n == 1 ? t0 : t0 + (t1 - t0) * k / (n - 1));
            }
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "frame_*.png")) File.Delete(old);
        var viewPng = Path.Combine(dir, "view.png");
        for (int i = 0; i < times.Count; i++)
        {
            Seek(times[i]);
            _playing = false;
            await Task.Delay(120);
            if (File.Exists(viewPng)) File.Delete(viewPng);
            View.RequestSnapshot(viewPng);
            for (int w = 0; w < 40 && !File.Exists(viewPng); w++) await Task.Delay(50);
            await Task.Delay(50);
            await SaveWindowShot(Path.Combine(dir, $"frame_{i + 1:D4}.png"), viewPng);
        }
        return string.Create(Inv, $"ui.frames n={times.Count} dir={ShownPath(dir)}");
    }

    private readonly DateTime _smokeStart = DateTime.UtcNow;

    private async Task SmokeTest()
    {
        var log = new StringBuilder();
        int code = 0;
        try
        {
            var a = Program.Args;
            int si = Array.IndexOf(a, "--smoke");
            string? scen = si + 1 < a.Length && !a[si + 1].StartsWith("--") ? a[si + 1] : null;
            string? Arg(string name) => Array.IndexOf(a, name) is var k and >= 0 && k + 1 < a.Length ? a[k + 1] : null;
            // 창 크기(예: --size 1920x1009 = 작업 표시줄을 뺀 작업 영역) — 그 크기에서 잘리지 않는지 캡처로 확인
            if (Arg("--size") is { } sz && sz.Split('x') is [var sw, var sh])
            {
                WindowState = WindowState.Normal;
                Width = double.Parse(sw, Inv);
                Height = double.Parse(sh, Inv);
                Position = new PixelPoint(0, 0);
                await Task.Delay(300);
            }
            log.AppendLine(string.Create(Inv, $"ui.theme={ThemeService.Current} window={Bounds.Width:F0}x{Bounds.Height:F0}"));
            if (scen is not null && scen.EndsWith(".mnsr", StringComparison.OrdinalIgnoreCase)) await LoadResultFromPath(Path.GetFullPath(scen));
            else
            {
                if (scen is not null) LoadScenario(Path.GetFullPath(scen));
                if (Arg("--import") is { } imp) ImportModelFromPath(Path.GetFullPath(imp));
                await RunSimulation();
            }
            log.AppendLine($"scenario={_doc?.Name} simEnd={_world?.T:F3} stop={_world?.StopReason}");
            log.AppendLine($"treeBodies={_bodyItems.Count(b => b is not null)} treeNodes={AllTreeItems().Count()}");
            log.AppendLine($"ui.icon={(Icon is not null ? "set" : "none")}");
            log.AppendLine($"ui.layout browserOverView={BrowserPanel.Parent == ViewGrid} browserWidth={BrowserBox.Width} logSection={LogSection.IsVisible} plotSection={PlotSection.IsVisible} tiles={TileGrid.Children.Count}");
            // 셸: --drawer input|mc|json(왼쪽 서랍 열기) · --pin-left(서랍 고정) · --unpin dash,bottom(겹쳐 뜨기) · --dash closed · --bottom closed
            foreach (var u in (Arg("--unpin") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (u == "dash") _dashPinned = false;
                if (u == "bottom") _bottomPinned = false;
            }
            if (a.Contains("--pin-left")) _leftPinned = true;
            if (Arg("--dash") == "closed") _paneOn[(int)Pane.Telemetry] = false;
            if (Arg("--bottom") == "closed") _paneOn[(int)Pane.Plot] = _paneOn[(int)Pane.Log] = false;
            switch (Arg("--drawer"))
            {
                case "input": _page = DrawerPage.Input; _paneOn[(int)Pane.Input] = true; break;
                case "mc": _page = DrawerPage.MonteCarlo; _paneOn[(int)Pane.Input] = true; break;
                case "json": _page = DrawerPage.Json; _paneOn[(int)Pane.Input] = true; break;
            }
            ApplyPanes();
            SyncChecks();
            await Task.Delay(300);
            log.AppendLine(ShellReport());
            log.AppendLine(AutomationReport());
            // 브라우저: --browser collapsed(머리줄만) | hidden(Ctrl+B 와 같음)
            switch (Arg("--browser"))
            {
                case "collapsed": SetBrowserCollapsed(true); break;
                case "hidden": ToggleBrowser(); break;
            }
            // --hide a,b: 브라우저 눈을 끈 것과 같음(흐린 행 · 3D 숨김 확인)
            foreach (var name in (Arg("--hide") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (AllTreeItems().FirstOrDefault(i => i.HasEye && i.Name == name) is { } hid) hid.Shown = false;
            // --tree-expand: 브라우저 '모두 펼침'과 같음(형상 아래 Fusion 사용자 파라미터 · 조인트 행 확인)
            if (a.Contains("--tree-expand")) foreach (var it in AllTreeItems()) it.IsExpanded = true;
            log.AppendLine($"ui.cad params={AllTreeItems().Count(i => i.Parent?.Name == "사용자 파라미터")} joints={AllTreeItems().Count(i => i.Parent?.Name == "조인트")} " +
                           $"colors={string.Join(",", _world?.Bodies.Select(b => $"{b.Name}:{b.Color}") ?? [])}");
            if (a.Contains("--structural"))
            {
                await RunStructural();
                log.AppendLine($"ui.structural bodies={_structural?.Bodies.Count} events={_world?.EventLog.Count(e => e.Name.EndsWith(".yield") || e.Name.EndsWith(".fracture") || e.Name.EndsWith(".buckling"))} impacts={_structural?.Bodies.Count(h => h.Impact is not null)}");
                foreach (var h in _structural?.Bodies.Where(h => h.Impact is not null) ?? [])
                    log.AppendLine($"ui.impact {h.Body.Name} {h.Impact!.State} {h.Impact.Verdict}");
            }
            if (Arg("--fea") is { } feaBody && _world is not null && _world.BodyMap.TryGetValue(feaBody, out var fb))
            {
                SelectBody(fb.Index);
                await RunFea();
                log.AppendLine($"ui.fea body={feaBody} colored={_feaValues.ContainsKey(fb.Index)} faces={(_feaValues.TryGetValue(fb.Index, out var fv) ? fv.Length : 0)} maxEps={(fv is null ? 0 : fv.Max()):F3}");
            }
            if (Arg("--ccx") is { } ccxBody && _world is not null && _world.BodyMap.TryGetValue(ccxBody, out var cb))
            {
                SelectBody(cb.Index);
                var an = Arg("--ccx-analysis") switch { "modal" => Sim.Core.Fea.CcxAnalysis.Modal, "buckle" => Sim.Core.Fea.CcxAnalysis.Buckle, _ => Sim.Core.Fea.CcxAnalysis.Static };
                double? g = Arg("--ccx-g") is { } gs ? double.Parse(gs, Inv) : null;
                var gdir = Arg("--ccx-dir") switch { "-x" => -Sim.Core.Numerics.Vec3.UnitX, "z" or "+z" => Sim.Core.Numerics.Vec3.UnitZ, "-z" => -Sim.Core.Numerics.Vec3.UnitZ, _ => Sim.Core.Numerics.Vec3.UnitX };
                await RunCcx(new CcxOptions(an, -1, g, gdir, null, Arg("--ccx-mesher") ?? "auto", false));
                var rec = _ccx.TryGetValue(cb.Index, out var rr) ? rr : null;
                log.AppendLine(string.Create(Inv, $"ui.ccx body={ccxBody} analysis={an} colored={rec is not null} faces={rec?.FaceVonMises.Length ?? 0} maxVm={(rec is null ? 0 : rec.FaceVonMises.DefaultIfEmpty(0).Max() / 1e6):F2}MPa deformed={View.MeshDeform?.Count ?? 0} verdict='{rec?.Summary["verdict"]}'"));
            }
            if (a.Contains("--all")) SelectedOnlyBox.IsChecked = false;
            if (a.Contains("--peak")) PeakHoldBox.IsChecked = true;
            if (Arg("--color") is { } cm) SetColorMode(ColorModeInfo.Parse(cm));
            // 분할: --split 1|2|4 · --modes off,stress,pressure,safety(화면 순서, 0번 = 상단 '색상') · --unsync(카메라 따로)
            if (Arg("--modes") is { } modes)
            {
                var ms = modes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ColorModeInfo.Parse).ToArray();
                for (int p = 1; p < Math.Min(ms.Length, _paneModes.Length); p++) _paneModes[p] = ms[p];
                if (ms.Length > 0) SetColorMode(ms[0]);
            }
            if (Arg("--split") is { } split) SetSplit(ViewSplit.FromCount(int.Parse(split, Inv)));
            if (a.Contains("--unsync")) CamSyncBox.IsChecked = false;
            if (a.Contains("--flowlines")) { View.Layers[ViewLayer.CfdStreamlines] = true; SyncChecks(); }
            if (Arg("--save") is { } savePath)
            {
                int framesBefore = _world?.Recorder.Frames.Count ?? 0;
                var full = Path.GetFullPath(savePath);
                bool saved = await SaveResultTo(full);
                bool loaded = saved && a.Contains("--reload") && await LoadResultFromPath(full);
                log.AppendLine($"ui.save ok={saved} reload={loaded} frames {framesBefore} -> {_world?.Recorder.Frames.Count} file={full}");
                if (Arg("--color") is { } cm2) SetColorMode(ColorModeInfo.Parse(cm2));
                log.AppendLine($"ui.save.fea restored={_ccx.Count + _feaValues.Count}");
            }
            // 장면 환경(그 실행에만): --env <환경 id|auto> | --env-file <environment.json|폴더|.hdr|.png>
            //   [--env-rot °] [--env-bright 배율] [--env-ev EV] [--env-bg solid] [--env-refl 거칠기] [--env-offset m] [--env-off ground,shadows,fog,flatten]
            if (Arg("--env") is { } || Arg("--env-file") is { })
            {
                var off = (Arg("--env-off") ?? "").Split(',');
                double? D(string name) => Arg(name) is { } v ? double.Parse(v, Inv) : null;
                var baseSet = View.SceneEnv with
                {
                    EnvironmentId = Arg("--env") ?? View.SceneEnv.EnvironmentId,
                    RotationDeg = D("--env-rot") ?? 0, Brightness = D("--env-bright") ?? 1, ExposureEV = D("--env-ev") ?? 0,
                    BackgroundType = Arg("--env-bg") == "solid" ? "solid" : "environment",
                    GroundReflections = Arg("--env-refl") is not null, GroundRoughness = D("--env-refl") ?? 0.5, GroundOffset = D("--env-offset") ?? 0,
                    GroundDisplayed = !off.Contains("ground"), Shadows = !off.Contains("shadows"), Fog = !off.Contains("fog"), GroundFlattened = !off.Contains("flatten"),
                };
                SetSceneEnv(baseSet);
                if (Arg("--env-file") is { } ef) log.AppendLine($"ui.env.file ok={AddUserEnvironment(Path.GetFullPath(ef))} path={ef}");
                var ae = View.ActiveEnvironment;
                log.AppendLine($"ui.env id={View.SceneEnv.EnvironmentId} active={ae.Id} name='{ae.DisplayName}' soil={View.Scene?.Soil} envs={string.Join(",", View.Environments.All.Select(e => e.Id))} errors={View.Environments.Errors.Count}");
            }
            var dirShot = Path.Combine(_repo, "results", "smoke");
            if (Array.IndexOf(a, "--at") is var ai and >= 0 && ai + 1 < a.Length)
            {
                // 지정 시각으로 이동해 정지 화면 캡처
                _playing = false;
                _time = double.Parse(a[ai + 1], Inv);
                if (Array.IndexOf(a, "--select") is var bi and >= 0 && bi + 1 < a.Length && _world!.BodyMap.TryGetValue(a[bi + 1], out var sb))
                    SelectBody(sb.Index);
                if (Array.IndexOf(a, "--dist") is var di and >= 0 && di + 1 < a.Length) View.ResetCamera(double.Parse(a[di + 1], Inv));
                // --view top|north|east|iso: 카메라 메뉴의 시점 프리셋(1 · 2 · 3 · 4 키와 같음)
                if (Arg("--view") is { } vp && Enum.TryParse<ViewPreset>(vp, true, out var preset)) View.SetPreset(preset);
                if (a.Contains("--section")) SectionBox.IsChecked = true;
                Refresh();
                // 충격 응답(보 동해석)·CalculiX 과도: 끝날 때까지 기다린 뒤 같은 시각으로 다시 그림
                if (_shockTask is { } st) await st;
                if (a.Contains("--ccxshock"))
                {
                    double keep = _time;
                    await RunCcxShock();
                    _time = keep;
                    log.AppendLine(string.Create(Inv, $"ui.ccxshock bodies={_ccxShock.Count} {string.Join("; ", _ccxShock.Values.Select(r => r.Verdict + $" outputs={r.Times.Length} {r.Times.FirstOrDefault():F4}–{r.Times.LastOrDefault():F4}s"))}"));
                }
                // --seek-shock N [--seek-dt s]: Ctrl+] 를 N 번 누른 것과 같음(N 번째 충격 창 시작 = 촉발 − 10 ms, 슬로모션) + s 초
                if (Arg("--seek-shock") is { } ssk)
                {
                    for (int k = 0; k < int.Parse(ssk, Inv); k++) SeekShock(1);
                    if (Arg("--seek-dt") is { } sdt) Seek(_time + double.Parse(sdt, Inv));
                    _playing = false;
                    log.AppendLine(string.Create(Inv, $"ui.seekshock n={ssk} t={_time:F4}"));
                }
                Refresh();
                foreach (var (shi, h) in _shock)
                    log.AppendLine(string.Create(Inv, $"ui.shock {_world!.Bodies[shi].Name} windows={h.Windows.Count} {string.Join("; ", h.Windows.Select((x, k) => $"{x.Label} {x.T0:F3}-{x.T1:F3} peak {(_shockPeaks.TryGetValue((shi, k), out var pk) ? $"{pk.G:F1}G/{pk.MPa:F2}MPa" : "?")}"))} | {h.Method}"));
                log.AppendLine($"ui.section body={View.SectionBody}");
                await Task.Delay(800);
                // --shot-name 이름: view.png 대신 results/smoke/이름.png(여러 시각을 나란히 남길 때)
                var viewPng = Path.Combine(dirShot, (Arg("--shot-name") ?? "view") + ".png");
                View.RequestSnapshot(viewPng);
                await Task.Delay(800);
                await SaveWindowShot(Path.Combine(dirShot, "window.png"), viewPng);
                log.AppendLine(BrowserReport());
                SaveBrowserZoom(Path.Combine(dirShot, "window.png"), Path.Combine(dirShot, "browser_zoom.png"));
                // --browser-menu: 선택 물체 행에서 오른쪽 클릭 메뉴를 열어 results/smoke/window_menu.png
                if (a.Contains("--browser-menu")) log.AppendLine(await BrowserMenuShot(Path.Combine(dirShot, "window_menu.png"), viewPng));
                // --menu 이름 [--submenu 이름]: 메인 메뉴(머리글이 그 이름으로 시작)를 펼친 창 → results/smoke/window_mainmenu.png
                if (Arg("--menu") is { } menuName) log.AppendLine(await MainMenuShot(menuName, Arg("--submenu"), Path.Combine(dirShot, "window_mainmenu.png"), viewPng));
                // --ccx-dialog 물체: CalculiX 옵션 창을 열어 results/smoke/dialog_ccx.png(실행하지 않고 닫음)
                if (Arg("--ccx-dialog") is { } cdb && _world!.BodyMap.TryGetValue(cdb, out var cdBody)) log.AppendLine(await CcxDialogShot(cdBody, Path.Combine(dirShot, "dialog_ccx.png")));
                // --frames t0:t1:n[,t0:t1:n…]: 시각마다 창 캡처 → results/smoke/frames/frame_0001.png …(데모 GIF 합성용)
                if (Arg("--frames") is { } frames) log.AppendLine(await FrameShots(frames, Path.Combine(dirShot, "frames")));
            }
            else
            {
                _rate = 20;
                await Task.Delay(2500);
            }
            if (a.Contains("--gpumc") && _doc?.Root["gpuMonteCarlo"] is JsonObject)
            {
                RunsBox.Text = "20000";
                await RunGpuMc();
                await Task.Delay(1000);
                log.AppendLine($"mc={View.Mc?.Stats.Count} cep={View.Mc?.Stats.Cep50:F2} backend={View.Mc?.Label}");
            }
            log.AppendLine($"ui.legend visible={Legend.IsVisible} title='{Legend.Title}' unit='{Legend.Unit}' min={Legend.Min:G4} max={Legend.Max:G4} map={Legend.ColorMap} bodiesColored={View.FaceColors?.Count ?? 0}");
            if (Legend.IsVisible && Legend.TranslatePoint(new Point(0, 0), View) is { } lp)
                log.AppendLine(string.Create(Inv, $"ui.legendPos left={lp.X:F0} right={lp.X + Legend.Bounds.Width:F0} top={lp.Y:F0} bottom={lp.Y + Legend.Bounds.Height:F0} viewW={View.Bounds.Width:F0} viewH={View.Bounds.Height:F0} ticks='{string.Join("|", ColorScale.Ticks(Legend.Range, Legend.Ticks).Select(t => t.Label))}' noData={Legend.NoData} detail='{Legend.Detail.Replace('\n', '/')}'"));
            // 분할: 화면별 모드·색칠된 물체 수와 GPU 버퍼 — 메시 버퍼는 분할 수와 무관(화면마다 늘어나는 것은 색 버퍼뿐)
            var bs = View.BufferStats;
            log.AppendLine(string.Create(Inv, $"ui.split layout={View.Layout} panes={View.PaneCount} sync={View.SyncCameras} active={View.ActivePane} meshVbos={bs.MeshBuffers} meshBytes={bs.MeshBytes} meshCreations={bs.MeshCreations} colorVbos={bs.ColorBuffers} colorBytes={bs.ColorBytes}"));
            for (int p = 0; p < View.PaneCount; p++)
            {
                var pl = _paneLegends[p];
                var pr = View.PaneRect(p);
                log.AppendLine(string.Create(Inv, $"ui.pane{p} mode={PaneMode(p)} rect={pr.X:F0},{pr.Y:F0},{pr.Width:F0}x{pr.Height:F0} bodiesColored={View.Panes[p].FaceColors?.Count ?? 0} legend={pl.IsVisible} title='{pl.Title}' min={pl.Min:G4} max={pl.Max:G4} note='{pl.Note.Replace('\n', '/')}' detail='{pl.Detail.Replace('\n', '/')}'"));
            }
            if (a.Contains("--helpshot")) await HelpShots(log);
            if (a.Contains("--shellshots")) await ShellShots(log, dirShot);
            if (a.Contains("--updateshot")) await UpdateShots(log, dirShot);
            // 실행 중 테마 전환(도구 막대 ☀/☾ 와 같은 경로) → results/smoke/window_toggled.png — 다시 칠해지는지(DynamicResource · GL 팔레트 · 그래프)
            if (a.Contains("--theme-toggle") && Array.IndexOf(a, "--at") >= 0)
            {
                var before = ThemeService.Current;
                ThemeService.Toggle();
                await Task.Delay(800);
                View.RequestSnapshot(Path.Combine(dirShot, "view_toggled.png"));
                await Task.Delay(800);
                await SaveWindowShot(Path.Combine(dirShot, "window_toggled.png"), Path.Combine(dirShot, "view_toggled.png"));
                log.AppendLine($"ui.theme.toggle {before} -> {ThemeService.Current} button='{ThemeIcon.Kind}' palette.bg={View.Palette.Background}");
            }
            // --theme 을 준 실행은 캡처를 results/smoke/theme_<테마>/ 에도 남긴다(다크 · 라이트 나란히 비교)
            if (Arg("--theme") is { } themeArg && Directory.Exists(dirShot))
            {
                var tdir = Path.Combine(dirShot, "theme_" + (ThemeStore.Parse(themeArg) ?? ThemeService.Current).ToString().ToLowerInvariant());
                Directory.CreateDirectory(tdir);
                foreach (var png in Directory.GetFiles(dirShot, "*.png").Where(p => File.GetLastWriteTimeUtc(p) >= _smokeStart))
                    File.Copy(png, Path.Combine(tdir, Path.GetFileName(png)), overwrite: true);
                log.AppendLine($"ui.theme.shots dir={tdir} arg={themeArg}");
            }
            // 풍동 보기: --windtunnel α [--wt-field cp|speed|cf] → results/smoke/windtunnel.png
            if (Arg("--windtunnel") is { } wta && OpenWindTunnel(double.Parse(wta, Inv)) is { } wt)
            {
                if (Arg("--wt-field") is { } wf) wt.SelectField(Sim.Core.Models.WindTunnel.ParseField(wf));
                log.AppendLine(await wt.Capture(Path.Combine(dirShot, "windtunnel.png")));
                wt.Close();
            }
            // GPU 가상 풍동: --airflow α [--airflow-beta β] [--airflow-field speed|vort] [--airflow-res low|medium|high] [--airflow-frames N] [--airflow-wait s]
            //   → 서버 시작 · N 프레임(기본 60, 평균 계수 준비까지) 대기 후 results/smoke/airflow.png
            if (Arg("--airflow") is { } afa && OpenAirflow(double.Parse(afa, Inv)) is { } af)
            {
                if (Arg("--airflow-field") is "vort" or "vorticity") af.SelectField(1);
                if (Arg("--airflow-beta") is { } afb) af.SetBeta(double.Parse(afb, Inv));
                if (Arg("--airflow-res") is { } afr) af.SetResolution(afr);
                int nf = Arg("--airflow-frames") is { } nfs ? int.Parse(nfs, Inv) : 60;
                double waitS = Arg("--airflow-wait") is { } ws ? double.Parse(ws, Inv) : 180;
                bool ok = await af.WaitFrames(nf, TimeSpan.FromSeconds(waitS), needMean: true);
                log.AppendLine($"ui.airflow.wait ok={ok} frames={af.FramesShown}");
                log.AppendLine(await af.Capture(Path.Combine(dirShot, "airflow.png")));
                af.Close();
                await Task.Delay(1500);   // 서버 종료(quit) 시간
            }
            log.AppendLine($"ui.dashboard body='{DashBody.Text}' mode='{DashMode.Text}' {string.Join(" ", _tiles.Select(t => $"{t.Key}={t.Value.Value.Text}"))}");
            log.AppendLine($"ui.struct {(StructText.Text ?? "").Replace('\n', ' ')}");
            log.AppendLine($"gl={View.GlInfo}");
            log.AppendLine($"framesRendered={View.FramesRendered}");
            log.AppendLine($"summary:\n{SummaryText.Text}");
            log.AppendLine($"log (warn {_warnCount}, error {_errCount}):");
            foreach (var l in _logAll) log.AppendLine(l.Text);
            if (View.FramesRendered < 10 || _world is null) code = 3;
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION " + ex); code = 4; }
        var dir = Path.Combine(_repo, "results", "smoke");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "smoke.log"), log.ToString());
        if (Array.IndexOf(Program.Args, "--shot") >= 0) await Task.Delay(500);
        System.Environment.ExitCode = code;
        Close();
    }
}
