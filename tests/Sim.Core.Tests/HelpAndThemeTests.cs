using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sim.App.Help;
using Sim.App.Rendering;
using Sim.App.Theme;

namespace Sim.Core.Tests;

/// <summary>
/// 테마 · "?" 도움말 계약 시험 — 글 표기(HelpText) · 설명 목록(키 ↔ 화면 연결) · 말풍선 배치(화면 밖으로 안 나감) · 테마 저장 ·
/// 토큰 두 벌 짝 · 하드코딩 색 금지. 앱(Avalonia) 없이 같은 소스를 링크해 시험한다.
/// </summary>
public class HelpTextTests
{
    [Fact]
    public void Parse_splits_key_chips_bold_and_text()
    {
        var runs = HelpText.Parse("열기 {Ctrl}+{O} — **목록 밖** 파일");
        Assert.Equal(
        [
            new HelpRun(HelpRunKind.Text, "열기 "), new HelpRun(HelpRunKind.Key, "Ctrl"), new HelpRun(HelpRunKind.Text, "+"),
            new HelpRun(HelpRunKind.Key, "O"), new HelpRun(HelpRunKind.Text, " — "), new HelpRun(HelpRunKind.Bold, "목록 밖"),
            new HelpRun(HelpRunKind.Text, " 파일"),
        ], runs);
    }

    [Theory]
    [InlineData("짝 없는 { 괄호", "짝 없는 { 괄호")]
    [InlineData("빈 {} 칩", "빈 {} 칩")]
    [InlineData("짝 없는 ** 굵게", "짝 없는 ** 굵게")]
    [InlineData("[실행] 단추 · 단위 [m/s]", "[실행] 단추 · 단위 [m/s]")]
    [InlineData("{F5} 실행", "F5 실행")]
    public void Unpaired_marks_and_brackets_stay_literal(string src, string plain)
    {
        Assert.Equal(plain, HelpText.Plain(src));
    }

    [Fact]
    public void Empty_text_has_no_runs()
    {
        Assert.Empty(HelpText.Parse(null));
        Assert.Empty(HelpText.Parse(""));
    }

    [Fact]
    public void Item_label_splits_into_chip_parts()
    {
        var it = new HelpItem("W · S · A", "이동");
        Assert.Equal(["W", "S", "A"], it.LabelParts);
        Assert.Contains("이동", it.SearchText);
    }
}

public class HelpCatalogTests
{
    private static string AppDir => Path.Combine(TestUtil.RepoRoot, "src", "Sim.App");

