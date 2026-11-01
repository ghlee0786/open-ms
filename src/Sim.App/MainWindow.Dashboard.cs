using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Structures;

namespace Sim.App;

/// <summary>오른쪽 텔레메트리 대시보드(FR-UI-01): KPI 타일 · 장치/연결 막대 · 착지 카드 · 구조 카드.</summary>
public partial class MainWindow
{
    private sealed record Tile(TextBlock Value, TextBlock Unit, Border Box);
    private readonly Dictionary<string, Tile> _tiles = new();
    private readonly List<(Func<Frame, (double Frac, string Text)> Eval, ProgressBar Bar, TextBlock Txt)> _deviceRows = new();
    private int _deviceRowsFor = -2;
    private double[]? _cumMaxG;
    private int _cumMaxFor = -1;

    // 타일 값 색 = 클래스(warn · alarm) → 토큰 WarnBrush · ErrBrush, 상태 칩 = 클래스(free · landed · stowed) → Chip*Brush
    private const string TileWarn = "warn", TileAlarm = "alarm";

    private static readonly (string Key, string Label, string Unit)[] TileDefs =
    [
        ("alt", "고도 AGL", "m"), ("spd", "속도", "m/s"), ("mach", "마하", "M"),
        ("vd", "하강률", "m/s"), ("air", "대기속도", "m/s"), ("q", "동압", "kPa"),
        ("g", "하중", "G"), ("gmax", "최대 하중(누적)", "G"), ("w", "각속도 |ω|", "°/s"),
        ("roll", "Roll", "°"), ("pitch", "Pitch", "°"), ("yaw", "Yaw", "°"),
    ];

    private void BuildDashboard()
    {
        TileGrid.Children.Clear();
        foreach (var (key, label, unit) in TileDefs)
        {
            var v = new TextBlock { Classes = { "tvalue" }, Text = "—", TextTrimming = TextTrimming.CharacterEllipsis };
            var u = new TextBlock { Classes = { "tunit" }, Text = unit };
            var box = new Border
            {
                Classes = { "tile" },
                Child = new StackPanel
                {
                    Spacing = 1,
                    Children = { new TextBlock { Classes = { "tlabel" }, Text = label, TextTrimming = TextTrimming.CharacterEllipsis }, v, u },
                },
            };
            _tiles[key] = new Tile(v, u, box);
            TileGrid.Children.Add(box);
        }
    }

    private void SetTile(string key, string value, string? level = null)
    {
        if (!_tiles.TryGetValue(key, out var t)) return;
        t.Value.Text = value;
        t.Value.Classes.Set(TileWarn, level == TileWarn);
        t.Value.Classes.Set(TileAlarm, level == TileAlarm);
    }

    private static string F(double v, string fmt) => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "—";

    private void RebuildDeviceRows(World w, Body b)
    {
        _deviceRows.Clear();
        DevicePanel.Children.Clear();
        _deviceRowsFor = b.Index;
        int di = 0;
        foreach (var ob in w.Bodies)
            foreach (var d in ob.Devices)
            {
                int k = di++;
                if (ob != b) continue;
                Func<Frame, (double, string)> eval = d switch
                {
                    Parachute p => f => (f.Devices[k].Fill, f.Devices[k].Visible ? $"충만 {f.Devices[k].Fill * 100:F0}% · {f.Devices[k].Force:F0} N" : "수납"),
                    Multirotor m => f => (f.Devices[k].Fill, f.Devices[k].Visible ? $"추력 {f.Devices[k].Force:F1} N" : "정지"),
                    Pulse pu => f => (f.Devices[k].Fill, f.Devices[k].Visible ? $"펄스 {f.Devices[k].Force:F0} N" : $"{pu.PeakG:F0} G · {pu.Duration * 1000:F0} ms"),
                    _ => f => (0, d.Type),
                };
                AddDeviceRow($"{d.Name} ({d.Type})", eval);
            }
        for (int ci = 0; ci < w.Connectors.Count; ci++)
        {
            int k = ci;
            var c = w.Connectors[ci];
            bool mine = c switch { Tether t => t.A == b || t.B == b, Hinge h => h.A == b || h.B == b, Rail r => r.Parent == b || r.Child == b, _ => false };
            if (!mine) continue;
            double scale = c switch { Tether t => double.IsFinite(t.BreakLoad) ? t.BreakLoad : 5000, Hinge h => double.IsFinite(h.BreakLoad) ? h.BreakLoad : 10000, _ => 1000 };
            AddDeviceRow($"{c.Name} ({c.Type})", f => (Math.Clamp(f.Connectors[k].Tension / scale, 0, 1),
                f.Connectors[k].Visible ? $"{(c is Hinge ? "반력" : "장력")} {f.Connectors[k].Tension:F0} N" : "해제/비활성"));
        }
        DeviceCard.IsVisible = _deviceRows.Count > 0;
    }

