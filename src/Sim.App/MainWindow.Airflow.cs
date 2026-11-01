using Sim.Core.Airflow;
using Sim.Core.Geometry;
using Sim.Core.Models;

namespace Sim.App;

/// <summary>
/// GPU 가상 풍동(해석 → GPU 가상 풍동 · Shift+F8, <see cref="AirflowWindow"/>) 열기.
/// 대상 = 메시가 있는 물체(표면 유동장이 있는 물체 → 선택 물체 순으로 앞). 장면이 없으면 실모델 컨테이너 STL
/// (MNS_REAL_MODELS 또는 기본 유동장과 같은 설계 태그의 validation/fusion_live_&lt;태그&gt;_groups/container.stl, Fusion 좌표 · 기수 −Z), 그것도 없으면 유동장 원통 외형.
/// </summary>
public partial class MainWindow
{
    /// <summary>실모델 컨테이너 STL(git 밖) — 기본 유동장 config/aero_db/container_&lt;태그&gt;_flow.json 과 같은 태그의 그룹 내보내기 폴더.</summary>
    private string RealContainerStl =>
        $"validation/fusion_live_{Path.GetFileNameWithoutExtension(DefaultFlowFile).Replace("container_", "").Replace("_flow", "").ToUpperInvariant()}_groups/container.stl";
    private AirflowWindow? _airflow;
    private readonly Dictionary<string, OpenFoamReference?> _ofRefs = new();

    private void WireAirflow() => MiAirflow.Click += (_, _) => OpenAirflow();

    private OpenFoamReference? OfReference(string path)
    {
        if (_ofRefs.TryGetValue(path, out var r)) return r;
        try { r = File.Exists(path) ? OpenFoamReference.Load(path) : null; }
        catch (Exception ex) { Log(LogLevel.Warn, "GPU 풍동", $"{path}: {ex.Message}"); r = null; }
        return _ofRefs[path] = r;
    }

    private List<AirflowSource> AirflowSources()
    {
        var list = new List<(int Order, AirflowSource Src)>();
        if (_world is not null && View.Scene is { } sc)
            for (int i = 0; i < _world.Bodies.Count && i < sc.BodyMeshes.Length; i++)
            {
                if (sc.BodyMeshes[i] is not { } mesh || mesh.TriangleCount == 0) continue;
                var ff = _world.Bodies[i].Aero?.FlowField;
                int order = (ff is not null ? 0 : 1000) + (i == _selected ? -500 : 0) + i;
                list.Add((order, ff is null
                    ? new AirflowSource(_world.Bodies[i].Name, mesh, 0, 0, null)
                    : new AirflowSource(_world.Bodies[i].Name, mesh, 2 * ff.Radius, Math.PI * ff.Radius * ff.Radius, OfReference(ff.Source))));
            }
        if (list.Count == 0)
        {
            var flowPath = Path.Combine(_repo, DefaultFlowFile);
            SurfaceFlowField? ff = null;
            try { if (File.Exists(flowPath)) ff = SurfaceFlowField.Load(flowPath); } catch (Exception ex) { Log(LogLevel.Warn, "GPU 풍동", ex.Message); }
            var real = System.Environment.GetEnvironmentVariable("MNS_REAL_MODELS") is { Length: > 0 } dir && dir != "synthetic"
                ? Path.Combine(dir, "container.stl") : Path.Combine(_repo, RealContainerStl);
            double refL = ff is null ? 0 : 2 * ff.Radius, refA = ff is null ? 0 : Math.PI * ff.Radius * ff.Radius;
            var of = OfReference(flowPath);
            if (File.Exists(real))
            {
                try
                {
                    var m = TriMesh.LoadStl(real);
                    var m2b = NoseGeometry.WithNose(Sim.Core.Geometry.Frames.FusionToBody, "-Z");
                    var body = new TriMesh();
                    foreach (var v in m.Vertices) body.Vertices.Add(m2b * v);
                    list.Add((0, new AirflowSource("container (실모델)", body, refL, refA, of)));
                }
                catch (Exception ex) { Log(LogLevel.Warn, "GPU 풍동", $"{real}: {ex.Message}"); }
            }
            if (list.Count == 0 && ff is not null)
                list.Add((0, new AirflowSource($"{ff.Name} (원통 외형)", WindTunnel.CylinderMesh(ff), refL, refA, of)));
        }
        return list.OrderBy(x => x.Order).Select(x => x.Src).ToList();
    }

    /// <summary>GPU 가상 풍동 창 열기(이미 열려 있으면 닫고 새로). 처음 받음각 = 선택 물체 현재 받음각(없으면 45°).</summary>
    private AirflowWindow? OpenAirflow(double? alpha = null)
    {
        var sources = AirflowSources();
        if (sources.Count == 0)
        {
            Log(LogLevel.Warn, "GPU 풍동", "가상 풍동에 놓을 물체 메시가 없습니다(시나리오를 실행하거나 모델을 가져오세요).");
            ShowLog();
            return null;
        }
        _airflow?.Close();
        var w = new AirflowWindow(sources, _repo, alpha ?? CurrentAlphaT() ?? 45, (lvl, msg) =>
            Log(lvl switch { "error" => LogLevel.Error, "warn" => LogLevel.Warn, _ => LogLevel.Info }, "GPU 풍동", msg)) { Icon = Icon };
        _airflow = w;
        w.Closed += (_, _) => { if (ReferenceEquals(_airflow, w)) _airflow = null; };
        w.Show(this);
        Log(LogLevel.Info, "GPU 풍동", $"GPU 가상 풍동: {sources[0].Name} (XLB 격자 볼츠만 · 시각화 근사)");
        return w;
    }
}
