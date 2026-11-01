using System.Globalization;
using System.Security;
using System.Text;

namespace Sim.Core.Analysis;

/// <summary>
/// 보고서용 정적 SVG 선 그래프(외부 라이브러리 없음). 한 축(y 하나), 범주 색은 고정 순서(재순환 없음 — 8개 초과 금지),
/// 선 2 px, 옅은 격자·축, 계열 ≥ 2 이면 범례 + 선 끝 직접 라벨, 각 계열에 &lt;title&gt; 툴팁(뷰어 hover).
/// 색: dataviz 기준 팔레트(라이트) 1–8번.
/// </summary>
public static class SvgPlot
{
    public static readonly string[] Palette = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948"];
    private const string Surface = "#fcfcfb", Ink = "#0b0b0b", Ink2 = "#52514e", Muted = "#898781", Grid = "#e1e0d9", Axis = "#c3c2b7";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record Series(string Name, IReadOnlyList<(double X, double Y)> Points, bool Markers = false);

    /// <summary>보기 좋은 눈금(1·2·5 × 10ⁿ).</summary>
    public static double[] Ticks(double lo, double hi, int target = 6)
    {
        if (!(hi > lo)) { hi = lo + 1; }
        double raw = (hi - lo) / Math.Max(target - 1, 1);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = new[] { 1.0, 2.0, 2.5, 5.0, 10.0 }.Select(m => m * mag).First(s => s >= raw);
        double start = Math.Ceiling(lo / step - 1e-9) * step;
        var t = new List<double>();
        for (double v = start; v <= hi + step * 1e-9; v += step) t.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);
        return t.ToArray();
    }

    private static string F(double v) => v.ToString("0.##", Inv);

    private static string Label(double v)
    {
        double a = Math.Abs(v);
        return a >= 1e4 ? v.ToString("0.#E0", Inv) : a >= 100 ? v.ToString("0", Inv) : a >= 1 ? v.ToString("0.##", Inv) : v.ToString("0.###", Inv);
    }

    public static string Line(string title, string xLabel, string yLabel, IReadOnlyList<Series> series,
        double? xMin = null, double? xMax = null, double? yMin = null, double? yMax = null, string? note = null, int width = 760, int height = 320)
    {
        if (series.Count > Palette.Length) throw new ArgumentException($"계열은 {Palette.Length}개 이하(그 이상은 묶거나 그래프를 나눈다)");
        var pts = series.SelectMany(s => s.Points).Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToList();
        double x0 = xMin ?? (pts.Count > 0 ? pts.Min(p => p.X) : 0), x1 = xMax ?? (pts.Count > 0 ? pts.Max(p => p.X) : 1);
        double y0 = yMin ?? (pts.Count > 0 ? Math.Min(0, pts.Min(p => p.Y)) : 0), y1 = yMax ?? (pts.Count > 0 ? pts.Max(p => p.Y) : 1);
        if (!(x1 > x0)) x1 = x0 + 1;
        if (!(y1 > y0)) y1 = y0 + 1;
        var yt = Ticks(y0, y1); var xt = Ticks(x0, x1, 8);
        y0 = Math.Min(y0, yt[0]); y1 = Math.Max(y1, yt[^1]);
        bool legend = series.Count >= 2;
        double ml = 64, mr = legend ? 120 : 24, mt = legend ? 58 : 40, mb = 48 + (note is null ? 0 : 18);
        double pw = width - ml - mr, ph = height - mt - mb;
        double X(double x) => ml + (x - x0) / (x1 - x0) * pw;
        double Y(double y) => mt + ph - (y - y0) / (y1 - y0) * ph;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"Malgun Gothic, Segoe UI, sans-serif\" font-size=\"12\">");
        sb.Append($"<rect width=\"{width}\" height=\"{height}\" fill=\"{Surface}\"/>");
        sb.Append($"<text x=\"{ml}\" y=\"22\" font-size=\"15\" font-weight=\"600\" fill=\"{Ink}\">{Esc(title)}</text>");
        foreach (var t in yt)
        {
            if (t < y0 - 1e-12 || t > y1 + 1e-12) continue;
            sb.Append($"<line x1=\"{F(ml)}\" x2=\"{F(ml + pw)}\" y1=\"{F(Y(t))}\" y2=\"{F(Y(t))}\" stroke=\"{Grid}\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{F(ml - 6)}\" y=\"{F(Y(t) + 4)}\" text-anchor=\"end\" fill=\"{Muted}\">{Label(t)}</text>");
        }
        foreach (var t in xt)
        {
            if (t < x0 - 1e-12 || t > x1 + 1e-12) continue;
            sb.Append($"<line x1=\"{F(X(t))}\" x2=\"{F(X(t))}\" y1=\"{F(mt + ph)}\" y2=\"{F(mt + ph + 4)}\" stroke=\"{Axis}\"/>");
            sb.Append($"<text x=\"{F(X(t))}\" y=\"{F(mt + ph + 18)}\" text-anchor=\"middle\" fill=\"{Muted}\">{Label(t)}</text>");
        }
        sb.Append($"<line x1=\"{F(ml)}\" x2=\"{F(ml + pw)}\" y1=\"{F(Y(Math.Max(y0, Math.Min(0, y1))))}\" y2=\"{F(Y(Math.Max(y0, Math.Min(0, y1))))}\" stroke=\"{Axis}\" stroke-width=\"1\"/>");
        sb.Append($"<text x=\"{F(ml + pw / 2)}\" y=\"{F(mt + ph + 36)}\" text-anchor=\"middle\" fill=\"{Ink2}\">{Esc(xLabel)}</text>");
        sb.Append($"<text transform=\"translate(16,{F(mt + ph / 2)}) rotate(-90)\" text-anchor=\"middle\" fill=\"{Ink2}\">{Esc(yLabel)}</text>");
        sb.Append($"<clipPath id=\"plot\"><rect x=\"{F(ml)}\" y=\"{F(mt)}\" width=\"{F(pw)}\" height=\"{F(ph)}\"/></clipPath>");
        for (int i = 0; i < series.Count; i++)
        {
            var s = series[i];
            var p = s.Points.Where(q => double.IsFinite(q.X) && double.IsFinite(q.Y) && q.X >= x0 - 1e-9 && q.X <= x1 + 1e-9).ToList();
            if (p.Count == 0) continue;
            var c = Palette[i];
            string tip = $"{s.Name}: {p.Count}점, y {Label(p.Min(q => q.Y))}…{Label(p.Max(q => q.Y))}";
            if (s.Markers)
            {
                sb.Append($"<g clip-path=\"url(#plot)\"><title>{Esc(tip)}</title>");
                foreach (var q in p)
                    sb.Append($"<circle cx=\"{F(X(q.X))}\" cy=\"{F(Y(q.Y))}\" r=\"4.5\" fill=\"{c}\" stroke=\"{Surface}\" stroke-width=\"2\"><title>{Esc($"{s.Name}: x={Label(q.X)}, y={Label(q.Y)}")}</title></circle>");
                if (p.Count > 1) sb.Append($"<polyline fill=\"none\" stroke=\"{c}\" stroke-width=\"2\" stroke-opacity=\"0.6\" points=\"{string.Join(" ", p.Select(q => $"{F(X(q.X))},{F(Y(q.Y))}"))}\"/>");
                sb.Append("</g>");
            }
            else
                sb.Append($"<polyline clip-path=\"url(#plot)\" fill=\"none\" stroke=\"{c}\" stroke-width=\"2\" stroke-linejoin=\"round\" points=\"{string.Join(" ", Decimate(p, (int)pw * 2).Select(q => $"{F(X(q.X))},{F(Y(Math.Clamp(q.Y, y0 - (y1 - y0), y1 + (y1 - y0))))}"))}\"><title>{Esc(tip)}</title></polyline>");
            if (legend)
            {
                // 범례(위쪽 한 줄 · 넘치면 오른쪽 열)
                double lx = ml + (i % 4) * 150, ly = 40 + (i / 4) * 0;
                if (i >= 4) { lx = ml + pw + 12; ly = mt + (i - 4) * 18; }
                sb.Append($"<line x1=\"{F(lx)}\" x2=\"{F(lx + 16)}\" y1=\"{F(ly - 4)}\" y2=\"{F(ly - 4)}\" stroke=\"{c}\" stroke-width=\"3\" stroke-linecap=\"round\"/>");
                sb.Append($"<text x=\"{F(lx + 22)}\" y=\"{F(ly)}\" fill=\"{Ink2}\">{Esc(s.Name)}</text>");
                if (series.Count <= 4)
                {
                    var last = p[^1];
                    sb.Append($"<text x=\"{F(Math.Min(X(last.X) + 6, ml + pw + 4))}\" y=\"{F(Y(Math.Clamp(last.Y, y0, y1)) + 4)}\" fill=\"{Ink2}\">{Esc(s.Name)}</text>");
                }
            }
        }
        if (note is not null) sb.Append($"<text x=\"{F(ml)}\" y=\"{F(height - 8)}\" fill=\"{Muted}\" font-size=\"11\">{Esc(note)}</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>화면 픽셀보다 촘촘한 점은 구간별 최소·최대만 남겨 줄인다(피크 보존).</summary>
    private static List<(double X, double Y)> Decimate(List<(double X, double Y)> p, int buckets)
    {
        if (p.Count <= buckets * 2 || buckets < 2) return p;
        double x0 = p[0].X, x1 = p[^1].X;
        if (!(x1 > x0)) return p;
        var res = new List<(double, double)>();
        int i = 0;
        for (int b = 0; b < buckets && i < p.Count; b++)
        {
            double hi = x0 + (x1 - x0) * (b + 1) / buckets;
            int s = i; (double X, double Y) mn = p[i], mx = p[i];
            while (i < p.Count && (p[i].X <= hi || b == buckets - 1))
            {
                if (p[i].Y < mn.Y) mn = p[i];
                if (p[i].Y > mx.Y) mx = p[i];
                i++;
            }
            if (i == s) continue;
            if (mn.X <= mx.X) { res.Add(mn); if (mx != mn) res.Add(mx); } else { res.Add(mx); res.Add(mn); }
        }
        return res;
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";
}