    private void AddDeviceRow(string name, Func<Frame, (double, string)> eval)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        var txt = new TextBlock { FontSize = 11.5, Classes = { "dim" }, HorizontalAlignment = HorizontalAlignment.Right };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        var nameTb = new TextBlock { Text = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        g.Children.Add(nameTb);
        Grid.SetColumn(txt, 1); g.Children.Add(txt);
        Grid.SetRow(bar, 1); Grid.SetColumnSpan(bar, 2); g.Children.Add(bar);
        DevicePanel.Children.Add(g);
        _deviceRows.Add((eval, bar, txt));
    }

    /// <summary>현재 시각·선택 물체로 대시보드 갱신.</summary>
    private void UpdateTelemetry()
    {
        if (_world is null || _selected < 0 || _selected >= _world.Bodies.Count)
        {
            DashBody.Text = "선택 없음"; DashMode.Text = "—"; DashPhase.Text = "";
            foreach (var k in _tiles.Keys) SetTile(k, "—");
            return;
        }
        int idx = _world.Recorder.IndexAt(_time);
        if (idx < 0) return;
        var w = _world;
        var f = w.Recorder.Frames[idx];
        var b = w.Bodies[_selected];
        var s = f.Bodies[_selected];
        var e = s.Q.ToEuler() * (180 / Math.PI);
        var atm = w.Env.Atmosphere.At(-s.Cg.Z);
        double mach = atm.SpeedOfSound > 0 ? s.Airspeed / atm.SpeedOfSound : 0;
        double q = 0.5 * atm.Density * s.Airspeed * s.Airspeed / 1000;

        DashBody.Text = b.Name;
        var mode = b.Mode == BodyMode.Kinematic ? BodyMode.Kinematic : s.Mode;
        string chip;
        (DashMode.Text, chip) = mode switch
        {
            BodyMode.Free => ("비행", "free"),
            BodyMode.Landed => ("착지", "landed"),
            BodyMode.Stowed => ("수납", "stowed"),
            _ => ("운반체", "stowed"),
        };
        foreach (var c in new[] { "free", "landed", "stowed" }) DashModeChip.Classes.Set(c, c == chip);
        var phase = w.EventLog.LastOrDefault(x => x.Time <= _time + 1e-9);
        DashPhase.Text = string.Format(CultureInfo.InvariantCulture, "t = {0:F2} s · N {1:F0} · E {2:F0} m · 단계: {3}", f.T, s.Cg.X, s.Cg.Y, phase?.Name ?? "시작");

        SetTile("alt", F(w.Env.Agl(s.Cg.Z), "F1"));
        SetTile("spd", F(s.Velocity.Norm, "F1"));
        SetTile("mach", F(mach, "F3"), mach > 0.7 ? TileWarn : null);
        SetTile("vd", F(s.Velocity.Z, "F2"));
        SetTile("air", F(s.Airspeed, "F1"));
        SetTile("q", F(q, "F2"));
        SetTile("g", F(s.LoadG, "F2"), s.LoadG > 40 ? TileAlarm : s.LoadG > 15 ? TileWarn : null);
        if (_cumMaxFor != _selected || _cumMaxG is null || _cumMaxG.Length != w.Recorder.Frames.Count)
        {
            var frames = w.Recorder.Frames;
            _cumMaxG = new double[frames.Count];
            double m = 0;
            for (int i = 0; i < frames.Count; i++) { var lg = frames[i].Bodies[_selected].LoadG; if (double.IsFinite(lg)) m = Math.Max(m, lg); _cumMaxG[i] = m; }
            _cumMaxFor = _selected;
        }
        double gmax = _cumMaxG[idx];
        SetTile("gmax", F(gmax, "F1"), gmax > 40 ? TileAlarm : gmax > 15 ? TileWarn : null);
        SetTile("w", F(s.Omega.Norm * 180 / Math.PI, "F0"));
        SetTile("roll", F(e.X, "F1")); SetTile("pitch", F(e.Y, "F1")); SetTile("yaw", F(e.Z, "F1"));

        if (_deviceRowsFor != _selected) RebuildDeviceRows(w, b);
        foreach (var (eval, bar, txt) in _deviceRows)
        {
            var (frac, text) = eval(f);
            bar.Value = Math.Clamp(double.IsFinite(frac) ? frac : 0, 0, 1);
            txt.Text = text;
        }

        var imp = b.Root.Impact ?? b.Impact;
        if (imp is not null)
        {
            bool done = imp.Time <= _time + 1e-9;
            ImpactText.Text = string.Format(CultureInfo.InvariantCulture,
                "{0} t = {1:F2} s\nN {2:F1} · E {3:F1} m (원점 거리 {4:F1} m)\n속도 {5:F2} m/s · 노즈각 {6:F1}°\n관입 {7:F3} m · {8:F0} g",
                done ? "착지" : "예정 착지", imp.Time, imp.Position.X, imp.Position.Y, imp.Position.HorizontalNorm, imp.Velocity.Norm, imp.NoseAngleDeg, imp.PenetrationDepth, imp.PenetrationG);
        }
        else ImpactText.Text = "착지 기록 없음 (비행 중 종료 또는 운반체)";
        UpdateStructCard(b);
    }