    private static IReadOnlyDictionary<string, string> KeyConstants() =>
        typeof(HelpKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Every_screen_key_has_a_catalog_entry()
    {
        foreach (var (name, key) in KeyConstants())
        {
            var e = HelpCatalog.Find(key);
            Assert.True(e is not null, $"HelpKeys.{name} = '{key}' 의 설명이 목록에 없다");
            Assert.False(string.IsNullOrWhiteSpace(e!.Title), key);
            Assert.True(e.AllItems.Any(), $"{key} 에 줄이 없다");
        }
    }

    [Fact]
    public void Every_section_entry_is_a_screen_key_no_orphans()
    {
        var consts = KeyConstants().Values.ToHashSet();
        foreach (var e in HelpCatalog.WithPrefix("App."))
            Assert.True(consts.Contains(e.Key), $"설명 '{e.Key}' 를 거는 HelpKeys 상수가 없다(고아)");
        Assert.Equal(AppHelp.Entries.Select(e => e.Key).Distinct().Count(), AppHelp.Entries.Count);
    }

    /// <summary>카탈로그 키가 모두 화면에 연결되어 있다 — 상수마다 화면 소스(axaml · 코드)에서 쓰인다.</summary>
    [Fact]
    public void Every_screen_key_is_wired_to_the_ui()
    {
        var sources = Directory.GetFiles(AppDir, "*.axaml", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(AppDir, "*.cs", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => Path.GetFileName(p) is not ("HelpKeys.cs" or "AppHelp.cs"))
            .Select(File.ReadAllText).ToList();
        foreach (var name in KeyConstants().Keys)
        {
            var rx = new Regex($@"HelpKeys\.{name}\b");
            Assert.True(sources.Any(s => rx.IsMatch(s)), $"HelpKeys.{name} 를 거는 화면이 없다 — '?' 가 연결되지 않음");
        }
    }

    [Fact]
    public void More_links_point_to_existing_guide_chapters()
    {
        foreach (var e in HelpCatalog.WithPrefix("App."))
        {
            Assert.False(string.IsNullOrEmpty(e.More), $"{e.Key} 에 전체 도움말 연결이 없다");
            Assert.True(HelpCatalog.Find(e.More)?.Key.StartsWith("Guide.") == true, $"{e.Key} → '{e.More}' 장이 없다");
        }
    }

    [Fact]
    public void Guide_keeps_all_previous_chapters_and_adds_theme()
    {
        var titles = HelpCatalog.WithPrefix("Guide.").Select(e => e.Title).ToList();
        foreach (var t in new[]
        {
            "시작하기 — 사용 흐름", "화면 구성", "도구 막대 버튼", "왼쪽 입력 패널", "3D 뷰 — 마우스", "3D 뷰 — 키보드", "카메라 버튼 · 카메라 메뉴",
            "색 모드 · 화면 분할 · 레전드", "재생 · 타임라인 · 이벤트", "그래프", "어셈블리 트리", "오른쪽 대시보드", "해석 (구조 · FEA · 신뢰도)",
            "GPU 가상 풍동 (실시간 흐름 · 시각화 근사)", "풍동 보기 (CFD 표면 압력 · 3-D 유선)", "Monte Carlo", "결과 저장 · 불러오기 · 내보내기",
            "Fusion 모델 가져오기", "시나리오 JSON 편집", "보기 메뉴 · 3D 레이어", "로그 · 상태 표시줄", "단축키 전체", "테마 · 화면 모양",
        })
            Assert.Contains(t, titles);
        // 키 상수마다 장이 있고 중복이 없다
        var guideKeys = typeof(HelpContent).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.All(guideKeys, k => Assert.NotNull(HelpCatalog.Find(k)));
        Assert.Equal(HelpContent.Guide().Count, HelpContent.Guide().Select(e => e.Key).Distinct().Count());
        // 단축키 · 색 모드 · 레이어 목록이 실려 있다
        var all = string.Join("\n", HelpContent.Guide().Select(e => e.ToPlainText()));
        foreach (var s in new[] { "Shift+F8", "Ctrl+Home", "Ctrl+0", "W · S · A · D · E · Q", "Turbo" }) Assert.Contains(s, all);
        foreach (var m in ColorModeInfo.All) Assert.Contains(m.Label, all);
        foreach (var (_, label, _) in ViewLayers.All) Assert.Contains(label, all);
    }

    [Fact]
    public void Markup_is_balanced_in_every_entry()
    {
        foreach (var key in HelpCatalog.Keys)
        {
            var e = HelpCatalog.Find(key)!;
            foreach (var it in e.AllItems)
            {
                var plain = HelpText.Plain(it.Text);
                Assert.False(plain.Contains('{') || plain.Contains('}') || plain.Contains("**"), $"{key}: 짝이 안 맞는 표기 — '{it.Text}'");
            }
        }
    }
}

/// <summary>말풍선 배치 — 화면(창) 밖으로 나가지 않는다(참고 PopupFitTests).</summary>
public class HelpCalloutPlacementTests
{
    private const double W = 436, H = 260;
    private static readonly HRect Window1920 = new HRect(0, 0, 1920, 1009).Deflate(6);

    [Fact]
    public void Prefers_below_with_tail_on_the_target()
    {
        var target = new HRect(400, 60, 20, 20);
        var r = HelpCalloutPlacement.Resolve(target, W, H, Window1920);
        Assert.Equal(CalloutSide.Below, r.Side);
        Assert.Equal(target.Bottom + HelpCalloutPlacement.Gap, r.Y);
        Assert.Equal(410, r.X + r.TailOffset, 6);   // 꼬리 중심 = "?" 중심
        Assert.True(Window1920.Contains(r.Rect(W, H)));
    }

    [Fact]
    public void Flips_above_near_the_bottom_edge()
    {
        var r = HelpCalloutPlacement.Resolve(new HRect(1880, 985, 20, 20), W, H, Window1920);
        Assert.Equal(CalloutSide.Above, r.Side);
        Assert.True(Window1920.Contains(r.Rect(W, H)));
    }

    [Fact]
    public void Slides_left_at_the_right_edge_and_keeps_tail_off_the_corner()
    {
        var target = new HRect(1890, 100, 20, 20);
        var r = HelpCalloutPlacement.Resolve(target, W, H, Window1920);
        Assert.True(Window1920.Contains(r.Rect(W, H)));
        Assert.InRange(r.TailOffset, HelpCalloutPlacement.TailMargin + HelpCalloutPlacement.TailEdgeInset, W - HelpCalloutPlacement.TailMargin - HelpCalloutPlacement.TailEdgeInset);
    }

    [Fact]
    public void Uses_side_when_neither_above_nor_below_fits()
    {
        var bounds = new HRect(0, 0, 1200, 300);
        var r = HelpCalloutPlacement.Resolve(new HRect(600, 140, 20, 20), W, 200, bounds);
        Assert.True(r.Side is CalloutSide.Left or CalloutSide.Right);
        Assert.True(bounds.Contains(r.Rect(W, 200)));
    }

    [Fact]
    public void Too_big_callout_is_pinned_inside_from_top_left()
    {
        var bounds = new HRect(10, 10, 300, 200);
        var r = HelpCalloutPlacement.Resolve(new HRect(150, 100, 20, 20), 400, 500, bounds);
        Assert.Equal(10, r.X);
        Assert.Equal(10, r.Y);
    }

    [Fact]
    public void Never_leaves_a_1920x1009_window_for_any_tip_position()
    {
        var rng = new Random(7);
        for (int i = 0; i < 5000; i++)
        {
            double w = 160 + rng.NextDouble() * 276, h = 80 + rng.NextDouble() * 520;
            var target = new HRect(Window1920.Left + rng.NextDouble() * (Window1920.Width - 20), Window1920.Top + rng.NextDouble() * (Window1920.Height - 20), 20, 20);
            var r = HelpCalloutPlacement.Resolve(target, w, h, Window1920);
            Assert.True(Window1920.Contains(r.Rect(w, h)), $"{target} {w}x{h} → {r}");
            double len = r.Side is CalloutSide.Below or CalloutSide.Above ? w : h;
            Assert.InRange(r.TailOffset, 0, len);
        }
    }

    [Fact]
    public void Window_first_then_screen()
    {
        var screen = new HRect(0, 0, 1920, 1009);
        var smallWindow = new HRect(700, 300, 500, 200);   // 대화창 — 말풍선이 창 안에 안 들어감
        var r = HelpCalloutPlacement.Resolve(new HRect(1100, 320, 20, 20), W, H, smallWindow, screen);
        Assert.True(screen.Contains(r.Rect(W, H)));
        var bigWindow = new HRect(100, 100, 1500, 800);
        var r2 = HelpCalloutPlacement.Resolve(new HRect(1560, 120, 20, 20), W, H, bigWindow, screen);
        Assert.True(bigWindow.Contains(r2.Rect(W, H)));
    }
}

public class ThemeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mns_theme_" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Missing_file_loads_null()
    {
        Assert.Null(new ThemeStore(FilePath).Load());
    }

    [Fact]
    public void Save_then_load_roundtrips_and_keeps_other_keys()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"other\": 42}");
        var store = new ThemeStore(FilePath);
        Assert.True(store.Save(AppTheme.Light));
        Assert.Equal(AppTheme.Light, new ThemeStore(FilePath).Load());
        Assert.Contains("\"other\": 42", File.ReadAllText(FilePath));
        Assert.True(store.Save(AppTheme.Dark));
        Assert.Equal(AppTheme.Dark, store.Load());
    }

    [Fact]
    public void Corrupt_file_is_ignored_quietly_and_rewritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ 이건 json 이 아님");
        var store = new ThemeStore(FilePath);
        Assert.Null(store.Load());
        Assert.True(store.Save(AppTheme.Light));
        Assert.Equal(AppTheme.Light, store.Load());
    }

    [Fact]
    public void Unwritable_location_fails_quietly()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "settings.json"));   // 파일 자리에 폴더 → 쓰기 실패
        var store = new ThemeStore(FilePath);
        Assert.False(store.Save(AppTheme.Light));
        Assert.Null(store.Load());
    }

    [Theory]
    [InlineData("dark", AppTheme.Dark)]
    [InlineData("LIGHT", AppTheme.Light)]
    [InlineData(" 라이트 ", AppTheme.Light)]
    [InlineData("blue", null)]
    [InlineData(null, null)]
    public void Parse_accepts_names(string? s, AppTheme? expected) => Assert.Equal(expected, ThemeStore.Parse(s));
}

