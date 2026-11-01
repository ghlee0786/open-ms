using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sim.App;

/// <summary>
/// 오른쪽 '결과 요약' — 요약 JSON(Results.Summary)을 항목별 카드(라벨 · 값 줄)로 표시.
/// 같은 내용의 텍스트(Results.SummaryText)는 숨은 SummaryText 에 두고 '요약 텍스트 복사'로 클립보드에 복사한다.
/// </summary>
public partial class MainWindow
{
    private bool _summaryCopyWired;

    private void BuildSummaryPanel(JsonObject? s)
    {
        if (!_summaryCopyWired)
        {
            _summaryCopyWired = true;
            SummaryCopyBtn.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb && !string.IsNullOrEmpty(SummaryText.Text))
                    await cb.SetTextAsync(SummaryText.Text);
            };
        }
        var panel = SummaryPanel;
        panel.Children.Clear();
        if (s is null) return;
        var inv = CultureInfo.InvariantCulture;

        static double? Num(JsonNode? n)
        {
            if (n is not JsonValue v) return null;
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return l;
            return null;
        }
        string F(JsonNode? n, string fmt, string unit = "") => Num(n) is { } d && double.IsFinite(d) ? d.ToString(fmt, inv) + unit : "—";
        static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var t) ? t : n?.ToJsonString() ?? "";
        static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        // 섹션 = 작은 머리글 + 줄 목록
        StackPanel Section(string title)
        {
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(new TextBlock { Text = title, Classes = { "cardh" }, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            panel.Children.Add(sp);
            return sp;
        }
        void Row(Panel parent, string label, string value, string? valueClass = null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), Margin = new Avalonia.Thickness(0, 1) };
            g.Children.Add(new TextBlock { Text = label, FontSize = 12, Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis });
            var v = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            if (valueClass is not null) v.Classes.Add(valueClass);
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            parent.Children.Add(g);
        }
        StackPanel Item(Panel parent, string name, string? badge = null)
        {
            var box = new StackPanel { Spacing = 1, Margin = new Avalonia.Thickness(0, 4, 0, 2) };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeight.SemiBold });
            if (badge is not null)
                head.Children.Add(new TextBlock { Text = badge, FontSize = 11, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
            box.Children.Add(head);
            parent.Children.Add(box);
            return box;
        }
        const string warnBrush = "warn";   // 경고 글자 클래스 → WarnBrush 토큰

        // 실행
        var run = Section("실행");
        Row(run, "시나리오", Str(s["scenario"]));
        Row(run, "종료", $"{F(s["endTime_s"], "F2", " s")} · {Str(s["stopReason"])}");
        if (Bool(s["suspect"])) Row(run, "수치 상태", "⚠ 수치 의심(suspect) 실행", warnBrush);

        // 착지한 물체
        if (s["bodies"] is JsonObject bodies)
        {
            var landed = bodies.Where(kv => kv.Value?["impact"] is JsonObject).ToList();
            if (landed.Count > 0)
            {
                var sec = Section($"착지 · 착탄 ({landed.Count})");
                foreach (var (name, node) in landed)
                {
                    var b = node!.AsObject(); var i = b["impact"]!.AsObject();
                    var it = Item(sec, name, $"{Str(b["aeroFidelity"])} · {Str(b["massSource"])}");
                    Row(it, "착지 시각", F(i["time_s"], "F2", " s"));
                    Row(it, "위치 N · E", $"{F(i["n_m"], "F1")} · {F(i["e_m"], "F1")} m");
                    Row(it, "속도 · 노즈각", $"{F(i["speed_mps"], "F2", " m/s")} · {F(i["noseAngleFromVertical_deg"], "F1", "°")}");
                    Row(it, "관입", $"{F(i["youngPenetration_m"], "F3", " m")} ({F(i["youngMeanDecel_g"], "F0", " g")})");
                    if (i["youngInEnvelope"] is JsonValue ie && !ie.GetValue<bool>()) Row(it, "", "관입 식 적용 범위 밖(질량 < 2.27 kg) — 외삽", warnBrush);
                    if (Num(b["staticMargin"]) is { } sm) Row(it, "정적 여유", $"{sm * 100:+0.0;-0.0} % ({(sm > 0 ? "기수 선행 안정" : "불안정 / 역방향")})", sm > 0 ? null : warnBrush);
                }
            }
            var others = bodies.Where(kv => kv.Value?["impact"] is not JsonObject).ToList();
            if (others.Count > 0)
            {
                var sec = Section($"그 밖의 물체 ({others.Count})");
                foreach (var (name, node) in others) Row(sec, name, $"{Str(node!["mode"])} · {Str(node["aeroFidelity"])}/{Str(node["massSource"])}");
            }
        }

        // 낙하산 · 테더 · 힌지
        if (s["devices"] is JsonObject devs)
        {
            var chutes = devs.Where(kv => Str(kv.Value?["type"]) == "parachute").ToList();
            if (chutes.Count > 0)
            {
                var sec = Section($"낙하산 ({chutes.Count})");
                foreach (var (name, node) in chutes)
                    Row(sec, name, $"{Str(node!["state"])} · 최대 {F(node["maxForce_N"], "N0", " N")} ({F(node["maxOpeningG"], "F1", " G")}) @ {F(node["maxForceTime_s"], "F2", " s")}");
            }
        }
        if (s["connectors"] is JsonObject cons)
        {
            var tethers = cons.Where(kv => Str(kv.Value?["type"]) == "tether").ToList();
            var hinges = cons.Where(kv => Str(kv.Value?["type"]) == "hinge").ToList();
            if (tethers.Count > 0)
            {
                var sec = Section($"테더 ({tethers.Count})");
                foreach (var (name, node) in tethers)
                    Row(sec, name, $"최대 장력 {F(node!["maxTension_N"], "N0", " N")}{(Bool(node["broken"]) ? " · 파단" : "")}", Bool(node["broken"]) ? warnBrush : null);
            }
            if (hinges.Count > 0)
            {
                var sec = Section($"힌지 ({hinges.Count})");
                foreach (var (name, node) in hinges)
                    Row(sec, name, $"반력 {F(node!["maxForce_N"], "N0", " N")} · 열림 {F(node["maxAngle_deg"], "F0", "°")}"
                        + (Bool(node["released"]) ? $" · 이탈 {F(node["releaseTime_s"], "F3", " s")} ({Str(node["releaseReason"])})" : ""));
            }
        }
        if (s["collisions"] is JsonObject col)
        {
            var sec = Section("충돌 판정");
            Row(sec, "접촉", $"{Str(col["contacts"])} 건");
            Row(sec, "낙하산 간섭", $"{Str(col["interferences"])} 건", Num(col["interferences"]) > 0 ? warnBrush : null);
        }

        // 지표 — 숫자·참거짓·짧은 문자열 필드만 줄로(긴 배열·객체는 '요약 텍스트 복사'에 원문)
        if (s["metrics"] is JsonObject metrics && metrics.Count > 0)
        {
            var sec = Section($"지표 ({metrics.Count})");
            foreach (var (name, node) in metrics)
            {
                var it = Item(sec, name);
                if (node is not JsonObject m) { Row(it, "값", Str(node)); continue; }
                int shown = 0;
                foreach (var (k, v) in m)
                {
                    if (k == "type" || v is JsonObject || v is JsonArray) continue;
                    string val = Num(v) is { } d ? (Math.Abs(d) >= 1000 || d == Math.Round(d) ? d.ToString("G6", inv) : d.ToString("G4", inv))
                               : v is JsonValue jv && jv.TryGetValue<bool>(out var bb) ? (bb ? "예" : "아니오") : Str(v);
                    bool fail = (k is "pass" or "ok") && v is JsonValue pv && pv.TryGetValue<bool>(out var p) && !p;
                    Row(it, k, val, fail ? warnBrush : null);
                    if (++shown >= 8) break;
                }
            }
        }

        // 경고
        if (s["warnings"] is JsonArray warns && warns.Count > 0)
        {
            var sec = Section($"경고 ({warns.Count})");
            foreach (var w in warns.Take(6))
                sec.Children.Add(new TextBlock { Text = "· " + Str(w), FontSize = 12, TextWrapping = TextWrapping.Wrap, Classes = { warnBrush } });
            if (warns.Count > 6) sec.Children.Add(new TextBlock { Text = $"… 외 {warns.Count - 6}건 (요약 텍스트 복사로 전체)", FontSize = 11, Classes = { "muted" } });
        }
    }
}
