using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sim.App.Controls;
using Sim.App.Rendering;

namespace Sim.App;

/// <summary>
/// 3D 뷰 1·2·4 분할: 화면마다 같은 장면을 다른 색 모드로(메시는 GlView 안에서 한 벌 공유).
/// 화면마다 레전드(오른쪽 가운데, 작은 화면에서는 축소)와 색 모드 콤보(왼쪽 아래).
/// </summary>
public partial class MainWindow
{
    private static readonly SplitLayout[] SplitChoices = [SplitLayout.Single, SplitLayout.Dual, SplitLayout.Quad];
    private readonly ColorLegend[] _paneLegends = Enumerable.Range(0, ViewSplit.MaxPanes).Select(_ => new ColorLegend { IsVisible = false, IsHitTestVisible = false }).ToArray();
    private readonly ComboBox[] _paneModeBoxes = new ComboBox[ViewSplit.MaxPanes];
    private readonly Grid[] _paneCells = new Grid[ViewSplit.MaxPanes];
    private bool _syncingPaneBoxes;

    /// <summary>0번 화면(분할하지 않을 때는 전체) 레전드.</summary>
    private ColorLegend Legend => _paneLegends[0];

    private void WireSplit()
    {
        for (int p = 0; p < ViewSplit.MaxPanes; p++)
        {
            int pane = p;
            var box = new ComboBox
            {
                ItemsSource = ColorModes.Select(m => m.Label).ToList(),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(6), MinHeight = 24, FontSize = 11.5, Padding = new Thickness(6, 2),
                Classes = { "pane" },   // 바탕 = HudBrush 토큰(3D 위 반투명)
                IsVisible = false,
            };
            ToolTip.SetTip(box, pane == 0 ? "왼쪽 위 화면 색 모드 (= 상단 '색상' 콤보)" : $"분할 화면 {pane + 1} 색 모드");
            box.SelectionChanged += (_, _) =>
            {
                if (_syncingPaneBoxes || box.SelectedIndex < 0) return;
                var m = ColorModes[box.SelectedIndex].Mode;
                if (pane == 0) { if (m != _colorMode) SetColorMode(m); return; }
                if (m == _paneModes[pane]) return;
                _paneModes[pane] = m;
                RecolorNow();
                WarnMissing(m);
            };
            _paneModeBoxes[p] = box;
            // 레전드는 화면 높이에 맞춰 축소(확대는 안 함), 위쪽 여백 = 오른쪽 위 카메라 버튼
            var legendBox = new Viewbox
            {
                Child = _paneLegends[p], Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 40, 10, 36), IsHitTestVisible = false,
            };
            var cell = new Grid { IsVisible = p == 0 };
            if (p == 0 && Overlay.Parent is Panel parent)
            {
                // HUD 는 0번 화면 안에서 줄바꿈(분할 때 이웃 화면 레전드를 가리지 않게)
                parent.Children.Remove(Overlay);
                Overlay.TextWrapping = TextWrapping.Wrap;
                cell.Children.Add(Overlay);
            }
            cell.Children.Add(legendBox);
            cell.Children.Add(box);
            _paneCells[p] = cell;
            PaneGrid.Children.Add(cell);
        }
        SplitBox.ItemsSource = new[] { "1 화면", "2 분할", "4 분할" };
        SplitBox.SelectedIndex = 0;
        SplitBox.SelectionChanged += (_, _) => SetSplit(SplitChoices[Math.Max(0, SplitBox.SelectedIndex)]);
        CamSyncBox.IsCheckedChanged += (_, _) => View.SyncCameras = CamSyncBox.IsChecked == true;
        SetSplit(SplitLayout.Single);
    }

    /// <summary>분할 바꾸기 — 새로 보이는 화면은 그 화면 모드로 색을 계산, 빠진 화면 색 버퍼는 GlView 가 해제.</summary>
    private void SetSplit(SplitLayout l)
    {
        View.Layout = l;
        int n = ViewSplit.PaneCount(l);
        var (rows, cols) = ViewSplit.Grid(l);
        PaneGrid.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("*", rows)));
        PaneGrid.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", cols)));
        for (int p = 0; p < ViewSplit.MaxPanes; p++)
        {
            _paneCells[p].IsVisible = p < n;
            Grid.SetRow(_paneCells[p], p < n ? p / cols : 0);
            Grid.SetColumn(_paneCells[p], p < n ? p % cols : 0);
            // 1 화면이면 상단 '색상' 콤보로 충분
            _paneModeBoxes[p].IsVisible = n > 1 && p < n;
        }
        // 카메라 동기는 분할일 때만 의미가 있다
        CamSyncBox.IsVisible = n > 1;
        UpdateBrowserLimits();
        UpdatePaneMargins();
        int i = Array.IndexOf(SplitChoices, l);
        if (SplitBox.SelectedIndex != i) SplitBox.SelectedIndex = i;
        RecolorNow();
        if (n > 1) for (int p = 1; p < n; p++) WarnMissing(_paneModes[p]);
    }

    /// <summary>화면별 콤보를 현재 모드에 맞춤(콤보 이벤트 재진입 없이).</summary>
    private void SyncPaneHeaders()
    {
        _syncingPaneBoxes = true;
        try
        {
            for (int p = 0; p < ViewSplit.MaxPanes; p++)
            {
                if (_paneModeBoxes[p] is not { } box) continue;
                int i = Array.FindIndex(ColorModes, x => x.Mode == PaneMode(p));
                if (box.SelectedIndex != i) box.SelectedIndex = i;
            }
        }
        finally { _syncingPaneBoxes = false; }
    }

    /// <summary>분할 화면 모드에 필요한 해석 결과가 없으면 알림(0번 화면은 SetColorMode 가 알림).</summary>
    private void WarnMissing(ColorMode m)
    {
        if (m == ColorMode.FeaPlastic && _feaValues.Count == 0)
            Log(LogLevel.Warn, "분할 화면", "FEA 소성변형률 화면: OpenCourant 결과가 없어 물체 색으로 보입니다 — 해석 → OpenCourant 착지 충돌 해석.");
        if (ColorModeInfo.Of(m).Source == ColorSource.Calculix && _ccx.Count == 0)
            Log(LogLevel.Warn, "분할 화면", "CalculiX 결과가 없어 FEA 화면이 비어 있습니다 — 해석 → 정적/진동/좌굴 FEA (F7).");
    }
}
