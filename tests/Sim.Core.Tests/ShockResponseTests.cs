using System.Globalization;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;
using Xunit.Abstractions;

namespace Sim.Core.Tests;

/// <summary>시나리오 09(합성 대체) 를 드론 테더 스냅까지 한 번만 실행해 공유 — 처음 쓰는 시험에서 실행(설계 자료 없는 공개판에서는 안 씀).</summary>
public sealed class Design09ShockFixture
{
    private readonly Lazy<World> _world = new(() =>
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("09_munition_3petal_longcontainer_real.json"));
        doc.Root["realModels"] = new JsonObject { ["mode"] = "synthetic" };
        doc.Root["sim"]!["tEnd"] = 40.0;
        var w = doc.Build();
        w.Run();
        return w;
    });

    public World World => _world.Value;
}

/// <summary>개방 충격 충격 응답(보 동해석, 근사 C) · StressModel 얇은 벽 퇴화 단면 회귀(FX-10).</summary>
public class ShockResponseTests(Design09ShockFixture fx, ITestOutputHelper output) : IClassFixture<Design09ShockFixture>
{
    private static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);

    private static World Bar(double l, double h, string material = "al6061-t6")
    {
        var al = MaterialLibrary.Find(material)!;
        double mass = al.Density * l * h * h;
        return TestUtil.Build($$"""
            { "schema": "mns.scenario/1", "name": "bar", "sim": { "dt": 0.001, "tEnd": 0.01 },
              "environment": { "vacuum": true, "gravity": 0 },
              "bodies": [ { "name": "bar", "position": [0, 0, -100], "mass": {{Inv(mass)}},
                "shape": { "type": "box", "size": [{{Inv(l)}}, {{Inv(h)}}, {{Inv(h)}}] }, "structure": { "material": "{{material}}", "section": "solid" } } ] }
            """);
    }

    [DesignFact]
    public void Design09_t12_approx_stress_is_physical_for_every_free_body()
    {
        // 회귀: 09 t=12 s '--all' 근사 응력 최대 1.75E+07 MPa — 원인은 nose(munition_nose.stl, 얇은 벽) 끝 슬라이스의 둘레 요소가
        // 한 줄로 늘어선 퇴화 단면(I_u → 1e-14 하한)과 단면 밖 끝판 면의 큰 섬유 거리. 벽 두께 관성 + 섬유 거리 제한으로 수정.
        var w = fx.World;
        var f = w.Recorder.Frames[w.Recorder.IndexAt(12)];
        int n = 0;
        foreach (var b in w.Bodies)
        {
            if (f.Bodies[b.Index].Mode != BodyMode.Free || StressModel.Build(b) is not { } m) continue;
            var r = m.Evaluate(m.InputFrom(w, f));
            output.WriteLine($"{b.Name}: σvm max {r.MaxVonMises / 1e6:G4} MPa ({r.DominantAtMax}), 가속도 {r.MaxAccelG:F2} G");
            Assert.True(double.IsFinite(r.MaxVonMises) && r.MaxVonMises < 50e6, $"{b.Name}: σvm {r.MaxVonMises / 1e6:G4} MPa");
            if (m.ThinWall)
                for (int k = 0; k < m.Slices; k++)
                {
                    double t = m.Def!.Thickness!.Value;
                    // 벽 자체 두께 항이 있으므로 I ≥ (t²/12)·A 의 일부 — 1e-14 하한에 붙은 퇴화 단면이 없어야 한다
                    Assert.True(m.SecIu[k] > 1e-3 * t * t / 12 * m.SecArea[k] && m.SecIv[k] > 1e-3 * t * t / 12 * m.SecArea[k], $"{b.Name} 슬라이스 {k}");
                }
            n++;
        }
        Assert.True(n >= 5);
        var nose = StressModel.Build(w.GetBody("nose"))!;
        Assert.True(nose.Evaluate(nose.InputFrom(w, f)).MaxVonMises < 10e6);
    }

    [Fact]
    public void Free_free_bar_frequencies_match_beam_theory()
    {
        double l = 1.0, h = 0.05;
        var w = Bar(l, h);
        var m = StressModel.Build(w.Bodies[0])!;
        var al = m.Def!.Material!;
        var (fa, fu, fv) = ShockResponse.Frequencies(m);
        double mL = m.TotalMass / l;
        double fAx = 1 / (2 * l) * Math.Sqrt(al.E * h * h / mL);                         // 자유-자유 막대 c/2L
        double fB = 22.373 / (2 * Math.PI) * Math.Sqrt(al.E * Math.Pow(h, 4) / 12 / (mL * Math.Pow(l, 4)));  // (β₁L)² = 22.373
        output.WriteLine($"축 {fa:F1} Hz (해석 {fAx:F1}), 굽힘 {fu:F1}/{fv:F1} Hz (해석 {fB:F1})");
        Assert.InRange(fa / fAx, 0.97, 1.03);
        Assert.InRange(fu / fB, 0.95, 1.05);
        Assert.InRange(fv / fB, 0.95, 1.05);
    }

    [Fact]
    public void Suddenly_applied_end_force_travels_as_stress_wave_and_doubles_midspan_axial_force()
    {
        // 자유-자유 막대 끝에 계단 하중 F: 준정적(관성 완화) 중앙 축력 = F/2, 동적으로는 응력파(크기 F)가 L/2c 뒤 중앙에 도달 → 약 2배.
        double l = 1.0, h = 0.05, force = 1000;
        var w = Bar(l, h);
        var m = StressModel.Build(w.Bodies[0])!;
        var al = m.Def!.Material!;
        double c = Math.Sqrt(al.E * h * h / (m.TotalMass / l));
        var p = m.FromAxisFrame(m.X0, 0, 0);
        var fVec = m.FromAxisFrame(-force, 0, 0); // 끝을 바깥(−축)으로 당김 → 인장
        void Loads(double t, Vec3[] f, Vec3[] mo)
        {
            Array.Clear(f); Array.Clear(mo);
            if (t <= 0) return;
            f[0] = fVec; mo[0] = Vec3.Cross(p, fVec);
        }
        double tEnd = 4 * l / c;
        var (times, acc) = ShockResponse.Integrate(m, Loads, 0, tEnd, zeta: 0.01, dt: l / c / 400, outDt: l / c / 200);
        var inp = new StressInput(Vec3.Zero, 0, 0, Vec3.Zero, [new PointLoad(p, fVec, "pull")]);
        int mid = m.Slices / 2;
        double qs = m.Evaluate(inp).AxialForce[mid];
        double peak = 0, tArrive = double.NaN;
        for (int i = 0; i < times.Length; i++)
        {
            double nMid = m.Evaluate(inp, sliceAccel: acc[i]).AxialForce[mid];
            if (double.IsNaN(tArrive) && nMid > 0.5 * force) tArrive = times[i];
            peak = Math.Max(peak, nMid);
        }
        output.WriteLine($"준정적 N_mid {qs:F1} N, 동적 최대 {peak:F1} N (DAF {peak / qs:F2}), 도달 {tArrive * 1e3:F3} ms (L/2c = {l / 2 / c * 1e3:F3} ms)");
        Assert.InRange(qs, 0.45 * force, 0.55 * force);
        Assert.InRange(peak / qs, 1.6, 2.3);
        Assert.InRange(tArrive / (l / 2 / c), 0.75, 1.3);
    }

    [DesignFact]
    public void Shock_capture_records_step_resolution_around_parachute_openings()
    {
        var w = fx.World;
        var frames = w.Recorder.Frames;
        double tDrogue = w.GetBody("container").Devices.OfType<Parachute>().First(p => p.Name == "drogue").MaxForceTime;
        Assert.True(double.IsFinite(tDrogue));
        int i = w.Recorder.IndexAt(tDrogue);
        Assert.True(frames[i + 1].T - frames[i].T < 0.0015, $"드로그 최대 하중 근처 기록 간격 {frames[i + 1].T - frames[i].T}");
    }

    [DesignFact]
    public void Design09_container_and_drone_shock_windows_give_time_varying_dynamic_response()
    {
        var w = fx.World;
        var c = StressModel.Build(w.GetBody("container"))!;
        var h = ShockResponse.Solve(w, c);
        foreach (var (win, k) in h.Windows.Select((x, k) => (x, k)))
        {
            var (g, t) = h.WindowPeak(k);
            output.WriteLine($"{win.Label}: {win.T0:F3}–{win.T1:F3} s, 하중 최대 {win.PeakLoadN:F0} N, 가속도 최대 {g:F1} G @ {t:F4}");
        }
        output.WriteLine(h.Method);
        Assert.Contains(h.Windows, x => x.Source == "container.drogue");
        Assert.Contains(h.Windows, x => x.Source == "container.main");
        int wd = h.Windows.FindIndex(x => x.Source == "container.drogue");
        var (gPk, tPk) = h.WindowPeak(wd);
        Assert.InRange(gPk, 1, 300);
        // 시간에 따라 변한다: 창 시작 가속도와 최대가 다르고, 최대 뒤 감쇠
        var p = h.PeakAccelG[wd];
        Assert.True(p.Max() > 1.5 * p[0]);
        Assert.True(p[^1] < 0.8 * p.Max());
        // 동적 응력 = 면 응력(창 안에서 Evaluate 에 동해석 가속도 대입), 유한하고 물리적 범위
        var f = w.Recorder.Interpolate(tPk)!;
        var rDyn = c.Evaluate(c.InputFrom(w, f), sliceAccel: h.AccelAt(tPk));
        var rQs = c.Evaluate(c.InputFrom(w, f));
        output.WriteLine($"드로그 최대 순간: 동적 σvm {rDyn.MaxVonMises / 1e6:F2} MPa / 준정적 {rQs.MaxVonMises / 1e6:F2} MPa, 가속도 {rDyn.MaxAccelG:F1} G / {rQs.MaxAccelG:F1} G");
        Assert.True(double.IsFinite(rDyn.MaxVonMises) && rDyn.MaxVonMises < 200e6 && rDyn.MaxVonMises > 0);
        Assert.InRange(rDyn.MaxAccelG, 0.3 * rQs.MaxAccelG, 5 * rQs.MaxAccelG + 1);
        Assert.Null(h.AccelAt(h.Windows[wd].T0 - 0.5 > 0 ? h.Windows[wd].T0 - 0.5 : -1));
        // 실린 자손(Stowed: 드론 4·센서 4·주낙하산 팩·도어)이 관성 완화 질량에 들어가 강체 가속도가 엔진 기록(합성 질량 기준)과 맞는다.
        // 실행 뒤 드론은 분리돼 Parent = null — 분리 전 부모(FormerParent)로 그 프레임의 사슬을 되짚는다.
        int wm = h.Windows.FindIndex(x => x.Source == "container.main");
        var fm = w.Recorder.Frames[w.Recorder.IndexAt(h.Windows[wm].TTrigger + 0.3)];
        var inpM = c.InputFrom(w, fm);
        Assert.Contains(inpM.Carried!, x => x.Name == "drone1");
        Assert.Contains(inpM.Carried!, x => x.Name == "sensor4");
        var rM = c.Evaluate(inpM);
        double engineG = fm.Bodies[c.Body.Index].LoadG;
        output.WriteLine($"주낙하산 +0.3 s: 실린 질량 {inpM.Carried!.Sum(x => x.Mass):F2} kg, 근사 강체 {(rM.RigidAccel.Norm / StressModel.G0):F2} G vs 엔진 {engineG:F2} G");
        Assert.InRange(rM.RigidAccel.Norm / StressModel.G0 / engineG, 0.75, 1.3);
        // 드론 테더 스냅(드론 쪽 창)
        var d1 = StressModel.Build(w.GetBody("drone1"))!;
        var hd = ShockResponse.Solve(w, d1);
        foreach (var (win, k) in hd.Windows.Select((x, k) => (x, k))) output.WriteLine($"drone1 {win.Label}: 가속도 최대 {hd.WindowPeak(k).G:F1} G");
        Assert.Contains(hd.Windows, x => x.Source.StartsWith("tether1", StringComparison.Ordinal));
    }
}
