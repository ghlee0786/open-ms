using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sim.Core.Scenario;

namespace Sim.App;

/// <summary>
/// 결과 신뢰도 표시(PRD FR-REL-05): `mns credibility` 가 쓴 validation/reports/credibility/credibility.json 을 읽어
/// 현재 시나리오의 출력별 신뢰 등급 배지(신뢰 녹색 · 참고 황색 · 추정 적색)와 90 % 구간을 대시보드 카드에 보이고,
/// 해석 메뉴에서 7009A 8요인·근거·시험 우선순위 창을 연다.
/// </summary>
public partial class MainWindow
{
    private JsonObject? _cred;
    private string? _credPath;

    /// <summary>등급 → 배지 클래스(trusted · reference · 그 밖 = 추정) — 색은 토큰 Badge*Brush.</summary>
    public static string BadgeClass(string cls) => cls switch { "신뢰" => "trusted", "참고" => "reference", _ => "estimate" };

    /// <summary>credibility.json 을 현재 폴더·실행 파일 폴더에서 위로 올라가며 찾는다.</summary>
    public static string? FindCredibilityJson(string? scenarioPath)
    {
        var starts = new List<string?> { scenarioPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(scenarioPath)), Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var s in starts.Where(s => s is not null))
        {
            var d = new DirectoryInfo(s!);
            while (d is not null)
            {
                var f = Path.Combine(d.FullName, "validation", "reports", "credibility", "credibility.json");
                if (File.Exists(f)) return f;
                d = d.Parent;
            }
        }
        return null;
    }

    private void WireCredibility() => MiCredibility.Click += async (_, _) => await ShowCredibility();

    /// <summary>시나리오가 바뀔 때 호출 — 그 시나리오의 출력 등급을 카드에 채운다.</summary>
    private void UpdateCredibilityCard(string? scenarioName, string? scenarioPath)
    {
        CredPanel.Children.Clear();
        _credPath ??= FindCredibilityJson(scenarioPath);
        if (_credPath is not null && _cred is null)
        {
            try { _cred = J.ParseObject(File.ReadAllText(_credPath)); }
            catch (Exception ex) { CredNote.Text = $"신뢰도 파일 읽기 실패: {ex.Message}"; return; }
        }
        var rows = (_cred?["outputs"] as JsonArray)?.OfType<JsonObject>().Where(o => J.Str(o, "scenario", "") == scenarioName).ToList() ?? [];
        if (rows.Count == 0)
        {
            CredNote.Text = _cred is null ? "신뢰도 분석 없음 (`mns credibility`)" : "이 시나리오의 신뢰도 분석 없음";
            return;
        }
        foreach (var o in rows) CredPanel.Children.Add(CredRow(o));
        CredNote.Text = "자세히: 해석 → 결과 신뢰도";
        ToolTip.SetTip(CredNote, "입력 · 모델 형태 · 수치 불확도 합성 5–95 %");
    }

    private static Control CredRow(JsonObject o)
    {
        string cls = J.Str(o, "class", "추정");
        var badge = new Border { Classes = { "badge", BadgeClass(cls) }, Child = new TextBlock { Text = cls } };
        // 배지 왼쪽 + 두 줄(이름 · 값). 줄 바꿈 없는 TextBlock 은 잘림(…)이어도 원하는 폭을 글 전체로 보고해 카드가 넓어져 '?' 가 스크롤 막대 밑으로 갔다 → Wrap + MaxLines 1 로 폭을 받은 만큼만
        var g = new DockPanel();
        badge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        DockPanel.SetDock(badge, Dock.Left); g.Children.Add(badge);
        var label = new TextBlock { Text = J.Str(o, "label", ""), FontSize = 12, Margin = new Avalonia.Thickness(6, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        var val = new TextBlock { Text = IntervalText(o), FontSize = 11.5, Classes = { "dim" }, Margin = new Avalonia.Thickness(6, 1, 0, 0), TextWrapping = TextWrapping.Wrap, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        g.Children.Add(new StackPanel { Children = { label, val } });
        ToolTip.SetTip(g, J.Str(o, "label", "") + "\n" + IntervalText(o) + "\n" + J.Str(o, "useScope", ""));
        return g;
    }

    public static string IntervalText(JsonObject o)
    {
        var ci = CultureInfo.InvariantCulture;
        string unit = J.Str(o, "unit", "");
        if (J.NumOpt(o, "probability") is { } p)
            return string.Format(ci, "P = {0:P1} (95 % {1:P1}–{2:P1})", p, J.Num(o, "probLo95", double.NaN), J.Num(o, "probHi95", double.NaN));
        return string.Format(ci, "공칭 {0:G4} · 90 % {1:G4}–{2:G4} {3}", J.Num(o, "nominal", double.NaN), J.Num(o, "lo90", double.NaN), J.Num(o, "hi90", double.NaN), unit);
    }

    private async Task ShowCredibility() => await CredibilityWindow().ShowDialog(this);

    private Window CredibilityWindow()
    {
        _credPath ??= FindCredibilityJson(null);
        var sb = new StringBuilder();
        var list = new StackPanel { Spacing = 6 };
        if (_credPath is null || !File.Exists(_credPath))
        {
            list.Children.Add(new TextBlock { Text = "credibility.json 이 없습니다 — `mns credibility` 를 실행하세요(validation/reports/credibility).", TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            _cred ??= J.ParseObject(File.ReadAllText(_credPath));
            list.Children.Add(new TextBlock { Text = $"{ShownPath(_credPath)}\n틀: {J.Str(_cred, "framework", "")} · 엔진 {J.Str(_cred, "engine", "")}", FontSize = 11.5, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
            foreach (var o in (_cred["outputs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock { Text = J.Str(o, "scenario", ""), FontSize = 11, Classes = { "muted" } });
                row.Children.Add(CredRow(o));
                var fs = (o["factors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Select(f => $"{J.Str(f, "factor", "")} {J.Num(f, "level", 0):0}{(J.Num(f, "level", 0) < J.Num(f, "threshold", 0) ? "(격차)" : "")}");
                row.Children.Add(new SelectableTextBlock { Text = "7009A: " + string.Join(" · ", fs) + "\n사용 범위: " + J.Str(o, "useScope", ""), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(6, 0, 0, 0) });
                list.Children.Add(new Border { Classes = { "rowline" }, Child = row });
            }
            sb.AppendLine("시험 우선순위 (신뢰도를 올리는 순서):");
            int k = 1;
            foreach (var t in (_cred["testPriorities"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,2}. {1}  (점수 {2:0.000})", k++, J.Str(t, "test", ""), J.Num(t, "score", 0)));
            list.Children.Add(new SelectableTextBlock { Text = sb.ToString(), Classes = { "mono" }, TextWrapping = TextWrapping.Wrap });
        }
        return DialogWindow("결과 신뢰도 — NASA-STD-7009A", Sim.App.Help.HelpKeys.CredibilityWindow,
            new ScrollViewer { Content = new Border { Classes = { "card" }, Child = list } }, 980, 760);
    }
}