    private void UpdateStructCard(Body b)
    {
        var sb = new System.Text.StringBuilder();
        var def = b.Structure;
        sb.AppendLine(def?.Material is { } m ? $"재질 {m.Name} ({(def.MaterialSource == "fusion-props" ? Sim.Core.Structures.MaterialLibrary.FusionSource : def.MaterialSource)})" : def?.RequestedMaterial is { } rq ? $"재질 '{rq}' 미지원 — 판정 없음" : "재질 미지정 — 압력·근사 응력만");
        if (def is not null) sb.AppendLine($"단면 {def.Section}{(def.Thickness is { } t ? $" · t {t * 1000:F2} mm" : "")}");
        if (_liveResults.TryGetValue(b.Index, out var r))
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "현재: σvm 최대 {0:F2} MPa · 압력 {1:F1}~{2:F1} kPa", r.MaxVonMises / 1e6, r.MinPressure / 1e3, r.MaxPressure / 1e3));
            if (double.IsFinite(r.MinSafety)) sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      최소 SF {0:F2} · {1}", r.MinSafety, Rendering.ColorMaps.StateLabel(r.Worst)));
        }
        if (_structural?.For(b.Index) is { } h)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "전 구간: σvm {0:F2} MPa @ {1:F2} s · 압력 {2:F1} kPa", h.PeakVm / 1e6, h.PeakVmTime, h.MaxPressure / 1e3));
            if (double.IsFinite(h.MinSafetyValue))
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      최소 SF {0:F2} (정적 {1:F2}, 속도배율 {2:F3}) · {3}", h.MinSafetyValue, h.MinSafetyStatic, h.RateFactorAtPeak, Rendering.ColorMaps.StateLabel(h.Worst)));
            if (double.IsFinite(h.FirstYieldTime)) sb.AppendLine($"      최초 항복 {h.FirstYieldTime:F2} s");
            if (double.IsFinite(h.FirstFractureTime)) sb.AppendLine($"      최초 파단 {h.FirstFractureTime:F2} s");
            if (double.IsFinite(h.FirstBucklingTime)) sb.AppendLine($"      좌굴 위험 {h.FirstBucklingTime:F2} s");
            if (h.Impact is { } im)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "착지 {0:F2} m/s ({1}) · 손상 시작 {2:F2} m/s", im.NormalSpeed, im.Contact, im.DamageSpeed));
                sb.AppendLine($"      {im.Verdict}");
            }
        }
        else if (_colorMode == Rendering.ColorMode.Off) sb.Append("색상 모드를 고르거나 구조 분석(F6)을 실행하세요.");
        StructText.Text = sb.ToString().TrimEnd();
    }
}
