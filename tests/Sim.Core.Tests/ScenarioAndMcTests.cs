using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Gpu;

namespace Sim.Core.Tests;

/// <summary>범용 시나리오 템플릿 실행, Fusion 가져오기, 분산·MC, GPU↔CPU 일치.</summary>
public class ScenarioAndMcTests
{
    [Theory(DisplayName = "시나리오 템플릿이 끝까지 실행되고 전 물체가 착지한다")]
    [InlineData("01_single_sensor_drop.json")]
    [InlineData("02_box_parachute_drop.json")]
    [InlineData("03_container_drone_sensor_poc.json")]
    [InlineData("04_fusion_model_drop.json")]
    public void TemplatesRunToCompletion(string file)
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario(file));
        var w = doc.Build();
        w.Run();
        Assert.False(w.Suspect, string.Join("\n", w.Warnings));
        Assert.Empty(w.Warnings);
        Assert.Equal("모든 물체 착지", w.StopReason);
        foreach (var b in w.Bodies.Where(b => b.Mode != BodyMode.Kinematic))
            Assert.True(b.Mode == BodyMode.Landed || b.Root.Mode == BodyMode.Landed, $"{b.Name}: {b.Mode}");
        var summary = Results.Summary(doc, w);
        Assert.NotNull(summary["provenance"]?["scenarioHash"]);
        Assert.False(string.IsNullOrWhiteSpace(Results.SummaryText(summary)));
    }

    [Fact(DisplayName = "센서는 스파이크 선행(노즈각 < 15°)으로 착지한다 (L1 CP 후방 안정)")]
    public void SensorLandsNoseFirst()
    {
        var w = ScenarioDoc.Load(TestUtil.Scenario("01_single_sensor_drop.json")).Build();
        w.Run();
        var imp = w.GetBody("sensor").Impact!;
        Assert.True(imp.NoseAngleDeg < 15, $"노즈각 {imp.NoseAngleDeg}");
        Assert.InRange(imp.Velocity.Norm, 30, 65);
    }

    [Fact(DisplayName = "컨테이너 시나리오: 단계 이벤트 순서와 레일·테더 동작")]
    public void ContainerScenarioPhases()
    {
        var w = ScenarioDoc.Load(TestUtil.Scenario("03_container_drone_sensor_poc.json")).Build();
        w.Run();
        double T(string e) => w.FiredEvents[e];
        Assert.True(T("container.separated") < T("lid_drogue"));
        Assert.True(T("lid_drogue") < T("main_deploy"));
        Assert.True(T("main_deploy") < T("eject1"));
        Assert.Equal(2.0, T("eject2") - T("eject1"), 3);
        Assert.True(T("rail1.exit") > T("rail1_start") && T("rail1.exit") - T("rail1_start") < 1.0);
        Assert.True(T("drone1_release") > T("drone1_out"));
        Assert.True(T("sensor1_drop") > T("drone1_release"));
        var tether = (Tether)w.Connectors.First(c => c.Name == "tether1");
        Assert.True(tether.MaxTension > 20 && !tether.Broken);
        var rail = (Rail)w.Connectors.First(c => c.Name == "rail1");
        Assert.Equal(0.34, rail.S, 9);
        var drogue = (Parachute)w.GetBody("container").Devices.First(d => d.Name == "drogue");
        Assert.InRange(drogue.MaxG, 3, 40);
    }

    [Fact(DisplayName = "VV-07(오프라인) Fusion 내보내기 JSON → 동체 FRD 질량특성 변환")]
    public void FusionImportConversion()
    {
        var w = ScenarioDoc.Load(TestUtil.Scenario("04_fusion_model_drop.json")).Build();
        var b = w.GetBody("model");
        Assert.Equal("fusion", b.MassSource);
        Assert.Equal(1.0, b.OwnMass, 12);
        // Fusion CG (0.02, 0.01, 0.15) m → 동체 (Z, −X, −Y)
        Assert.True((b.OwnCg - new Vec3(0.15, -0.02, -0.01)).Norm < 1e-12);
        double iAx = 0.5 * 1.0 * 0.04 * 0.04, iTr = 1.0 * (3 * 0.04 * 0.04 + 0.3 * 0.3) / 12;
        Assert.True(b.OwnInertia.MaxAbsDiff(Mat3.Diagonal(iAx, iTr, iTr)) < 1e-12);
        Assert.Equal(ShapeKind.Mesh, b.Shape!.Kind);
        var (min, max) = b.Shape.Mesh!.Bounds();
        Assert.Equal(0.0, min.X, 5);
        Assert.Equal(0.3, max.X, 5);
        // 메시 균일밀도 질량특성이 Fusion 값과 일치(대체 경로 교차검증)
        var (vol, cg, ipd) = b.Shape.Mesh.VolumeProperties();
        Assert.True((cg - b.OwnCg).Norm < 1e-6);
        var im = ipd * (1.0 / vol);
        Assert.True(im.MaxAbsDiff(b.OwnInertia) / iTr < 0.01);
    }

    [Fact(DisplayName = "FR-NUM-05 결정성: 같은 설정·시드 → 비트 동일")]
    public void Determinism()
    {
        Vec3 Run()
        {
            var w = ScenarioDoc.Load(TestUtil.Scenario("01_single_sensor_drop.json")).Build();
            w.Run();
            return w.GetBody("sensor").Impact!.Position;
        }
        Assert.Equal(Run(), Run());
    }

    [Fact(DisplayName = "분산 경로·LHS 층화·통계")]
    public void DispersionAndStats()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("02_box_parachute_drop.json"));
        Assert.Equal(2.2, JsonPath.GetNumber(doc.Root, "bodies.box.devices.chute.cd0"));
        Assert.Equal(1.0, JsonPath.GetNumber(doc.Root, "events.chute_deploy.when.delay"));
        JsonPath.SetNumber(doc.Root, "bodies.box.devices.chute.cd0", 2.0);
        Assert.Equal(2.0, JsonPath.GetNumber(doc.Root, "bodies.box.devices.chute.cd0"));

        var u = Sampler.Uniforms(100, 3, "lhs", 5);
        for (int d = 0; d < 3; d++)
            Assert.Equal(Enumerable.Range(0, 100), u.Select(r => (int)(r[d] * 100)).OrderBy(x => x));

        var nd = new DispersionDef { Path = "x", Dist = "normal", Sd = 2 };
        Assert.Equal(0, nd.Sample(0.5), 9);
        Assert.Equal(2 * 1.6448536, nd.Sample(0.95), 4);
        var tn = new DispersionDef { Path = "x", Dist = "truncnormal", Sd = 1, Min = -1, Max = 1 };
        Assert.InRange(tn.Sample(0.999), -1, 1);

        var pts = new List<(double, double)>();
        for (int i = 0; i < 360; i++) pts.Add((10 + Math.Cos(i * Math.PI / 180), 20 + Math.Sin(i * Math.PI / 180)));
        var s = ImpactStats.Compute(pts);
        Assert.Equal(10, s.MeanN, 9); Assert.Equal(20, s.MeanE, 9);
        Assert.Equal(1, s.Cep50, 9);
    }

    [Fact(DisplayName = "전체 6DOF Monte Carlo(CPU) 실행·재현")]
    public void FullMonteCarloSmall()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("01_single_sensor_drop.json"));
        var r1 = MonteCarlo.Run(doc, 16, "sensor", "lhs", 3);
        var r2 = MonteCarlo.Run(doc, 16, "sensor", "lhs", 3);
        Assert.All(r1.Runs, r => Assert.True(r.Landed && r.Error is null, r.Error));
        Assert.Equal(r1.Runs.Select(r => r.N), r2.Runs.Select(r => r.N));
        Assert.True(r1.Stats.Cep50 > 0);
    }

    [Fact(DisplayName = "GPU(ILGPU) ↔ CPU 동일 결과 (|Δ| < 1e-6 m)")]
    public void GpuMatchesCpu()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("02_box_parachute_drop.json"));
        var (ps, _, _) = DropMonteCarlo.Sample(doc, 512, 9, "lhs");
        var cpu = new CpuDropBackend().Run(ps);
        using var gpu = GpuDropBackend.TryCreate(allowCpu: true, out var msg);
        Assert.True(gpu is not null, msg);
        var g = gpu!.Run(ps);
        for (int i = 0; i < ps.Length; i++)
        {
            Assert.Equal(1, cpu[i].Landed);
            Assert.Equal(cpu[i].Landed, g[i].Landed);
            Assert.True(Math.Abs(cpu[i].N - g[i].N) < 1e-6 && Math.Abs(cpu[i].E - g[i].E) < 1e-6 && Math.Abs(cpu[i].T - g[i].T) < 1e-6,
                $"{msg}: run {i} CPU({cpu[i].N},{cpu[i].E},{cpu[i].T}) GPU({g[i].N},{g[i].E},{g[i].T})");
        }
    }

    [Fact(DisplayName = "GPU 단순 모델 ↔ 전체 6DOF 엔진(L0 등가) 일치")]
    public void DropModelMatchesEngine()
    {
        var w = TestUtil.Run("""
        { "schema":"mns.scenario/1", "sim":{"dt":0.002,"tEnd":300},
          "environment":{"wind":{"speed":4,"directionFrom":300,"refHeight":10,"shear":"log","roughness":0.05}},
          "carrier":{"name":"heli","altitude":300,"speed":20,"heading":90},
          "bodies":[{"name":"box","parent":"heli","mass":5.5,"inertia":[0.1,0.1,0.1],"aero":{"cd":1.05,"area":0.0784},
            "devices":[{"type":"parachute","name":"c","d0":1.22,"cd0":2.2,"attach":[0,0,0],"lineStretchTime":0.35}]}],
          "events":[{"name":"r","when":{"type":"time","t":1.0},"do":[{"type":"separate","body":"box","relVel":[0,0,1.5]}]},
                    {"name":"d","when":{"type":"after","event":"box.separated","delay":1.0},"do":[{"type":"deploy","device":"box.c"}]}] }
        """);
        var imp = w.GetBody("box").Impact!;
        var model = new JsonObject
        {
            ["mass"] = 5.5, ["cdA"] = 1.05 * 0.0784, ["dt"] = 0.002, ["tMax"] = 300,
            ["chute"] = new JsonObject { ["d0"] = 1.22, ["cd0"] = 2.2, ["delay"] = 1.0, ["lineStretchTime"] = 0.35 },
            ["release"] = new JsonObject { ["altitude"] = 300, ["speed"] = 20, ["heading"] = 90, ["vd"] = 1.5 },
        };
        var env = new JsonObject { ["wind"] = new JsonObject { ["speed"] = 4, ["directionFrom"] = 300, ["refHeight"] = 10, ["roughness"] = 0.05 } };
        var p = DropMonteCarlo.Nominal(model, env);
        p.N0 = 0; p.E0 = 20; // 분리 시각(1 s) 운반체 위치
        var r = DropModel.Simulate(p);
        Assert.Equal(1, r.Landed);
        Assert.True(Math.Abs(r.T + 1.0 - imp.Time) < 0.05, $"T {r.T + 1} vs {imp.Time}");
        Assert.True(Math.Sqrt(Math.Pow(r.N - imp.Position.X, 2) + Math.Pow(r.E - imp.Position.Y, 2)) < 0.5,
            $"착지 ({r.N},{r.E}) vs ({imp.Position.X},{imp.Position.Y})");
    }

    [Fact(DisplayName = "결과 폴더 출력: CSV·summary·scene·메시(provenance 포함)")]
    public void ResultFolderOutputs()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("02_box_parachute_drop.json"));
        var w = doc.Build();
        w.Run();
        var dir = Path.Combine(Path.GetTempPath(), $"mns_res_{Guid.NewGuid():N}");
        try
        {
            Results.WriteAll(doc, w, dir, "testhash");
            foreach (var f in new[] { "timeseries.csv", "devices.csv", "summary.json", "events.json", "scene.json", "scenario.json", "meshes/box.stl" })
                Assert.True(File.Exists(Path.Combine(dir, f)), f);
            var lines = File.ReadAllLines(Path.Combine(dir, "timeseries.csv"));
            Assert.StartsWith("t_s,body,mode", lines[0]);
            Assert.True(lines.Length > 100);
            var s = J.ParseObject(File.ReadAllText(Path.Combine(dir, "summary.json")));
            Assert.Equal("testhash", s["provenance"]!["gitHash"]!.GetValue<string>());
            Assert.Equal(doc.Hash(), s["provenance"]!["scenarioHash"]!.GetValue<string>());
            Assert.True(TriMesh.LoadStl(Path.Combine(dir, "meshes", "box.stl")).TriangleCount == 12);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact(DisplayName = "요소 등록부: 새 Action 을 코어 수정 없이 추가 (FR-GEN-03)")]
    public void RegistryExtension()
    {
        int hits = 0;
        ElementRegistry.RegisterAction("countHit", (d, ctx) => new CountAction(() => hits++));
        TestUtil.Run("""
        { "schema":"mns.scenario/1", "sim":{"dt":0.01,"tEnd":1}, "environment":{"vacuum":true},
          "bodies":[{"name":"b","mass":1,"inertia":[0.1,0.1,0.1],"aero":"none","position":[0,0,-100]}],
          "events":[{"name":"e","when":{"type":"time","t":0.5},"do":[{"type":"countHit"}]}] }
        """);
        Assert.Equal(1, hits);
    }

    private sealed class CountAction(Action a) : Sim.Core.Engine.IAction
    {
        public void Execute(Sim.Core.Engine.World w) => a();
        public string Describe() => "countHit";
    }
}
