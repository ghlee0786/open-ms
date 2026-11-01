using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sim.App.Rendering;

namespace Sim.App;

/// <summary>
/// 브라우저(어셈블리) — 3D 뷰(분할이면 GL 영역 전체) 왼쪽 위에 겹친 반투명 패널(Fusion 360 BROWSER).
/// 머리줄(접기 · 펼침/접기/모두 보기 · ?) · 오른쪽 가장자리 끌기 폭 조절 · 높이 상한(넘으면 스크롤) · 오른쪽 클릭 메뉴 · Ctrl+B 표시.
/// 보이기 상태는 AsmItem.Shown(눈 토글) — 3D 숨김(View.HiddenBodies)과 같은 데이터.
/// </summary>
public partial class MainWindow
{
    private static readonly KeyGesture BrowserGesture = new(Key.B, KeyModifiers.Control);
    private const double BrowserMinWidth = 210, BrowserDefaultWidth = 280, BrowserMargin = 8;
    private bool _browserCollapsed;
    private double? _gripStartX;
    private double _gripStartW;

    private AsmItem? SelectedTreeItem => AsmTree.SelectedItem as AsmItem;

    private void WireBrowser()
    {
        // 접기(머리줄만) — 화살표 또는 제목 클릭
        BrowserCollapseBtn.IsCheckedChanged += (_, _) => SetBrowserCollapsed(BrowserCollapseBtn.IsChecked != true);
        BrowserTitle.Tapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is null) SetBrowserCollapsed(!_browserCollapsed);
        };

        // 폭: 오른쪽 가장자리 끌기(더블클릭 = 기본 폭)
        BrowserGrip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(BrowserGrip).Properties.IsLeftButtonPressed) return;
            _gripStartX = e.GetPosition(ViewGrid).X;
            _gripStartW = BrowserBox.Bounds.Width > 0 ? BrowserBox.Bounds.Width : BrowserBox.Width;
            e.Pointer.Capture(BrowserGrip);
            e.Handled = true;
        };
        BrowserGrip.PointerMoved += (_, e) =>
        {
            if (_gripStartX is not { } x0) return;
            BrowserBox.Width = ClampBrowserWidth(_gripStartW + e.GetPosition(ViewGrid).X - x0);
            e.Handled = true;
        };
        BrowserGrip.PointerReleased += (_, e) => { _gripStartX = null; e.Pointer.Capture(null); e.Handled = true; };
        BrowserGrip.PointerCaptureLost += (_, _) => _gripStartX = null;
        BrowserGrip.DoubleTapped += (_, e) => { BrowserBox.Width = ClampBrowserWidth(BrowserDefaultWidth); e.Handled = true; };

        // 높이 상한 · HUD 비키기
        ViewGrid.SizeChanged += (_, _) => UpdateBrowserLimits();
        BrowserBox.SizeChanged += (_, _) => UpdateHudOffset();

        // 오른쪽 클릭 = 그 행을 선택한 뒤 메뉴(3D · 대시보드 · 그래프도 그 물체로)
        AsmTree.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(AsmTree).Properties.IsRightButtonPressed) return;
            if ((e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is AsmItem it) AsmTree.SelectedItem = it;
        }, RoutingStrategies.Tunnel);
        // 메뉴 글자 · 사용 가능 · 체크를 여는 행에 맞춤(오른쪽 클릭 · 메뉴 키 · 코드로 연 경우 모두)
        AsmTree.ContextRequested += (_, _) => SyncBrowserMenu();
        BrowserMenu.Opening += (_, _) => SyncBrowserMenu();
        BrowserMenu.Opened += (_, _) => SyncBrowserMenu();
        CmToggleShow.Click += (_, _) => { if (SelectedTreeItem is { HasEye: true } it) it.Shown = !it.Shown; };
        CmIsolate.Click += (_, _) => { if (SelectedTreeItem is { IsBody: true } it) Isolate(it); };
        CmShowAll.Click += (_, _) => ShowAllBodies();
        CmFocus.Click += (_, _) => { if (SelectedTreeItem is { IsBody: true } it) { SelectBody(it.BodyIndex); FocusSelected(); } };
        CmFollow.Click += (_, _) =>
        {
            if (SelectedTreeItem is not { IsBody: true } it) return;
            bool on = !(View.Follow && _selected == it.BodyIndex);
            SelectBody(it.BodyIndex);
            SetFollow(on);
        };
        CmDashboard.Click += (_, _) => { if (SelectedTreeItem is { IsBody: true } it) ShowInDashboard(it); };
    }

    /// <summary>메뉴를 여는 행에 맞춤: 보이기/숨기기 글자, 물체 전용 항목 사용 가능, 추적 체크.</summary>
    private void SyncBrowserMenu()
    {
        var it = SelectedTreeItem;
        bool body = it is { IsBody: true };
        CmToggleShow.Header = it is { HasEye: true, Shown: false } ? "보이기" : "숨기기";
        CmToggleShow.IsEnabled = it is { HasEye: true };
        CmIsolate.IsEnabled = CmFocus.IsEnabled = CmFollow.IsEnabled = CmDashboard.IsEnabled = body;
        CmFollow.IsChecked = body && View.Follow && _selected == it!.BodyIndex;
        CmDashboard.Header = body ? $"대시보드에서 보기 — {it!.Name}" : "대시보드에서 보기 (물체만)";
    }

    /// <summary>이것만 보기 — 모든 눈을 끄고 이 물체(하위 물체 포함)만 켠다.</summary>
    private void Isolate(AsmItem it)
    {
        foreach (var x in AllTreeItems().Where(i => i.HasEye)) x.Shown = false;
        it.Shown = true;
        UpdateHidden();
        Log(LogLevel.Info, "보기", $"이것만 보기: {it.Name} (모두 보기 = 브라우저 ◉ · Ctrl+0)");
    }

    /// <summary>대시보드에서 보기 — 오른쪽 대시보드 패널을 열고(미끄러져 들어옴) 맨 위(선택 물체 카드)로, 그 물체 선택.</summary>
    private void ShowInDashboard(AsmItem it)
    {
        if (!_paneOn[(int)Pane.Telemetry]) { _paneOn[(int)Pane.Telemetry] = true; ApplyPanes(); SyncChecks(); }
        SelectBody(it.BodyIndex);
        RightPanel.Offset = new Vector(RightPanel.Offset.X, 0);
    }

    /// <summary>접기 = 머리줄만(트리 숨김). 화살표 단추 상태도 맞춘다.</summary>
    private void SetBrowserCollapsed(bool collapsed)
    {
        _browserCollapsed = collapsed;
        AsmTree.IsVisible = !collapsed;
        BrowserHead.Classes.Set("collapsed", collapsed);
        if (BrowserCollapseBtn.IsChecked != !collapsed) BrowserCollapseBtn.IsChecked = !collapsed;
    }

    /// <summary>브라우저 표시 켜기/끄기(Ctrl+B · 보기 메뉴 '브라우저 표시').</summary>
    private void ToggleBrowser()
    {
        _paneOn[(int)Pane.Tree] = !_paneOn[(int)Pane.Tree];
        ApplyPanes();
        SyncChecks();
    }

    private double ClampBrowserWidth(double w)
    {
        double max = ViewGrid.Bounds.Width > 0 ? Math.Max(BrowserMinWidth, ViewGrid.Bounds.Width * 0.55) : 600;
        return Math.Clamp(w, BrowserMinWidth, max);
    }

    /// <summary>
    /// 높이 상한: 1행 배치(1 · 2 분할) = GL 높이의 72 %, 4 분할 = 위 칸 높이 − 44(0번 화면 왼쪽 아래 색 모드 콤보를 가리지 않게). 폭은 GL 폭의 55 % 까지.
    /// </summary>
    private void UpdateBrowserLimits()
    {
        double h = ViewGrid.Bounds.Height;
        if (h <= 0) return;
        int rows = ViewSplit.Grid(View.Layout).Rows;
        BrowserBox.MaxHeight = Math.Max(120, rows > 1 ? h / rows - 44 : h * 0.72 - BrowserMargin);
        BrowserBox.Width = ClampBrowserWidth(BrowserBox.Width);
    }

    /// <summary>HUD(선택 · 카메라 글)는 같은 왼쪽 위 — 브라우저가 보이면 그 오른쪽으로 비킨다.</summary>
    private void UpdateHudOffset()
    {
        double left = BrowserPanel.IsVisible ? BrowserMargin + BrowserBox.Width + BrowserMargin : BrowserMargin;
        Overlay.Margin = new Thickness(left, BrowserMargin, BrowserMargin, BrowserMargin);
    }

    /// <summary>스모크 기록 — 배치 · 상한 · 행 수 · 스크롤 · 입력이 패널로 가는지(3D 로 새지 않는지) · 아이콘 종류.</summary>
    private string BrowserReport()
    {
        var inv = CultureInfo.InvariantCulture;
        var p = BrowserBox.TranslatePoint(new Point(0, 0), ViewGrid) ?? default;
        var hud = Overlay.TranslatePoint(new Point(0, 0), ViewGrid) ?? default;
        var cam = CamBar.TranslatePoint(new Point(0, 0), ViewGrid) ?? default;
        int rows = AsmTree.IsVisible ? AsmTree.GetVisualDescendants().OfType<TreeViewItem>().Count(t => t.IsEffectivelyVisible) : 0;
        var sv = AsmTree.FindDescendantOfType<ScrollViewer>();
        // 3D(GlView)는 장면 영역(ViewGrid) 밖 — 본문(Body) 기준으로 맞혀 본다
        string Hit(Point pt) => ViewGrid.TranslatePoint(pt, Body) is { } bp && Body.InputHitTest(bp) is Visual v
            ? IsInBrowser(v) ? "browser" : v.FindAncestorOfType<GlView>(includeSelf: true) is not null ? "3D" : v.GetType().Name
            : "none";
        var inside = new Point(p.X + BrowserBox.Bounds.Width / 2, p.Y + Math.Min(BrowserBox.Bounds.Height - 4, 60));
        var outside = new Point(p.X + BrowserBox.Bounds.Width + 60, p.Y + BrowserBox.Bounds.Height + 40);
        return string.Create(inv, $"ui.browser visible={BrowserPanel.IsVisible} collapsed={_browserCollapsed} rect={p.X:F0},{p.Y:F0},{BrowserBox.Bounds.Width:F0}x{BrowserBox.Bounds.Height:F0} maxH={BrowserBox.MaxHeight:F0} view={ViewGrid.Bounds.Width:F0}x{ViewGrid.Bounds.Height:F0} layout={View.Layout} rows={rows} scroll={sv?.Extent.Height ?? 0:F0}/{sv?.Viewport.Height ?? 0:F0} hitInside={Hit(inside)} hitOutside={Hit(outside)} hudLeft={hud.X:F0} camLeft={cam.X:F0} icons={string.Join(",", AllTreeItems().Select(i => i.IconKind).Distinct())}");
    }

    /// <summary>스모크 확인용 — 창 캡처에서 브라우저 둘레(메뉴가 열려 있으면 메뉴까지)를 잘라 2배로 저장.</summary>
    private void SaveBrowserZoom(string windowPng, string path, double extraWidth = 0)
    {
        if (!File.Exists(windowPng) || BrowserBox.TranslatePoint(new Point(0, 0), this) is not { } o) return;
        using var src = new Avalonia.Media.Imaging.Bitmap(windowPng);
        var r = new Rect(Math.Max(0, o.X - 8), Math.Max(0, o.Y - 8), BrowserBox.Bounds.Width + 16 + extraWidth, BrowserBox.Bounds.Height + 16)
            .Intersect(new Rect(src.Size));
        if (r.Width < 1 || r.Height < 1) return;
        using var dst = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)(r.Width * 2), (int)(r.Height * 2)));
        using (var ctx = dst.CreateDrawingContext(true))
        using (ctx.PushRenderOptions(new Avalonia.Media.RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.None }))
            ctx.DrawImage(src, r, new Rect(0, 0, r.Width * 2, r.Height * 2));
        dst.Save(path);
    }

    private bool IsInBrowser(Visual v)
    {
        for (Visual? x = v; x is not null; x = x.GetVisualParent())
            if (ReferenceEquals(x, BrowserPanel)) return true;
        return false;
    }

    /// <summary>스모크 --browser-menu: 선택 물체 행에서 오른쪽 클릭 메뉴를 열고 창 캡처(메뉴는 별도 팝업 창 → 합성).</summary>
    private async Task<string> BrowserMenuShot(string path, string glPng)
    {
        if (!BrowserPanel.IsVisible || _browserCollapsed) return "ui.browser.menu skipped=hidden-or-collapsed";
        var items = AsmTree.GetVisualDescendants().OfType<TreeViewItem>().Where(t => t.IsEffectivelyVisible).ToList();
        var row = items.FirstOrDefault(t => t.DataContext is AsmItem { IsBody: true } it && it.BodyIndex == _selected)
                  ?? items.FirstOrDefault(t => t.DataContext is AsmItem { IsBody: true });
        if (row is null) return "ui.browser.menu row=none";
        AsmTree.SelectedItem = row.DataContext;
        var header = row.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_LayoutRoot") ?? row;
        // 메뉴는 트리에 붙어 있다 — 위치 기준만 그 행으로(포인터 없이 열 때)
        BrowserMenu.Placement = PlacementMode.RightEdgeAlignedTop;
        BrowserMenu.PlacementTarget = header;
        SyncBrowserMenu();
        BrowserMenu.Open(AsmTree);
        await Task.Delay(500);
        await SaveWindowShot(path, glPng, BrowserMenu);
        SaveBrowserZoom(path, Path.Combine(Path.GetDirectoryName(path)!, "browser_menu_zoom.png"), BrowserMenu.Bounds.Width + 8);
        var heads = BrowserMenu.Items.OfType<MenuItem>().Select(m => $"{m.Header}{(m.IsEnabled ? "" : "(off)")}{(m.IsChecked ? "(v)" : "")}");
        string s = $"ui.browser.menu open={BrowserMenu.IsOpen} row='{(row.DataContext as AsmItem)?.Name}' items='{string.Join(" | ", heads)}'";
        BrowserMenu.Close();
        BrowserMenu.Placement = PlacementMode.Pointer;
        BrowserMenu.PlacementTarget = null;
        return s;
    }
}
