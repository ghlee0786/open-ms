using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sim.App.Theme;

namespace Sim.App.Help;

/// <summary>
/// "?" 말풍선의 몸 — 둥근 카드 + 꼬리(“?” 를 가리킴) + 그림자, 안에 제목 · 글머리 · 키 칩 · "전체 도움말에서 보기".
/// 바깥 크기는 꼬리 자리(<see cref="HelpCalloutPlacement.TailMargin"/>)를 네 변에 늘 포함한다 — 어느 쪽에 뜨든 크기가 같다.
/// 카드 · 테두리 · 그림자 색은 Render 때 토큰(Surface2Brush · ControlBorderBrush · ShadowLg)을 찾는다 → 테마를 바꾸면 다시 칠해진다.
/// </summary>
public sealed class HelpCallout : Decorator
{
    /// <summary>카드 최대 폭(꼬리 자리 제외).</summary>
    public const double MaxCardWidth = 420;
    private const double Radius = 10, TailHalf = 7, Inner = 14;

    public HelpEntry Entry { get; }

    public static readonly StyledProperty<CalloutSide> SideProperty = AvaloniaProperty.Register<HelpCallout, CalloutSide>(nameof(Side));
    public CalloutSide Side { get => GetValue(SideProperty); set => SetValue(SideProperty, value); }

    public static readonly StyledProperty<double> TailOffsetProperty = AvaloniaProperty.Register<HelpCallout, double>(nameof(TailOffset), 36);
    public double TailOffset { get => GetValue(TailOffsetProperty); set => SetValue(TailOffsetProperty, value); }

    static HelpCallout()
    {
        AffectsRender<HelpCallout>(SideProperty, TailOffsetProperty);
    }

    /// <param name="openGuide">"전체 도움말에서 보기" — 장 키를 받아 도움말 창을 연다(없으면 줄을 빼다).</param>
    public HelpCallout(HelpEntry entry, Action<string>? openGuide)
    {
        Entry = entry;
        double m = HelpCalloutPlacement.TailMargin;
        Padding = new Thickness(m + Inner, m + Inner - 2, m + Inner, m + Inner - 4);
        AutomationProperties.SetAutomationId(this, $"Help.{entry.Key}.Body");
        AutomationProperties.SetName(this, entry.ToPlainText());

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(HelpView.Title(entry.Title));
        content.Children.Add(HelpView.Body(entry, compact: true));
        if (entry.More is { } more && openGuide is not null && HelpCatalog.Find(more) is { } guide)
        {
            var link = new Button { Classes = { "link" }, Content = $"전체 도움말에서 보기 — {guide.Title} →", HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(link, $"Help.{entry.Key}.More");
            link.Click += (_, _) => openGuide(more);
            content.Children.Add(new Border { Child = link, Margin = new Thickness(0, 2, 0, 0), Padding = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) }
                .Res(Border.BorderBrushProperty, "LineBrush"));
        }
        Child = new ScrollViewer
        {
            Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        MaxWidth = MaxCardWidth + 2 * m;
        ClipToBounds = false;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();   // 카드 · 테두리 · 그림자는 Render 에서 토큰을 찾는다
    }

    public override void Render(DrawingContext ctx)
    {
        double m = HelpCalloutPlacement.TailMargin;
        var size = Bounds.Size;
        if (size.Width <= 2 * m || size.Height <= 2 * m) return;
        var card = new Rect(m + 0.5, m + 0.5, size.Width - 2 * m - 1, size.Height - 2 * m - 1);
        var fill = this.Brush("Surface2Brush");
        var stroke = this.Brush("ControlBorderBrush");
        var pen = stroke is null ? null : new Pen(stroke, 1);
        var shadow = this.TryFindResource("ShadowLg", ActualThemeVariant, out var s) && s is BoxShadows bs ? bs : default;
        ctx.DrawRectangle(fill, pen, new RoundedRect(card, Radius), shadow);

        // 꼬리: 카드 테두리 선을 덮는 삼각형 + 바깥 두 변만 선
        double t = TailOffset;
        Point a, tip, b;
        switch (Side)
        {
            case CalloutSide.Below: a = new(t - TailHalf, card.Top + 1); tip = new(t, card.Top - m + 1); b = new(t + TailHalf, card.Top + 1); break;
            case CalloutSide.Above: a = new(t - TailHalf, card.Bottom - 1); tip = new(t, card.Bottom + m - 1); b = new(t + TailHalf, card.Bottom - 1); break;
            case CalloutSide.Left: a = new(card.Right - 1, t - TailHalf); tip = new(card.Right + m - 1, t); b = new(card.Right - 1, t + TailHalf); break;
            default: a = new(card.Left + 1, t - TailHalf); tip = new(card.Left - m + 1, t); b = new(card.Left + 1, t + TailHalf); break;
        }
        var tail = new StreamGeometry();
        using (var g = tail.Open())
        {
            g.BeginFigure(a, true);
            g.LineTo(tip);
            g.LineTo(b);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(fill, null, tail);
        if (pen is not null)
        {
            ctx.DrawLine(pen, a, tip);
            ctx.DrawLine(pen, tip, b);
        }
    }
}
