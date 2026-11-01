using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sim.App.Rendering;
using Sim.App.Theme;

namespace Sim.App.Controls;

/// <summary>
/// 가벼운 2D 그래프: 시간이력(최대 3계열, 좌/우 축) 또는 MC 착탄 산포(등축).
/// 축 범위·선 지오메트리는 데이터·크기가 바뀔 때만 다시 만들고(재생 중 커서만 이동),
/// 픽셀 열마다 최소·최대를 남겨 축약하므로 짧은 하중(G) 피크가 사라지지 않는다.
/// 그래프 영역을 클릭·드래그하면 그 시각으로 이동(TimeSeek). 포커스가 있으면 ←/→ = 기록 간격 한 칸씩(Shift = 10칸) 이동.
/// 휠(또는 Shift+휠) = 마우스 위치 기준 시간축 확대·축소(세로축은 보이는 구간에 맞춤), 오른쪽·가운데 드래그 또는 터치패드 가로 스크롤 = 확대 구간 이동, 더블클릭 = 전체 범위.
/// </summary>
public sealed class PlotView : Control
{
    /// <summary>계열 하나 — 색은 테마 토큰 PlotSeries{ColorIndex}Color(1–7, 테마마다 대비가 맞는 짝).</summary>
    public sealed record Series(string Name, double[] X, double[] Y, int ColorIndex, bool RightAxis = false);

    private IReadOnlyList<Series> _series = [];
    private McOverlay? _mc;
    private double _cursor = double.NaN;
    public string Title { get; set; } = "";
    /// <summary>그래프 클릭·드래그로 시각 선택.</summary>
    public event Action<double>? TimeSeek;

    private static readonly Typeface Face = new("Malgun Gothic");

    // 테마 토큰(Render 때 찾음 — 테마가 바뀌면 InvalidateTheme)
    private IBrush Bg => this.Brush("PlotBgBrush") ?? this.Brush("SurfaceBrush")!;
    private IBrush TextBrush => this.Brush("PlotTextBrush")!;
    private IBrush TitleBrush => this.Brush("PlotTitleBrush")!;
    private IBrush TextBack => this.Brush("PlotLabelBackBrush")!;
    private Pen AxisPen => new(this.Brush("PlotAxisBrush"), 1);
    private Pen GridPen => new(this.Brush("PlotGridBrush"), 1);
    private Pen CursorPen => new(this.Brush("PlotCursorBrush"), 1);
    private Pen FocusPen => new(this.Brush("FocusRingBrush"), 1.5);
    private readonly Dictionary<int, IBrush> _seriesBrush = new();

    private IBrush SeriesBrush(int i)
    {
        if (!_seriesBrush.TryGetValue(i, out var b))
            _seriesBrush[i] = b = new SolidColorBrush(this.ColorRes($"PlotSeries{i}Color", this.ColorRes("PlotSeries1Color", default)));
        return b;
    }

    /// <summary>테마가 바뀜 — 계열 색 · 선 캐시를 버리고 다시 그린다.</summary>
    public void InvalidateTheme()
    {
        _seriesBrush.Clear();
        _dirty = true;
        InvalidateVisual();
    }


    // 캐시
    private Size _cacheSize;
    private bool _dirty = true;
    private double _xmin, _xmax = 1, _l0, _l1 = 1, _r0, _r1 = 1;
    private readonly List<(StreamGeometry Geo, Pen Pen)> _geos = new();
    private bool _seeking;
    // 시간축 보기 구간(NaN = 전체) · 오른쪽 드래그 이동
    private double _viewX0 = double.NaN, _viewX1 = double.NaN;
    private bool _panning;
    private double _panLastX;

    public PlotView()
    {
        ClipToBounds = true;
        Focusable = true;   // 포커스가 있으면 ←/→ 로 시각 이동(MainWindow 키 처리)
        ActualThemeVariantChanged += (_, _) => InvalidateTheme();
        DoubleTapped += (_, e) =>
        {
            if (!double.IsNaN(_viewX0)) { ResetView(); e.Handled = true; }
        };
    }

    /// <summary>시간축을 전체 범위로.</summary>
    public void ResetView()
    {
        _viewX0 = _viewX1 = double.NaN;
        _dirty = true;
        InvalidateVisual();
    }

    private (double Lo, double Hi) DataXRange()
    {
        double lo = _series.Min(s => s.X.Length > 0 ? s.X[0] : 0);
        double hi = _series.Max(s => s.X.Length > 0 ? s.X[^1] : 1);
        return hi > lo ? (lo, hi) : (lo, lo + 1);
    }

