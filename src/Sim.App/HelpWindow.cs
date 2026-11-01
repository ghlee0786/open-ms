using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sim.App.Help;
using Sim.App.Theme;

namespace Sim.App;

/// <summary>
/// 전체 도움말 창(F1 · 도구 막대 [? 도움말]) — 왼쪽: 제목 · 검색 · 목차, 오른쪽: 장(章) 카드. 내용은 설명 목록(<see cref="HelpCatalog"/>)의
/// <c>Guide.*</c> 장과 화면별 섹션 설명(<c>App.*</c>, 섹션 "?" 말풍선과 같은 문구)을 말풍선과 같은 디자인(<see cref="HelpView"/>)으로 그린다.
/// 검색어가 있으면 맞는 줄만 남긴다.
/// </summary>
public sealed class HelpWindow : Window
{
    private const string SectionsKey = "App.*";
    private readonly IReadOnlyList<HelpEntry> _chapters = HelpCatalog.WithPrefix("Guide.");
    private readonly IReadOnlyList<HelpEntry> _sections = HelpCatalog.WithPrefix("App.");
    private readonly StackPanel _body = new() { Spacing = 14, Margin = new Thickness(28, 22, 28, 28), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ListBox _toc = new() { Classes = { "nav" }, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _search = new() { Watermark = "검색 (예: 휠, F6, 분할, 테마)" };
    private readonly ScrollViewer _scroll = new();
    private readonly Dictionary<string, Control> _anchors = new();
    private bool _syncToc;

    public HelpWindow()
    {
        Title = "도움말 — MnS 시뮬레이터";
        Width = 1120; Height = 780; MinWidth = 760; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.KeepOnScreen();

        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 4) };
        var tip = new HelpTip { HelpKey = HelpKeys.HelpSearch, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(tip, Dock.Right);
        head.Children.Add(tip);
        head.Children.Add(HelpView.Title("도움말", 16));
        var sub = new TextBlock { Text = "섹션마다 있는 ? 는 그 부분만 짧게 — 이 창은 전체입니다.", Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 12) };

        var side = new DockPanel { Margin = new Thickness(14) };
        foreach (var c in new Control[] { head, sub, _search }) { DockPanel.SetDock(c, Dock.Top); side.Children.Add(c); }
        side.Children.Add(_toc);
        var sideBorder = new Border { Child = side, Width = 280, BorderThickness = new Thickness(0, 0, 1, 0) }
            .Bg("SurfaceBrush").Res(Border.BorderBrushProperty, "LineBrush");

        _scroll.Content = _body;
        var root = new DockPanel();
        DockPanel.SetDock(sideBorder, Dock.Left);
        root.Children.Add(sideBorder);
        root.Children.Add(_scroll);
        Content = root;

        _search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Render(); };
        _toc.SelectionChanged += (_, _) =>
        {
            if (_syncToc || _toc.SelectedItem is not ListBoxItem { Tag: string key }) return;
            ScrollTo(key);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.F && (e.KeyModifiers & KeyModifiers.Control) != 0) _search.Focus();
        };
        Opened += (_, _) => _search.Focus();
        Render();
    }

    /// <summary>장(Guide.*) 하나로 이동 — 섹션 "?" 말풍선의 '전체 도움말에서 보기'.</summary>
    public void ShowChapter(string key)
    {
        if (!string.IsNullOrEmpty(_search.Text)) _search.Text = "";
        Dispatcher.UIThread.Post(() =>
        {
            ScrollTo(key);
            _syncToc = true;
            _toc.SelectedItem = (_toc.ItemsSource as IEnumerable<ListBoxItem>)?.FirstOrDefault(i => (string?)i.Tag == key);
            _syncToc = false;
        }, DispatcherPriority.Background);
    }

    private void ScrollTo(string key)
    {
        if (!_anchors.TryGetValue(key, out var a)) return;
        if (a.TranslatePoint(new Point(0, 0), _body) is { } p) _scroll.Offset = new Vector(0, Math.Max(0, p.Y + _body.Margin.Top - 8));
    }

    private void Render()
    {
        var q = _search.Text?.Trim() ?? "";
        bool filter = q.Length > 0;
        bool Hit(string s) => s.Contains(q, StringComparison.OrdinalIgnoreCase);
        _body.Children.Clear();
        _anchors.Clear();
        _syncToc = true;
        var toc = new List<ListBoxItem>();
        foreach (var ch in _chapters)
        {
            bool all = !filter || Hit(HelpText.Plain(ch.Title));
            Func<HelpItem, bool>? f = all ? null : it => Hit(it.SearchText);
            if (!all && !ch.AllItems.Any(f!)) continue;
            var card = Card(ch.Title, HelpView.Body(ch, compact: false, f));
            _body.Children.Add(card);
            _anchors[ch.Key] = card;
            toc.Add(new ListBoxItem { Content = HelpText.Plain(ch.Title), Tag = ch.Key });
        }
        // 화면별 섹션 설명(섹션 "?" 와 같은 문구)
        var secPanel = new StackPanel { Spacing = 6 };
        foreach (var s in _sections)
        {
            bool all = !filter || Hit(HelpText.Plain(s.Title));
            Func<HelpItem, bool>? f = all ? null : it => Hit(it.SearchText);
            if (!all && !s.AllItems.Any(f!)) continue;
            secPanel.Children.Add(new TextBlock { Text = HelpText.Plain(s.Title), Classes = { "doc-h2" } });
            secPanel.Children.Add(HelpView.Body(s, compact: false, f));
        }
        if (secPanel.Children.Count > 0)
        {
            var card = Card("화면별 설명 — 섹션 ? 말풍선", secPanel);
            _body.Children.Add(card);
            _anchors[SectionsKey] = card;
            toc.Add(new ListBoxItem { Content = "화면별 설명 (섹션 ?)", Tag = SectionsKey });
        }
        if (_body.Children.Count == 0)
            _body.Children.Add(new TextBlock { Text = $"'{q}' 에 맞는 내용이 없습니다.", Classes = { "dim" } });
        _toc.ItemsSource = toc;
        _scroll.Offset = default;
        _syncToc = false;
    }

    /// <summary>장 카드 — 말풍선과 같은 제목 줄(주색 ? + 제목) + 본문.</summary>
    private static Border Card(string title, Control body)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(HelpView.Title(title, 16));
        panel.Children.Add(body);
        return new Border { Classes = { "card" }, Padding = new Thickness(20, 16, 20, 18), Child = panel };
    }
}

/// <summary>읽기 전용 정보 창(시나리오 설명 등): 제목 · 부제 · 본문(선택 · 복사 가능) · 닫기.</summary>
public sealed class InfoWindow : Window
{
    public InfoWindow(string title, string subtitle, string body)
    {
        Title = title;
        Width = 760; Height = 580; MinWidth = 420; MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.KeepOnScreen();
        var close = new Button { Content = "닫기", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), IsCancel = true, IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();
        var head = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 16) };
        var titleRow = new DockPanel();
        var tip = new HelpTip { HelpKey = HelpKeys.ScenarioInfo, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(tip, Dock.Right);
        titleRow.Children.Add(tip);
        titleRow.Children.Add(new SelectableTextBlock { Text = title, Classes = { "doc-h1" }, TextWrapping = TextWrapping.Wrap });
        head.Children.Add(titleRow);
        if (subtitle.Length > 0)
            head.Children.Add(new SelectableTextBlock { Text = subtitle, FontSize = 11, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        var text = new SelectableTextBlock { Text = body, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        var card = new Border { Classes = { "card" }, Padding = new Thickness(18), Child = new ScrollViewer { Content = text } };
        var root = new DockPanel { Margin = new Thickness(24) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(close);
        root.Children.Add(card);
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
