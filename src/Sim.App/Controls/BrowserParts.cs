using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sim.App.Theme;

namespace Sim.App.Controls;

/// <summary>
/// 브라우저(3D 위 어셈블리 트리) 행의 들여쓰기 가이드 — 조상 단계마다 세로줄 하나(조상 펼침 화살표 가운데).
/// 행 사이에 틈이 없어 이웃 행의 선과 이어진다. 폭 = Level × Step.
/// </summary>
public sealed class IndentGuides : Control
{
    public static readonly StyledProperty<int> LevelProperty = AvaloniaProperty.Register<IndentGuides, int>(nameof(Level));
    public static readonly StyledProperty<double> StepProperty = AvaloniaProperty.Register<IndentGuides, double>(nameof(Step), 14);

    static IndentGuides()
    {
        AffectsMeasure<IndentGuides>(LevelProperty, StepProperty);
        AffectsRender<IndentGuides>(LevelProperty, StepProperty);
    }

    public IndentGuides()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public int Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Max(0, Level) * Step, 0);

    public override void Render(DrawingContext ctx)
    {
        if (Level <= 0 || this.Brush("BrowserGuideBrush") is not { } b) return;
        var pen = new Pen(b, 1);
        double h = Bounds.Height;
        for (int k = 0; k < Level; k++)
        {
            double x = Math.Floor(k * Step + Step / 2) + 0.5;
            ctx.DrawLine(pen, new Point(x, 0), new Point(x, h));
        }
    }
}