/// <summary>토큰 두 벌(다크 · 라이트) 짝 · 화면이 쓰는 토큰이 정의됨 · 하드코딩 색 없음.</summary>
public class ThemeTokenTests
{
    private static string AppDir => Path.Combine(TestUtil.RepoRoot, "src", "Sim.App");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static (HashSet<string> Dark, HashSet<string> Light, HashSet<string> Shared) Tokens()
    {
        var doc = XDocument.Load(Path.Combine(AppDir, "App.axaml"));
        HashSet<string> Keys(XElement dict) => dict.Elements().Select(e => (string?)e.Attribute(X + "Key")).Where(k => k is not null).ToHashSet()!;
        var themeDicts = doc.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(X + "Key") is not null).ToList();
        var dark = Keys(themeDicts.Single(d => (string?)d.Attribute(X + "Key") == "Dark"));
        var light = Keys(themeDicts.Single(d => (string?)d.Attribute(X + "Key") == "Light"));
        var root = doc.Descendants().First(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(X + "Key") is null);
        var shared = Keys(root);
        return (dark, light, shared);
    }

    [Fact]
    public void Dark_and_light_define_the_same_token_set()
    {
        var (dark, light, _) = Tokens();
        Assert.Empty(dark.Except(light));
        Assert.Empty(light.Except(dark));
        Assert.True(dark.Count > 60);
    }

    private static IEnumerable<string> UiSources(string pattern) =>
        Directory.GetFiles(AppDir, pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Every_token_used_by_the_ui_is_defined()
    {
        var (dark, _, shared) = Tokens();
        var known = dark.Union(shared).ToHashSet();
        var used = new HashSet<string>();
        foreach (var f in UiSources("*.axaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\{(?:DynamicResource|StaticResource) (\w+)\}")) used.Add(m.Groups[1].Value);
        foreach (var f in UiSources("*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"(?:\.Res\([^,]+,|\.Fg\(|\.Bg\(|\.Brush\(|ColorRes\(|ColorOf\(|Find\(|\(\$?)""(\w+(?:Brush|Color)|Shadow\w+)""")) used.Add(m.Groups[1].Value);
        used.RemoveWhere(k => k.StartsWith("PlotSeries"));   // PlotSeries{i}Color 는 아래에서 따로
        foreach (var k in used) Assert.True(known.Contains(k), $"토큰 '{k}' 가 App.axaml 에 없다");
        for (int i = 1; i <= 7; i++) Assert.Contains($"PlotSeries{i}Color", known);
    }

    [Fact]
    public void Ui_does_not_use_static_resource_for_theme_tokens()
    {
        var (dark, _, _) = Tokens();
        foreach (var f in UiSources("*.axaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\{StaticResource (\w+)\}"))
                Assert.False(dark.Contains(m.Groups[1].Value), $"{Path.GetFileName(f)}: 테마 토큰 '{m.Groups[1].Value}' 를 StaticResource 로 — 테마를 바꿔도 안 바뀐다");
    }

    /// <summary>화면 색은 토큰만 — 색 리터럴은 App.axaml(토큰 정의)과 데이터 색(컬러맵 · 상태 · 물체 · MC — 줄에 data-color 표시)뿐.</summary>
    [Fact]
    public void No_hardcoded_ui_colors()
    {
        var rx = new Regex(@"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b|Color\.FromA?rgb\(|\bBrushes\.\w+|\bColors\.\w+");
        var bad = new List<string>();
        foreach (var f in UiSources("*.cs").Concat(UiSources("*.axaml")))
        {
            var name = Path.GetFileName(f);
            if (name is "App.axaml" or "ColorMaps.cs") continue;
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var code = line.TrimStart();
                if (code.StartsWith("//") || code.StartsWith("///") || code.StartsWith("<!--")) continue;
                if (rx.IsMatch(line) && !line.Contains("data-color")) bad.Add($"{name}:{i + 1}: {code}");
            }
        }
        Assert.True(bad.Count == 0, "하드코딩 색:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Gl_palette_default_matches_previous_dark_look()
    {
        var p = ViewPalette.Dark;
        Assert.Equal((0.07f, 0.09f, 0.12f), p.Background);
        Assert.Equal((0.4f, 0.6f, 0.45f), p.Grid);
    }
}
