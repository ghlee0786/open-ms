using System.Text;

namespace Sim.App.Help;

/// <summary>말풍선 · 도움말 글 조각의 종류.</summary>
public enum HelpRunKind
{
    /// <summary>보통 글.</summary>
    Text,
    /// <summary>굵게 — <c>**…**</c>.</summary>
    Bold,
    /// <summary>단축키 칩 — <c>{…}</c>(고정폭 글꼴 · 테두리 상자).</summary>
    Key,
}

/// <summary>글의 한 조각.</summary>
public readonly record struct HelpRun(HelpRunKind Kind, string Text);

/// <summary>
/// 설명 목록의 작은 표기를 조각으로 나눈다 — <c>{Ctrl}+{O}</c> 는 단축키 칩, <c>**한 번만**</c> 은 굵게. 순수 함수(Avalonia 없음 — 시험이 링크).
/// </summary>
/// <remarks>
/// 대괄호(<c>[실행]</c>)는 표기가 아니라 단추 이름을 적는 글 관례다(참고 HostHelp 와 같음) — 단위 표기 <c>[m/s]</c> 와 섞여도 안전하다.
/// 짝이 없는 <c>{</c> · <c>**</c> 는 글자 그대로 둔다.
/// </remarks>
public static class HelpText
{
    public static IReadOnlyList<HelpRun> Parse(string? text)
    {
        var runs = new List<HelpRun>();
        if (string.IsNullOrEmpty(text)) return runs;
        var buffer = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '{')
            {
                int close = text.IndexOf('}', i + 1);
                if (close > i + 1 && text.IndexOf('{', i + 1, close - i - 1) < 0)
                {
                    Flush(runs, buffer);
                    runs.Add(new HelpRun(HelpRunKind.Key, text.Substring(i + 1, close - i - 1)));
                    i = close + 1;
                    continue;
                }
            }
            else if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    Flush(runs, buffer);
                    runs.Add(new HelpRun(HelpRunKind.Bold, text.Substring(i + 2, close - i - 2)));
                    i = close + 2;
                    continue;
                }
            }
            buffer.Append(text[i]);
            i++;
        }
        Flush(runs, buffer);
        return runs;
    }

    /// <summary>표기를 걷은 평문(검색 · 자동화 이름 · 중복 판정).</summary>
    public static string Plain(string? text) => string.Concat(Parse(text).Select(r => r.Text));

    private static void Flush(List<HelpRun> runs, StringBuilder buffer)
    {
        if (buffer.Length == 0) return;
        runs.Add(new HelpRun(HelpRunKind.Text, buffer.ToString()));
        buffer.Clear();
    }
}

/// <summary>
/// 한 줄 — 이름표(<see cref="Label"/>, 키·단추 이름 " · " 로 여러 개 → 칩) + 설명, 또는 이름표 없는 문단.
/// </summary>
public sealed record HelpItem(string? Label, string Text)
{
    /// <summary>이름표를 칩 단위로 나눈 것(" · " 구분).</summary>
    public IReadOnlyList<string> LabelParts => Label is null ? [] : Label.Split(" · ", StringSplitOptions.RemoveEmptyEntries);

    public string SearchText => (Label is null ? "" : HelpText.Plain(Label) + " ") + HelpText.Plain(Text);
}

/// <summary>한 묶음 — (선택) 소제목 + 줄들.</summary>
public sealed record HelpGroup(string? Heading, IReadOnlyList<HelpItem> Items);

/// <summary>
/// 설명 하나 — 키(<c>App.Main.Tree</c> · <c>Guide.Start</c>) · 제목 · 묶음들 · (선택) 전체 도움말에서 이어 볼 장의 키(<see cref="More"/>).
/// 섹션 "?" 말풍선과 전체 도움말 창이 같은 모델을 같은 디자인으로 그린다.
/// </summary>
public sealed record HelpEntry(string Key, string Title, IReadOnlyList<HelpGroup> Groups, string? More = null)
{
    /// <summary>제목 + 문단(소제목 없는 한 묶음).</summary>
    public static HelpEntry Create(string key, string title, params string[] items)
        => new(key, title, [new HelpGroup(null, items.Select(t => new HelpItem(null, t)).ToList())]);

