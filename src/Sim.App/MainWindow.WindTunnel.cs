using Sim.App.Rendering;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Structures;
using Vec3 = Sim.Core.Numerics.Vec3;

namespace Sim.App;

/// <summary>
/// 풍동 보기(해석 → 풍동 보기 · F8, <see cref="WindTunnelWindow"/>) 열기와 재생 장면 겹쳐 보기(보기 레이어 'CFD 유선').
/// 대상 = aero.flowField 가 있는 물체(선택 물체 우선, 메시는 메인 3D 뷰와 같은 TriMesh). 없으면 기본 유동장 파일 + 원통 외형.
/// </summary>
public partial class MainWindow
{
    /// <summary>기본 유동장 파일(저장소 상대) = config/aero_db/*_flow.json 중 이름순 첫 파일. 없으면 그 무늬 그대로(파일 없음으로 처리).</summary>
    private string DefaultFlowFile
    {
        get
        {
            var dir = Path.Combine(_repo, "config", "aero_db");
            var f = Directory.Exists(dir) ? Directory.GetFiles(dir, "*_flow.json").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault() : null;
            return f is null ? "config/aero_db/*_flow.json" : Path.GetRelativePath(_repo, f).Replace('\\', '/');
        }
    }
    private WindTunnelWindow? _windTunnel;

    private void WireWindTunnel()
    {
        MiWindTunnel.Click += (_, _) => OpenWindTunnel();
        WireAirflow();
        View.FlowLinesProvider = FlowLinesFor;
    }

    /// <summary>풍동 보기 대상 목록(선택 물체가 맨 앞).</summary>
    private List<WindTunnelSource> WindTunnelSources()
    {
        var list = new List<(int Order, WindTunnelSource Src)>();
        if (_world is not null)
            for (int i = 0; i < _world.Bodies.Count; i++)
            {
                if (_world.Bodies[i].Aero?.FlowField is not { } ff) continue;
                var mesh = View.Scene is { } sc && i < sc.BodyMeshes.Length && sc.BodyMeshes[i] is { } m ? m : WindTunnel.CylinderMesh(ff);
                list.Add((i == _selected ? -1 : i, new WindTunnelSource(_world.Bodies[i].Name, ff, mesh)));
            }
        if (list.Count == 0)
        {
            var path = Path.Combine(_repo, DefaultFlowFile);
            if (File.Exists(path))
            {
                try
                {
                    var ff = SurfaceFlowField.Load(path);
                    list.Add((0, new WindTunnelSource($"{ff.Name} (원통 외형)", ff, WindTunnel.CylinderMesh(ff))));
                }
                catch (Exception ex) { Log(LogLevel.Warn, "풍동", $"{DefaultFlowFile}: {ex.Message}"); }
            }
        }
        return list.OrderBy(x => x.Order).Select(x => x.Src).ToList();
    }

    /// <summary>풍동 보기 창 열기(이미 열려 있으면 닫고 새로). alpha 가 없으면 선택 물체의 현재 대기상대 받음각에 가장 가까운 케이스.</summary>
    private WindTunnelWindow? OpenWindTunnel(double? alpha = null)
    {
        var sources = WindTunnelSources();
        if (sources.Count == 0)
        {
            Log(LogLevel.Warn, "풍동", $"aero.flowField 가 있는 물체도, 기본 유동장 파일({DefaultFlowFile})도 없습니다.");
            ShowLog();
            return null;
        }
        _windTunnel?.Close();
        var w = new WindTunnelWindow(sources, _doc?.Name ?? "") { Icon = Icon };
        w.KeepOnScreen();
        _windTunnel = w;
        w.Closed += (_, _) => { if (ReferenceEquals(_windTunnel, w)) _windTunnel = null; };
        if ((alpha ?? CurrentAlphaT()) is { } a) w.SelectAlpha(a);
        w.Show(this);
        Log(LogLevel.Info, "풍동", $"풍동 보기: {sources[0].Name} — {Path.GetFileName(sources[0].Flow.Source)}");
        return w;
    }

    /// <summary>선택 물체(없으면 첫 유동장 물체)의 현재 시각 대기상대 받음각 α_t [deg](앞뒤 대칭이면 0~90 으로 접음).</summary>
    private double? CurrentAlphaT()
    {
        if (_world is null || _world.Recorder.Frames.Count == 0) return null;
        int i = _selected >= 0 && _selected < _world.Bodies.Count && _world.Bodies[_selected].Aero?.FlowField is not null ? _selected
            : _world.Bodies.FindIndex(b => b.Aero?.FlowField is not null);
        if (i < 0) return null;
        var f = _world.Recorder.Interpolate(_time) ?? _world.Recorder.Frames[^1];
        var s = f.Bodies[i];
        var air = s.Q.InverseRotate(s.AirVelocity);
        if (air.Norm < 1e-6) return null;
        double at = SurfaceFlowField.Attitude(air).AlphaT;
        return _world.Bodies[i].Aero!.FlowField!.ForeAftSymmetric && at > 90 ? 180 - at : at;
    }

    // ---------------- 재생 장면 겹쳐 보기 ----------------

    private (string Key, IReadOnlyList<(Vec3[] P, float[] Rgba)> Lines)? _flowLines;

    /// <summary>물체 i 의 현재 자세에 가장 가까운 받음각 케이스 유선(동체 좌표, 속력비 Turbo 색). 유동장이 없거나 정지면 null.</summary>
    private IReadOnlyList<(Vec3[] P, float[] Rgba)>? FlowLinesFor(int i, BodySample s)
    {
        if (_world is null || i < 0 || i >= _world.Bodies.Count || _world.Bodies[i].Aero?.FlowField is not { } ff) return null;
        var air = s.Q.InverseRotate(s.AirVelocity);
        if (air.Norm < 1e-6) return null;
        var (at, ep) = SurfaceFlowField.Attitude(air);
        string group = ff.GroupNearest(s.AirVelocity.Norm / 340.3);
        // 같은 케이스 · 1° 이내 공력 롤이면 그대로(프레임마다 다시 만들지 않음)
        string key = FormattableString.Invariant($"{i}|{group}|{Math.Round(at)}|{Math.Round(Math.Atan2(ep.Y, ep.Z) * 180 / Math.PI)}");
        if (_flowLines is { } c && c.Key == key) return c.Lines;
        var (_, lines) = ff.StreamlinesBody(air, group);
        var res = lines.Select(l =>
        {
            var rgba = new float[4 * l.Points.Length];
            for (int k = 0; k < l.Points.Length; k++)
            {
                var (r, g, b) = ColorScale.Turbo(Math.Clamp(l.SpeedRatio[k] / 1.5, 0, 1));
                rgba[4 * k] = r; rgba[4 * k + 1] = g; rgba[4 * k + 2] = b; rgba[4 * k + 3] = 0.8f;
            }
            return (l.Points, rgba);
        }).ToList();
        _flowLines = (key, res);
        return res;
    }
}
