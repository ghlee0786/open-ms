using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Sim.App.Theme;

namespace Sim.App.Help;

/// <summary>
/// 설명 하나(<see cref="HelpEntry"/>)를 화면 요소로 — 섹션 "?" 말풍선(compact)과 전체 도움말 창(wide)이 같은 디자인을 쓴다:
/// 굵은 제목 · 흐린 소제목 · 주색 점 글머리 · 키 칩(<c>{F5}</c>) · 굵게(<c>**…**</c>). 색은 모두 토큰(스타일 클래스 · 바인딩).
/// </summary>
public static class HelpView
{
    /// <summary>제목 줄: 주색 원 "?" + 제목.</summary>
    public static Control Title(string title, double size = 13)
    {
        var mark = new Border
        {
            Width = size + 5, Height = size + 5, CornerRadius = new CornerRadius((size + 5) / 2), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "?", FontSize = size - 2, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                .Fg("OnAccentBrush"),
        }.Bg("AccentBrush");
        var text = new TextBlock { Text = HelpText.Plain(title), Classes = { "help-title" }, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel();
        mark.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(text);
        return row;
    }

    /// <summary>
    /// 본문(제목 제외). compact = 말풍선(이름표 칩을 글 앞에 이어 씀), 아니면 도움말 창(이름표 칩 | 설명 두 칸, 선택 · 복사 가능).
    /// <paramref name="filter"/> 가 있으면 그 줄만(검색).
    /// </summary>
    public static StackPanel Body(HelpEntry entry, bool compact, Func<HelpItem, bool>? filter = null)
    {
        var panel = new StackPanel { Spacing = compact ? 5 : 6 };
        bool first = true;
        foreach (var g in entry.Groups)
        {
            var items = filter is null ? g.Items : g.Items.Where(filter).ToList();
            if (items.Count == 0) continue;
            if (!string.IsNullOrWhiteSpace(g.Heading))
                panel.Children.Add(new TextBlock
                {
                    Text = HelpText.Plain(g.Heading), Classes = { compact ? "help-heading" : "doc-h2" },
                    Margin = new Thickness(0, first ? 0 : (compact ? 6 : 10), 0, 2),
                });
            foreach (var it in items) panel.Children.Add(compact ? CompactRow(it) : WideRow(it));
            first = false;
        }
        return panel;
    }

    /// <summary>말풍선 줄: ● 글머리 + [칩…] 설명.</summary>
    private static Control CompactRow(HelpItem it)
    {
        var text = new TextBlock { Classes = { "help-body" } };
        var inlines = new InlineCollection();
        foreach (var part in it.LabelParts)
        {
            inlines.Add(InlineChip(part));
            inlines.Add(new Run(" "));
        }
        if (it.Label is not null) inlines.Add(new Run("— ") { FontSize = 12 });
        AddRuns(inlines, it.Text);
        text.Inlines = inlines;
        return Bullet(text);
    }

    /// <summary>도움말 창 줄: 이름표 칩(왼쪽 칸) | 설명. 이름표가 없으면 글머리 문단.</summary>
    private static Control WideRow(HelpItem it)
    {
        var text = new SelectableTextBlock { Classes = { "help-body" }, FontSize = 13, LineHeight = 21 };
        var inlines = new InlineCollection();
        AddRuns(inlines, it.Text);
        text.Inlines = inlines;
        if (it.Label is null) return Bullet(text);
        var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
        foreach (var part in it.LabelParts) chips.Children.Add(Chip(part, small: false));
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*"), Margin = new Thickness(0, 2) };
        g.Children.Add(chips);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        return g;
    }

    private static Control Bullet(Control text)
    {
        var dot = new Ellipse { Width = 5, Height = 5, Classes = { "help-dot" }, Margin = new Thickness(2, 8, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*") };
        g.Children.Add(dot);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        return g;
    }

    /// <summary>키 · 단추 이름 칩(고정폭 글꼴 · 테두리 상자, 키캡처럼 아래 테두리가 두꺼움).</summary>
    public static Border Chip(string text, bool small)
    {
        var label = new TextBlock { Text = HelpText.Plain(text), FontSize = small ? 10.5 : 12, FontWeight = small ? FontWeight.Normal : FontWeight.SemiBold, LineHeight = small ? 14 : double.NaN };
        return new Border { Classes = { "kbd" }, Child = label, Margin = small ? new Thickness(0) : new Thickness(0, 0, 4, 3) };
    }

    /// <summary>글 줄 안의 칩 — 글자 가운데 높이에 맞춘다(기준선에 올리면 칩이 위로 떠 보인다).</summary>
    private static InlineUIContainer InlineChip(string text)
        => new() { Child = Chip(text, small: true), BaselineAlignment = BaselineAlignment.Center };

    /// <summary>표기 조각 → 인라인(굵게 · 키 칩 · 보통 글).</summary>
    public static void AddRuns(InlineCollection inlines, string? source)
    {
        foreach (var run in HelpText.Parse(source))
        {
            switch (run.Kind)
            {
                case HelpRunKind.Key:
                    inlines.Add(InlineChip(run.Text));
                    break;
                case HelpRunKind.Bold:
                    inlines.Add(new Run(run.Text) { FontWeight = FontWeight.Bold });
                    break;
                default:
                    inlines.Add(new Run(run.Text));
                    break;
            }
        }
    }
}
