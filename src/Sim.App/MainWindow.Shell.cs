using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Sim.App.Rendering;

namespace Sim.App;

/// <summary>
/// 셸 — 참고(Dotnet.Monitoring ShellView) 배치를 Avalonia 로 재구성:
/// 사용자 정의 제목 표시줄(끌어 이동 · 더블클릭 최대화 · 창 단추) · 3D 가 본문 전체 · 왼쪽 아이콘 레일 + 서랍(CompactOverlay) ·
/// 오른쪽 대시보드 패널 · 아래 서랍(그래프 | 로그) · 닫힌 패널 다시 열기 둥근 단추. 열고 닫기 = 0.2 s 미끄러짐 + 페이드(참고 Grow/Shrink 0.2 s).
/// 패널 '고정' = 3D 장면 영역(<see cref="GlView.ContentInset"/> = ViewGrid.Margin)이 그 패널을 비켜 그림, 해제 = 3D 위에 겹침.
/// </summary>
public partial class MainWindow
{
    private enum DrawerPage { Input, MonteCarlo, Json }

    private const double RailWidth = 56;
    private const double LeftDefault = 320, DashDefault = 340, BottomDefault = 260;
    private static readonly TimeSpan ShellAnim = TimeSpan.FromSeconds(0.2);
    private DrawerPage _page = DrawerPage.Input;
    private bool _leftPinned, _dashPinned = true, _bottomPinned = true;
    private bool _shellAnimate;
    private readonly Dictionary<Control, int> _shellVersion = new();