    /// <summary>"자세히" 연결을 붙인 사본.</summary>
    public HelpEntry WithMore(string guideKey) => this with { More = guideKey };

    /// <summary>검색 · 자동화용 평문(제목 · 소제목 · 줄).</summary>
    public string ToPlainText()
    {
        var lines = new List<string> { HelpText.Plain(Title) };
        foreach (var g in Groups)
        {
            if (!string.IsNullOrWhiteSpace(g.Heading)) lines.Add(HelpText.Plain(g.Heading));
            lines.AddRange(g.Items.Select(i => i.SearchText));
        }
        return string.Join("\n", lines);
    }

    public IEnumerable<HelpItem> AllItems => Groups.SelectMany(g => g.Items);
}

/// <summary>장(章) 쓰기 도우미 — <c>P</c> 문단 · <c>K</c> 이름표+설명 · <c>S</c> 소제목.</summary>
public sealed class HelpBuilder(string key, string title)
{
    private readonly List<HelpGroup> _groups = [];
    private string? _heading;
    private List<HelpItem> _items = [];

    public HelpBuilder P(string text) { _items.Add(new HelpItem(null, text)); return this; }
    public HelpBuilder K(string label, string text) { _items.Add(new HelpItem(label, text)); return this; }

    public HelpBuilder S(string heading)
    {
        Close();
        _heading = heading;
        return this;
    }

    public HelpBuilder Add(IEnumerable<HelpItem> items) { _items.AddRange(items); return this; }

    private void Close()
    {
        if (_items.Count > 0 || _heading is not null) _groups.Add(new HelpGroup(_heading, _items));
        _heading = null;
        _items = [];
    }

    public HelpEntry Build(string? more = null)
    {
        Close();
        return new HelpEntry(key, title, _groups.ToList(), more);
    }
}

/// <summary>
/// 설명 목록 — 키 → <see cref="HelpEntry"/>. 화면은 키(<see cref="HelpKeys"/>)만 걸고 문구는 여기(<see cref="AppHelp"/> · <see cref="HelpContent"/>)에 한 번만 둔다.
/// 없는 키는 null — 화면은 "?" 를 끄고 죽지 않는다. 같은 키를 다시 등록하면 나중 것이 이긴다.
/// </summary>
public static class HelpCatalog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HelpEntry> Map = new(StringComparer.Ordinal);
    private static readonly List<string> Order = [];
    private static bool _defaults;

    public static void Register(IEnumerable<HelpEntry> entries)
    {
        lock (Gate)
        {
            foreach (var e in entries)
            {
                if (!Map.ContainsKey(e.Key)) Order.Add(e.Key);
                Map[e.Key] = e;
            }
        }
    }

    /// <summary>앱 기본 목록(섹션 설명 + 전체 도움말 장)을 한 번 등록.</summary>
    public static void EnsureDefaults()
    {
        lock (Gate)
        {
            if (_defaults) return;
            _defaults = true;
        }
        Register(AppHelp.Entries);
        Register(HelpContent.Guide());
    }

    public static HelpEntry? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        EnsureDefaults();
        lock (Gate) return Map.TryGetValue(key, out var e) ? e : null;
    }

    /// <summary>등록 순서대로 모든 키(사본).</summary>
    public static IReadOnlyList<string> Keys
    {
        get
        {
            EnsureDefaults();
            lock (Gate) return Order.ToList();
        }
    }

    public static IReadOnlyList<HelpEntry> WithPrefix(string prefix)
    {
        EnsureDefaults();
        lock (Gate) return Order.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => Map[k]).ToList();
    }
}
