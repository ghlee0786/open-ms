using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sim.App.Rendering;
using Sim.App.Theme;
using Sim.Core.Structures;

namespace Sim.App.Controls;

/// <summary>
/// 3D 뷰 오른쪽 색상 레전드(FR-STR-03): 세로 그라데이션 막대 + 눈금 ≥ 5 + 단위 + 모드명 + "근사(FEA 아님)".
/// 범주 모드(부위 상태)는 색 견본 목록.
/// </summary>
public sealed class ColorLegend : Control
{
    private static readonly Typeface Face = new("Malgun Gothic, Segoe UI");
    // 패널 바탕 · 글자 · 테두리 = 테마 토큰(Legend*). 막대 · 견본 색 = 표준 컬러맵 · 상태 색(데이터 — 테마 무관)
    private IBrush Bg => this.Brush("LegendBgBrush")!;
    private IBrush Fg => this.Brush("LegendTextBrush")!;
    private IBrush Dim => this.Brush("LegendDimBrush")!;
    private IPen Border => new Pen(this.Brush("LegendLineBrush"), 1);

    public ColorLegend()
    {
        ActualThemeVariantChanged += (_, _) => Changed();
    }

    public string Title { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Note { get; set; } = "근사(FEA 아님)";
    public double Min { get; set; }
    public double Max { get; set; } = 1;
    public string ColorMap { get; set; } = "Turbo";
    public bool Categorical { get; set; }
    /// <summary>값이 클수록 위험이 아닌 모드(안전계수)는 색을 뒤집어 낮은 값이 빨강.</summary>
    public bool Reverse { get; set; }
    public int Ticks { get; set; } = 6;
    /// <summary>NaN(자료 없음) 면이 있으면 회색 견본 표시.</summary>
    public bool NoData { get; set; }
    /// <summary>보조 줄(예: 최대점 지배 성분 · 판 처짐 지표).</summary>
    public string Detail { get; set; } = "";
    public ScaleRange Range => new(Min, Max, false);
    private string[] DetailLines => Detail.Length == 0 ? [] : Detail.Split('\n');

    public void Changed()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 제목·단위·각주가 잘리지 않게 폭을 글자 폭에 맞춤
        double w = Math.Max(Math.Max(Text(Title, 12.5, Fg, FontWeight.SemiBold).Width, DetailLines.Select(l => Text(l, 10.5, Dim).Width).DefaultIfEmpty(0).Max()),
            Math.Max(Text(Unit, 11, Dim).Width, Text(Note, 10.5, Dim).Width)) + 22;
        double extra = Categorical ? 0 : (NoData ? 20 : 0) + 15 * DetailLines.Length;
        return new(Math.Max(Categorical ? 190 : 150, w), Categorical ? 32 + 6 * 20 + 26 : 300 + extra);
    }

    private static FormattedText Text(string s, double size, IBrush b, FontWeight w = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, w), size, b);

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        var Fg = this.Fg;
        var Dim = this.Dim;
        var Border = this.Border;
        ctx.DrawRectangle(Bg, Border, r, 8, 8);
        ctx.DrawText(Text(Title, 12.5, Fg, FontWeight.SemiBold), new Point(10, 7));
        if (Categorical)
        {
            double y = 32;
            foreach (var s in new[] { FailureState.Fracture, FailureState.Yield, FailureState.Buckling, FailureState.Caution, FailureState.Safe, FailureState.Unknown })
            {
                var (cr, cg, cb) = ColorMaps.State(s);
                ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(cr * 255), (byte)(cg * 255), (byte)(cb * 255))), null, new Rect(12, y + 2, 14, 14), 3, 3);   // data-color: 상태 색 표준
                ctx.DrawText(Text(ColorMaps.StateLabel(s), 11.5, Fg), new Point(32, y));
                y += 20;
            }
            ctx.DrawText(Text(Note, 10.5, Dim), new Point(10, y + 6));
            return;
        }
        ctx.DrawText(Text(Unit, 11, Dim), new Point(10, 24));
        double extra = (NoData ? 20 : 0) + 15 * DetailLines.Length;
        double top = 46, bottom = r.Height - 30 - extra, x = 12, w = 18;
        // 막대 색 = 그 높이 값의 면 색(ColorScale.LegendColor) — 면 색과 같은 함수
        var range = Range;
        var stops = new GradientStops();
        for (int i = 0; i <= 32; i++)
        {
            double t = i / 32.0;
            var (cr, cg, cb) = ColorScale.LegendColor(ColorMap, range, t, Reverse);
            stops.Add(new GradientStop(Color.FromRgb((byte)(cr * 255), (byte)(cg * 255), (byte)(cb * 255)), t));   // data-color: 컬러맵 표준
        }
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = stops };
        ctx.DrawRectangle(brush, Border, new Rect(x, top, w, bottom - top), 3, 3);
        var ticks = ColorScale.Ticks(range, Ticks);
        for (int i = 0; i < ticks.Length; i++)
        {
            double y = top + (bottom - top) * i / (ticks.Length - 1);
            ctx.DrawLine(new Pen(Fg, 1), new Point(x + w, y), new Point(x + w + 5, y));
            var label = Text(ticks[i].Label, 11, Fg);
            ctx.DrawText(label, new Point(x + w + 9, y - label.Height / 2));
        }
        double yb = bottom + 8;
        if (NoData)
        {
            var (nr, ng, nb) = ColorScale.NoData;
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(nr * 255), (byte)(ng * 255), (byte)(nb * 255))), null, new Rect(12, yb + 2, 14, 12), 2, 2);   // data-color: 자료 없음 회색(면 색과 같음)
            ctx.DrawText(Text("자료 없음 (재질 미지정 등)", 10.5, Dim), new Point(32, yb));
            yb += 20;
        }
        foreach (var line in DetailLines) { ctx.DrawText(Text(line, 10.5, Dim), new Point(10, yb)); yb += 15; }
        ctx.DrawText(Text(Note, 10.5, Dim), new Point(10, r.Height - 22));
    }
}