    private void WireShell()
    {
        // ---- 제목 표시줄: 빈 곳 끌기 = 창 이동, 더블클릭 = 최대화 ↔ 복원 ----
        foreach (var area in new Control[] { DragArea, TitleBrand })
            area.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(area).Properties.IsLeftButtonPressed) return;
                if (e.ClickCount >= 2) ToggleMaximize();
                else BeginMoveDrag(e);
                e.Handled = true;
            };
        MinBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxBtn.Click += (_, _) => ToggleMaximize();
        CloseBtn.Click += (_, _) => Close();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == OffScreenMarginProperty) SyncWindowChrome();
        };
        // 좁은 창에서는 제목 글을 숨겨 메뉴 · 빠른 단추 자리를 지킨다(아이콘은 남음)
        SizeChanged += (_, _) => TitleText.IsVisible = Bounds.Width >= 1320;

        // ---- 레일: 서랍 페이지 ----
        RailInput.Click += (_, _) => ToggleDrawer(DrawerPage.Input);
        RailMc.Click += (_, _) => ToggleDrawer(DrawerPage.MonteCarlo);
        RailJson.Click += (_, _) => ToggleDrawer(DrawerPage.Json);
        // ---- 레일: 묶음(메인 메뉴와 같은 항목) ----
        RailFile.Click += (_, _) => OpenRailGroup(RailFile, TopMenu("파일"));
        RailAnalysis.Click += (_, _) => OpenRailGroup(RailAnalysis, AnalysisMenu.Items.OfType<Control>());
        RailWind.Click += (_, _) => OpenRailGroup(RailWind, [MiWindTunnel, MiAirflow]);
        // ---- 레일: 패널 켜기 · 끄기 ----
        RailBrowser.Click += (_, _) => ToggleBrowser();
        RailDash.Click += (_, _) => SetPanes(!_paneOn[(int)Pane.Telemetry], Pane.Telemetry);
        RailBottom.Click += (_, _) => SetPanes(!BottomOpen, Pane.Plot, Pane.Log);
        RailHelp.Click += async (_, _) => await ShowHelp();

        // ---- 패널 머리: 고정 · 닫기 · 섹션 켜기 ----
        LeftPinBtn.Click += (_, _) => { _leftPinned = LeftPinBtn.IsChecked == true; ApplyPanes(); };
        DashPinBtn.Click += (_, _) => { _dashPinned = DashPinBtn.IsChecked == true; ApplyPanes(); };
        BottomPinBtn.Click += (_, _) => { _bottomPinned = BottomPinBtn.IsChecked == true; ApplyPanes(); };
        LeftCloseBtn.Click += (_, _) => SetPanes(false, Pane.Input);
        DashCloseBtn.Click += (_, _) => SetPanes(false, Pane.Telemetry);
        BottomCloseBtn.Click += (_, _) => SetPanes(false, Pane.Plot, Pane.Log);
        PlotToggle.Click += (_, _) => SetPanes(PlotToggle.IsChecked == true, Pane.Plot);
        LogToggle.Click += (_, _) => SetPanes(LogToggle.IsChecked == true, Pane.Log);
        DashFab.Click += (_, _) => SetPanes(true, Pane.Telemetry);
        BottomFab.Click += (_, _) => SetPanes(true, Pane.Plot, Pane.Log);

        // ---- 가장자리 끌기(폭 · 높이) — 더블클릭 = 기본 ----
        WireGrip(LeftGrip, horizontal: true, sign: 1, () => LeftDrawer.Width, w => LeftDrawer.Width = ClampLeft(w), LeftDefault);
        WireGrip(DashGrip, horizontal: true, sign: -1, () => DashPanel.Width, w => DashPanel.Width = ClampDash(w), DashDefault);
        WireGrip(BottomGrip, horizontal: false, sign: -1, () => BottomDrawer.Height, h => BottomDrawer.Height = ClampBottom(h), BottomDefault);
        Body.SizeChanged += (_, _) =>
        {
            LeftDrawer.Width = ClampLeft(LeftDrawer.Width);
            DashPanel.Width = ClampDash(DashPanel.Width);
            BottomDrawer.Height = ClampBottom(BottomDrawer.Height);
            LayoutShell();
        };
        FloatStack.SizeChanged += (_, _) => UpdatePaneMargins();

        // 고정하지 않은 서랍(3D 위에 겹침)은 3D 를 누르면 닫힘(참고 HamburgerMenu CompactOverlay)
        View.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (_paneOn[(int)Pane.Input] && !_leftPinned) SetPanes(false, Pane.Input);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // 시작 모양: 레일 + 3D + 오른쪽 대시보드 + 아래 서랍(왼쪽 서랍은 닫힘) — 첫 배치는 애니메이션 없이
        _paneOn[(int)Pane.Input] = false;
        ApplyPanes();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _shellAnimate = true, DispatcherPriority.Background);
        SyncWindowChrome();
    }

    private bool BottomOpen => _paneOn[(int)Pane.Plot] || _paneOn[(int)Pane.Log];

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>최대화 단추 아이콘 · 이름, 최대화 때 화면 밖으로 나간 틀만큼 안쪽 여백(NoChrome 창).</summary>
    private void SyncWindowChrome()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxIcon.Kind = max ? MaterialIconKind.WindowRestore : MaterialIconKind.WindowMaximize;
        ToolTip.SetTip(MaxBtn, max ? "이전 크기로" : "최대화");
        AutomationProperties.SetName(MaxBtn, max ? "이전 크기로" : "최대화");
        RootPanel.Margin = max ? OffScreenMargin : default;
    }

    private IEnumerable<Control> TopMenu(string startsWith) =>
        MainMenu.Items.OfType<MenuItem>().First(m => (m.Header as string ?? "").StartsWith(startsWith, StringComparison.Ordinal)).Items.OfType<Control>();

    /// <summary>패널 켜기 · 끄기(보기 메뉴 '패널' 과 같은 상태).</summary>
    private void SetPanes(bool on, params Pane[] panes)
    {
        foreach (var p in panes) _paneOn[(int)p] = on;
        ApplyPanes();
        SyncChecks();
    }

    /// <summary>레일 페이지 아이콘: 닫혀 있거나 다른 페이지면 그 페이지로 열고, 같은 페이지면 접는다.</summary>
    private void ToggleDrawer(DrawerPage page)
    {
        bool open = _paneOn[(int)Pane.Input];
        if (open && _page == page) { SetPanes(false, Pane.Input); return; }
        _page = page;
        SetPanes(true, Pane.Input);
    }

    /// <summary>서랍을 그 페이지로 연다(코드 · 스모크).</summary>
    private void OpenDrawer(DrawerPage page)
    {
        _page = page;
        SetPanes(true, Pane.Input);
    }

    private void ShowDrawerPage()
    {
        InputPage.IsVisible = _page == DrawerPage.Input;
        McPage.IsVisible = _page == DrawerPage.MonteCarlo;
        JsonPage.IsVisible = _page == DrawerPage.Json;
        (DrawerTitle.Text, DrawerIcon.Kind) = _page switch
        {
            DrawerPage.MonteCarlo => ("Monte Carlo", MaterialIconKind.ChartScatterPlot),
            DrawerPage.Json => ("시나리오 JSON", MaterialIconKind.CodeJson),
            _ => ("입력", MaterialIconKind.TuneVariant),
        };
    }

    /// <summary>패널 상태(_paneOn · 고정)를 화면에 — 미끄러짐 · 장면 영역 · 다시 열기 단추 · 레일 체크.</summary>
    private void ApplyPanes()
    {
        bool inp = _paneOn[(int)Pane.Input], tree = _paneOn[(int)Pane.Tree], tel = _paneOn[(int)Pane.Telemetry];
        bool plot = _paneOn[(int)Pane.Plot], log = _paneOn[(int)Pane.Log], bot = plot || log;
        BrowserPanel.IsVisible = tree;
        UpdateHudOffset();
        ShowDrawerPage();
        Animate(LeftDrawer, inp, "translate(0px, 0px)", "translate(-48px, 0px)");
        Animate(DashPanel, tel, "translate(0px, 0px)", "translate(96px, 0px)");
        Animate(BottomDrawer, bot, "translate(0px, 0px)", "translate(0px, 72px)");
        Animate(DashFab, !tel, "scale(1)", "scale(0)");
        Animate(BottomFab, !bot, "scale(1)", "scale(0)");
        SetCol(BottomGrid.ColumnDefinitions[0], plot, new GridLength(1, GridUnitType.Star));
        SetCol(BottomGrid.ColumnDefinitions[1], plot && log, new GridLength(4));
        SetCol(BottomGrid.ColumnDefinitions[2], log, new GridLength(1, GridUnitType.Star));
        PlotSection.IsVisible = plot; LogSection.IsVisible = log;
        LayoutShell();
        SyncShellChecks();
        Redraw();
    }

    /// <summary>
    /// 패널 자리 · 3D 장면 영역(고정된 열린 패널을 비킨 사각형). 겹쳐 뜬(고정 해제) 패널 밑에 깔리는 떠 있는 막대 · 단추는 그 패널 바깥으로 비킨다.
    /// </summary>
    private void LayoutShell()
    {
        bool inp = _paneOn[(int)Pane.Input], tel = _paneOn[(int)Pane.Telemetry], bot = BottomOpen;
        double leftW = LeftDrawer.Width, dashW = DashPanel.Width, botH = BottomDrawer.Height;
        double sceneL = RailWidth + (inp && _leftPinned ? leftW : 0);
        double sceneR = tel && _dashPinned ? dashW : 0;
        double sceneB = bot && _bottomPinned ? botH : 0;
        LeftDrawer.Margin = new Thickness(RailWidth, 0, 0, 0);
        BottomDrawer.Margin = new Thickness(sceneL, 0, tel ? dashW : 0, 0);
        var inset = new Thickness(sceneL, 0, sceneR, sceneB);
        View.ContentInset = inset;
        ViewGrid.Margin = inset;
        // 겹쳐 뜬 패널 밑으로 들어가지 않게
        double lift = bot && !_bottomPinned ? botH : 0;
        double dashOver = tel && !_dashPinned ? dashW : 0;
        int fabs = (tel ? 0 : 1) + (bot ? 0 : 1);
        PlayBar.Margin = new Thickness(12, 0, 12 + fabs * 43 + dashOver, 12 + lift);
        FabBar.Margin = new Thickness(0, 0, 14 + dashOver, 16 + lift);
        BusyOverlay.Margin = new Thickness(0, 0, 0, 76 + lift);
        FloatStack.Margin = new Thickness(8, 8, 8 + dashOver, 8);
        UpdatePaneMargins();
    }

    /// <summary>레일 · 머리줄 토글 체크를 상태에 맞춤.</summary>
    private void SyncShellChecks()
    {
        bool inp = _paneOn[(int)Pane.Input];
        RailInput.IsChecked = inp && _page == DrawerPage.Input;
        RailMc.IsChecked = inp && _page == DrawerPage.MonteCarlo;
        RailJson.IsChecked = inp && _page == DrawerPage.Json;
        RailBrowser.IsChecked = _paneOn[(int)Pane.Tree];
        RailDash.IsChecked = _paneOn[(int)Pane.Telemetry];
        RailBottom.IsChecked = BottomOpen;
        PlotToggle.IsChecked = _paneOn[(int)Pane.Plot];
        LogToggle.IsChecked = _paneOn[(int)Pane.Log];
        foreach (var (btn, on) in new[] { (LeftPinBtn, _leftPinned), (DashPinBtn, _dashPinned), (BottomPinBtn, _bottomPinned) })
        {
            btn.IsChecked = on;
            if (btn.Content is MaterialIcon icon) icon.Kind = on ? MaterialIconKind.Pin : MaterialIconKind.PinOutline;
        }
    }

    /// <summary>
    /// 분할 화면 칸의 레전드 · 모드 콤보 여백: 오른쪽 위 칸은 떠 있는 막대(카메라 · 표시) 아래로, 아래 줄 칸은 재생 막대 위로.
    /// </summary>
    private void UpdatePaneMargins()
    {
        if (_paneCells[0] is null) return;
        var (rows, cols) = ViewSplit.Grid(View.Layout);
        double top = Math.Max(40, FloatStack.Bounds.Height + 16);
        for (int p = 0; p < ViewSplit.MaxPanes; p++)
        {
            int row = p / cols, col = p % cols;
            bool bottomRow = row == rows - 1, topRight = row == 0 && col == cols - 1;
            foreach (var vb in _paneCells[p].Children.OfType<Viewbox>())
                vb.Margin = new Thickness(0, topRight ? top : 40, 10, bottomRow ? 84 : 36);
            // 재생 막대(높이 ≈ 60 + 아래 여백 12) 위로
            _paneModeBoxes[p].Margin = new Thickness(6, 6, 6, bottomRow ? 80 : 6);
        }
    }

    /// <summary>
    /// 열고 닫기 0.2 s(참고 Grow/Shrink · 패널 미끄러짐 + 페이드). 닫힘은 전환이 끝난 뒤 숨김(입력 안 받음). 첫 배치 · 스모크 시작 전은 즉시.
    /// </summary>
    private void Animate(Control c, bool open, string openTransform, string closedTransform)
    {
        int ver = _shellVersion[c] = _shellVersion.GetValueOrDefault(c) + 1;
        if (_shellAnimate && c.Transitions is null)
            c.Transitions =
            [
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = ShellAnim, Easing = new CubicEaseOut() },
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = ShellAnim },
            ];
        if (open)
        {
            c.IsVisible = true;
            c.IsHitTestVisible = true;
            c.RenderTransform = TransformOperations.Parse(openTransform);
            c.Opacity = 1;
            return;
        }
        c.IsHitTestVisible = false;
        c.RenderTransform = TransformOperations.Parse(closedTransform);
        c.Opacity = 0;
        if (!_shellAnimate) { c.IsVisible = false; return; }
        DispatcherTimer.RunOnce(() => { if (_shellVersion[c] == ver) c.IsVisible = false; }, ShellAnim + TimeSpan.FromMilliseconds(40));
    }

    private double ClampLeft(double w) => Math.Clamp(w, 260, Math.Max(260, Math.Min(560, Body.Bounds.Width * 0.45)));
    private double ClampDash(double w) => Math.Clamp(w, 280, Math.Max(280, Math.Min(620, Body.Bounds.Width * 0.45)));
    private double ClampBottom(double h) => Math.Clamp(h, 140, Math.Max(160, Body.Bounds.Height * 0.7));

    /// <summary>패널 가장자리 끌기 — sign = 끄는 방향과 크기 변화의 부호(왼쪽 가장자리 · 위 가장자리 = −1).</summary>
    private void WireGrip(Border grip, bool horizontal, int sign, Func<double> get, Action<double> set, double def)
    {
        double? start = null;
        double startSize = 0;
        double Pos(PointerEventArgs e) { var p = e.GetPosition(Body); return horizontal ? p.X : p.Y; }
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount >= 2) { set(def); LayoutShell(); e.Handled = true; return; }
            start = Pos(e);
            startSize = get();
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (start is not { } s0) return;
            set(startSize + sign * (Pos(e) - s0));
            LayoutShell();
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) => { start = null; e.Pointer.Capture(null); e.Handled = true; };
        grip.PointerCaptureLost += (_, _) => start = null;
    }

    /// <summary>
    /// 레일 묶음 아이콘 → 레일 오른쪽에 멤버 목록(참고 LeftMenu 묶음 ContextMenu Placement=Right). 항목은 메인 메뉴의 복제 —
    /// 머리글 · 단축키 · 사용 가능 · 체크를 그대로, 누르면 원래 메뉴 항목의 Click 을 일으킨다(동작은 한 곳).
    /// </summary>
    private void OpenRailGroup(Button anchor, IEnumerable<Control> source)
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.RightEdgeAlignedTop, PlacementTarget = anchor, MinWidth = 220, MaxWidth = 760,
            HorizontalOffset = 6,
        };
        foreach (var x in CloneMenu(source)) menu.Items.Add(x);
        anchor.ContextMenu = menu;
        menu.Open(anchor);
    }

    private static IEnumerable<Control> CloneMenu(IEnumerable<Control> source)
    {
        foreach (var x in source)
        {
            if (x is Separator) { yield return new Separator(); continue; }
            if (x is not MenuItem src || !src.IsVisible) continue;
            var mi = new MenuItem
            {
                Header = src.Header, InputGesture = src.InputGesture, IsEnabled = src.IsEnabled,
                ToggleType = src.ToggleType, IsChecked = src.IsChecked, GroupName = src.GroupName,
            };
            var kids = src.Items.OfType<Control>().ToList();
            if (kids.Count > 0) foreach (var k in CloneMenu(kids)) mi.Items.Add(k);
            else mi.Click += (_, _) => src.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, src));
            yield return mi;
        }
    }

    /// <summary>재생 단추 아이콘(재생 ↔ 일시정지).</summary>
    private void SetPlayIcon(bool playing)
    {
        PlayIcon.Kind = playing ? MaterialIconKind.Pause : MaterialIconKind.Play;
        AutomationProperties.SetName(PlayBtn, playing ? "일시정지" : "재생");
    }

    /// <summary>스모크 기록 — 셸 배치(장면 영역 · 패널 · 고정 · 레일 체크) 한 줄.</summary>
    private string ShellReport()
    {
        var inv = CultureInfo.InvariantCulture;
        string R(Control c) => c.IsVisible && c.TranslatePoint(new Point(0, 0), this) is { } p
            ? string.Create(inv, $"{p.X:F0},{p.Y:F0},{c.Bounds.Width:F0}x{c.Bounds.Height:F0}") : "hidden";
        var ins = View.ContentInset;
        return string.Create(inv,
            $"ui.shell inset={ins.Left:F0},{ins.Top:F0},{ins.Right:F0},{ins.Bottom:F0} scene={ViewGrid.Bounds.Width:F0}x{ViewGrid.Bounds.Height:F0} " +
            $"titlebar={TitleBar.Bounds.Height:F0} rail={R(Rail)} drawer={(_paneOn[(int)Pane.Input] ? _page.ToString() : "closed")}:{R(LeftDrawer)} pinned={_leftPinned} " +
            $"dash={R(DashPanel)} pinned={_dashPinned} bottom={R(BottomDrawer)} pinned={_bottomPinned} playbar={R(PlayBar)} " +
            $"fabs={(DashFab.IsVisible ? "dash" : "")}{(BottomFab.IsVisible ? "+bottom" : "")} float={R(FloatStack)} maximized={WindowState == WindowState.Maximized}");
    }

    /// <summary>스모크: 남아 있어야 하는 자동화 ID(이름) · 메뉴 머리글 점검.</summary>
    private string AutomationReport()
    {
        string[] ids =
        [
            "TimeSlider", "PlayBtn", "PrevEventBtn", "NextEventBtn", "RateBox", "PlotSel", "LogLevelBox", "LogSearch", "LogCopyBtn", "LogAutoScroll",
            "ScenarioBox", "ScenarioInfoBtn", "SplitBox", "ColorModeBox", "ColorMapBox", "ColorOptsBtn", "SectionBox", "ThemeBtn", "HelpBtn",
            "TbOpen", "TbLoad", "TbSave", "TbRun", "TbStruct", "OpenBtn", "ImportBtn", "RunBtn", "SaveBtn", "TurbBox", "AltBox", "SpeedBox",
            "HeadingBox", "WindBox", "WindDirBox", "DtBox", "RunsBox", "FullRunsBox", "FollowToggle", "ZoomInBtn", "ZoomOutBtn", "FocusBtn",
            "FitBtn", "ResetCamBtn", "TreeExpandBtn", "TreeCollapseBtn", "TreeShowAllBtn", "WarnBadge", "ErrBadge",
        ];
        var missing = ids.Where(id => this.FindControl<Control>(id) is null).ToList();
        var heads = MainMenu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        int items = MainMenu.Items.OfType<MenuItem>().Sum(m => m.Items.OfType<MenuItem>().Count());
        return $"ui.automation ids={ids.Length - missing.Count}/{ids.Length} missing='{string.Join(",", missing)}' menus='{string.Join("|", heads)}' menuItems={items}";
    }

    /// <summary>
    /// 스모크 --shellshots: 셸 상태별 창 캡처(results/smoke/shell_*.png) — 기본 · 서랍(겹침) · 서랍(고정) · 패널 모두 닫힘(다시 열기 단추) · 패널 겹침.
    /// 장면 영역이 바뀌므로 상태마다 GL 스냅숏을 새로 떠서 합성한다.
    /// </summary>
    private async Task ShellShots(System.Text.StringBuilder log, string dir)
    {
        var saved = (_paneOn.ToArray(), _page, _leftPinned, _dashPinned, _bottomPinned);
        async Task Shot(string name)
        {
            await Task.Delay(400);
            var gl = Path.Combine(dir, $"view_{name}.png");
            View.RequestSnapshot(gl);
            await Task.Delay(900);
            await SaveWindowShot(Path.Combine(dir, $"shell_{name}.png"), gl);
            log.AppendLine($"{ShellReport()} shot={name}");
        }
        await Shot("default");
        OpenDrawer(DrawerPage.Input);
        await Shot("drawer_input");
        OpenDrawer(DrawerPage.MonteCarlo);
        await Shot("drawer_mc");
        _leftPinned = true;
        OpenDrawer(DrawerPage.Json);
        await Shot("drawer_json_pinned");
        _leftPinned = false;
        SetPanes(false, Pane.Input, Pane.Telemetry, Pane.Plot, Pane.Log);
        await Shot("closed");
        _dashPinned = _bottomPinned = false;
        SetPanes(true, Pane.Telemetry, Pane.Plot, Pane.Log);
        await Shot("overlay");
        // 묶음 메뉴(해석) — 레일 오른쪽 멤버 목록
        OpenRailGroup(RailAnalysis, AnalysisMenu.Items.OfType<Control>());
        await Task.Delay(400);
        if (RailAnalysis.ContextMenu is { } cm)
        {
            await SaveWindowShot(Path.Combine(dir, "shell_railgroup.png"), Path.Combine(dir, "view_overlay.png"), cm);
            log.AppendLine($"ui.shell.railgroup open={cm.IsOpen} items={cm.Items.OfType<MenuItem>().Count()}");
            cm.Close();
        }
        // 복제 항목이 원래 메뉴 동작을 일으키는지: 추적 끔 → 복제한 '카메라 초기화' 클릭 → 추적이 다시 켜져야 함
        SetFollow(false);
        var reset = CloneMenu(TopMenu("카메라")).OfType<MenuItem>().First(m => m.Header as string == "카메라 초기화");
        reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, reset));
        log.AppendLine($"ui.shell.cloneClick header='{reset.Header}' followAfter={View.Follow}");
        (var on, _page, _leftPinned, _dashPinned, _bottomPinned) = saved;
        Array.Copy(on, _paneOn, _paneOn.Length);
        ApplyPanes();
        SyncChecks();
        await Task.Delay(300);
    }
}