/// <summary>
/// 브라우저 행의 종류 아이콘(16 단위 벡터, 글꼴 · 이모지 의존 없음). 색 = 테마 토큰, 물체는 물체 고유 색(시나리오 데이터 색)으로 채움.
/// 종류 키는 <see cref="AsmItem.IconKind"/>.
/// </summary>
public sealed class BrowserIcon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<BrowserIcon, string?>(nameof(Kind));
    public static readonly StyledProperty<IBrush?> SwatchProperty = AvaloniaProperty.Register<BrowserIcon, IBrush?>(nameof(Swatch));

    static BrowserIcon()
    {
        AffectsRender<BrowserIcon>(KindProperty, SwatchProperty);
    }

    public BrowserIcon()
    {
        Width = Height = 16;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Swatch { get => GetValue(SwatchProperty); set => SetValue(SwatchProperty, value); }

    /// <summary>도형 한 조각: 경로 · 채움(토큰 키 · "swatch" = 물체 색 · null = 없음) · 선(토큰 키 · null = 없음).</summary>
    private readonly record struct Part(Geometry Geo, string? Fill, string? Stroke);

    private static Part P(string data, string? fill, string? stroke) => new(Geometry.Parse(data), fill, stroke);

    private const string Cube = "M8,1 L14.5,4.5 L14.5,11.5 L8,15 L1.5,11.5 L1.5,4.5 Z";
    private const string CubeEdges = "M1.5,4.5 L8,8 L14.5,4.5 M8,8 L8,15";

    private static readonly Dictionary<string, Part[]> Shapes = new()
    {
        // 장면(루트 어셈블리) · 하위 물체를 가진 물체(어셈블리 — 겹친 두 상자)
        ["scene"] = [P(Cube, "AccentSoftBrush", "AccentTextBrush"), P(CubeEdges, null, "AccentTextBrush")],
        ["assembly"] = [
            P("M10,1 L15,3.5 L15,8.5 L10,11 L5,8.5 L5,3.5 Z", null, "TextDimBrush"),
            P("M6,5 L11,7.5 L11,12.5 L6,15 L1,12.5 L1,7.5 Z", "swatch", "TextDimBrush"),
            P("M1,7.5 L6,10 L11,7.5 M6,10 L6,15", null, "TextDimBrush")],
        // 물체 — 형상 종류별(메시 · 상자 · 원통 · 구 · 외피 조각)
        ["body-mesh"] = [P(Cube, "swatch", "TextDimBrush"), P(CubeEdges + " M1.5,11.5 L8,8 L14.5,11.5", null, "TextDimBrush")],
        ["body-box"] = [P(Cube, "swatch", "TextDimBrush"), P(CubeEdges, null, "TextDimBrush")],
        ["body-cyl"] = [
            P("M3,4 L3,12 A5,2.2 0 0 0 13,12 L13,4 A5,2.2 0 0 0 3,4 Z", "swatch", "TextDimBrush"),
            P("M3,4 A5,2.2 0 0 0 13,4", null, "TextDimBrush")],
        ["body-sphere"] = [
            P("M1.5,8 A6.5,6.5 0 1 1 14.5,8 A6.5,6.5 0 1 1 1.5,8 Z", "swatch", "TextDimBrush"),
            P("M1.5,8 A6.5,2.2 0 0 0 14.5,8", null, "TextDimBrush")],
        ["body-shell"] = [P("M1.5,12.5 A7,7 0 0 1 14.5,12.5 L11.5,12.5 A4,4 0 0 0 4.5,12.5 Z", "swatch", "TextDimBrush")],
        // 운반체(드론) — 네 로터 · 몸체
        ["carrier"] = [
            P("M4,4 L12,12 M12,4 L4,12", null, "InfoBrush"),
            P("M1.5,4 A2.5,2.5 0 1 1 6.5,4 A2.5,2.5 0 1 1 1.5,4 Z M9.5,4 A2.5,2.5 0 1 1 14.5,4 A2.5,2.5 0 1 1 9.5,4 Z M1.5,12 A2.5,2.5 0 1 1 6.5,12 A2.5,2.5 0 1 1 1.5,12 Z M9.5,12 A2.5,2.5 0 1 1 14.5,12 A2.5,2.5 0 1 1 9.5,12 Z", null, "InfoBrush"),
            P("M6,6 L10,6 L10,10 L6,10 Z", "swatch", "InfoBrush")],
        // 장치 — 낙하산 · 로터 · 펄스 · 그 밖(완충 등)
        ["chute"] = [
            P("M1.5,7.5 A6.5,6 0 0 1 14.5,7.5 Q11.25,6 8,7.5 Q4.75,6 1.5,7.5 Z", "WarnBrush", null),
            P("M1.5,7.5 L8,14 L14.5,7.5 M8,7.5 L8,14", null, "WarnBrush")],
        ["rotor"] = [
            P("M2,6 A6,1.8 0 1 0 14,6 A6,1.8 0 1 0 2,6 Z", null, "TextDimBrush"),
            P("M8,6 L8,14 M5,14 L11,14", null, "TextDimBrush")],
        ["pulse"] = [P("M9.5,1 L3,9 L7.5,9 L6.5,15 L13,6.5 L8.5,6.5 Z", "HighlightBrush", null)],
        ["device"] = [P("M2,2.5 L14,2.5 M2,13.5 L14,13.5 M4,2.5 L12,5 L4,7.5 L12,10 L4,12 L8,13.5", null, "TextDimBrush")],
        // 연결 — 테더 · 레일 · 힌지 · 스윙 · 그 밖
        ["tether"] = [
            P("M2,13 C4,2 7.5,3 8,8 C8.5,13 12,14 14,3", null, "TextDimBrush"),
            P("M0.5,13 A1.5,1.5 0 1 1 3.5,13 A1.5,1.5 0 1 1 0.5,13 Z M12.5,3 A1.5,1.5 0 1 1 15.5,3 A1.5,1.5 0 1 1 12.5,3 Z", "TextDimBrush", null)],
        ["rail"] = [P("M1.5,5 L14.5,5 M1.5,11 L14.5,11 M4.5,3 L4.5,13 M8,3 L8,13 M11.5,3 L11.5,13", null, "TextDimBrush")],
        ["hinge"] = [
            P("M2.5,14 L8,7 L13.5,14", null, "TextDimBrush"),
            P("M5.5,7 A2.5,2.5 0 1 1 10.5,7 A2.5,2.5 0 1 1 5.5,7 Z", "TextDimBrush", null)],
        ["swing"] = [
            P("M3,2 L13,2 M8,2 L8,10", null, "TextDimBrush"),
            P("M5.5,12 A2.5,2.5 0 1 1 10.5,12 A2.5,2.5 0 1 1 5.5,12 Z", "TextDimBrush", null)],
        ["link"] = [
            P("M1,8 L15,8", null, "TextDimBrush"),
            P("M2,8 A2,2 0 1 1 6,8 A2,2 0 1 1 2,8 Z M10,8 A2,2 0 1 1 14,8 A2,2 0 1 1 10,8 Z", "TextDimBrush", null)],
        // 정보 줄 — 형상 · 공력, 이벤트 규칙, 묶음(폴더)
        ["shape"] = [P("M8,2.5 L14,13.5 L2,13.5 Z", null, "TextMutedBrush")],
        ["aero"] = [P("M1.5,5 L10,5 C13.5,5 13,1 10,2.2 M1.5,8.5 L13.5,8.5 M1.5,12 L9,12 C12.5,12 12,15.8 9,14.8", null, "TextMutedBrush")],
        ["event"] = [P("M3.5,15 L3.5,1.5", null, "TextDimBrush"), P("M3.5,2 L13,2 L10.5,5.5 L13,9 L3.5,9 Z", "HighlightBrush", null)],
        ["folder"] = [P("M1.5,3.5 L6,3.5 L7.5,5 L14.5,5 L14.5,13 L1.5,13 Z", null, "TextMutedBrush")],
    };

    /// <summary>알려진 종류 키(시험 · 스모크 확인용).</summary>
    public static IReadOnlyCollection<string> Kinds => Shapes.Keys;

    public override void Render(DrawingContext ctx)
    {
        if (Kind is null || !Shapes.TryGetValue(Kind, out var parts)) return;
        double s = Math.Min(Bounds.Width, Bounds.Height) / 16;
        using var _ = ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation((Bounds.Width - 16 * s) / 2, (Bounds.Height - 16 * s) / 2));
        foreach (var p in parts)
        {
            IBrush? fill = p.Fill switch { null => null, "swatch" => Swatch ?? this.Brush("Surface3Brush"), var k => this.Brush(k) };
            var pen = p.Stroke is { } sk && this.Brush(sk) is { } sb ? new Pen(sb, 1.2 / Math.Max(s, 0.5) * 0.9, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round) : null;
            ctx.DrawGeometry(fill, pen, p.Geo);
        }
    }
}