    /// <summary>보기 구간을 데이터 범위 안으로(최소 폭 = 전체의 1/2000).</summary>
    private void SetView(double x0, double x1)
    {
        var (lo, hi) = DataXRange();
        double full = hi - lo, width = Math.Clamp(x1 - x0, full / 2000, full);
        if (width >= full * 0.999) { ResetView(); return; }
        x0 = Math.Clamp(x0, lo, hi - width);
        _viewX0 = x0; _viewX1 = x0 + width;
        _dirty = true;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_mc is null && _series.Count > 0)
        {
            bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            var plot = PlotRect(Bounds.Size);
            // 터치패드 가로 스크롤(Shift 없이 Delta.X 만) = 시간축 이동
            if (!shift && e.Delta.Y == 0 && e.Delta.X != 0)
            {
                double dt = -e.Delta.X * 0.1 * (_xmax - _xmin);
                SetView(_xmin + dt, _xmax + dt);
                e.Handled = true;
                base.OnPointerWheelChanged(e);
                return;
            }
            // 휠(또는 Shift+휠 — Windows 는 Shift+휠을 가로 스크롤 Delta.X 로 보내기도 한다) = 마우스 기준 확대·축소
            double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            double f = Math.Clamp((e.GetPosition(this).X - plot.Left) / plot.Width, 0, 1);
            double anchor = _xmin + f * (_xmax - _xmin);
            double scale = Math.Pow(1.25, -delta);           // 위로 굴리면 확대(구간 좁힘)
            double w = (_xmax - _xmin) * scale;
            SetView(anchor - f * w, anchor - f * w + w);
            e.Handled = true;
        }
        base.OnPointerWheelChanged(e);
    }

    public void SetSeries(IReadOnlyList<Series> s, string title)
    {
        _series = s; _mc = null; Title = title; _dirty = true;
        InvalidateVisual();
    }

    public void SetMc(McOverlay? mc, string title)
    {
        _mc = mc; _series = []; Title = title; _dirty = true;
        InvalidateVisual();
    }

    public double CursorTime
    {
        get => _cursor;
        set
        {
            if (_cursor.Equals(value)) return;
            _cursor = value;
            if (_mc is null && _series.Count > 0) InvalidateVisual(); // MC 산포는 커서 없음 → 다시 그리지 않음
        }
    }

    private Rect PlotRect(Size b) => new(55, 22, Math.Max(10, b.Width - 110), Math.Max(10, b.Height - 45));

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);

    // ---------------- 시각 이동 ----------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        Focus();
        if (_mc is null && _series.Count > 0 && props.IsLeftButtonPressed)
        {
            _seeking = true;
            e.Pointer.Capture(this);
            Seek(e.GetPosition(this));
        }
        else if (_mc is null && _series.Count > 0 && (props.IsRightButtonPressed || props.IsMiddleButtonPressed) && !double.IsNaN(_viewX0))
        {
            _panning = true;
            _panLastX = e.GetPosition(this).X;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_seeking) Seek(e.GetPosition(this));
        else if (_panning)
        {
            double x = e.GetPosition(this).X;
            double dt = (x - _panLastX) / PlotRect(Bounds.Size).Width * (_xmax - _xmin);
            _panLastX = x;
            SetView(_xmin - dt, _xmax - dt);
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _seeking = false;
        _panning = false;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    /// <summary>확대 중 커서가 보기 구간 밖으로 나가면 구간을 옮겨 커서를 보이게(키보드 시각 이동용).</summary>
    public void KeepCursorInView()
    {
        if (double.IsNaN(_viewX0) || double.IsNaN(_cursor)) return;
        double w = _viewX1 - _viewX0, margin = w * 0.05;
        if (_cursor < _viewX0 + margin) SetView(_cursor - margin, _cursor - margin + w);
        else if (_cursor > _viewX1 - margin) SetView(_cursor + margin - w, _cursor + margin);
    }

    protected override void OnGotFocus(GotFocusEventArgs e) { InvalidateVisual(); base.OnGotFocus(e); }
    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e) { InvalidateVisual(); base.OnLostFocus(e); }

    private void Seek(Point p)
    {
        var plot = PlotRect(Bounds.Size);
        double f = Math.Clamp((p.X - plot.Left) / plot.Width, 0, 1);
        TimeSeek?.Invoke(_xmin + f * (_xmax - _xmin));
    }

    // ---------------- 캐시 생성 ----------------

    private void Rebuild(Rect plot)
    {
        _geos.Clear();
        (_xmin, _xmax) = DataXRange();
        if (!double.IsNaN(_viewX0))
        {
            // 새 데이터가 보기 구간을 벗어나면 전체로
            if (_viewX1 <= _xmin || _viewX0 >= _xmax) _viewX0 = _viewX1 = double.NaN;
            else (_xmin, _xmax) = (Math.Max(_xmin, _viewX0), Math.Min(_xmax, _viewX1));
        }
        (double, double) Range(bool right)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var s in _series)
            {
                if (s.RightAxis != right) continue;
                for (int i = 0; i < s.Y.Length; i++)
                {
                    double y = s.Y[i];
                    if (i < s.X.Length && (s.X[i] < _xmin || s.X[i] > _xmax)) continue;   // 보이는 구간만
                    if (double.IsFinite(y)) { if (y < lo) lo = y; if (y > hi) hi = y; }
                }
            }
            if (lo > hi) return (0, 1);
            if (hi - lo < 1e-9) { hi += 1; lo -= 1; }
            double pad = (hi - lo) * 0.05;
            return (lo - pad, hi + pad);
        }
        (_l0, _l1) = Range(false);
        (_r0, _r1) = Range(true);
        foreach (var s in _series)
        {
            var (y0, y1) = s.RightAxis ? (_r0, _r1) : (_l0, _l1);
            double X(double x) => plot.Left + (x - _xmin) / (_xmax - _xmin) * plot.Width;
            double Y(double y) => plot.Bottom - (y - y0) / (y1 - y0) * plot.Height;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool started = false;
                void To(double px, double py)
                {
                    var p = new Point(px, py);
                    if (!started) { g.BeginFigure(p, false); started = true; } else g.LineTo(p);
                }
                // 픽셀 열 단위 min/max 축약
                int col = int.MinValue;
                double mn = 0, mx = 0, first = 0, last = 0;
                int k = 0, mnK = 0, mxK = 0;
                void Flush()
                {
                    if (col == int.MinValue) return;
                    To(col, Y(first));
                    if (mnK <= mxK) { To(col, Y(mn)); To(col, Y(mx)); } else { To(col, Y(mx)); To(col, Y(mn)); }
                    To(col, Y(last));
                }
                for (int i = 0; i < s.X.Length; i++)
                {
                    double y = s.Y[i];
                    if (!double.IsFinite(y)) continue;
                    int c = (int)Math.Round(X(s.X[i]));
                    if (c != col)
                    {
                        Flush();
                        col = c; mn = mx = first = last = y; k = mnK = mxK = 0;
                        continue;
                    }
                    k++;
                    if (y < mn) { mn = y; mnK = k; }
                    if (y > mx) { mx = y; mxK = k; }
                    last = y;
                }
                Flush();
            }
            _geos.Add((geo, new Pen(SeriesBrush(s.ColorIndex), 1.5)));
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(Bg, new Rect(size));
        if (IsFocused) ctx.DrawRectangle(FocusPen, new Rect(size).Deflate(0.5));   // ←/→ 시각 이동이 이 그래프에 걸려 있음
        var plot = PlotRect(size);
        ctx.DrawText(Text(Title, 12, TitleBrush), new Point(8, 3));
        ctx.DrawRectangle(AxisPen, plot);
        if (_mc is not null) { RenderMc(ctx, plot); return; }
        if (_series.Count == 0) return;
        if (_dirty || size != _cacheSize) { Rebuild(plot); _dirty = false; _cacheSize = size; }

        var inv = CultureInfo.InvariantCulture;
        bool hasRight = _series.Any(s => s.RightAxis);
        var grid = GridPen;
        var dim = TextBrush;
        for (int i = 0; i <= 4; i++)
        {
            double fy = plot.Bottom - plot.Height * i / 4;
            ctx.DrawLine(grid, new Point(plot.Left, fy), new Point(plot.Right, fy));
            ctx.DrawText(Text((_l0 + (_l1 - _l0) * i / 4).ToString("G4", inv), 10, dim), new Point(4, fy - 7));
            if (hasRight)
                ctx.DrawText(Text((_r0 + (_r1 - _r0) * i / 4).ToString("G4", inv), 10, dim), new Point(plot.Right + 4, fy - 7));
            double fx = plot.Left + plot.Width * i / 4;
            ctx.DrawText(Text((_xmin + (_xmax - _xmin) * i / 4).ToString(_xmax - _xmin < 1 ? "F3" : _xmax - _xmin < 10 ? "F2" : "F1", inv) + " s", 10, dim), new Point(fx - 12, plot.Bottom + 3));
        }
        double legendX = plot.Left + 8;
        for (int k = 0; k < _series.Count; k++)
        {
            var s = _series[k];
            using (ctx.PushClip(plot)) ctx.DrawGeometry(null, _geos[k].Pen, _geos[k].Geo);
            var label = Text(s.Name + (s.RightAxis ? " (우)" : ""), 11, SeriesBrush(s.ColorIndex));
            TextBg(ctx, label, new Point(legendX, plot.Top + 2));
            legendX += label.Width + 14;
        }
        if (double.IsFinite(_cursor))
        {
            double cx = plot.Left + (_cursor - _xmin) / (_xmax - _xmin) * plot.Width;
            // 확대 구간 밖의 커서는 선을 그리지 않고(축 숫자 위로 넘어가던 문제) 가장자리에 방향 표시만
            if (_cursor >= _xmin && _cursor <= _xmax)
                ctx.DrawLine(CursorPen, new Point(cx, plot.Top), new Point(cx, plot.Bottom));
            else
            {
                bool left = _cursor < _xmin;
                var mark = Text(left ? "◀ 커서" : "커서 ▶", 10, TitleBrush);
                TextBg(ctx, mark, new Point(left ? plot.Left + 4 : plot.Right - mark.Width - 4, plot.Bottom - 16));
            }
            // 커서 시각의 값 읽기
            double ly = plot.Top + 18;
            foreach (var s in _series)
            {
                if (s.X.Length == 0) continue;
                int i = Array.BinarySearch(s.X, _cursor);
                if (i < 0) i = Math.Clamp(~i - 1, 0, s.X.Length - 1);
                TextBg(ctx, Text($"{s.Name} = {s.Y[i].ToString("G5", inv)}", 10, SeriesBrush(s.ColorIndex)), new Point(plot.Left + 8, ly));
                ly += 13;
            }
        }
    }

    /// <summary>곡선 위에 겹치는 범례·커서 값 글자 — 반투명 배경을 깔고 그린다.</summary>
    private void TextBg(DrawingContext ctx, FormattedText t, Point p)
    {
        ctx.FillRectangle(TextBack, new Rect(p.X - 2, p.Y, t.Width + 4, t.Height));
        ctx.DrawText(t, p);
    }

    private void RenderMc(DrawingContext ctx, Rect plot)
    {
        var mc = _mc!;
        var st = mc.Stats;
        double half = Math.Max(st.R95 * 1.6, 5);
        // 등축: 짧은 변 기준
        double scale = Math.Min(plot.Width, plot.Height) / (2 * half);
        var c = plot.Center;
        Point P(double n, double e) => new(c.X + (e - st.MeanE) * scale, c.Y - (n - st.MeanN) * scale);
        var grid = GridPen;
        ctx.DrawLine(grid, new Point(plot.Left, c.Y), new Point(plot.Right, c.Y));
        ctx.DrawLine(grid, new Point(c.X, plot.Top), new Point(c.X, plot.Bottom));
        var dot = new SolidColorBrush(Color.FromArgb(150, 255, 190, 60));   // data-color: MC 착탄점(3D 와 같은 데이터 색)
        int step = Math.Max(1, mc.Points.Length / 20000);
        for (int i = 0; i < mc.Points.Length; i += step)
        {
            var p = P(mc.Points[i].N, mc.Points[i].E);
            if (plot.Contains(p)) ctx.FillRectangle(dot, new Rect(p.X - 1, p.Y - 1, 2, 2));
        }
        ctx.DrawEllipse(null, new Pen(Brushes.LimeGreen, 1.5), P(st.MeanN, st.MeanE), st.Cep50 * scale, st.Cep50 * scale);   // data-color: CEP 원 초록(3D 레이어와 같음)
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            double az = st.EllipseAzimuthDeg * Math.PI / 180;
            for (int i = 0; i <= 72; i++)
            {
                double a = 2 * Math.PI * i / 72;
                double dn = Math.Cos(az) * Math.Cos(a) * st.EllipseMajor - Math.Sin(az) * Math.Sin(a) * st.EllipseMinor;
                double de = Math.Sin(az) * Math.Cos(a) * st.EllipseMajor + Math.Cos(az) * Math.Sin(a) * st.EllipseMinor;
                var p = P(st.MeanN + dn, st.MeanE + de);
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
        }
        ctx.DrawGeometry(null, new Pen(Brushes.Tomato, 1.5), geo);   // data-color: 95 % 타원 빨강(3D 레이어와 같음)
        var inv = CultureInfo.InvariantCulture;
        ctx.DrawText(Text(string.Format(inv, "N {0} 회  MPI ({1:F1}, {2:F1}) m  CEP {3:F1} m  R95 {4:F1} m  95%타원 {5:F1}×{6:F1} m  [{7}]",
            st.Count, st.MeanN, st.MeanE, st.Cep50, st.R95, st.EllipseMajor, st.EllipseMinor, mc.Label), 11, TitleBrush), new Point(plot.Left + 4, plot.Bottom - 16));
        ctx.DrawText(Text("↑N  →E   눈금 반폭 " + half.ToString("F0", inv) + " m", 10, TextBrush), new Point(plot.Left + 4, plot.Top + 2));
    }
}
